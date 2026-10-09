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
        shutil.copytree(libs_dir, libs, dirs_exist_ok=True)
        for dylib in libs.glob("*.dylib"):
            for line in subprocess.run(["otool", "-L", dylib], capture_output=True, text=True).stdout.splitlines():
                if "@executable_path" in line:
                    dep = line.strip().split(" ")[0]
                    name = dep.rsplit("/", 1)[1]
                    run("install_name_tool", "-change", dep, f"@loader_path/{name}", dylib)
            run("codesign", "--force", "--sign", "-", dylib)


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
