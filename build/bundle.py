"""Bundle pinned libmpv runtime next to the published app. Run with the target RID on its native OS.

macOS: mpv.app 主二进制重写为 libmpv.dylib（install_name_tool 把 @executable_path 依赖改为
@loader_path，逐 dylib 重签），随包分发。
Windows: mpv-2.dll / mpv.exe 直接复制进发布目录。
Linux: 由发行版包依赖系统 libmpv（不在 app 内捆绑），此处跳过。
"""
import argparse
import hashlib
import json
import os
import shutil
import subprocess
import tarfile
import tempfile
import urllib.request
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
ASSETS = json.loads((ROOT / "build" / "native-assets.json").read_text())


def download(rid: str) -> Path:
    asset = ASSETS["runtimes"][rid]
    cache = ROOT / ".cache" / "downloads"
    cache.mkdir(parents=True, exist_ok=True)
    path = cache / asset["url"].rsplit("/", 1)[1]
    if not path.exists():
        partial = path.with_suffix(path.suffix + ".partial")
        with urllib.request.urlopen(asset["url"], timeout=120) as response, partial.open("wb") as output:
            shutil.copyfileobj(response, output)
        partial.replace(path)
    with path.open("rb") as cached:
        digest = hashlib.file_digest(cached, "sha256").hexdigest()
    if digest != asset["sha256"]:
        raise ValueError(f"SHA256 mismatch: {path.name}")
    return path


def run(*args):
    subprocess.run([str(x) for x in args], check=True)


def copy_libraries(libs_dir: Path, libs: Path) -> list[Path]:
    """Copy the pinned dylibs into `libs`, preserving aliases without duplicating payloads.

    Each entry is keyed by resolved inode, so if an upstream build ever ships version symlinks
    (`libfoo.dylib -> libfoo.1.dylib`) the alias is recreated as a symlink instead of a second
    full copy. The pinned mpv.app currently ships 97 distinct real files and no symlinks, so this
    is behaviour-identical today and only guards the duplicate-bytes regression.
    """
    libs.mkdir(parents=True, exist_ok=True)
    copied: dict[tuple[int, int], str] = {}
    written: list[Path] = []
    for dylib in sorted(libs_dir.glob("*.dylib")):
        stat = dylib.resolve().stat()
        canonical = copied.get((stat.st_dev, stat.st_ino))
        if canonical is None:
            shutil.copy2(dylib, libs / dylib.name, follow_symlinks=True)
            copied[(stat.st_dev, stat.st_ino)] = dylib.name
            written.append(libs / dylib.name)
        elif canonical != dylib.name:
            alias = libs / dylib.name
            alias.unlink(missing_ok=True)
            alias.symlink_to(canonical)
    return written


def missing_dependencies(lib: Path, libs: Path, seen: set[str] | None = None) -> list[str]:
    """Names of `@loader_path` dependencies reachable from `lib` that are absent from `libs`."""
    seen = set() if seen is None else seen
    missing: list[str] = []
    # `check=True`: without it a failing otool yields empty stdout, which reads as a clean,
    # fully-resolved closure and silently hides a missing-dependency regression.
    listing = subprocess.run(["otool", "-L", str(lib)], capture_output=True, text=True, check=True).stdout
    for line in listing.splitlines()[1:]:
        text = line.strip()
        if not text:
            continue
        dep = text.split(" ")[0]
        if "@loader_path/" not in dep:
            continue
        name = dep.rsplit("/", 1)[1]
        if name in seen:
            continue
        seen.add(name)
        target = libs / name
        if target.exists():
            missing += missing_dependencies(target, libs, seen)
        else:
            missing.append(name)
    return missing


def bundle_macos(archive: Path, output: Path):
    with tempfile.TemporaryDirectory(prefix="vodbox-mpv-", dir=ROOT / ".cache") as temp:
        with zipfile.ZipFile(archive) as source:
            source.extractall(temp)
        inner = Path(temp) / "mpv.tar.gz"
        with tarfile.open(inner) as source:
            source.extractall(temp)
        app = Path(temp) / "mpv.app"
        binary = app / "Contents" / "MacOS" / "mpv"
        libs_dir = app / "Contents" / "MacOS" / "lib"
        target = output / "libmpv.dylib"
        shutil.copy2(binary, target)
        # 主库依赖：@executable_path/lib/xxx → @loader_path/lib/xxx
        for line in subprocess.run(["otool", "-L", target], capture_output=True, text=True).stdout.splitlines():
            if "@executable_path/lib/" in line:
                dep = line.strip().split(" ")[0]
                name = dep.rsplit("/", 1)[1]
                run("install_name_tool", "-change", dep, f"@loader_path/lib/{name}", target)
        run("codesign", "--force", "--sign", "-", target)
        # lib/ 内部互相依赖 → @loader_path
        libs = output / "lib"
        # The upstream mpv.app ships stray dotfiles in lib/ (`.gitkeep`, AppleDouble `._*`).
        # Copying only real dylibs keeps the bundle signable: codesign descends into a sibling
        # `lib/` directory and rejects anything there that is not signed code.
        for dylib in copy_libraries(libs_dir, libs):
            for line in subprocess.run(["otool", "-L", dylib], capture_output=True, text=True).stdout.splitlines():
                if "@executable_path" in line:
                    dep = line.strip().split(" ")[0]
                    name = dep.rsplit("/", 1)[1]
                    run("install_name_tool", "-change", dep, f"@loader_path/{name}", dylib)
            run("codesign", "--force", "--sign", "-", dylib)
        # libmpv is useless if any transitive @loader_path dependency is missing. The asset is
        # pinned by SHA256, so a non-empty result means the copy/closure logic itself regressed.
        broken = missing_dependencies(target, libs)
        if broken:
            raise ValueError(f"bundled libmpv is missing dependencies: {', '.join(sorted(set(broken)))}")


def bundle_windows(archive: Path | None, output: Path):
    """Official mpv Windows archives ship mpv.exe (statically linked) and no libmpv DLL.

    The only libmpv DLL builds are rolling third-party dev packages whose pinned URLs expire,
    so Windows gets the same contract as Linux: libmpv-2.dll must be supplied at runtime
    (`VODBOX_MPV_LIB`, next to the executable, or on PATH). Failing the release here would
    only hide that requirement.
    """
    if archive is not None:
        for candidate in Path(archive).parent.rglob("*.dll"):
            if "mpv" not in candidate.name.lower():
                continue
            shutil.copy2(candidate, output / candidate.name)
            print(f"bundled {candidate.name} for win")
            return
    print("win: no libmpv DLL in the official archive; the app resolves libmpv-2.dll at runtime")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("rid")
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    valid={"osx-x64","osx-arm64","win-x64","win-arm64","linux-x64","linux-arm64"}
    if args.rid not in valid: parser.error("unsupported runtime identifier")
    args.output.mkdir(parents=True, exist_ok=True)
    if args.rid.startswith("linux-"):
        print(f"{args.rid}: Linux 使用系统 libmpv，不捆绑")
        return
    if args.rid.startswith("win-"):
        bundle_windows(None, args.output)
        return
    archive = download(args.rid)
    if args.rid.startswith("osx"):
        bundle_macos(archive, args.output)
        print(f"bundled libmpv.dylib + lib/ for {args.rid}")
    elif args.rid.startswith("win"):
        bundle_windows(archive, args.output)
        print(f"bundled mpv dlls for {args.rid}")
    else:
        print(f"{args.rid}: Linux 使用系统 libmpv，不捆绑")


if __name__ == "__main__":
    main()
