"""Independent, content-addressed corresponding source for the runtime packages."""
import argparse
from contextlib import contextmanager
import gzip
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import tarfile
import tempfile

ASSETS = None
EXPECTED_VERSION = ""
EXPECTED_SHA = ""

TARGET_TOOLS = (
    "build/source_assets.py", "build/release_evidence.py", "build/ci_reuse.py",
    "build/native_coverage.py", "build/bundle.py", "build/fetch-dex-deps.py",
    "build/jlink_runtime.py", "build/nativeverify.py", "build/dex-deps.json",
    "build/native-assets.json", "build/package.py", ".github/workflows/package.yml",
)
TARGET_RESOURCE_DIRS = ("build/licenses", "src/VodBox.Desktop/Assets/Icons")


def target_resources(source_sha):
    return subprocess.run(
        ["git", "ls-tree", "-r", "--name-only", source_sha, "--", *TARGET_RESOURCE_DIRS],
        check=True, capture_output=True).stdout.decode("utf-8").splitlines()



def digest(path):
    with Path(path).open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def encoded(value):
    return (json.dumps(value, sort_keys=True, separators=(",", ":")) + "\n").encode()


def inventory(root):
    files = {}
    for path in sorted(root.rglob("*")):
        if path.is_symlink():
            raise ValueError(f"source closure contains a symlink: {path}")
        if path.is_file():
            files[path.relative_to(root).as_posix()] = {"size": path.stat().st_size,
                                                       "sha256": digest(path)}
    if not files:
        raise ValueError("empty corresponding source closure")
    return files


def git_bytes(ref, name):
    return subprocess.run(["git", "show", f"{ref}:{name}"], check=True,
                          capture_output=True).stdout


def canonical_text(data):
    # Only CRLF is equivalent to LF; bare CR, BOM and all other bytes remain significant.
    data.decode("utf-8", errors="strict")
    if b"\0" in data:
        raise ValueError("NUL in source text")
    return data.replace(b"\r\n", b"\n")


def canonicalize_tracked(source, producer_sha):
    import rebuild_native_backend as rebuild
    protected = rebuild.NATIVE_INPUTS.relative_to(rebuild.ROOT).as_posix() + "/"
    listing = subprocess.run(["git", "ls-tree", "-r", "--name-only", producer_sha],
                             check=True, capture_output=True).stdout.decode("utf-8").splitlines()
    tracked = set(listing)
    for path in sorted(source.rglob("*")):
        if path.is_symlink():
            raise ValueError(f"source closure contains a symlink: {path}")
        if not path.is_file():
            continue
        name = path.relative_to(source).as_posix()
        original = name
        if name.startswith("src/") or name == "pom.xml":
            original = "third_party/tvbox-silicon/Runtime/JavaHost/" + name
        elif name.startswith("vodbox-adapter/") or name in ("LICENSE", "UPSTREAM.json"):
            original = "third_party/tvbox-silicon/" + name
        # Archive payloads and native bridge inputs keep their actual raw-byte hashes.
        if original not in tracked or original.startswith(protected) or \
                "/dependency-sources/archives/" in original:
            continue
        text_suffixes = {".txt", ".md", ".java", ".c", ".h", ".cpp", ".hpp", ".xml",
                         ".json", ".py", ".sh", ".yml", ".yaml", ".cmake", ".in",
                         ".properties", ".gradle", ".bat", ".ps1", ".cfg", ".toml"}
        text_name = path.name.lower().startswith(("license", "copying", "notice", "copyright"))
        if path.suffix.lower() not in text_suffixes and not text_name and \
                path.name.lower() not in ("makefile", "cmakelists.txt"):
            continue
        raw = path.read_bytes()
        blob = git_bytes(producer_sha, original)
        try:
            canonical = canonical_text(blob)
            if canonical_text(raw) != canonical:
                raise ValueError(f"source text differs from producer Git blob: {original}")
        except UnicodeError:
            continue
        except ValueError as error:
            if b"\0" in blob or b"\0" in raw:
                continue
            raise error
        path.write_bytes(canonical)


def split(host, output, version, source_sha, existing=False, producer_sha=""):
    from ci_reuse import ensure_commit, native_build_hash
    producer_sha = producer_sha or source_sha
    for sha in dict.fromkeys((producer_sha, source_sha)):
        if not ensure_commit(sha):
            raise ValueError(f"source commit unavailable: {sha}")
    if native_build_hash(producer_sha) != native_build_hash(source_sha):
        raise ValueError("actual backend producer and target native inputs differ")
    host, output = Path(host), Path(output)
    source = host / "corresponding-source"
    if not source.is_dir():
        raise ValueError("runtime build has no corresponding source closure")
    repository = _repository()
    canonicalize_tracked(source, producer_sha)
    # Bind backend recipes to the authenticated binary producer, not this helper's checkout.
    (source / "build").mkdir(exist_ok=True)
    recipes = {}
    for name in ("build/tvbox-host.py", "build/rebuild_native_backend.py"):
        content = canonical_text(git_bytes(producer_sha, name))
        path = source / name
        if path.exists() and canonical_text(path.read_bytes()) != content:
            raise ValueError(f"packaged recipe differs from actual producer: {name}")
        path.write_bytes(content)
        recipes[name] = hashlib.sha256(content).hexdigest()
    # Deliver the target's source-splitting and evidence tooling alongside the actual recipe.
    for name in TARGET_TOOLS:
        path = source / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(canonical_text(git_bytes(source_sha, name)))
    # License payloads and packaging icons are resources, not normalized source text.
    for name in target_resources(source_sha):
        path = source / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(git_bytes(source_sha, name))
    (source / "README-build.txt").write_text(
        "Requirements: Python 3.11+, Git, JDK 17+ and Maven.\n"
        "This archive includes the component sources and matching runtime/package tools.\n"
        "It is not a standalone checkout of the entire VodBox app. Full app builds need\n"
        "the original repository, .NET SDK and the platform packaging toolchains.\n"
        "Source and evidence tools also require the matching Git source commit objects.\n"
        f"Clone https://github.com/{repository}.git into a separate working directory.\n"
        "Fetch both commits if the clone is shallow, then checkout the matching target:\n"
        f"  git checkout {source_sha}\n"
        "Use that checkout for the full app and target runtime/package tools.\n"
        "For the actual backend producer recipe, use a separate checkout/worktree:\n"
        f"  git worktree add --detach ../vodbox-producer {producer_sha}\n"
        "Keep both commits available for source/evidence validation; unpacking this\n"
        "archive alone does not provide Git history or reproduce the entire app.\n"
        "Native bridge files retain the actual producer bytes, including line endings;\n"
        "preserve them when rebuilding or comparing receipt hashes. Byte-different\n"
        "source variants intentionally have different source archive digests.\n"
        "Rebuild the Java host with JDK 17+ and Maven:\n"
        "  python3 build/tvbox-host.py --output build/component\n"
        "On the native target runner, rebuild and integrate the backend:\n"
        "  python3 build/rebuild_native_backend.py build --rid <RID> --deps build/component/deps\n"
        "  python3 build/rebuild_native_backend.py verify --receipt build/component/native-backend-receipt.json --deps build/component/deps\n"
        "See third_party/tvbox-silicon/rebuild/backend-unicorn2/toolchains.json for toolchains.\n"
        "For macOS use VODBOX_ENGINE_CMAKE_FLAGS=-DCMAKE_OSX_DEPLOYMENT_TARGET=11.6.\n"
        "win-arm64 uses the classes-only exclusion recipe. Other RIDs rebuild their JNI native.\n"
        "The runtime package receipt records its output hash, bridge digest and engine archive hash.\n",
        encoding="utf-8", newline="\n")
    manifest = {"schema": 1, "version": version, "sourceSha": source_sha,
                "backendProducerRecipes": recipes,
                "textNormalization": "tracked UTF-8 text matching producer Git blobs; CRLF to LF only; native bridge and archives excluded",
                "files": inventory(source)}
    manifest_bytes = encoded(manifest)
    manifest_digest = hashlib.sha256(manifest_bytes).hexdigest()
    asset = f"VodBox-{version}-source-{manifest_digest}.tar.gz"
    index_name = f"VodBox-{version}-source-{manifest_digest}.json"
    output.mkdir(parents=True, exist_ok=True)
    archive = output / asset
    index_path = output / index_name
    if index_path.is_file():
        index = validate_index(index_path, version, source_sha)
        if index["manifest"] != manifest:
            raise ValueError("matching source closure differs from independent manifest")
    else:
        if existing:
            raise ValueError("producer source closure has no independent source asset")
        # Canonical metadata makes identical closures produce identical assets on every RID.
        with archive.open("wb") as raw, gzip.GzipFile(filename="", fileobj=raw, mode="wb", mtime=0) as compressed:
            with tarfile.open(fileobj=compressed, mode="w") as writer:
                for name in manifest["files"]:
                    path = source / name
                    info = tarfile.TarInfo(name)
                    info.size = path.stat().st_size
                    info.mode = 0o755 if name.endswith(".sh") else 0o644
                    with path.open("rb") as stream:
                        writer.addfile(info, stream)
        index = {"schema": 1, "asset": asset, "assetSHA256": digest(archive),
                 "assetSize": archive.stat().st_size, "manifestDigest": manifest_digest,
                 "manifest": manifest}
        index_path.write_bytes(encoded(index))
    # Retained texts remain in the runtime; the full source closure is independently delivered.
    retained = source / "third_party/tvbox-silicon/dependency-notices"
    shutil.copytree(retained, host / "dependency-notices", dirs_exist_ok=True)
    (host / "SOURCE-MANIFEST.json").write_bytes(manifest_bytes)
    offer = {key: index[key] for key in ("asset", "assetSHA256", "assetSize", "manifestDigest")}
    offer.update({"schema": 1, "index": index_name, "version": version, "sourceSha": source_sha,
                  "backendSourceSha": producer_sha,
                  "backendReceiptSHA256": digest(host / "native-backend-receipt.json")})
    (host / "source-offer.json").write_bytes(encoded(offer))
    (host / "SOURCE-OFFER.txt").write_text(
        f"Complete matching corresponding source for VodBox {version} ({source_sha})\n"
        f"Download {asset} and {index_name} from the same release:\n"
        f"https://github.com/{repository}/releases/tag/v{version}\n"
        f"Source archive SHA-256: {offer['assetSHA256']}\n"
        f"Matching source manifest SHA-256: {manifest_digest}\n"
        "Extract the archive and follow README-build.txt. Backend recipes cover each RID;\n"
        "the package's native-backend-receipt.json records its actual native output and source inputs.\n"
        f"Actual backend producer commit: {producer_sha}\n"
        f"Original backend receipt SHA-256: {offer['backendReceiptSHA256']}\n"
        "The release target commit above identifies the source-delivery tooling; the actual\n"
        "backend producer commit and original receipt retain the binary's build provenance.\n"
        "License and legal provenance: LICENSE-tvbox-silicon in this runtime and LICENSE\n"
        "and third_party/tvbox-silicon/LICENSE in the source archive contain the upstream\n"
        "Java host license. UPSTREAM.json records the upstream repository and revision.\n"
        "The VodBox adapter source is included under third_party/tvbox-silicon/vodbox-adapter.\n"
        "third_party/tvbox-silicon/dependency-sources/native-source-manifest.json records\n"
        "dependency coordinates, source origins and retained license evidence;\n"
        "dependency-sources/download-archives.json records upstream source URLs and hashes.\n"
        "The rebuilt Unicorn engine's GPL/LGPL terms and QEMU component notices are in\n"
        "the original engine source archive identified by rebuild/backend-unicorn2/engine-pin.json.\n"
        "The quarantined Unicorn1 sources retain COPYING, COPYING.LGPL2, COPYING.LIB\n"
        "and LICENSE texts under dependency-notices/unicorn-1.0.14; consult each upstream\n"
        "text and source archive for the license scope of the respective component.\n"
        "Other dependency licenses and notices are retained under dependency-notices in\n"
        "this runtime and third_party/tvbox-silicon/dependency-notices in the source archive.\n"
        "Bundled Java runtime terms are in JavaHost/jre legal/LICENSE files; media component\n"
        "license origins are recorded in the package's licenses/media/asset.json.\n"
        "This file provides direct access to the accompanying corresponding source;\n"
        "the retained upstream license texts govern their respective components.\n",
        encoding="utf-8", newline="\n")
    # Only remove the packaged copy, after the standalone artifact and binding are written.
    shutil.rmtree(source)


def _repository():
    import os
    value = os.environ.get("GITHUB_REPOSITORY")
    if not value:
        raise ValueError("GITHUB_REPOSITORY is required for the source acquisition URL")
    return value


def validate_index(path, version="", source_sha=""):
    index = json.loads(Path(path).read_text(encoding="utf-8"))
    manifest = index["manifest"]
    actual = hashlib.sha256(encoded(manifest)).hexdigest()
    if index.get("schema") != 1 or manifest.get("schema") != 1 or actual != index["manifestDigest"]:
        raise ValueError("invalid matching source manifest digest")
    if (version and manifest["version"] != version) or (source_sha and manifest["sourceSha"] != source_sha):
        raise ValueError("independent source version/commit mismatch")
    expected = f"VodBox-{manifest['version']}-source-{actual}"
    if Path(path).name != expected + ".json" or index["asset"] != expected + ".tar.gz":
        raise ValueError("invalid source asset/index name")
    archive = Path(path).parent / index["asset"]
    if not archive.is_file() or archive.stat().st_size != index["assetSize"] or digest(archive) != index["assetSHA256"]:
        raise ValueError("independent source archive hash/size mismatch")
    return index


@contextmanager
def extracted(index, directory):
    with tempfile.TemporaryDirectory(prefix=".vodbox-source-", dir=Path(directory).resolve()) as temporary:
        root = Path(temporary)
        with tarfile.open(Path(directory) / index["asset"], "r:gz") as reader:
            seen = set()
            for member in reader:
                path = Path(member.name)
                if (not member.isfile() or path.is_absolute() or ".." in path.parts
                        or "\\" in member.name or ":" in member.name
                        or path.as_posix() != member.name or member.name in seen):
                    raise ValueError("unsafe or duplicate independent source member")
                record = index["manifest"]["files"].get(member.name)
                if record is None or member.size != record["size"]:
                    raise ValueError("independent source member differs from manifest")
                seen.add(member.name)
                target = root / path
                target.parent.mkdir(parents=True, exist_ok=True)
                with reader.extractfile(member) as source, target.open("wb") as destination:
                    shutil.copyfileobj(source, destination)
                target.chmod(0o755 if member.name.endswith(".sh") else 0o644)
        if inventory(root) != index["manifest"]["files"]:
            raise ValueError("independent source content hash mismatch")
        yield root


@contextmanager
def matching_source(host):
    if ASSETS is None:
        raise ValueError("independent source artifacts directory is required")
    host = Path(host)
    if (host / "corresponding-source").exists():
        raise ValueError("runtime package still contains corresponding-source")
    offer = json.loads((host / "source-offer.json").read_text(encoding="utf-8"))
    if offer.get("schema") != 1 or Path(offer["index"]).name != offer["index"]:
        raise ValueError("invalid source index path")
    index = validate_index(Path(ASSETS) / offer["index"], EXPECTED_VERSION, EXPECTED_SHA)
    for key in ("asset", "assetSHA256", "assetSize", "manifestDigest"):
        if offer.get(key) != index[key]:
            raise ValueError(f"runtime/source binding mismatch: {key}")
    for key in ("version", "sourceSha"):
        if offer.get(key) != index["manifest"][key]:
            raise ValueError(f"runtime/source binding mismatch: {key}")
    if (host / "SOURCE-MANIFEST.json").read_bytes() != encoded(index["manifest"]):
        raise ValueError("runtime matching source manifest differs from independent source")
    if offer.get("backendReceiptSHA256") != digest(host / "native-backend-receipt.json"):
        raise ValueError("original backend receipt differs from source binding")
    producer_sha = offer.get("backendSourceSha")
    from ci_reuse import ensure_commit, native_build_hash
    if not isinstance(producer_sha, str) or not ensure_commit(producer_sha) or \
            not ensure_commit(offer["sourceSha"]) or \
            native_build_hash(producer_sha) != native_build_hash(offer["sourceSha"]):
        raise ValueError("actual backend source and target native inputs differ")
    recipes = index["manifest"].get("backendProducerRecipes", {})
    for name in ("build/tvbox-host.py", "build/rebuild_native_backend.py"):
        expected = hashlib.sha256(canonical_text(git_bytes(producer_sha, name))).hexdigest()
        if recipes.get(name) != expected or \
                index["manifest"]["files"][name]["sha256"] != expected:
            raise ValueError(f"independent recipe differs from actual backend producer: {name}")
    for name in TARGET_TOOLS:
        expected = hashlib.sha256(canonical_text(git_bytes(offer["sourceSha"], name))).hexdigest()
        if index["manifest"]["files"][name]["sha256"] != expected:
            raise ValueError(f"independent target tooling differs from source commit: {name}")
    for name in target_resources(offer["sourceSha"]):
        expected = hashlib.sha256(git_bytes(offer["sourceSha"], name)).hexdigest()
        if index["manifest"]["files"][name]["sha256"] != expected:
            raise ValueError(f"independent target resource differs from source commit: {name}")
    instructions = (host / "SOURCE-OFFER.txt").read_text(encoding="utf-8")
    for value in (offer["asset"], offer["index"], offer["assetSHA256"], offer["manifestDigest"],
                  offer["backendSourceSha"], offer["backendReceiptSHA256"],
                  "native-source-manifest.json", "download-archives.json", "engine-pin.json",
                  "LICENSE-tvbox-silicon", "dependency-notices/unicorn-1.0.14",
                  f"/releases/tag/v{offer['version']}"):
        if value not in instructions:
            raise ValueError("runtime source acquisition instructions do not match binding")
    with extracted(index, ASSETS) as root:
        yield root


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--version", required=True)
    parser.add_argument("--source-sha", required=True)
    parser.add_argument("--producer-sha", required=True,
                        help="Authenticated sourceSha of the actual binary producer")
    parser.add_argument("--existing", action="store_true",
                        help="Bind only to a previously produced independent source asset")
    args = parser.parse_args()
    split(args.host, args.output, args.version, args.source_sha, args.existing, args.producer_sha)
