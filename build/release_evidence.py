#!/usr/bin/env python3
"""Verify the *actual* artifacts a release would publish, per RID, before anything is published.

The release gate has two different jobs and they must not be conflated:

  * `build/release_preflight.py` answers "may a release proceed at all?" from the repository's own
    trees (the coverage lock table, the retaining manifest and the rebuild receipts under
    `third_party/`). It runs *before* a package exists, so it cannot and must not demand packaging
    output.

  * this module answers "are the artifacts we are about to publish actually what those trees
    describe?" from the real, downloaded, unpacked packages — one verdict per RID. A manifest that
    says `true`, a synthetic receipt, or a job that merely concluded `success` is **not** evidence
    about the bytes in the archive.

So this is deliberately *not* the preflight: it runs only after every package artifact has been
downloaded and extracted, and it refuses the release when any of the six RIDs is missing its
artifact/hash, when a package's staged `JavaHost` does not carry the RID's real dependencies, when
the packaged `native-backend-receipt.json` is not a real receipt for that RID, or when the retained
license/notice material is not physically inside the package.

Nothing here mutates repository state and nothing here trusts a summary flag.
"""
from __future__ import annotations

import argparse
import base64
import contextlib
import gzip
import hashlib
import io
import json
import shutil
import stat
import subprocess
import sys
import tarfile
import tempfile
import zipfile
from collections import deque
from pathlib import Path, PurePosixPath

import native_coverage
from ci_reuse import ensure_commit, native_build_hash, source_hash
import rebuild_native_backend as rebuild

# The six RIDs a release publishes, in the order the packaging matrix declares them. A release with
# any of them missing is refused: "five of six" is not a distribution of this product.
RIDS = native_coverage.RIDS

# Where the release workflow downloads `pattern: package.*` into. download-artifact with
# `merge-multiple: true` puts every package file directly in this directory.
ARCHIVES_DIR = Path("artifacts/release")
# Where each RID's package is unpacked for inspection.
EXTRACT_DIR = Path("artifacts/package-verify")
# The per-RID metadata artifact the packaging job uploads.
META_DIR = Path("artifacts/meta")

# The Java host inside a published package, relative to the package root. This mirrors
# `bundle.py` (JAVA_HOST_DIRECTORY / RECEIPT_NAME); it is the tree a plugin actually loads.
JAVA_HOST_DIRECTORY = "JavaHost"
RECEIPT_NAME = "native-backend-receipt.json"
DEPS_DIRECTORY = "deps"
JAVA_HOST_FILES = ("host.jar", "java-host.json", "upstream-runtime.json")
JAVA_HOST_NOTICE_FILES = ("LICENSE-tvbox-silicon",)

# The notice tree the bundle copies onto itself for the media kernel (bundle.copy_media_notices).
MEDIA_NOTICES = (Path("licenses") / "media" / "asset.json",)
# The JDK/Temurin license a bundled runtime must carry.
JRE_NOTICES = ("LICENSE", "legal/java.base/LICENSE", "Contents/Home/LICENSE", "Contents/Home/legal/java.base/LICENSE")

# Package extensions the packaging matrix produces (package.py / package.yml).
ARCHIVE_SUFFIXES = (".zip", ".tar", ".tar.gz", ".tar.xz", ".tar.bz2", ".tar.zst", ".dmg", ".deb", ".rpm")


# Evidence limits apply to uncompressed bytes; exceeding them fails without dropping checks.
MAX_SUBSET_BYTES = 512 * 1024 * 1024
MAX_RECEIPT_BYTES = 32 * 1024 * 1024
MAX_MANIFEST_FILES = 100_000
COPY_CHUNK = 1024 * 1024


class ReleaseEvidenceError(RuntimeError):
    """A published package does not prove what the release claims; publishing must stop."""


def sha256_file(path: Path) -> str:
    with Path(path).open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def normalize_member(name: str) -> str:
    """A zip member name without its leading `./` decorations."""
    while name.startswith("./"):
        name = name[2:]
    return name


def _member_path(root: Path, name: str) -> Path:
    name = normalize_member(name)
    parts = PurePosixPath(name)
    if not name or parts.is_absolute() or ".." in parts.parts or "\\" in name or \
            (parts.parts and ":" in parts.parts[0]):
        raise ReleaseEvidenceError(f"unsafe package member {name!r}")
    return root.joinpath(*parts.parts)


def _command(command: list[str]) -> bytes:
    result = subprocess.run(command, capture_output=True)
    if result.returncode:
        raise ReleaseEvidenceError(
            f"cannot read package: {' '.join(command)}: {result.stderr.decode(errors='replace').strip()}")
    return result.stdout


def _command_to_file(command: list[str], target: Path) -> None:
    with target.open("wb") as output, tempfile.TemporaryFile() as errors:
        result = subprocess.run(command, stdout=output, stderr=errors)
        if result.returncode:
            errors.seek(0)
            detail = errors.read(4096).decode(errors="replace")
            raise ReleaseEvidenceError(f"cannot read package: {' '.join(command)}: {detail}")


def _extract_package(archive: Path, root: Path) -> None:
    """Materialize real file bytes, resolving tar links only to files within the payload."""
    suffix = next((s for s in ARCHIVE_SUFFIXES if archive.name.lower().endswith(s)), None)
    if suffix is None:
        raise ReleaseEvidenceError(f"{archive.name}: unsupported package format")
    seen: set[Path] = set()

    def write(name: str, stream) -> None:
        target = _member_path(root, name)
        if target in seen:
            raise ReleaseEvidenceError(f"duplicate package member {name!r}")
        seen.add(target)
        target.parent.mkdir(parents=True, exist_ok=True)
        with target.open("wb") as output:
            shutil.copyfileobj(stream, output, COPY_CHUNK)

    if suffix == ".zip":
        with zipfile.ZipFile(archive) as reader:
            for info in reader.infolist():
                _member_path(root, info.filename)
                if stat.S_ISLNK(info.external_attr >> 16):
                    raise ReleaseEvidenceError(f"{archive.name}: linked member {info.filename}")
                if not info.is_dir():
                    with reader.open(info) as stream:
                        write(info.filename, stream)
        return
    payload = archive
    if suffix == ".deb":
        payloads = []
        with archive.open("rb") as container:
            if container.read(8) != b"!<arch>\n":
                raise ReleaseEvidenceError("invalid DEB ar header")
            while header := container.read(60):
                if len(header) != 60 or header[58:] != b"`\n":
                    raise ReleaseEvidenceError("invalid DEB ar member header")
                name = header[:16].decode("ascii").strip().rstrip("/")
                size = int(header[48:58].decode("ascii").strip())
                if size < 0:
                    raise ReleaseEvidenceError("invalid DEB member size")
                if name in ("data.tar", "data.tar.gz", "data.tar.xz", "data.tar.zst"):
                    payload = root.parent / name
                    with payload.open("wb") as output:
                        remaining = size
                        while remaining:
                            block = container.read(min(1024 * 1024, remaining))
                            if not block: raise ReleaseEvidenceError("truncated DEB payload")
                            output.write(block)
                            remaining -= len(block)
                    payloads.append(payload)
                else:
                    container.seek(size, 1)
                if size % 2: container.read(1)
        if len(payloads) != 1:
            raise ReleaseEvidenceError(f"{archive.name}: expected exactly one data.tar payload")
        payload = payloads[0]
    if suffix.startswith(".tar") or suffix == ".deb":
        try:
            with tarfile.open(payload) as reader:
                entries = {}
                directories = {""}
                for info in reader.getmembers():
                    target = _member_path(root, info.name)
                    name = target.relative_to(root).as_posix()
                    if name == "." and info.isdir():
                        continue
                    if name == "." or name in entries:
                        raise ReleaseEvidenceError(f"duplicate or invalid package member {info.name!r}")
                    if not (info.isfile() or info.isdir() or info.issym() or info.islnk()):
                        raise ReleaseEvidenceError(f"{archive.name}: non-file member {info.name}")
                    entries[name] = info
                    directories.update(parent.as_posix() if parent != PurePosixPath(".") else ""
                                       for parent in PurePosixPath(name).parents)
                    if info.isdir():
                        directories.add(name)
                for name in directories - {""}:
                    if name in entries and not entries[name].isdir():
                        raise ReleaseEvidenceError(f"{archive.name}: conflicting package directory {name}")

                def resolve(name: str) -> str:
                    pending = deque(PurePosixPath(name).parts)
                    resolved = []
                    active = set()
                    while pending:
                        part = pending.popleft()
                        if isinstance(part, tuple):
                            active.remove(part[0])
                            continue
                        if part in ("", "."):
                            continue
                        if part == "..":
                            if not resolved:
                                raise ReleaseEvidenceError(f"{archive.name}: escaping link {name}")
                            resolved.pop()
                            continue
                        candidate = "/".join([*resolved, part])
                        info = entries.get(candidate)
                        if info is not None and (info.issym() or info.islnk()):
                            link = PurePosixPath(info.linkname)
                            if not info.linkname or link.is_absolute() or "\\" in info.linkname or \
                                    (link.parts and ":" in link.parts[0]):
                                raise ReleaseEvidenceError(f"{archive.name}: unsafe link {candidate}")
                            if candidate in active:
                                raise ReleaseEvidenceError(f"{archive.name}: cyclic link {name}")
                            active.add(candidate)
                            if info.islnk():
                                resolved = []  # Tar hardlink names are relative to the archive root.
                            pending.extendleft(reversed([*link.parts, (candidate,)]))
                            continue
                        if candidate not in directories and (info is None or not info.isfile()):
                            raise ReleaseEvidenceError(f"{archive.name}: unresolved link {name}")
                        if info is not None and info.isfile() and any(
                                isinstance(item, str) for item in pending):
                            raise ReleaseEvidenceError(f"{archive.name}: non-directory link target {name}")
                        resolved.append(part)
                    result = "/".join(resolved)
                    if result not in entries or not entries[result].isfile():
                        raise ReleaseEvidenceError(f"{archive.name}: link does not resolve to a file {name}")
                    return result

                for name, info in entries.items():
                    if not info.isdir():
                        actual = entries[resolve(name)]
                        with reader.extractfile(actual) as stream:
                            write(name, stream)
            return
        except tarfile.ReadError:
            # Convert unsupported compression to tar, preserving link metadata for the same checks.
            reader = shutil.which("bsdtar")
            if reader is None:
                raise ReleaseEvidenceError(f"{archive.name}: no archive reader for {suffix}")
            converted = root.parent / "converted.tar"
            _command([reader, "-cf", str(converted), "--format=pax", "@" + str(payload)])
            _extract_package(converted, root)
            return
    if suffix == ".dmg":
        if sys.platform != "darwin":
            raise ReleaseEvidenceError(f"{archive.name}: DMG requires a macOS package evidence receipt")
        mount = root.parent / "mount"
        mount.mkdir()
        _command(["hdiutil", "attach", str(archive), "-readonly", "-nobrowse",
                  "-mountpoint", str(mount)])
        try:
            for path in mount.rglob("*"):
                if path.is_symlink():
                    # macOS DMGs commonly carry an Applications shortcut, not runtime evidence.
                    continue
                if path.is_file():
                    with path.open("rb") as stream:
                        write(path.relative_to(mount).as_posix(), stream)
        finally:
            _command(["hdiutil", "detach", str(mount)])
        return
    # RPM is the remaining format. Convert once, preserving links for the TAR resolver above.
    # bsdtar(1) documents @archive in create mode for conversion and --format=pax.
    reader = shutil.which("bsdtar")
    if reader is None:
        raise ReleaseEvidenceError(f"{archive.name}: no archive reader for {suffix}")
    converted = root.parent / "rpm-payload.tar"
    _command([reader, "--format=pax", "-cf", str(converted), "@" + str(payload)])
    _extract_package(converted, root)


@contextlib.contextmanager
def extracted_package(archive: Path):
    archive = Path(archive).resolve()
    try:
        with tempfile.TemporaryDirectory(prefix=".release-evidence-", dir=archive.parent) as directory:
            root = Path(directory) / "payload"
            root.mkdir()
            _extract_package(archive, root)
            yield root
    except (OSError, ValueError, tarfile.TarError, zipfile.BadZipFile,
            native_coverage.NativeCoverageError, rebuild.RebuildError, KeyError,
            TypeError, AttributeError, UnicodeError) as error:
        raise ReleaseEvidenceError(f"{archive.name}: cannot verify package bytes: {error}") from error


def members_of(archive: Path) -> list[str]:
    """Read the package payload using the same reader as the byte verification."""
    with extracted_package(archive) as root:
        return sorted(path.relative_to(root).as_posix() for path in root.rglob("*") if path.is_file())


def package_java_host(members: list[str]) -> str | None:
    """The `JavaHost` prefix a package's members are rooted at, or None when there is no host.

    The prefix is discovered from the members instead of assumed: a Windows zip roots the host at
    `JavaHost/`, a macOS app nests it under `VodBox.app/Contents/MacOS/`, and a Linux deb roots it
    at `opt/vodbox/`. Finding it from the real members is the whole point of this module.
    """
    for member in members:
        parts = member.split("/")
        if JAVA_HOST_DIRECTORY in parts:
            index = parts.index(JAVA_HOST_DIRECTORY)
            return "/".join(parts[:index + 1]) + "/"
    return None


def verify_java_host(rid: str, archive: Path, members: list[str]) -> dict:
    """Verify the RID's real Java host: archive hash, host files, deps and the packaged receipt."""
    digest = sha256_file(archive)
    prefixes = {member.removesuffix("host.jar") for member in members
                if member.endswith("JavaHost/host.jar")}
    if len(prefixes) > 1:
        raise ReleaseEvidenceError(f"{rid}: {archive.name} carries multiple JavaHost trees")
    prefix = package_java_host(members)
    if prefix is None:
        raise ReleaseEvidenceError(
            f"{rid}: {archive.name} does not carry the {JAVA_HOST_DIRECTORY} plugin host")
    present = set(members)

    def require(name: str) -> None:
        if prefix + name not in present:
            raise ReleaseEvidenceError(f"{rid}: {archive.name} is missing {prefix}{name}")

    for name in JAVA_HOST_FILES + JAVA_HOST_NOTICE_FILES:
        require(name)

    # The real Maven closure must be inside the package: an empty deps/ links nothing.
    deps = sorted(m for m in members
                  if m.startswith(prefix + DEPS_DIRECTORY + "/") and m.endswith(".jar"))
    if not deps:
        raise ReleaseEvidenceError(
            f"{rid}: {archive.name} carries no staged {DEPS_DIRECTORY}/*.jar closure")

    # The whole point of the per-RID source rebuild: the packaged backend jar is the rebuilt one.
    backend = [m for m in deps if Path(m).name.startswith(
        f"{native_coverage.BACKEND_ARTIFACT_PREFIX}-{native_coverage.BACKEND_VERSION}")]
    if not backend:
        raise ReleaseEvidenceError(
            f"{rid}: {archive.name} does not carry "
            f"{native_coverage.BACKEND_ARTIFACT_PREFIX}-{native_coverage.BACKEND_VERSION}.jar: the "
            f"upstream closures are never staged as a source-rebuilt per-RID host")
    if prefix + RECEIPT_NAME not in present:
        raise ReleaseEvidenceError(
            f"{rid}: {archive.name} is missing {prefix}{RECEIPT_NAME}; a package without its "
            f"source-rebuild receipt does not prove where its native came from")

    return {
        "archive": archive.name,
        "sha256": digest,
        "members": len(members),
        "hostPrefix": prefix,
        "depsJars": len(deps),
        "backendJar": Path(backend[0]).name,
        "receipt": prefix + RECEIPT_NAME,
    }


def verify_licenses(rid: str, archive: Path, members: list[str]) -> dict:
    """Verify the retained license/notice material is physically inside the package.

    A license file that exists only in the build tree is not retained material: the obligation
    attaches to the *distributed* archive. This checks the package's own notice tree, by content,
    not a summary flag that says the notices were collected.
    """
    license_like = [m for m in members if Path(m).name.lower().startswith(
        ("license", "copying", "notice", "copyright"))]
    if not license_like:
        raise ReleaseEvidenceError(f"{rid}: {archive.name} carries no license/notice file at all")
    # The notice tree is nested under the app root on every platform (`JavaHost`, `VodBox.app/
    # Contents/MacOS`, `opt/vodbox`), so each notice is matched by suffix: the check is that the
    # notices are physically present, not that a platform prefix was guessed.
    missing = [name.as_posix() for name in MEDIA_NOTICES
               if not any(m.endswith(name.as_posix()) for m in members)]
    if missing:
        raise ReleaseEvidenceError(
            f"{rid}: {archive.name} does not retain the media notices {', '.join(sorted(missing))}")
    prefix = package_java_host(members)
    jre_notice = next((prefix + "jre/" + name for name in JRE_NOTICES
                       if prefix and prefix + "jre/" + name in members), None)
    if jre_notice is None:
        raise ReleaseEvidenceError(
            f"{rid}: {archive.name} does not retain the bundled runtime's license "
            f"({'/'.join(JRE_NOTICES)})")
    return {
        "archive": archive.name,
        "licenseFiles": len(license_like),
        "mediaNotices": [name.as_posix() for name in MEDIA_NOTICES
                         if any(m.endswith(name.as_posix()) for m in members)],
        "jreNotice": jre_notice,
    }


def verify_receipt_artifact(rid: str, receipts_dir: Path) -> dict:
    """The RID's own downloaded source-rebuild receipt artifact, verified as a real file.

    This is deliberately separate from the packaged copy: the release must prove that the receipt
    which travelled with *this* package exists at all, and that it is a real receipt for this RID —
    not empty, not another RID's, and not a synthetically-authored stub.
    """
    path = Path(receipts_dir) / f"native-backend-receipt.{rid}.json"
    if not path.is_file() or path.stat().st_size == 0:
        raise ReleaseEvidenceError(
            f"{rid}: no downloaded source-rebuild receipt artifact {path.name}")
    try:
        receipt = json.loads(path.read_text(encoding="utf-8"))
    except ValueError as error:
        raise ReleaseEvidenceError(f"{rid}: {path.name} is not valid JSON: {error}") from error
    if receipt.get("rid") != rid:
        raise ReleaseEvidenceError(
            f"{rid}: {path.name} is a receipt for {receipt.get('rid')!r}, not {rid}")
    backend = receipt.get("backend") or {}
    if not backend or backend.get("coordinate") is None:
        raise ReleaseEvidenceError(
            f"{rid}: {path.name} records no backend coordinate; a receipt without the source it "
            f"rebuilt is not evidence")
    return {"path": str(path), "sha256": sha256_file(path), "rid": rid,
            "coordinate": backend.get("coordinate"),
            "excluded": bool(backend.get("excluded")),
            "engineSource": (backend.get("engine") or {}).get("source_archive_sha256")}


def _read_object(path: Path) -> dict:
    data = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(data, dict):
        raise ReleaseEvidenceError(f"{path.name}: expected a JSON object")
    return data


def verify_packaged_evidence(rid: str, host: Path) -> dict:
    """Bind the packaged receipt to native bytes and the source/notices actually distributed."""
    receipt = _read_object(host / RECEIPT_NAME)
    if receipt.get("rid") != rid:
        raise ReleaseEvidenceError(f"{rid}: packaged receipt is for {receipt.get('rid')!r}")
    backend = receipt.get("backend") or {}
    unicorn1 = receipt.get("unicorn1") or {}
    if backend.get("coordinate") != rebuild.BACKEND_COORDINATE or \
            unicorn1.get("coordinate") != rebuild.UNICORN1_COORDINATE:
        raise ReleaseEvidenceError(f"{rid}: packaged receipt has unexpected backend/unicorn coordinates")
    deps = host / DEPS_DIRECTORY
    for jar in deps.glob("*.jar"):
        with zipfile.ZipFile(jar) as reader:
            if reader.testzip() is not None or len(reader.namelist()) != len(set(reader.namelist())):
                raise ReleaseEvidenceError(f"{rid}: corrupt or duplicate jar members in {jar.name}")
    coverage = native_coverage.verify_deps(deps, rid)
    problems = rebuild.verify_receipt(receipt, deps=deps)
    problems += native_coverage.published_input_problems(receipt, deps)
    if not backend.get("excluded"):
        problems += native_coverage.receipt_problems(receipt)
    if problems:
        raise ReleaseEvidenceError(f"{rid}: packaged receipt: {'; '.join(problems)}")
    integration = backend["jar_integration"]
    backend_jar = rebuild._maven_jar(deps, rebuild.BACKEND_JAR_GLOB, rebuild.BACKEND_COORDINATE)
    if not integration.get("post_rewrite_jar_sha256"):
        raise ReleaseEvidenceError(f"{rid}: receipt records no rewritten backend jar hash")
    if not backend.get("excluded"):
        with zipfile.ZipFile(backend_jar) as reader:
            info = reader.getinfo(integration["native_entry"])
            with reader.open(info) as stream:
                native_hash = hashlib.file_digest(stream, "sha256").hexdigest()
        output = backend["output_library"]
        if not info.file_size or native_hash != output.get("sha256") or \
                info.file_size != output.get("size"):
            raise ReleaseEvidenceError(f"{rid}: output native bytes hash/size does not match receipt")
    u1 = rebuild._maven_jar(deps, rebuild.UNICORN1_JAR_GLOB, rebuild.UNICORN1_COORDINATE)
    if not unicorn1.get("post_quarantine_jar_sha256") or \
            sha256_file(u1) != unicorn1["post_quarantine_jar_sha256"]:
        raise ReleaseEvidenceError(f"{rid}: unicorn quarantine jar hash does not match receipt")
    with zipfile.ZipFile(u1) as reader:
        if not any(name.endswith(".class") for name in reader.namelist()):
            raise ReleaseEvidenceError(f"{rid}: unicorn parent classes are missing")

    source = host / "corresponding-source"
    retained = source / "third_party/tvbox-silicon"
    manifest = _read_object(retained / "dependency-sources/native-source-manifest.json")
    entries = native_coverage._manifest_artifacts(manifest)
    for coordinate in native_coverage.DISTRIBUTED_SOURCE_ARTIFACTS:
        if coordinate not in entries:
            raise ReleaseEvidenceError(f"{rid}: packaged source manifest omits {coordinate}")
        artifact = coordinate.split(":")
        expected = deps / f"{artifact[-2]}-{artifact[-1]}.jar"
        if not expected.is_file():
            raise ReleaseEvidenceError(f"{rid}: distributed dependency is missing: {expected.name}")

    def require_hash(path: Path, digest: str, label: str) -> None:
        if not digest or not path.is_file() or not path.stat().st_size or sha256_file(path) != digest:
            raise ReleaseEvidenceError(f"{rid}: packaged {label} hash does not match retained evidence")

    engine = backend["engine"]
    pin = _read_object(retained / "rebuild/backend-unicorn2/engine-pin.json")["engine"]
    pinned = rebuild._engines()
    if pin.get("archive_sha256") != pinned["archive_sha256"] or \
            engine.get("source_archive_sha256") != pinned["archive_sha256"]:
        raise ReleaseEvidenceError(f"{rid}: packaged engine source does not match the pinned archive")
    require_hash(_member_path(source, engine.get("source_archive", "").replace("\\", "/")),
                 engine.get("source_archive_sha256"), "engine source archive")
    u1_source = unicorn1["source_evidence"]
    require_hash(_member_path(source, u1_source.get("archive", "").replace("\\", "/")),
                 u1_source.get("archive_sha256"), "unicorn source archive")
    bridge = backend["bridge"]
    bridge_path = rebuild.NATIVE_INPUTS.relative_to(rebuild.ROOT).as_posix()
    if bridge.get("source_tree", "").replace("\\", "/") != bridge_path:
        raise ReleaseEvidenceError(f"{rid}: packaged bridge source tree does not match receipt")
    combined = hashlib.sha256()
    files = {}
    for name in rebuild.BRIDGE_SOURCES + rebuild.BRIDGE_HEADERS:
        path = source / bridge_path / name
        if not path.is_file() or not path.stat().st_size:
            raise ReleaseEvidenceError(f"{rid}: packaged bridge source is missing: {name}")
        files[name] = sha256_file(path)
        combined.update(name.encode("utf-8"))
        combined.update(b"\0")
        combined.update(bytes.fromhex(files[name]))
    if combined.hexdigest() != bridge.get("source_digest_sha256") or files != bridge.get("files"):
        raise ReleaseEvidenceError(f"{rid}: packaged bridge source digest does not match receipt")

    # Use the retaining manifest's required texts; resolve them in the shipped source tree.
    reference_entries = native_coverage._manifest_artifacts(native_coverage._source_manifest())
    notices: set[Path] = set()
    for coordinate, obligation in native_coverage.NATIVE_COMPONENT_OBLIGATIONS.items():
        if obligation["class"] == "permissive-notice-only":
            required = native_coverage._notice_files(reference_entries.get(coordinate, {}), obligation)
            if not required:
                raise ReleaseEvidenceError(f"{rid}: no retained license evidence for {coordinate}")
            notices.update(required)
    # The GPL engine and quarantined Unicorn1 also retain all their upstream license texts.
    notices.update(path for path in (native_coverage.DEPENDENCY_NOTICES / "unicorn-1.0.14").rglob("*")
                   if path.is_file() and path.name.lower().startswith(("license", "copying", "notice")))
    if not notices:
        raise ReleaseEvidenceError(f"{rid}: no retained dependency licenses")
    for reference in sorted(notices):
        require_hash(retained / reference.relative_to(rebuild.SOURCE),
                     sha256_file(reference), f"license {reference.name}")
    for path in (host / "LICENSE-tvbox-silicon", source / "LICENSE"):
        if not path.is_file() or not path.read_bytes().strip():
            raise ReleaseEvidenceError(f"{rid}: retained license is missing or empty: {path.name}")
    if not any((host / "jre" / name).is_file() and (host / "jre" / name).read_bytes().strip()
               for name in JRE_NOTICES):
        raise ReleaseEvidenceError(f"{rid}: bundled runtime license is missing or empty")
    return {"packagedReceipt": receipt, "nativeCoverage": coverage,
            "sourceEvidence": {"bridgeDigest": combined.hexdigest(), "retainedLicenses": len(notices)}}


def verify_payload(rid: str, archive: Path, root: Path, meta: Path | None = None,
                   receipts_dir: Path | None = None, members: list[str] | None = None) -> dict:
    """Check actual extracted evidence bytes, including the source rebuild receipt."""
    if members is None:
        members = sorted(p.relative_to(root).as_posix() for p in root.rglob("*") if p.is_file())
    if not members:
        raise ReleaseEvidenceError(f"{rid}: {archive.name} contains no files")
    verdict = {"rid": rid, **verify_java_host(rid, archive, members)}
    verdict.update(verify_licenses(rid, archive, members))
    host = root / verdict["hostPrefix"]
    packaged = verify_packaged_evidence(rid, host)
    receipt = packaged.pop("packagedReceipt")
    verdict.update(packaged)
    for member in members:
        if Path(member).name.lower().startswith(("license", "copying", "notice", "copyright")):
            if not (root / member).read_bytes().strip():
                raise ReleaseEvidenceError(f"{rid}: retained license/notice is empty: {member}")
    app = host.parent
    if app.name == "MacOS" and app.parent.name == "Contents":
        app = app.parent / "Resources"
    for name in MEDIA_NOTICES:
        _read_object(app / name)
    verdict["meta"] = verify_meta(rid, meta) if meta is not None else None
    verdict["receiptArtifact"] = None
    if receipts_dir is not None:
        verdict["receiptArtifact"] = verify_receipt_artifact(rid, receipts_dir)
        sidecar = Path(receipts_dir) / f"native-backend-receipt.{rid}.json"
        if _read_object(sidecar) != receipt:
            raise ReleaseEvidenceError(f"{rid}: sidecar receipt differs from the packaged receipt")
    return verdict


def verify_rid(rid: str, archive: Path, meta: Path | None = None,
               receipts_dir: Path | None = None) -> dict:
    """One asset's full verdict from its actual installed payload."""
    if not archive.is_file() or archive.stat().st_size == 0:
        raise ReleaseEvidenceError(f"{rid}: package artifact {archive.name} is missing or empty")
    with extracted_package(archive) as root:
        return verify_payload(rid, archive, root, meta, receipts_dir)


def package_receipt_path(directory: Path, archive: Path) -> Path:
    return Path(directory) / f"{archive.name}.package-evidence.json"


def _retain_member(name: str) -> bool:
    """Keep every byte used by source/native/license checks, excluding runtime binaries."""
    parts = PurePosixPath(name).parts
    if JAVA_HOST_DIRECTORY in parts:
        relative = parts[parts.index(JAVA_HOST_DIRECTORY) + 1:]
        if relative and relative[0] != "jre":
            return True
    return ("licenses" in parts or
            Path(name).name.lower().startswith(("license", "copying", "notice", "copyright")) or
            any(name.endswith(notice.as_posix()) for notice in MEDIA_NOTICES))


def _payload_manifest(root: Path) -> dict:
    files = {}
    for path in sorted(root.rglob("*")):
        if path.is_file():
            if len(files) >= MAX_MANIFEST_FILES:
                raise ReleaseEvidenceError("package manifest exceeds file limit")
            files[path.relative_to(root).as_posix()] = {
                "size": path.stat().st_size, "sha256": sha256_file(path)}
    return files


def _unique_object(pairs) -> dict:
    result = {}
    for key, value in pairs:
        if key in result:
            raise ReleaseEvidenceError(f"duplicate evidence JSON key {key!r}")
        result[key] = value
    return result


def _read_package_receipt(path: Path) -> dict:
    # Bound decompression before json.load; text decoding avoids a second complete read_text copy.
    limit = 2 * MAX_RECEIPT_BYTES + 4 * ((MAX_SUBSET_BYTES + 2) // 3) + 4 * MAX_MANIFEST_FILES
    with gzip.open(path, "rb") as stream, tempfile.TemporaryFile() as expanded:
        total = 0
        while chunk := stream.read(COPY_CHUNK):
            total += len(chunk)
            if total > limit:
                raise ReleaseEvidenceError("evidence receipt exceeds decompressed byte limit")
            expanded.write(chunk)
        expanded.seek(0)
        with io.TextIOWrapper(expanded, encoding="utf-8") as text:
            data = json.load(text, object_pairs_hook=_unique_object)
    if not isinstance(data, dict):
        raise ReleaseEvidenceError(f"{path.name}: expected a JSON object")
    return data


def write_package_receipt(rid: str, archive: Path, directory: Path,
                          receipts_dir: Path | None = None) -> Path:
    """Fully verify extraction, then stream a bounded subset into a gzip JSON receipt."""
    if rid not in RIDS:
        raise ReleaseEvidenceError(f"unknown runtime identifier: {rid}")
    if archive.name.lower().endswith(".dmg") and (sys.platform != "darwin" or not rid.startswith("osx-")):
        raise ReleaseEvidenceError(f"{rid}: DMG receipt must come from a macOS runner")
    path = package_receipt_path(directory, archive)
    path.parent.mkdir(parents=True, exist_ok=True)
    with extracted_package(archive) as root:
        verdict = verify_payload(rid, archive, root, receipts_dir=receipts_dir)
        files = _payload_manifest(root)
        subset = [name for name in files if _retain_member(name)]
        if sum(files[name]["size"] for name in subset) > MAX_SUBSET_BYTES:
            raise ReleaseEvidenceError(f"{rid}: evidence subset exceeds byte limit")
        receipt = {"schema": 2, "rid": rid, "archive": archive.name,
                   "archiveSHA": sha256_file(archive), "size": archive.stat().st_size,
                   "format": next(s for s in ARCHIVE_SUFFIXES if archive.name.lower().endswith(s)),
                   "platform": sys.platform, "files": files, "checks": verdict}
        # Only metadata is serialized in memory; retained bytes are encoded in aligned chunks.
        header = json.dumps(receipt, sort_keys=True).encode("utf-8")
        if len(header) > MAX_RECEIPT_BYTES:
            raise ReleaseEvidenceError(f"{rid}: evidence receipt exceeds metadata byte limit")
        with tempfile.TemporaryDirectory(dir=path.parent) as temporary:
            staged = Path(temporary) / path.name
            with gzip.open(staged, "wb") as output:
                output.write(header[:-1] + b', "subset": {')
                for index, name in enumerate(subset):
                    if index:
                        output.write(b",")
                    output.write(json.dumps(name).encode("utf-8") + b':"')
                    with (root / name).open("rb") as stream:
                        while chunk := stream.read(3 * (COPY_CHUNK // 3)):
                            output.write(base64.b64encode(chunk))
                    output.write(b'"')
                output.write(b"}}\n")
            staged.replace(path)
    return path


def verify_package_receipt(rid: str, archive: Path, directory: Path,
                           receipts_dir: Path | None = None) -> dict:
    """Bind the asset and manifest, then replay all checks from bounded evidence bytes.

    Receipts are trusted runner artifacts, not signatures. Workflow provenance must enforce
    the native producer's identity; a self-reported platform cannot authenticate that runner.
    Readable formats additionally compare every extracted file against the producer manifest.
    """
    path = package_receipt_path(directory, archive)
    try:
        data = _read_package_receipt(path)
        metadata = {key: value for key, value in data.items() if key != "subset"}
        if len(json.dumps(metadata, sort_keys=True).encode("utf-8")) > MAX_RECEIPT_BYTES:
            raise ReleaseEvidenceError(f"{rid}: evidence receipt exceeds metadata byte limit")
        suffix = next(s for s in ARCHIVE_SUFFIXES if archive.name.lower().endswith(s))
        if data.get("schema") != 2 or data.get("rid") != rid or \
                data.get("archive") != archive.name or data.get("format") != suffix or \
                data.get("archiveSHA") != sha256_file(archive) or \
                type(data.get("size")) is not int or data["size"] != archive.stat().st_size:
            raise ReleaseEvidenceError(f"{rid}: {path.name}: archive SHA/size or identity mismatch")
        if suffix == ".dmg" and (not rid.startswith("osx-") or data.get("platform") != "darwin"):
            raise ReleaseEvidenceError(f"{rid}: DMG receipt must come from a macOS runner")
        files = data.get("files")
        if not isinstance(files, dict) or not files or len(files) > MAX_MANIFEST_FILES:
            raise ReleaseEvidenceError(f"{rid}: receipt has no bounded extracted manifest")
        normalized = set()
        for name, entry in files.items():
            target = _member_path(Path("payload"), name)
            if target in normalized or name != target.relative_to("payload").as_posix():
                raise ReleaseEvidenceError(f"{rid}: duplicate or noncanonical manifest member {name!r}")
            normalized.add(target)
            if not isinstance(entry, dict) or set(entry) != {"size", "sha256"} or \
                    type(entry["size"]) is not int or entry["size"] < 0 or \
                    not isinstance(entry["sha256"], str) or len(entry["sha256"]) != 64 or \
                    any(c not in "0123456789abcdef" for c in entry["sha256"]):
                raise ReleaseEvidenceError(f"{rid}: invalid manifest entry {name!r}")
        subset = {name for name in files if _retain_member(name)}
        if sum(files[name]["size"] for name in subset) > MAX_SUBSET_BYTES:
            raise ReleaseEvidenceError(f"{rid}: evidence subset exceeds byte limit")
        retained = data.get("subset")
        if not isinstance(retained, dict) or set(retained) != subset:
            raise ReleaseEvidenceError(f"{rid}: missing or unexpected extracted evidence bytes")
        with tempfile.TemporaryDirectory(prefix=".package-receipt-", dir=archive.parent) as temporary:
            root = Path(temporary) / "payload"
            root.mkdir()
            for name, encoded in retained.items():
                if not isinstance(encoded, str) or len(encoded) != 4 * ((files[name]["size"] + 2) // 3):
                    raise ReleaseEvidenceError(f"{rid}: invalid evidence member size: {name}")
                target = _member_path(root, name)
                target.parent.mkdir(parents=True, exist_ok=True)
                with target.open("wb") as output:
                    for offset in range(0, len(encoded), COPY_CHUNK):
                        output.write(base64.b64decode(encoded[offset:offset + COPY_CHUNK], validate=True))
                if target.stat().st_size != files[name]["size"] or sha256_file(target) != files[name]["sha256"]:
                    raise ReleaseEvidenceError(f"{rid}: evidence member hash/size mismatch: {name}")
            verdict = verify_payload(rid, archive, root, receipts_dir=receipts_dir, members=sorted(files))
        if suffix != ".dmg" or sys.platform == "darwin":
            with extracted_package(archive) as actual:
                if _payload_manifest(actual) != files:
                    raise ReleaseEvidenceError(f"{rid}: receipt manifest differs from actual asset")
        recorded = dict(verdict)
        checks = data.get("checks")
        if not isinstance(checks, dict):
            raise ReleaseEvidenceError(f"{rid}: receipt has no detailed checks")
        # Sidecar paths can change across runners; its bytes and identity must still agree.
        producer_sidecar = checks.get("receiptArtifact")
        consumer_sidecar = recorded.get("receiptArtifact")
        if consumer_sidecar is not None:
            if not isinstance(producer_sidecar, dict) or \
                    {k: v for k, v in producer_sidecar.items() if k != "path"} != \
                    {k: v for k, v in consumer_sidecar.items() if k != "path"}:
                raise ReleaseEvidenceError(f"{rid}: package receipt source sidecar mismatch")
        recorded["receiptArtifact"] = producer_sidecar
        if recorded != checks:
            raise ReleaseEvidenceError(f"{rid}: receipt checks differ from actual payload evidence")
        return verdict
    except (OSError, EOFError, ValueError, TypeError, AttributeError, KeyError, StopIteration,
            native_coverage.NativeCoverageError, rebuild.RebuildError, tarfile.TarError) as error:
        raise ReleaseEvidenceError(f"{rid}: invalid package evidence receipt {path.name}: {error}") from error


def verify_metadata_source(rid: str, data: dict) -> None:
    """Recompute reuse provenance from the original and packaged Git input trees."""
    actual = data.get("sourceSha")
    target = data.get("packageSourceSha", actual)
    cross_commit = target != actual
    fields = ("fingerprint", "originalFingerprint", "nativeBuildHash", "targetPackageInput")
    if not cross_commit and not any(field in data for field in fields):
        return  # Legacy metadata for a package built from the same commit.
    try:
        for sha in dict.fromkeys((actual, target)):
            if not isinstance(sha, str) or not sha or not ensure_commit(sha):
                raise ReleaseEvidenceError(f"{rid}: build input commit unavailable: {sha!r}")
        original = source_hash(actual)
        for field in ("fingerprint", "originalFingerprint"):
            if (cross_commit or field in data) and data.get(field) != original:
                raise ReleaseEvidenceError(f"{rid}: {field} mismatch for original sourceSha")
        native = native_build_hash(actual)
        if native_build_hash(target) != native:
            raise ReleaseEvidenceError(f"{rid}: native build inputs differ between sourceSha and packageSourceSha")
        if (cross_commit or "nativeBuildHash" in data) and data.get("nativeBuildHash") != native:
            raise ReleaseEvidenceError(f"{rid}: recorded nativeBuildHash mismatch")
        if "targetPackageInput" in data and data["targetPackageInput"] != source_hash(target):
            raise ReleaseEvidenceError(f"{rid}: targetPackageInput mismatch")
    except (OSError, subprocess.CalledProcessError) as error:
        raise ReleaseEvidenceError(f"{rid}: cannot verify package source inputs: {error}") from error


def verify_meta(rid: str, meta: Path) -> dict:
    """The packaging job's own per-RID metadata, verified as a real file rather than a job token."""
    if not meta.is_file() or meta.stat().st_size == 0:
        raise ReleaseEvidenceError(f"{rid}: package metadata {meta} is missing or empty")
    try:
        data = json.loads(meta.read_text(encoding="utf-8"))
    except ValueError as error:
        raise ReleaseEvidenceError(f"{rid}: package metadata {meta} is not valid JSON: {error}") from error
    if data.get("rid") not in (None, rid):
        raise ReleaseEvidenceError(f"{rid}: {meta} is metadata for {data.get('rid')!r}")
    verify_metadata_source(rid, data)
    return {"path": str(meta), "sha256": sha256_file(meta), "rid": data.get("rid"),
            "version": data.get("version"), "sourceSha": data.get("sourceSha"),
            "packageSourceSha": data.get("packageSourceSha", data.get("sourceSha"))}


def find_archives(rid: str, archives_dir: Path) -> list[Path]:
    """Every published asset, including underscore-named Linux installers."""
    matches = [path for path in sorted(Path(archives_dir).iterdir())
               if path.is_file() and any(path.name.lower().endswith(s) for s in ARCHIVE_SUFFIXES)
               and any(marker in path.name for marker in (f".{rid}.", f"_{rid}."))]
    if not matches:
        raise ReleaseEvidenceError(f"{rid}: no downloaded package artifact in {archives_dir}")
    return matches


def find_archive(rid: str, archives_dir: Path) -> Path:
    """Compatibility helper for callers requiring exactly one asset."""
    matches = find_archives(rid, archives_dir)
    if len(matches) != 1:
        raise ReleaseEvidenceError(f"{rid}: multiple package artifacts match; use find_archives")
    return matches[0]


def verify_release(rids=None, archives_dir: Path = ARCHIVES_DIR, meta_dir: Path | None = None,
                   expected_sha: str = "", version: str = "",
                   receipts_dir: Path | None = None,
                   package_receipts_dir: Path | None = None) -> dict:
    """Verify every published asset; a valid sibling cannot hide a broken installer."""
    targets = list(rids or RIDS)
    unknown = [rid for rid in targets if rid not in RIDS]
    if unknown:
        raise ReleaseEvidenceError(f"unknown runtime identifier(s): {', '.join(unknown)}")
    verdicts: dict[str, dict] = {}
    problems: list[str] = []
    for rid in targets:
        try:
            archives = find_archives(rid, archives_dir)
            meta = Path(meta_dir) / f"{rid}.json" if meta_dir is not None else None
            assets = []
            for archive in archives:
                try:
                    attested = None
                    if package_receipts_dir is not None:
                        attested = verify_package_receipt(rid, archive, package_receipts_dir, receipts_dir)
                    if archive.suffix.lower() == ".dmg" and sys.platform != "darwin":
                        if attested is None:
                            raise ReleaseEvidenceError(f"{rid}: {archive.name}: missing macOS package receipt")
                        verdict = attested
                        verdict["meta"] = verify_meta(rid, meta) if meta is not None else None
                    else:
                        verdict = verify_rid(rid, archive, meta, receipts_dir)
                        if attested is not None:
                            comparable = dict(verdict)
                            comparable["meta"] = None
                            if comparable != attested:
                                raise ReleaseEvidenceError(f"{rid}: {archive.name}: receipt differs from actual asset")
                    recorded = verdict.get("meta") or {}
                    if version and recorded.get("version") != version:
                        raise ReleaseEvidenceError(f"{rid}: package metadata version {recorded.get('version')!r} != {version}")
                    if expected_sha and recorded.get("packageSourceSha") != expected_sha:
                        raise ReleaseEvidenceError(f"{rid}: package metadata packageSourceSha (legacy sourceSha) {recorded.get('packageSourceSha')!r} != {expected_sha}")
                    assets.append(verdict)
                except ReleaseEvidenceError as error:
                    problems.append(f"{archive.name}: {error}")
            if len(assets) == len(archives):
                verdicts[rid] = {**assets[0], "assets": assets}
        except ReleaseEvidenceError as error:
            problems.append(str(error))
    missing = [rid for rid in targets if rid not in verdicts]
    if missing or problems:
        detail = "\n".join(f"  - {problem}" for problem in problems)
        raise ReleaseEvidenceError(
            f"published package verification failed for {len(missing) or len(problems)} of "
            f"{len(targets)} RIDs:\n{detail}")
    return {"rids": targets, "verdicts": verdicts}


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Verify the downloaded per-RID packages before publishing")
    parser.add_argument("--archives", type=Path, default=ARCHIVES_DIR,
                        help="Directory of downloaded package artifacts (pattern: package.*)")
    parser.add_argument("--meta", type=Path, default=None,
                        help="Directory of per-RID `vodbox-meta.<rid>` metadata (optional)")
    parser.add_argument("--receipts", type=Path, default=None,
                        help="Directory of per-RID `native-backend-receipt.<rid>.json` artifacts; "
                             "when given, every RID must carry its own real receipt.")
    parser.add_argument("--package-receipts", type=Path, default=None,
                        help="Per-asset runner receipts; required for DMGs on non-macOS runners")
    parser.add_argument("--write-package-receipts", type=Path, default=None,
                        help="Extract/check every selected asset and write runner receipts here")
    parser.add_argument("--rid", default="", help="Verify a single RID (default: all six)")
    parser.add_argument("--expected-sha", default="", help="Source commit the release is bound to.")
    parser.add_argument("--version", default="", help="Version the release is bound to.")
    args = parser.parse_args()
    targets = [rid.strip() for rid in args.rid.split(",") if rid.strip()] or None
    try:
        if args.write_package_receipts is not None:
            for rid in targets or RIDS:
                if rid not in RIDS:
                    raise ReleaseEvidenceError(f"unknown runtime identifier: {rid}")
                for archive in find_archives(rid, args.archives):
                    path = write_package_receipt(rid, archive, args.write_package_receipts, args.receipts)
                    print(f"Wrote package evidence: {path.name}")
        result = verify_release(targets, args.archives, args.meta, args.expected_sha, args.version,
                                args.receipts, args.package_receipts)
    except ReleaseEvidenceError as error:
        print(f"Release evidence verification failed: {error}", file=sys.stderr)
        print("Refusing to publish: every one of the six RIDs must carry its own artifact, hash, "
              "staged deps, source-rebuild receipt and retained licenses. A manifest summary or a "
              "synthetic receipt is not evidence about the bytes in the archive.", file=sys.stderr)
        return 1
    for rid, summary in result["verdicts"].items():
        for verdict in summary["assets"]:
            print(f"OK {rid}: {verdict['archive']} sha256={verdict['sha256'][:16]}… "
                  f"host={verdict['hostPrefix']} deps={verdict['depsJars']} "
                  f"backend={verdict['backendJar']} receipt={verdict['receipt']}")
    print(f"Verified {len(result['verdicts'])} RIDs from their real package artifacts")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
