#!/usr/bin/env python3
"""Per-RID from-pinned-source rebuild of the ARM64 emulation backend, with a verifiable receipt.

This is the *integration* half of `third_party/tvbox-silicon/rebuild/` (the recipe half): it
drives the retained shell recipe, swaps the rebuilt native into the staged Maven jars CI resolves,
and emits a machine-checkable receipt that a release gate can act on.

What CI does per native RID (see `.github/workflows/build.yml` integration):
  1. `tvbox-host.py` resolves `deps/` from the pinned pom (Maven pulls `unidbg-unicorn2:0.9.8`).
  2. **this script** rebuilds `libunicorn.{so,dylib}` / `unicorn.dll` from the pinned engine commit
     and the retained bridge sources, then rewrites the staged `unidbg-unicorn2` jar so that its
     `natives/` tree carries **this RID's library and nothing else**.
  3. every other RID's upstream prebuilt native is *removed from the jar*, not left riding along:
     the package must not distribute an unprovenanced GPL engine for a platform it did not rebuild.

The swap is deliberately a *replace*, not a prove-the-old-binary: `rebuild/README.md` §1 records
that the upstream prebuilt cannot be mapped to a public source revision, so the honest position is
"we no longer distribute the upstream prebuilt; we distribute what we built from the pin".

`com.github.zhkl0228:unicorn:1.0.14` is handled separately and *not* by dropping the dependency:
`unidbg-android`/`unidbg-unicorn2` reference Unicorn1 Java parent types, so removing the artifact
breaks linking at runtime. Its `natives/*/libunicorn_java.*` (GPL native) is stripped from the jar
while the classes stay, and the pinned archived source (`archives/unicorn-15cad005...tar.gz`) plus
the retained notice (`dependency-notices/unicorn-1.0.14/`) are recorded in the receipt so the
corresponding-source obligation travels with the distribution.

Boundaries (enforced by the gate that reads the receipt, not by claims here):
  * No build is executed by this module's authoring environment. `build` runs the recipe; the
    receipt records what actually happened and the hashes of what was produced.
  * `win-arm64` has no upstream JNI native and no source rebuild, so it gets a **classes-only
    exclusion receipt** instead of a compiled library: the staged backend jar is rewritten to drop
    every `natives/` entry (there is nothing rebuilt to keep) and the receipt records the exclusion.
    A plain Java package still ships for `win-arm64`; it must not carry an unprovenanced GPL engine.

Usage:
    python3 build/rebuild_native_backend.py plan
    python3 build/rebuild_native_backend.py build  --rid osx-x64 --deps <JavaHost/deps> [--engine-cache DIR] [--out DIR]
    python3 build/rebuild_native_backend.py build  --rid win-arm64 --deps <JavaHost/deps>
    python3 build/rebuild_native_backend.py verify --receipt <receipt.json> [--deps <JavaHost/deps>]
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import shutil
import subprocess
import sys
import tempfile
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SOURCE = ROOT / "third_party/tvbox-silicon"
REBUILD = SOURCE / "rebuild/backend-unicorn2"
REBUILD_LOCK = SOURCE / "dependency-sources/rebuild-lock.json"
ENGINE_PIN = REBUILD / "engine-pin.json"
TOOLCHAINS = REBUILD / "toolchains.json"
NATIVE_INPUTS = (SOURCE / "dependency-sources/upstream-evidence/native-provenance"
                 / "release-inputs/backend/unicorn2/src/main/native")
LICENSE_NOTICES = SOURCE / "dependency-notices"

# The Maven coordinates whose staged jar is rewritten. The backend is the one this script builds;
# `unicorn:1.0.14` is the Unicorn1 Java dependency whose GPL native is quarantined, not dropped.
BACKEND_COORDINATE = "com.github.zhkl0228:unidbg-unicorn2:0.9.8"
BACKEND_JAR_GLOB = "unidbg-unicorn2-*.jar"
UNICORN1_COORDINATE = "com.github.zhkl0228:unicorn:1.0.14"
UNICORN1_JAR_GLOB = "unicorn-1.0.14.jar"

# Jar `natives/<dir>/` -> RID, matching the upstream loader. A built library lands under exactly
# one of these directories; every other directory is *removed* from the rewritten jar.
NATIVE_DIRECTORY_RID = {
    "osx_64": "osx-x64",
    "osx_arm64": "osx-arm64",
    "windows_64": "win-x64",
    "windows_arm64": "win-arm64",
    "linux_64": "linux-x64",
    "linux_arm64": "linux-arm64",
}

# Per-RID output file name inside the jar (from Unicorn2Factory's NativeLoader.loadLibrary("unicorn")).
RID_OUTPUT_NAME = {
    "osx-x64": "libunicorn.dylib",
    "osx-arm64": "libunicorn.dylib",
    "linux-x64": "libunicorn.so",
    "linux-arm64": "libunicorn.so",
    "win-x64": "unicorn.dll",
}

SUPPORTED_RIDS = ("linux-x64", "linux-arm64", "osx-x64", "osx-arm64", "win-x64")
UNSUPPORTED_RIDS = ("win-arm64",)

# `win-arm64` is not a *supported source rebuild*: the upstream backend has no windows_arm64 JNI
# native, so there is nothing to compile. But a Java-only package still ships there, and it must
# not carry an unprovenanced GPL engine for some other platform. So `win-arm64` gets a
# **classes-only exclusion receipt**: the backend jar is rewritten to drop every `natives/` entry
# (there is no rebuilt library to keep), and the receipt records the exclusion rather than an
# output library. This is deliberately a receipt, not a `throw`: the RID is a legitimate package
# target, it just has no source-rebuilt native to carry.
EXCLUSION_RIDS = ("win-arm64",)
ALL_RIDS = SUPPORTED_RIDS + EXCLUSION_RIDS

# Bridge C sources compiled by the recipe, and the retained bridge headers it consumes.
BRIDGE_SOURCES = ("unicorn.c", "sample_arm.c", "sample_arm64.c")
BRIDGE_HEADERS = (
    "unicorn.h",
    "khash.h",
    "com_github_unidbg_arm_backend_unicorn_Unicorn.h",
    "com_github_unidbg_arm_backend_unicorn_Unicorn_NewHook.h",
    "com_github_unidbg_arm_backend_unicorn_Unicorn_UnHook.h",
    "com_github_unidbg_arm_backend_unicorn_Unicorn_Tuple.h",
)

RECEIPT_SCHEMA = 1


class RebuildError(RuntimeError):
    """A rebuild step could not produce a defensible artifact."""


def sha256_file(path: Path) -> str:
    with Path(path).open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def _engines() -> dict:
    return json.loads(ENGINE_PIN.read_text(encoding="utf-8"))["engine"]


def _lock() -> dict:
    return json.loads(REBUILD_LOCK.read_text(encoding="utf-8"))


def backend_artifact_lock() -> dict:
    for artifact in _lock()["artifacts"]:
        if artifact["id"] == "backend-unicorn2":
            return artifact
    raise RebuildError("rebuild-lock.json has no backend-unicorn2 entry")


def bridge_source_digest() -> tuple[str, dict]:
    """A single digest over the *bridge* sources (the JNI glue), plus the per-file hashes.

    The bridge ships as source in the unidbg release tree, so hashing the retained copy is a real
    identity for it — unlike the engine, whose old prebuilt is not claimed to match any commit.
    """
    per_file: dict[str, str] = {}
    combined = hashlib.sha256()
    for name in BRIDGE_SOURCES + BRIDGE_HEADERS:
        path = NATIVE_INPUTS / name
        if not path.is_file():
            raise RebuildError(f"retained bridge input missing: {path}")
        digest = sha256_file(path)
        per_file[name] = digest
        combined.update(name.encode("utf-8"))
        combined.update(b"\0")
        combined.update(bytes.fromhex(digest))
    return combined.hexdigest(), per_file


def toolchain_record(rid: str) -> dict:
    targets = json.loads(TOOLCHAINS.read_text(encoding="utf-8"))["targets"]
    if rid not in targets:
        raise RebuildError(f"toolchains.json has no target for {rid}")
    return targets[rid]


def engine_archive() -> Path:
    """The retained, immutable engine archive the recipe extracts (hash-checked by fetch-engine.sh).

    `engine-pin.json` stores `archive_path` relative to `rebuild/backend-unicorn2/`, so resolve it
    against that directory (and accept an already-absolute path) rather than assuming a base.
    """
    pinned = Path(_engines()["archive_path"])
    candidate = pinned if pinned.is_absolute() else (REBUILD / pinned)
    candidate = candidate.resolve()
    if candidate.is_file():
        return candidate
    raise RebuildError(
        f"engine archive not retained at {candidate}; the rebuild cannot be run offline without it")


def _run(command: list[str], *, cwd: Path | None = None, env: dict | None = None) -> None:
    merged = dict(os.environ)
    if env:
        merged.update(env)
    if merged.get("VODBOX_NATIVE_CC"):
        tools = [str(Path(merged["VODBOX_NATIVE_CC"]).parent)]
        if merged.get("VODBOX_NATIVE_BASH"):
            tools.append(str(Path(merged["VODBOX_NATIVE_BASH"]).parent))
        merged["PATH"] = os.pathsep.join(tools + [merged.get("PATH", "")])
    print("+ " + " ".join(command), flush=True)
    subprocess.run(command, cwd=str(cwd) if cwd else None, env=merged, check=True)


def _tool_version(command: list[str]) -> str:
    """The concrete version string of a toolchain, recorded rather than inherited silently."""
    try:
        result = subprocess.run(command, capture_output=True, text=True, timeout=30, check=False)
    except (OSError, subprocess.SubprocessError) as error:
        return f"unavailable: {error}"
    text = (result.stdout or "") + (result.stderr or "")
    return text.strip().splitlines()[0] if text.strip() else "unknown"


def toolchain_probe(rid: str) -> dict:
    """Record the actual toolchain chosen on this runner (never assumed from the pin alone).

    `win-x64` prefers the runner's MinGW (`gcc`/`x86_64-w64-mingw32-gcc`) so the build uses the
    toolchain actually present rather than a copied assumption; the probe records which one won.
    """
    record = toolchain_record(rid)
    probe: dict = {"declared_compiler": record["compiler"], "flags": record["flags"]}
    if rid.startswith("osx"):
        probe["command"] = ["xcrun", "-sdk", "macosx", "clang"]
        probe["version"] = _tool_version(["xcrun", "-sdk", "macosx", "clang", "--version"])
        probe["deployment_target"] = "11.6"
        if "-mmacosx-version-min=11.6" not in probe["flags"].split():
            probe["flags"] += " -mmacosx-version-min=11.6"
        # The static engine archive is linked into the bridge library, so the engine's own CMake
        # build must be held to the same minimum OS; otherwise the engine's default (the runner SDK)
        # raises the linked library's min-OS above the bridge's -mmacosx-version-min and the bundle's
        # 11.6 floor is not what actually ships. CI must pass this CMake knob (see build.yml).
        probe["engine_cmake_flags"] = "-DCMAKE_OSX_DEPLOYMENT_TARGET=11.6"
        probe["deployment_target_note"] = (
            "The bundled JRE is built with a macOS 11 deployment target; the backend library is "
            "held to the same -mmacosx-version-min so the JNI library loads on the same minimum OS. "
            "The linked static engine is built with -DCMAKE_OSX_DEPLOYMENT_TARGET=11.6 for the same "
            "reason. Do not raise either to the runner's SDK default (the host must not borrow it).")
    elif rid.startswith("linux"):
        compiler = os.environ.get("CROSS_CC") or ("aarch64-linux-gnu-gcc" if rid == "linux-arm64" else "gcc")
        probe["command"] = [compiler]
        probe["version"] = _tool_version([compiler, "--version"])
    elif rid == "win-x64":
        # MSYS2 commonly supplies gcc.exe without a target-prefixed executable name.
        requested = os.environ.get("VODBOX_NATIVE_CC") or os.environ.get("CROSS_CC")
        compiler = shutil.which(requested) if requested else (
            shutil.which("x86_64-w64-mingw32-gcc") or shutil.which("gcc"))
        if not compiler:
            raise RebuildError(f"win-x64: MinGW compiler not found: {requested or 'gcc'}")
        target = _tool_version([compiler, "-dumpmachine"])
        if target != "x86_64-w64-mingw32":
            raise RebuildError(f"win-x64: {compiler} targets {target}, expected x86_64-w64-mingw32")
        probe["command"] = [compiler]
        probe["version"] = _tool_version([compiler, "--version"])
        probe["target"] = target
        probe["engine_generator"] = os.environ.get("VODBOX_ENGINE_GENERATOR", "MinGW Makefiles")
        for key, variable, default in (
            ("engine_cmake", "VODBOX_ENGINE_CMAKE", "cmake"),
            ("engine_make", "VODBOX_ENGINE_MAKE",
             "ninja" if probe["engine_generator"] == "Ninja" else "mingw32-make"),
        ):
            tool = shutil.which(os.environ.get(variable) or default)
            if not tool:
                raise RebuildError(f"win-x64: missing {variable} tool ({default})")
            probe[key] = tool
        probe["static"] = True
    else:
        raise RebuildError(f"{rid}: no toolchain is defined; {rid} is not built from source")
    probe["java_home"] = os.environ.get("JAVA_HOME", "")
    if not probe["java_home"]:
        raise RebuildError("JAVA_HOME must point at the JDK whose include/ carries jni.h for the build")
    return probe


def built_library(rid: str, out_root: Path) -> Path:
    """The library the recipe must have produced for `rid`."""
    name = RID_OUTPUT_NAME[rid]
    candidate = out_root / rid / name
    if not candidate.is_file():
        raise RebuildError(
            f"{rid}: expected rebuilt native at {candidate}; the backend recipe did not produce it")
    if candidate.stat().st_size == 0:
        raise RebuildError(f"{rid}: rebuilt native {candidate} is empty")
    return candidate


def rewrite_backend_jar(jar: Path, rid: str, library: Path, *, work: Path) -> dict:
    """Rewrite `unidbg-unicorn2` so its `natives/` tree carries *only* this RID's rebuilt library.

    Every other RID's native directory and file is dropped from the archive. This is the point of
    the exercise: the shipped package must not continue to distribute the unprovenanced upstream
    GPL engine for any platform VodBox did not rebuild. The Java classes are copied verbatim.
    """
    target_directory = next(directory for directory, mapped in NATIVE_DIRECTORY_RID.items() if mapped == rid)
    target_entry = f"natives/{target_directory}/{library.name}"
    original_hash = sha256_file(jar)
    # Write to a sibling staging name so a jar that already lives in `work` is never read and
    # written at the same path (which truncates it mid-copy).
    staged = work / (jar.name + ".staged")
    removed: list[str] = []
    kept_natives: list[str] = []

    with zipfile.ZipFile(jar) as source, zipfile.ZipFile(staged, "w", zipfile.ZIP_DEFLATED) as output:
        for info in source.infolist():
            name = info.filename
            if name.endswith("/"):
                continue  # directory entries are re-created implicitly by the file paths
            if name.startswith("natives/"):
                if name == target_entry:
                    continue  # re-added below from the rebuilt bytes
                removed.append(name)
                continue
            output.writestr(info, source.read(name))
        output.writestr(target_entry, library.read_bytes())
        kept_natives.append(target_entry)

    staged_hash = sha256_file(staged)
    shutil.move(str(staged), str(jar))

    with zipfile.ZipFile(jar) as archive:
        present = sorted(
            name for name in archive.namelist()
            if name.startswith("natives/") and not name.endswith("/"))
    if present != kept_natives:
        raise RebuildError(
            f"{jar.name}: rewritten natives/ tree is {present}, expected exactly {kept_natives}; "
            f"the jar still carries a foreign native")
    return {
        "jar": jar.name,
        "rid": rid,
        "native_entry": target_entry,
        "jar_native_entries": present,
        "removed_native_entries": sorted(removed),
        "pre_rewrite_jar_sha256": original_hash,
        "post_rewrite_jar_sha256": staged_hash,
        "library_sha256": sha256_file(library),
        "library_size": library.stat().st_size,
    }


def quarantine_unicorn1_native(jar: Path, *, work: Path) -> dict:
    """Strip `unicorn:1.0.14`'s GPL native but KEEP its classes.

    Dropping the dependency outright is not an option: `unidbg-android` / `unidbg-unicorn2` Java
    sources extend Unicorn1 parent types (`com.github.zhkl0228.unicorn.*`), so removing the artifact
    fails class resolution at runtime ("Unicorn2 父类型链接失败"). The native `libunicorn_java.*`
    files are the GPL-engine part that is not provenanced by the pin, so they are removed while the
    Java side — the part that is actually required for linking — remains.
    """
    original_hash = sha256_file(jar)
    staged = work / (jar.name + ".staged")
    removed: list[str] = []
    with zipfile.ZipFile(jar) as source, zipfile.ZipFile(staged, "w", zipfile.ZIP_DEFLATED) as output:
        for info in source.infolist():
            name = info.filename
            if name.endswith("/"):
                continue
            if name.startswith("natives/"):
                removed.append(name)
                continue
            output.writestr(info, source.read(name))
    shutil.move(str(staged), str(jar))
    with zipfile.ZipFile(jar) as archive:
        classes = [n for n in archive.namelist() if n.endswith(".class")]
    if not classes:
        raise RebuildError(f"{jar.name}: quarantining the native also dropped the Java classes; "
                           f"the Unicorn1 parent types are required for linking")
    remaining_natives = [n for n in zipfile.ZipFile(jar).namelist() if n.startswith("natives/")]
    if remaining_natives:
        raise RebuildError(f"{jar.name}: GPL native survived the quarantine: {remaining_natives}")
    return {
        "coordinate": UNICORN1_COORDINATE,
        "jar": jar.name,
        "removed_native_entries": sorted(removed),
        "remaining_class_count": len(classes),
        "pre_quarantine_jar_sha256": original_hash,
        "post_quarantine_jar_sha256": sha256_file(jar),
        "kept_dependency": True,
        "why_not_removed": (
            "unidbg-android/unidbg-unicorn2 reference Unicorn1 Java parent types; removing the jar "
            "breaks linkage. Only the unprovenanced GPL native is withheld."),
    }


def unicorn1_source_evidence() -> dict:
    """The pinned archived source + retained notice for the quarantined Unicorn1 native."""
    sources = SOURCE / "dependency-sources"
    archive = sources / "archives/unicorn-15cad005ed027685e5e3da429eb84325fba55f8a.tar.gz"
    notice = LICENSE_NOTICES / "unicorn-1.0.14"
    evidence: dict = {
        "coordinate": UNICORN1_COORDINATE,
        "archive": str(archive.relative_to(ROOT)) if archive.is_file() else None,
        "archive_sha256": sha256_file(archive) if archive.is_file() else None,
        "notice": str(notice.relative_to(ROOT)) if notice.is_dir() else None,
        "license": "GPL-2.0-or-later (engine); see notice COPYING / COPYING.LGPL2 / COPYING_GLIB",
    }
    if not archive.is_file() or not notice.is_dir():
        raise RebuildError(
            f"{UNICORN1_COORDINATE}: corresponding-source archive and/or notice is not retained "
            f"(archive={archive.is_file()}, notice={notice.is_dir()}); keeping the dependency "
            f"without its source would ship an unprovenanced GPL native")
    return evidence


def _maven_jar(deps: Path, glob: str, coordinate: str) -> Path:
    matches = sorted(Path(deps).glob(glob))
    if not matches:
        raise RebuildError(f"{deps}: {coordinate} is not staged; nothing to rewrite")
    if len(matches) > 1:
        raise RebuildError(f"{deps}: {coordinate} matched multiple jars: {[m.name for m in matches]}")
    return matches[0]


def exclude_backend_natives(jar: Path, rid: str, *, work: Path) -> dict:
    """Rewrite `unidbg-unicorn2` for a RID with no source-rebuilt native: drop every `natives/` entry.

    `win-arm64` has no upstream JNI native and no rebuild recipe, so there is nothing to keep — but
    the upstream jar's *other-platform* natives must not ride along into a package that never
    rebuilt them. The classes are copied verbatim (the jar stays on the classpath). The recorded
    `native_entry` is `None` and `excluded_native_entries` lists what was withheld.
    """
    original_hash = sha256_file(jar)
    staged = work / (jar.name + ".staged")
    removed: list[str] = []
    with zipfile.ZipFile(jar) as source, zipfile.ZipFile(staged, "w", zipfile.ZIP_DEFLATED) as output:
        for info in source.infolist():
            name = info.filename
            if name.endswith("/"):
                continue
            if name.startswith("natives/"):
                removed.append(name)
                continue
            output.writestr(info, source.read(name))
    shutil.move(str(staged), str(jar))
    with zipfile.ZipFile(jar) as archive:
        present = sorted(n for n in archive.namelist()
                         if n.startswith("natives/") and not n.endswith("/"))
        classes = [n for n in archive.namelist() if n.endswith(".class")]
    if present:
        raise RebuildError(f"{jar.name}: exclusion left foreign natives behind: {present}")
    if not classes:
        raise RebuildError(f"{jar.name}: exclusion also dropped the Java classes; the backend jar "
                           f"must stay linkable on the classpath")
    return {
        "jar": jar.name,
        "rid": rid,
        "native_entry": None,
        "jar_native_entries": [],
        "excluded_native_entries": sorted(removed),
        "remaining_class_count": len(classes),
        "pre_rewrite_jar_sha256": original_hash,
        "post_rewrite_jar_sha256": sha256_file(jar),
        "reason": (f"{rid} has no upstream JNI native and no source rebuild; every natives/ entry is "
                   f"withheld so the package carries no unprovenanced GPL engine on this RID."),
    }


def build(rid: str, deps: Path, *, engine_cache: Path, out_root: Path) -> dict:
    """Run the recipe for `rid` and integrate the rebuilt native into the staged jars."""
    if rid not in ALL_RIDS:
        raise RebuildError(f"unknown RID: {rid} (known: {', '.join(ALL_RIDS)})")
    deps = Path(deps).resolve()
    if not deps.is_dir():
        raise RebuildError(f"deps directory does not exist: {deps}")
    if rid in EXCLUSION_RIDS:
        return build_exclusion(rid, deps)

    lock = backend_artifact_lock()
    engine = _engines()
    archive = engine_archive()
    archive_hash = sha256_file(archive)
    if archive_hash != engine["archive_sha256"]:
        raise RebuildError(
            f"engine archive sha256 mismatch: expected {engine['archive_sha256']}, got {archive_hash}")
    bridge_digest, bridge_files = bridge_source_digest()
    toolchain = toolchain_probe(rid)

    env = {
        "RID": rid,
        "VODBOX_BUILD_ENGINE": "1",
        "VODBOX_REBUILD_CACHE": str(Path(engine_cache).resolve()),
        "VODBOX_REBUILD_OUT": str(Path(out_root).resolve()),
        "VODBOX_ENGINE_CMAKE_FLAGS": toolchain.get("engine_cmake_flags", ""),
    }
    if rid == "win-x64":
        env.update({
            "VODBOX_NATIVE_CC": toolchain["command"][0],
            "VODBOX_ENGINE_CMAKE": toolchain["engine_cmake"],
            "VODBOX_ENGINE_GENERATOR": toolchain["engine_generator"],
            "VODBOX_ENGINE_MAKE": toolchain["engine_make"],
        })
    shell = os.environ.get("VODBOX_NATIVE_BASH", "bash")
    if rid == "win-x64" and shell == "bash":
        raise RebuildError("Windows source rebuild requires explicit VODBOX_NATIVE_BASH; bare bash may select WSL")
    _run([shell, str(REBUILD / "fetch-engine.sh")], env=env)
    _run([shell, str(REBUILD / "build-backend.sh")], env=env)
    library = built_library(rid, out_root)

    with tempfile.TemporaryDirectory(prefix=f"native-receipt-{rid}-") as scratch:
        work = Path(scratch)
        backend_jar = _maven_jar(deps, BACKEND_JAR_GLOB, BACKEND_COORDINATE)
        integration = rewrite_backend_jar(backend_jar, rid, library, work=work)
        unicorn1: dict | None = None
        try:
            unicorn1_jar = _maven_jar(deps, UNICORN1_JAR_GLOB, UNICORN1_COORDINATE)
        except RebuildError:
            unicorn1_jar = None
        if unicorn1_jar is not None:
            unicorn1 = quarantine_unicorn1_native(unicorn1_jar, work=work)
            unicorn1["source_evidence"] = unicorn1_source_evidence()

    receipt = {
        "schema": RECEIPT_SCHEMA,
        "rid": rid,
        "backend": {
            "coordinate": BACKEND_COORDINATE,
            "engine": {
                "repo": engine["repo"],
                "branch": engine["branch"],
                "commit": engine["commit"],
                "tag": engine["tag"],
                "build_system": engine["build_system"],
                "build_command": engine["build_command"],
                "source_archive": str(archive.relative_to(ROOT)) if archive.is_relative_to(ROOT) else str(archive),
                "source_archive_sha256": archive_hash,
                "not_claimed": engine_pin_not_claimed(),
            },
            "bridge": {
                "source_tree": str(NATIVE_INPUTS.relative_to(ROOT)),
                "source_commit": _bridge_commit(),
                "source_digest_sha256": bridge_digest,
                "files": bridge_files,
            },
            "toolchain": toolchain,
            "flags": toolchain["flags"],
            "declared_toolchain_source": toolchain_record(rid)["source_reference"],
            "output_library": {
                "file": library.name,
                "sha256": sha256_file(library),
                "size": library.stat().st_size,
            },
            "jar_integration": integration,
        },
        "unicorn1": unicorn1,
        "lock_output_sha256": lock.get("output_sha256"),
    }
    if receipt["backend"]["jar_integration"]["jar_native_entries"] != [
            receipt["backend"]["jar_integration"]["native_entry"]]:
        raise RebuildError("receipt would record more than the rebuilt native; refusing to write")
    return receipt


def build_exclusion(rid: str, deps: Path) -> dict:
    """The classes-only exclusion receipt for a RID with no source-rebuilt native (`win-arm64`).

    This is deliberately *not* a throw: the RID is a legitimate Java package target, it simply has
    no native backend to source-rebuild. The receipt records the same provenance frame (engine pin,
    bridge digest, toolchain absence) plus the explicit exclusion of every `natives/` entry, so a
    release gate can tell "no native was rebuilt for this RID, and none was carried" apart from
    "a native was rebuilt but its evidence is missing".
    """
    engine = _engines()
    bridge_digest, bridge_files = bridge_source_digest()
    archive = engine_archive()
    archive_hash = sha256_file(archive)
    if archive_hash != engine["archive_sha256"]:
        raise RebuildError(
            f"engine archive sha256 mismatch: expected {engine['archive_sha256']}, got {archive_hash}")
    with tempfile.TemporaryDirectory(prefix=f"native-receipt-{rid}-") as scratch:
        work = Path(scratch)
        backend_jar = _maven_jar(deps, BACKEND_JAR_GLOB, BACKEND_COORDINATE)
        integration = exclude_backend_natives(backend_jar, rid, work=work)
        unicorn1: dict | None = None
        try:
            unicorn1_jar = _maven_jar(deps, UNICORN1_JAR_GLOB, UNICORN1_COORDINATE)
        except RebuildError:
            unicorn1_jar = None
        if unicorn1_jar is not None:
            unicorn1 = quarantine_unicorn1_native(unicorn1_jar, work=work)
            unicorn1["source_evidence"] = unicorn1_source_evidence()
    return {
        "schema": RECEIPT_SCHEMA,
        "rid": rid,
        "sourceRebuilt": False,
        "backend": {
            "coordinate": BACKEND_COORDINATE,
            "excluded": True,
            "exclusion_reason": (
                f"{rid} has no upstream JNI native in {BACKEND_COORDINATE} and the source rebuild "
                f"covers only {', '.join(SUPPORTED_RIDS)}; a Java-only package still ships, with no "
                f"native backend library distributed for this RID."),
            "engine": {
                "repo": engine["repo"],
                "branch": engine["branch"],
                "commit": engine["commit"],
                "tag": engine["tag"],
                "build_system": engine["build_system"],
                "build_command": engine["build_command"],
                "source_archive": str(archive.relative_to(ROOT)) if archive.is_relative_to(ROOT) else str(archive),
                "source_archive_sha256": archive_hash,
                "not_claimed": engine_pin_not_claimed(),
            },
            "bridge": {
                "source_tree": str(NATIVE_INPUTS.relative_to(ROOT)),
                "source_commit": _bridge_commit(),
                "source_digest_sha256": bridge_digest,
                "files": bridge_files,
            },
            "toolchain": {"declared_compiler": None, "flags": None,
                          "note": "no compilation is performed for an exclusion RID"},
            "flags": None,
            "declared_toolchain_source": None,
            "output_library": None,
            "jar_integration": integration,
        },
        "unicorn1": unicorn1,
        "lock_output_sha256": backend_artifact_lock().get("output_sha256"),
    }


def engine_pin_not_claimed() -> list[str]:
    return json.loads(ENGINE_PIN.read_text(encoding="utf-8")).get("not_claimed", [])


def _bridge_commit() -> str:
    return json.loads(ENGINE_PIN.read_text(encoding="utf-8"))["bridge"]["source_commit"]


def verify_receipt(receipt: dict, *, deps: Path | None) -> list[str]:
    """Fail-closed checks a release gate can act on. Returns the list of problems (empty is good)."""
    problems: list[str] = []
    if receipt.get("schema") != RECEIPT_SCHEMA:
        problems.append(f"receipt schema {receipt.get('schema')!r} is not {RECEIPT_SCHEMA}")
    rid = receipt.get("rid")
    backend = receipt.get("backend") or {}
    jar = backend.get("jar_integration") or {}
    exclusion = bool(backend.get("excluded"))
    if rid not in ALL_RIDS:
        problems.append(f"receipt is for {rid!r}, not one of {', '.join(ALL_RIDS)}")
    elif exclusion and rid not in EXCLUSION_RIDS:
        problems.append(f"receipt marks {rid!r} as an exclusion, but it is a source-rebuilt RID")
    elif not exclusion and rid not in SUPPORTED_RIDS:
        problems.append(f"receipt is for {rid!r}, not one of the five rebuilt RIDs")
    entries = jar.get("jar_native_entries") or []
    if exclusion:
        # Classes-only exclusion: withhold every native and record that nothing was rebuilt.
        if entries:
            problems.append(
                f"exclusion receipt still records native entries {entries}; the RID must carry no "
                f"unprovenanced engine library")
        if not (backend.get("exclusion_reason") or "").strip():
            problems.append("exclusion receipt records no reason for withholding the native")
        if backend.get("output_library"):
            problems.append("exclusion receipt records an output library; nothing was compiled")
    else:
        native = (backend.get("output_library") or {})
        if not native.get("sha256"):
            problems.append("receipt records no output library sha256")
        if len(entries) != 1 or entries != [jar.get("native_entry")]:
            problems.append(
                f"receipt does not record exactly this RID's native entry (got {entries}); other "
                f"RIDs must be removed from the package, not carried")
    if not backend.get("bridge", {}).get("source_digest_sha256"):
        problems.append("receipt records no bridge source digest")
    if not (backend.get("engine") or {}).get("source_archive_sha256"):
        problems.append("receipt records no engine source archive hash")
    if not backend.get("toolchain"):
        problems.append("receipt records no toolchain")
    unicorn1 = receipt.get("unicorn1")
    if unicorn1 is None:
        problems.append("receipt has no unicorn:1.0.14 quarantine record (parent types are required)")
    else:
        if unicorn1.get("removed_native_entries") is None:
            problems.append("unicorn:1.0.14 record does not list the removed GPL native")
        if not (unicorn1.get("source_evidence") or {}).get("archive_sha256"):
            problems.append("unicorn:1.0.14 record has no pinned source archive hash")
        if unicorn1.get("kept_dependency") is not True:
            problems.append("unicorn:1.0.14 dependency must stay (parent types), only its native is withheld")
    if deps is not None:
        deps = Path(deps)
        if rid in ALL_RIDS:
            jar_path = _maven_jar(deps, BACKEND_JAR_GLOB, BACKEND_COORDINATE)
            with zipfile.ZipFile(jar_path) as archive:
                actual = sorted(n for n in archive.namelist()
                                if n.startswith("natives/") and not n.endswith("/"))
            if actual != entries:
                problems.append(f"{jar_path.name} natives/ is {actual}, receipt says {entries}")
            if jar.get("post_rewrite_jar_sha256") and \
                    sha256_file(jar_path) != jar["post_rewrite_jar_sha256"]:
                problems.append(f"{jar_path.name} hash does not match the receipt (staged jar changed)")
        if unicorn1 is not None:
            u1 = _maven_jar(deps, UNICORN1_JAR_GLOB, UNICORN1_COORDINATE)
            with zipfile.ZipFile(u1) as archive:
                leftover = [n for n in archive.namelist() if n.startswith("natives/")]
            if leftover:
                problems.append(f"{u1.name} still carries GPL native: {leftover}")
    return problems


def _command_plan(_args: argparse.Namespace) -> int:
    engine = _engines()
    print("per-RID source rebuild of the ARM64 backend (replace, not prove-the-old-binary)")
    print(f"  engine : {engine['repo']}@{engine['commit']} ({engine['build_system']})")
    print(f"  archive: {engine['archive_path']}  sha256={engine['archive_sha256']}")
    print(f"  bridge : {NATIVE_INPUTS.relative_to(ROOT)}")
    for rid in SUPPORTED_RIDS:
        target = toolchain_record(rid)
        print(f"  {rid:<12} -> natives/{Path(target['output']).parent.name}/{RID_OUTPUT_NAME[rid]}"
              f"  via {target['compiler']}")
    for rid in EXCLUSION_RIDS:
        print(f"  {rid:<12} classes only: no source rebuild; every natives/* entry withheld "
              f"(Java-only package may ship)")
    print("  unicorn:1.0.14 -> keep classes (parent types), strip natives/* (GPL), record pinned source")
    print("  receipt -> <JavaHost>/native-backend-receipt.json, also archived inside the package")
    return 0


def _command_build(args: argparse.Namespace) -> int:
    out_root = Path(args.out).resolve() if args.out else REBUILD / "out"
    engine_cache = Path(args.engine_cache).resolve() if args.engine_cache \
        else REBUILD / ".cache"
    receipt = build(args.rid, Path(args.deps), engine_cache=engine_cache, out_root=out_root)
    problems = verify_receipt(receipt, deps=Path(args.deps))
    print(json.dumps(receipt, indent=2, ensure_ascii=False))
    if problems:
        for problem in problems:
            print(f"FAIL {problem}", file=sys.stderr)
        return 1
    # Default placement is beside the staged Java host, so the receipt travels with the package;
    # `--receipt` overrides it for a caller that wants the file elsewhere.
    destination = Path(args.receipt) if args.receipt else (Path(args.deps).resolve().parent
                                                           / "native-backend-receipt.json")
    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.write_text(json.dumps(receipt, indent=2, ensure_ascii=False), encoding="utf-8")
    print(f"receipt written: {destination}")
    return 0


def _command_verify(args: argparse.Namespace) -> int:
    receipt = json.loads(Path(args.receipt).read_text(encoding="utf-8"))
    problems = verify_receipt(receipt, deps=Path(args.deps) if args.deps else None)
    if problems:
        for problem in problems:
            print(f"FAIL {problem}", file=sys.stderr)
        return 1
    if receipt.get("backend", {}).get("excluded"):
        print(f"OK receipt for {receipt['rid']}: classes-only exclusion, every native withheld, "
              f"engine source recorded")
    else:
        print(f"OK receipt for {receipt['rid']}: exactly one native entry "
              f"({receipt['backend']['jar_integration']['native_entry']}), source + toolchain recorded")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(
        description="从固定源码为单个 RID 重建 ARM64 后端并生成可核验回执")
    subparsers = parser.add_subparsers(dest="command", required=True)

    subparsers.add_parser("plan", help="打印每个 RID 的引擎/bridge/toolchain 与产出位置").set_defaults(
        func=_command_plan)

    build_parser = subparsers.add_parser("build", help="为单个 RID 执行重建并改写 deps/ 中的 jar")
    build_parser.add_argument("--rid", required=True, choices=ALL_RIDS,
                              help="固定源码重建 RID；win-arm64 只产出仅类排除回执（无原生可重建）")
    build_parser.add_argument("--deps", required=True, help="已 stage 的 JavaHost/deps 目录")
    build_parser.add_argument("--engine-cache", default="", help="引擎源码/静态库缓存目录")
    build_parser.add_argument("--out", default="", help="重建产物的输出根目录（默认 rebuild/out）")
    build_parser.add_argument("--receipt", default="",
                              help="把回执写入该路径（默认 <deps 上级>/native-backend-receipt.json）")
    build_parser.set_defaults(func=_command_build)

    verify_parser = subparsers.add_parser("verify", help="核验一份回执（可选对照 deps/ 实测）")
    verify_parser.add_argument("--receipt", required=True)
    verify_parser.add_argument("--deps", default="", help="对照实测的 deps 目录（可选）")
    verify_parser.set_defaults(func=_command_verify)

    args = parser.parse_args()
    return args.func(args)


if __name__ == "__main__":
    sys.exit(main())
