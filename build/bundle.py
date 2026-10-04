"""Bundle pinned external runtimes. Run with the target RID on its native OS."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import shutil
import subprocess
import tarfile
import tempfile
import urllib.request
import zipfile

ROOT = Path(__file__).resolve().parent.parent
ASSETS = json.loads((ROOT / "build/native-assets.json").read_text())

def run(*args):
    subprocess.run([str(x) for x in args], check=True)

def download(asset):
    cache = ROOT / ".cache/downloads"
    cache.mkdir(parents=True, exist_ok=True)
    path = cache / asset["url"].rsplit("/", 1)[1]
    if not path.exists():
        partial = path.with_suffix(path.suffix + ".partial")
        with urllib.request.urlopen(asset["url"], timeout=60) as response, partial.open("wb") as output:
            shutil.copyfileobj(response, output)
        partial.replace(path)
    with path.open("rb") as source:
        digest = hashlib.file_digest(source, "sha256").hexdigest()
    if digest != asset["sha256"]:
        raise ValueError(f"SHA256 mismatch: {path.name}")
    return path

def unpack(archive, destination):
    if archive.suffix == ".zip":
        with zipfile.ZipFile(archive) as source:
            source.extractall(destination)
    else:
        with tarfile.open(archive) as source:
            source.extractall(destination, filter="data")

def bundle(rid, output):
    asset = ASSETS["runtimes"][rid]
    output.mkdir(parents=True, exist_ok=True)
    for runtime in ("python", "node"):
        with tempfile.TemporaryDirectory(prefix="vodbox-runtime-") as temp:
            unpack(download(asset[runtime]), Path(temp))
            roots = [x for x in Path(temp).iterdir() if x.is_dir()]
            if len(roots) != 1:
                raise ValueError(f"Unexpected {runtime} archive layout")
            target = output / "runtimes" / runtime
            if target.exists(): shutil.rmtree(target)
            if runtime == "node":
                # Providers need Node itself, not npm, development headers or documentation.
                binary = "node.exe" if rid.startswith("win") else "bin/node"
                destination = target / binary
                destination.parent.mkdir(parents=True, exist_ok=True)
                shutil.copy2(roots[0] / binary, destination)
                for notice in roots[0].glob("LICENSE*"):
                    shutil.copy2(notice, target / notice.name)
            else:
                shutil.copytree(roots[0], target, symlinks=True)
    if "vlc" in asset:
        archive = download(asset["vlc"])
        if rid.startswith("osx"):
            with tempfile.TemporaryDirectory(prefix="vodbox-vlc-") as mount:
                run("hdiutil", "attach", archive, "-nobrowse", "-readonly", "-mountpoint", mount)
                try:
                    source = Path(mount) / "VLC.app/Contents/MacOS"
                    for directory in ("lib", "plugins"):
                        # copy2 preserves mtimes: VLC's module cache validates them on startup.
                        target = output / "native/vlc" / directory
                        if target.exists(): shutil.rmtree(target)
                        shutil.copytree(source / directory, target, symlinks=True)
                    notices = output / "licenses/vlc"
                    notices.mkdir(parents=True, exist_ok=True)
                    resources = Path(mount) / "VLC.app/Contents/Resources"
                    for file in resources.rglob("*"):
                        if file.is_file() and any(term in file.name.lower() for term in ("copying", "license", "authors")):
                            shutil.copy2(file, notices / file.name)
                finally:
                    run("hdiutil", "detach", mount)
        else:
            with tempfile.TemporaryDirectory(prefix="vodbox-vlc-") as temp:
                run("7z", "x", archive, "-o" + temp, "-y")
                source = next(Path(temp).glob("vlc-*"))
                target = output / "native/vlc"
                target.mkdir(parents=True, exist_ok=True)
                for file in source.glob("*.dll"):
                    shutil.copy2(file, target / file.name)
                shutil.copytree(source / "plugins", target / "plugins", dirs_exist_ok=True)
                for file in source.glob("COPYING*"):
                    (output / "licenses/vlc").mkdir(parents=True, exist_ok=True)
                    shutil.copy2(file, output / "licenses/vlc" / file.name)
    else:
        bundle_linux(output)
    native_build = ROOT / "artifacts/quickjs"
    suffix = ".dll" if rid.startswith("win") else ".dylib" if rid.startswith("osx") else ".so"
    library = next(native_build.rglob("*vodbox_quickjs" + suffix))
    (output / "plugin-host").mkdir(exist_ok=True)
    shutil.copy2(library, output / "plugin-host" / library.name)
    quickjs_license = native_build / "_deps/quickjs-src/LICENSE"
    notices = output / "licenses/quickjs"
    notices.mkdir(parents=True, exist_ok=True)
    shutil.copy2(quickjs_license, notices / "LICENSE")
    for directory in ("examples", "plugins"):
        shutil.copytree(ROOT / directory, output / directory, dirs_exist_ok=True,
                        ignore=shutil.ignore_patterns("__pycache__", "*.pyc"))
    manifest = {"rid": rid, "python": ASSETS["pythonVersion"], "node": ASSETS["nodeVersion"],
                "vlc": ASSETS["vlcVersion"] if "vlc" in asset else "system 3.x", "assets": asset,
                "files": {}}
    for file in output.rglob("*"):
        if file.is_file() and not file.is_symlink() and any(x in file.parts for x in ("native", "plugin-host", "runtimes")):
            with file.open("rb") as content:
                manifest["files"][file.relative_to(output).as_posix()] = hashlib.file_digest(content, "sha256").hexdigest()
    (output / "native-manifest.json").write_text(json.dumps(manifest, indent=2) + "\n")

def bundle_linux(output):
    triplet = "aarch64-linux-gnu" if platform.machine().lower() in ("aarch64", "arm64") else "x86_64-linux-gnu"
    system_lib = Path("/usr/lib") / triplet
    target = output / "native/vlc/lib"
    target.mkdir(parents=True, exist_ok=True)
    plugins = system_lib / "vlc/plugins"
    if not plugins.exists():
        raise FileNotFoundError("Install libvlc-dev, vlc-plugin-base and vlc-plugin-video-output first")
    shutil.copytree(plugins, output / "native/vlc/plugins", dirs_exist_ok=True, symlinks=True)
    for pattern in ("libvlc.so*", "libvlccore.so*", "libicu*.so*"):
        for file in system_lib.glob(pattern):
            canonical = file.resolve()
            destination = target / canonical.name
            if not destination.exists(): shutil.copy2(canonical, destination)
            if file.name != canonical.name:
                alias = target / file.name
                alias.unlink(missing_ok=True)
                alias.symlink_to(canonical.name)
    # Resolve the closure against the target runner. Keep the platform's glibc/loader and GPU drivers.
    excluded = ("ld-linux", "libc.so", "libm.so", "libpthread.so", "libdl.so", "librt.so", "libresolv.so",
                "libnss_", "libGLX_mesa", "libEGL_mesa", "libvulkan", "libdrm", "libgbm")
    queue = [p for p in output.rglob("*") if p.is_file() and (".so" in p.name or p.name in ("VodBox", "node", "python3", "VodBox.PluginHost"))]
    visited = set()
    while queue:
        binary = queue.pop()
        if str(binary) in visited:
            continue
        visited.add(str(binary))
        result = subprocess.run(["ldd", str(binary)], capture_output=True, text=True)
        for line in result.stdout.splitlines():
            if "=> not found" in line:
                raise RuntimeError(f"Unresolved dependency in {binary}: {line}")
            if "=> /" not in line:
                continue
            dependency = Path(line.split("=>", 1)[1].strip().split()[0])
            if dependency.name.startswith(excluded):
                continue
            copied = target / dependency.name
            if not copied.exists():
                shutil.copy2(dependency, copied)
                queue.append(copied)
    executable = output / "VodBox"
    if executable.exists() and not (output / "VodBox.bin").exists():
        executable.rename(output / "VodBox.bin")
        executable.write_text('#!/bin/sh\nset -eu\nbase=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)\nexport LD_LIBRARY_PATH="$base/native/vlc/lib${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"\nexport VLC_PLUGIN_PATH="$base/native/vlc/plugins"\nexec "$base/VodBox.bin" "$@"\n')
        executable.chmod(0o755)
    (output / "LINUX-DEPENDENCIES.txt").write_text("Built on Ubuntu 22.04: glibc >= 2.35, desktop compositor/display server, system fonts and working GPU drivers required.\n")

if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("rid", choices=ASSETS["runtimes"])
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    system = {"Darwin": "osx", "Windows": "win", "Linux": "linux"}[platform.system()]
    arch = "arm64" if platform.machine().lower() in ("arm64", "aarch64") else "x64"
    if args.rid != f"{system}-{arch}":
        parser.error("Run bundling on the native target runner; do not mix architectures.")
    bundle(args.rid, args.output.resolve())
