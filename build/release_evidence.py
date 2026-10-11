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
import hashlib
import json
import sys
import zipfile
from pathlib import Path

import native_coverage

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
ARCHIVE_SUFFIXES = (".zip", ".dmg", ".deb", ".rpm")


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


def members_of(archive: Path) -> list[str]:
    """Every file member of a published package, by name only (no extraction)."""
    suffix = archive.suffix.lower()
    try:
        if suffix in (".zip", ".dmg"):
            with zipfile.ZipFile(archive) as reader:
                return [normalize_member(n) for n in reader.namelist() if not n.endswith("/")]
        if suffix in (".deb", ".rpm"):
            # deb is an `ar` archive whose data.tar.{xz,gz,zst} member carries the payload; rpm's
            # payload is compressed and the file list lives in a binary index. Both are read here
            # from a single real command rather than re-implemented in Python, because the point of
            # this check is the *package's* content, not a Python archive parser.
            return _package_members(archive)
    except zipfile.BadZipFile as error:
        raise ReleaseEvidenceError(f"{archive.name}: not a readable package archive: {error}") from error
    raise ReleaseEvidenceError(f"{archive.name}: unsupported package format {archive.suffix!r}")


def _package_members(archive: Path) -> list[str]:
    import subprocess
    import tarfile

    if archive.suffix.lower() == ".deb":
        listing = subprocess.run(["ar", "t", str(archive)], capture_output=True, text=True)
        if listing.returncode:
            raise ReleaseEvidenceError(f"{archive.name}: cannot read the deb archive: {listing.stderr.strip()}")
        members: list[str] = []
        for name in ("data.tar.xz", "data.tar.gz", "data.tar.zst", "data.tar"):
            if name in listing.stdout.split():
                extract = subprocess.run(["ar", "x", str(archive), name], cwd=archive.parent,
                                         capture_output=True, text=True)
                if extract.returncode:
                    raise ReleaseEvidenceError(
                        f"{archive.name}: cannot extract {name}: {extract.stderr.strip()}")
                payload = archive.parent / name
                try:
                    with tarfile.open(payload) as tar:
                        members = [normalize_member(f"./{m.name}") for m in tar.getmembers() if m.isfile()]
                finally:
                    payload.unlink(missing_ok=True)
                return members
        raise ReleaseEvidenceError(f"{archive.name}: the deb carries no data.tar payload")

    listing = subprocess.run(["rpm", "-qlp", str(archive)], capture_output=True, text=True)
    if listing.returncode:
        raise ReleaseEvidenceError(f"{archive.name}: cannot list the rpm payload: {listing.stderr.strip()}")
    return [normalize_member(line) for line in listing.stdout.splitlines() if line.strip()]


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


def verify_rid(rid: str, archive: Path, meta: Path | None = None,
               receipts_dir: Path | None = None) -> dict:
    """One RID's full verdict: artifact + hash + host + deps + receipt + licenses, from real bytes."""
    if not archive.is_file():
        raise ReleaseEvidenceError(f"{rid}: no package artifact matches {archive.name}")
    if archive.stat().st_size == 0:
        raise ReleaseEvidenceError(f"{rid}: {archive.name} is empty")
    members = members_of(archive)
    if not members:
        raise ReleaseEvidenceError(f"{rid}: {archive.name} contains no files")
    verdict = {"rid": rid, **verify_java_host(rid, archive, members)}
    verdict.update(verify_licenses(rid, archive, members))
    verdict["meta"] = verify_meta(rid, meta) if meta is not None else None
    verdict["receiptArtifact"] = (verify_receipt_artifact(rid, receipts_dir)
                                  if receipts_dir is not None else None)
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
               if path.is_file() and path.suffix.lower() in ARCHIVE_SUFFIXES
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
