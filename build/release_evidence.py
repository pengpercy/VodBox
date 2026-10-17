#!/usr/bin/env python3
"""Verify the package bytes a release would publish, without a Java runtime payload.

The product now ships C# native providers and the media kernel only.  This module
therefore checks the two things that must still be true before publication:

* all six RID installers exist, are non-empty, and retain the media notices;
* no stale JavaHost/JRE/JAR/DEX/class or native-backend receipt is present.

The per-RID package metadata and the runner-produced package receipts are also
verified.  A metadata flag or a successful CI job is not evidence about the
bytes in an archive.  Nothing here mutates repository state.
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
import time
import zipfile
from collections import deque
from pathlib import Path, PurePosixPath

from ci_reuse import ensure_commit, native_build_hash, source_hash

RIDS = ("win-x64", "win-arm64", "osx-x64", "osx-arm64", "linux-x64", "linux-arm64")
ARCHIVES_DIR = Path("artifacts/release")
EXTRACT_DIR = Path("artifacts/package-verify")
META_DIR = Path("artifacts/meta")

MEDIA_NOTICES = (Path("licenses") / "media" / "asset.json",)
FORBIDDEN_COMPONENTS = frozenset(("javahost", "jre", "jdk"))
FORBIDDEN_SUFFIXES = (
    ".jar", ".dex", ".class", ".jmod", ".war",
    ".package-evidence.json", ".native-backend-receipt.json",
)
FORBIDDEN_NAMES = frozenset((
    "java-host.json", "upstream-runtime.json", "native-backend-receipt.json",
    "source-offer.json", "license-tvbox-silicon",
    "java", "javaw", "javac", "java.exe", "javaw.exe", "javac.exe",
))

ARCHIVE_SUFFIXES = (".zip", ".tar", ".tar.gz", ".tar.xz", ".tar.bz2", ".tar.zst", ".dmg", ".deb", ".rpm")
MAX_SUBSET_BYTES = 512 * 1024 * 1024
MAX_RECEIPT_BYTES = 32 * 1024 * 1024
MAX_MANIFEST_FILES = 100_000
COPY_CHUNK = 1024 * 1024


class ReleaseEvidenceError(RuntimeError):
    """A package cannot be published because its bytes are incomplete or unsafe."""


def sha256_file(path: Path) -> str:
    with Path(path).open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def normalize_member(name: str) -> str:
    while name.startswith("./"):
        name = name[2:]
    return name


def _member_path(root: Path, name: str) -> Path:
    name = normalize_member(name)
    parts = PurePosixPath(name)
    if (not name or parts.is_absolute() or ".." in parts.parts or "\\" in name
            or (parts.parts and ":" in parts.parts[0])):
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
    """Materialize package files while resolving links only inside the payload."""
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
                            block = container.read(min(COPY_CHUNK, remaining))
                            if not block:
                                raise ReleaseEvidenceError("truncated DEB payload")
                            output.write(block)
                            remaining -= len(block)
                    payloads.append(payload)
                else:
                    container.seek(size, 1)
                if size % 2:
                    container.read(1)
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
                    directories.update(
                        parent.as_posix() if parent != PurePosixPath(".") else ""
                        for parent in PurePosixPath(name).parents
                    )
                    if info.isdir():
                        directories.add(name)
                for name in directories - {""}:
                    if name in entries and not entries[name].isdir():
                        raise ReleaseEvidenceError(f"{archive.name}: conflicting package directory {name}")

                def resolve(name: str) -> str:
                    pending = deque(PurePosixPath(name).parts)
                    resolved: list[str] = []
                    active: set[str] = set()
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
                            if (not info.linkname or link.is_absolute() or "\\" in info.linkname
                                    or (link.parts and ":" in link.parts[0])):
                                raise ReleaseEvidenceError(f"{archive.name}: unsafe link {candidate}")
                            if candidate in active:
                                raise ReleaseEvidenceError(f"{archive.name}: cyclic link {name}")
                            active.add(candidate)
                            if info.islnk():
                                resolved = []
                            pending.extendleft(reversed([*link.parts, (candidate,)]))
                            continue
                        if candidate not in directories and (info is None or not info.isfile()):
                            raise ReleaseEvidenceError(f"{archive.name}: unresolved link {name}")
                        if (info is not None and info.isfile()
                                and any(isinstance(item, str) for item in pending)):
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
        attach = ["hdiutil", "attach", str(archive), "-readonly", "-nobrowse", "-mountpoint", str(mount)]
        for attempt in range(5):
            try:
                _command(attach)
                break
            except ReleaseEvidenceError as error:
                if attempt == 4 or "Resource temporarily unavailable" not in str(error):
                    raise
                time.sleep(5 * (attempt + 1))
        try:
            for path in mount.rglob("*"):
                if path.is_symlink():
                    continue
                if path.is_file():
                    with path.open("rb") as stream:
                        write(path.relative_to(mount).as_posix(), stream)
        finally:
            _command(["hdiutil", "detach", str(mount)])
        return

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
    except (OSError, ValueError, tarfile.TarError, zipfile.BadZipFile, KeyError,
            TypeError, AttributeError, UnicodeError) as error:
        raise ReleaseEvidenceError(f"{archive.name}: cannot verify package bytes: {error}") from error


def members_of(archive: Path) -> list[str]:
    with extracted_package(archive) as root:
        return sorted(path.relative_to(root).as_posix() for path in root.rglob("*") if path.is_file())


def _forbidden_member(name: str) -> str | None:
    parts = PurePosixPath(normalize_member(name)).parts
    lower_parts = tuple(part.lower() for part in parts)
    if any(part in FORBIDDEN_COMPONENTS for part in lower_parts):
        return "JavaHost/JRE/JDK path"
    filename = lower_parts[-1] if lower_parts else ""
    if filename in FORBIDDEN_NAMES:
        return "Java host/receipt file"
    if filename.endswith(FORBIDDEN_SUFFIXES):
        return "Java/JAR/DEX payload"
    return None


def verify_no_java_payload(rid: str, archive: Path, members: list[str]) -> dict:
    rejected = []
    for member in members:
        reason = _forbidden_member(member)
        if reason is not None:
            rejected.append(f"{reason}: {member}")
    if rejected:
        preview = "; ".join(sorted(rejected)[:8])
        raise ReleaseEvidenceError(f"{rid}: {archive.name} carries forbidden Java payload: {preview}")
    return {"javaPayload": False}


def verify_licenses(rid: str, archive: Path, members: list[str]) -> dict:
    license_like = [m for m in members if Path(m).name.lower().startswith(
        ("license", "copying", "notice", "copyright"))]
    if not license_like:
        raise ReleaseEvidenceError(f"{rid}: {archive.name} carries no license/notice file at all")
    missing = [name.as_posix() for name in MEDIA_NOTICES
               if not any(m.endswith(name.as_posix()) for m in members)]
    if missing:
        raise ReleaseEvidenceError(
            f"{rid}: {archive.name} does not retain the media notices {', '.join(sorted(missing))}")
    return {
        "archive": archive.name,
        "licenseFiles": len(license_like),
        "mediaNotices": [name.as_posix() for name in MEDIA_NOTICES
                         if any(m.endswith(name.as_posix()) for m in members)],
    }


def _read_object(path: Path) -> dict:
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except (ValueError, UnicodeDecodeError) as error:
        raise ReleaseEvidenceError(f"{path.name}: notice is not a readable JSON object: {error}") from error
    if not isinstance(data, dict):
        raise ReleaseEvidenceError(f"{path.name}: expected a JSON object")
    return data


def verify_payload(rid: str, archive: Path, root: Path, meta: Path | None = None,
                   members: list[str] | None = None) -> dict:
    if members is None:
        members = sorted(p.relative_to(root).as_posix() for p in root.rglob("*") if p.is_file())
    if not members:
        raise ReleaseEvidenceError(f"{rid}: {archive.name} contains no files")
    verdict = {
        "rid": rid,
        "archive": archive.name,
        "sha256": sha256_file(archive),
        "size": archive.stat().st_size,
        "members": len(members),
    }
    verdict.update(verify_no_java_payload(rid, archive, members))
    verdict.update(verify_licenses(rid, archive, members))
    for member in members:
        if Path(member).name.lower().startswith(("license", "copying", "notice", "copyright")):
            if not (root / member).read_bytes().strip():
                raise ReleaseEvidenceError(f"{rid}: retained license/notice is empty: {member}")
    for name in MEDIA_NOTICES:
        paths = [root / member for member in members if member.endswith(name.as_posix())]
        if not paths:
            raise ReleaseEvidenceError(f"{rid}: package does not retain the media notice {name}")
        for path in paths:
            if not path.read_bytes().strip():
                raise ReleaseEvidenceError(f"{rid}: media notice is empty: {name}")
            _read_object(path)  # raises ReleaseEvidenceError on a malformed notice
    verdict["meta"] = verify_meta(rid, meta) if meta is not None else None
    return verdict


def verify_rid(rid: str, archive: Path, meta: Path | None = None) -> dict:
    if not archive.is_file() or archive.stat().st_size == 0:
        raise ReleaseEvidenceError(f"{rid}: package artifact {archive.name} is missing or empty")
    with extracted_package(archive) as root:
        return verify_payload(rid, archive, root, meta)


def package_receipt_path(directory: Path, archive: Path) -> Path:
    return Path(directory) / f"{archive.name}.package-evidence.json"


def _retain_member(name: str) -> bool:
    return ("licenses" in PurePosixPath(name).parts
            or Path(name).name.lower().startswith(("license", "copying", "notice", "copyright"))
            or any(name.endswith(notice.as_posix()) for notice in MEDIA_NOTICES))


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


def write_package_receipt(rid: str, archive: Path, directory: Path) -> Path:
    if rid not in RIDS:
        raise ReleaseEvidenceError(f"unknown runtime identifier: {rid}")
    if archive.name.lower().endswith(".dmg") and (sys.platform != "darwin" or not rid.startswith("osx-")):
        raise ReleaseEvidenceError(f"{rid}: DMG receipt must come from a macOS runner")
    path = package_receipt_path(directory, archive)
    path.parent.mkdir(parents=True, exist_ok=True)
    with extracted_package(archive) as root:
        verdict = verify_payload(rid, archive, root)
        files = _payload_manifest(root)
        subset = [name for name in files if _retain_member(name)]
        if sum(files[name]["size"] for name in subset) > MAX_SUBSET_BYTES:
            raise ReleaseEvidenceError(f"{rid}: evidence subset exceeds byte limit")
        receipt = {"schema": 3, "rid": rid, "archive": archive.name,
                   "archiveSHA": sha256_file(archive), "size": archive.stat().st_size,
                   "format": next(s for s in ARCHIVE_SUFFIXES if archive.name.lower().endswith(s)),
                   "platform": sys.platform, "files": files, "checks": verdict}
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


def verify_package_receipt(rid: str, archive: Path, directory: Path) -> dict:
    path = package_receipt_path(directory, archive)
    try:
        data = _read_package_receipt(path)
        metadata = {key: value for key, value in data.items() if key != "subset"}
        if len(json.dumps(metadata, sort_keys=True).encode("utf-8")) > MAX_RECEIPT_BYTES:
            raise ReleaseEvidenceError(f"{rid}: evidence receipt exceeds metadata byte limit")
        suffix = next(s for s in ARCHIVE_SUFFIXES if archive.name.lower().endswith(s))
        if (data.get("schema") != 3 or data.get("rid") != rid
                or data.get("archive") != archive.name or data.get("format") != suffix
                or data.get("archiveSHA") != sha256_file(archive)
                or type(data.get("size")) is not int or data["size"] != archive.stat().st_size):
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
            if (not isinstance(entry, dict) or set(entry) != {"size", "sha256"}
                    or type(entry["size"]) is not int or entry["size"] < 0
                    or not isinstance(entry["sha256"], str) or len(entry["sha256"]) != 64
                    or any(c not in "0123456789" + "abcdef" for c in entry["sha256"])):
                raise ReleaseEvidenceError(f"{rid}: invalid manifest entry {name!r}")
        verify_no_java_payload(rid, archive, sorted(files))
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
            verdict = verify_payload(rid, archive, root, members=sorted(files))
        if suffix != ".dmg" or sys.platform == "darwin":
            with extracted_package(archive) as actual:
                if _payload_manifest(actual) != files:
                    raise ReleaseEvidenceError(f"{rid}: receipt manifest differs from actual asset")
        checks = data.get("checks")
        if not isinstance(checks, dict) or checks != verdict:
            raise ReleaseEvidenceError(f"{rid}: receipt checks differ from actual payload evidence")
        return verdict
    except (OSError, EOFError, ValueError, TypeError, AttributeError, KeyError, StopIteration,
            tarfile.TarError) as error:
        raise ReleaseEvidenceError(f"{rid}: invalid package evidence receipt {path.name}: {error}") from error


def verify_metadata_source(rid: str, data: dict) -> None:
    actual = data.get("sourceSha")
    target = data.get("packageSourceSha", actual)
    cross_commit = target != actual
    fields = ("fingerprint", "originalFingerprint", "nativeBuildHash", "targetPackageInput")
    if not cross_commit and not any(field in data for field in fields):
        return
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
    matches = [path for path in sorted(Path(archives_dir).iterdir())
               if path.is_file() and any(path.name.lower().endswith(s) for s in ARCHIVE_SUFFIXES)
               and any(marker in path.name for marker in (f".{rid}.", f"_{rid}."))]
    if not matches:
        raise ReleaseEvidenceError(f"{rid}: no downloaded package artifact in {archives_dir}")
    return matches


def find_archive(rid: str, archives_dir: Path) -> Path:
    matches = find_archives(rid, archives_dir)
    if len(matches) != 1:
        raise ReleaseEvidenceError(f"{rid}: multiple package artifacts match; use find_archives")
    return matches[0]


def verify_release(rids=None, archives_dir: Path = ARCHIVES_DIR, meta_dir: Path | None = None,
                   expected_sha: str = "", version: str = "",
                   package_receipts_dir: Path | None = None) -> dict:
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
                attested = None
                if package_receipts_dir is not None:
                    attested = verify_package_receipt(rid, archive, package_receipts_dir)
                if archive.suffix.lower() == ".dmg" and sys.platform != "darwin":
                    if attested is None:
                        raise ReleaseEvidenceError(f"{rid}: {archive.name}: missing macOS package receipt")
                    verdict = attested
                    verdict["meta"] = verify_meta(rid, meta) if meta is not None else None
                else:
                    verdict = verify_rid(rid, archive, meta)
                    if attested is not None:
                        comparable = dict(verdict)
                        comparable["meta"] = None
                        if comparable != attested:
                            raise ReleaseEvidenceError(f"{rid}: {archive.name}: receipt differs from actual asset")
                recorded = verdict.get("meta") or {}
                if version and recorded.get("version") != version:
                    raise ReleaseEvidenceError(
                        f"{rid}: package metadata version {recorded.get('version')!r} != {version}")
                if expected_sha and recorded.get("packageSourceSha") != expected_sha:
                    raise ReleaseEvidenceError(
                        f"{rid}: package metadata packageSourceSha (legacy sourceSha) "
                        f"{recorded.get('packageSourceSha')!r} != {expected_sha}")
                assets.append(verdict)
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
        description="Verify downloaded per-RID packages before publishing")
    parser.add_argument("--archives", type=Path, default=ARCHIVES_DIR)
    parser.add_argument("--meta", type=Path, default=None)
    parser.add_argument("--package-receipts", type=Path, default=None)
    parser.add_argument("--write-package-receipts", type=Path, default=None)
    parser.add_argument("--rid", default="")
    parser.add_argument("--expected-sha", default="")
    parser.add_argument("--version", default="")
    args = parser.parse_args()
    targets = [rid.strip() for rid in args.rid.split(",") if rid.strip()] or None
    try:
        if args.write_package_receipts is not None:
            for rid in targets or RIDS:
                if rid not in RIDS:
                    raise ReleaseEvidenceError(f"unknown runtime identifier: {rid}")
                for archive in find_archives(rid, args.archives):
                    path = write_package_receipt(rid, archive, args.write_package_receipts)
                    print(f"Wrote package evidence: {path.name}")
        result = verify_release(targets, args.archives, args.meta, args.expected_sha, args.version,
                                args.package_receipts)
    except ReleaseEvidenceError as error:
        print(f"Release evidence verification failed: {error}", file=sys.stderr)
        print("Refusing to publish: every RID must carry its own non-empty package, retained media "
              "notices and matching metadata, and no JavaHost/JRE/JAR/DEX payload.", file=sys.stderr)
        return 1
    for rid, summary in result["verdicts"].items():
        for verdict in summary["assets"]:
            print(f"OK {rid}: {verdict['archive']} sha256={verdict['sha256'][:16]}… "
                  f"files={verdict['members']} javaPayload={verdict['javaPayload']}")
    print(f"Verified {len(result['verdicts'])} RIDs from their real package artifacts")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
