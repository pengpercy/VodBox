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
import contextlib
import hashlib
import json
import shutil
import stat
import subprocess
import sys
import tarfile
import tempfile
import zipfile
from pathlib import Path, PurePosixPath

import native_coverage
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
JRE_NOTICES = ("LICENSE", "legal/java.base/LICENSE")

# Package extensions the packaging matrix produces (package.py / package.yml).
ARCHIVE_SUFFIXES = (".zip", ".tar", ".tar.gz", ".tar.xz", ".tar.bz2", ".tar.zst", ".dmg", ".deb", ".rpm")


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
    if not name or parts.is_absolute() or ".." in parts.parts or "\\" in name:
        raise ReleaseEvidenceError(f"unsafe package member {name!r}")
    return root.joinpath(*parts.parts)


def _command(command: list[str]) -> bytes:
    result = subprocess.run(command, capture_output=True)
    if result.returncode:
        raise ReleaseEvidenceError(
            f"cannot read package: {' '.join(command)}: {result.stderr.decode(errors='replace').strip()}")
    return result.stdout


def _extract_package(archive: Path, root: Path) -> None:
    """Materialize real file bytes; never extract links or trust a filename-only listing."""
    suffix = next((s for s in ARCHIVE_SUFFIXES if archive.name.lower().endswith(s)), None)
    if suffix is None:
        raise ReleaseEvidenceError(f"{archive.name}: unsupported package format")
    seen: set[Path] = set()

    def write(name: str, data: bytes) -> None:
        target = _member_path(root, name)
        if target in seen:
            raise ReleaseEvidenceError(f"duplicate package member {name!r}")
        seen.add(target)
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(data)

    if suffix == ".zip" or (suffix == ".dmg" and zipfile.is_zipfile(archive)):
        with zipfile.ZipFile(archive) as reader:
            for info in reader.infolist():
                _member_path(root, info.filename)
                if stat.S_ISLNK(info.external_attr >> 16):
                    raise ReleaseEvidenceError(f"{archive.name}: linked member {info.filename}")
                if not info.is_dir():
                    write(info.filename, reader.read(info))
        return
    payload = archive
    if suffix == ".deb":
        names = _command(["ar", "t", str(archive)]).decode().splitlines()
        data = [n for n in names if n in ("data.tar", "data.tar.gz", "data.tar.xz", "data.tar.zst")]
        if len(data) != 1:
            raise ReleaseEvidenceError(f"{archive.name}: expected exactly one data.tar payload")
        payload = root.parent / data[0]
        payload.write_bytes(_command(["ar", "p", str(archive), data[0]]))
    if suffix.startswith(".tar") or suffix == ".deb":
        try:
            with tarfile.open(payload) as reader:
                for info in reader.getmembers():
                    _member_path(root, info.name)
                    if info.isfile():
                        with reader.extractfile(info) as stream:
                            write(info.name, stream.read())
                    elif not info.isdir():
                        raise ReleaseEvidenceError(f"{archive.name}: non-file member {info.name}")
            return
        except tarfile.ReadError:
            # libarchive can read compression formats unavailable to this Python runtime.
            pass
    if suffix == ".dmg":
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
                    write(path.relative_to(mount).as_posix(), path.read_bytes())
        finally:
            _command(["hdiutil", "detach", str(mount)])
        return
    # Read each payload member through the available archive reader rather than extracting links.
    reader = shutil.which("bsdtar")
    if reader is None:
        raise ReleaseEvidenceError(f"{archive.name}: no archive reader for {suffix}")
    for name in _command([reader, "-tf", str(payload)]).decode().splitlines():
        _member_path(root, name)
        if not name.endswith("/"):
            write(name, _command([reader, "-xOf", str(payload), "--", name]))


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
    missing = [str(name) for name in MEDIA_NOTICES if not any(m.endswith(str(name)) for m in members)]
    if missing:
        raise ReleaseEvidenceError(
            f"{rid}: {archive.name} does not retain the media notices {', '.join(sorted(missing))}")
    jre_notice = next((m for m in members for name in JRE_NOTICES if m.endswith(name)), None)
    if jre_notice is None:
        raise ReleaseEvidenceError(
            f"{rid}: {archive.name} does not retain the bundled runtime's license "
            f"({'/'.join(JRE_NOTICES)})")
    return {
        "archive": archive.name,
        "licenseFiles": len(license_like),
        "mediaNotices": [str(name) for name in MEDIA_NOTICES if any(m.endswith(str(name)) for m in members)],
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
            native = reader.read(integration["native_entry"])
        output = backend["output_library"]
        if not native or hashlib.sha256(native).hexdigest() != output.get("sha256") or \
                len(native) != output.get("size"):
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
    require_hash(_member_path(source, engine.get("source_archive", "")),
                 engine.get("source_archive_sha256"), "engine source archive")
    u1_source = unicorn1["source_evidence"]
    require_hash(_member_path(source, u1_source.get("archive", "")),
                 u1_source.get("archive_sha256"), "unicorn source archive")
    bridge = backend["bridge"]
    bridge_path = rebuild.NATIVE_INPUTS.relative_to(rebuild.ROOT).as_posix()
    if bridge.get("source_tree") != bridge_path:
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


def verify_rid(rid: str, archive: Path, meta: Path | None = None,
               receipts_dir: Path | None = None) -> dict:
    """One RID's full verdict, from the package's own receipt, binaries, sources and licenses."""
    if not archive.is_file():
        raise ReleaseEvidenceError(f"{rid}: no package artifact matches {archive.name}")
    if archive.stat().st_size == 0:
        raise ReleaseEvidenceError(f"{rid}: {archive.name} is empty")
    with extracted_package(archive) as root:
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
    return {"path": str(meta), "sha256": sha256_file(meta), "rid": data.get("rid"),
            "version": data.get("version"), "sourceSha": data.get("sourceSha")}


def find_archive(rid: str, archives_dir: Path) -> Path:
    """The single package artifact for `rid` inside the downloaded artifact directory."""
    matches = [path for path in sorted(Path(archives_dir).iterdir())
               if path.is_file() and any(path.name.lower().endswith(s) for s in ARCHIVE_SUFFIXES)
               and f".{rid}." in path.name]
    if not matches:
        raise ReleaseEvidenceError(
            f"{rid}: no downloaded package artifact in {archives_dir} (expected a "
            f"{'/'.join(ARCHIVE_SUFFIXES)} carrying '.{rid}.')")
    if len(matches) > 1:
        names = ", ".join(path.name for path in matches)
        raise ReleaseEvidenceError(f"{rid}: multiple package artifacts match: {names}")
    return matches[0]


def verify_release(rids=None, archives_dir: Path = ARCHIVES_DIR, meta_dir: Path | None = None,
                   expected_sha: str = "", version: str = "",
                   receipts_dir: Path | None = None) -> dict:
    """Verify every published RID; refuse the release unless all six are present and provable."""
    targets = list(rids or RIDS)
    unknown = [rid for rid in targets if rid not in RIDS]
    if unknown:
        raise ReleaseEvidenceError(f"unknown runtime identifier(s): {', '.join(unknown)}")
    verdicts: dict[str, dict] = {}
    problems: list[str] = []
    for rid in targets:
        try:
            archive = find_archive(rid, archives_dir)
            meta = None
            if meta_dir is not None:
                candidate = Path(meta_dir) / f"{rid}.json"
                meta = candidate if candidate.is_file() else None
            verdicts[rid] = verify_rid(rid, archive, meta, receipts_dir)
            if expected_sha and version:
                recorded = (verdicts[rid].get("meta") or {})
                if recorded and recorded.get("version") != version:
                    problems.append(f"{rid}: package metadata version {recorded.get('version')!r} != {version}")
                if recorded and recorded.get("sourceSha") != expected_sha:
                    problems.append(
                        f"{rid}: package metadata sourceSha {recorded.get('sourceSha')!r} != {expected_sha}")
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
    parser.add_argument("--rid", default="", help="Verify a single RID (default: all six)")
    parser.add_argument("--expected-sha", default="", help="Source commit the release is bound to.")
    parser.add_argument("--version", default="", help="Version the release is bound to.")
    args = parser.parse_args()
    targets = [rid.strip() for rid in args.rid.split(",") if rid.strip()] or None
    try:
        result = verify_release(targets, args.archives, args.meta, args.expected_sha, args.version,
                                args.receipts)
    except ReleaseEvidenceError as error:
        print(f"Release evidence verification failed: {error}", file=sys.stderr)
        print("Refusing to publish: every one of the six RIDs must carry its own artifact, hash, "
              "staged deps, source-rebuild receipt and retained licenses. A manifest summary or a "
              "synthetic receipt is not evidence about the bytes in the archive.", file=sys.stderr)
        return 1
    for rid, verdict in result["verdicts"].items():
        print(f"OK {rid}: {verdict['archive']} sha256={verdict['sha256'][:16]}… "
              f"host={verdict['hostPrefix']} deps={verdict['depsJars']} "
              f"backend={verdict['backendJar']} receipt={verdict['receipt']}")
    print(f"Verified {len(result['verdicts'])} RIDs from their real package artifacts")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
