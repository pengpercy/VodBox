"""Native packaging: Windows ZIP, macOS app/DMG, Linux deb/rpm/AppImage."""
import argparse
import os
import plistlib
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

from bundle import verify_windows_closure
from nativeverify import verify_linux_closure

ROOT = Path(__file__).resolve().parent.parent


def run(*args, **kwargs):
    subprocess.run([str(x) for x in args], check=True, **kwargs)


def package_macos(rid, source: Path, output: Path, version: str):
    stem = f"VodBox_{version}.{rid}"
    app = output / rid / "VodBox.app"
    if app.exists():
        shutil.rmtree(app)
    contents = app / "Contents"
    # Debug payloads never ship in the app: `.dSYM` alone is >100 MB of DWARF the runtime never
    # loads. archive.py already diverts them into the `symbols.<rid>` artifact; ignoring them
    # here too keeps a direct `package.py` run on a publish tree from re-shipping them.
    shutil.copytree(source, contents / "MacOS", symlinks=True,
                    ignore=shutil.ignore_patterns("*.pdb", "*.dbg", "*.dSYM"))
    resources = contents / "Resources"
    resources.mkdir(parents=True)
    assets = contents / "MacOS" / "Assets"
    if assets.is_dir():
        shutil.move(str(assets), resources / "Assets")
    # Archive round-trips (tar/zip on macOS) can leave `.DS_Store`, AppleDouble `._*` or upstream
    # `.gitkeep` files behind. They are never runtime content and they break codesign, which walks
    # sibling code directories such as lib/ and requires every entry there to be signed code.
    for detritus in sorted(contents.rglob("*"), key=lambda item: len(item.parts), reverse=True):
        if detritus.name.startswith("."):
            shutil.rmtree(detritus, ignore_errors=True) if detritus.is_dir() else detritus.unlink(missing_ok=True)
    # Keep libmpv and lib/ together: native dependencies use @loader_path/lib.
    shutil.copy2(ROOT / "src/VodBox.Desktop/Assets/Icons/vodbox.icns", resources / "vodbox.icns")
    with (contents / "Info.plist").open("wb") as file:
        plistlib.dump({
            "CFBundleName": "VodBox", "CFBundleDisplayName": "VodBox",
            "CFBundleIdentifier": "app.vodbox.desktop", "CFBundleExecutable": "VodBox.Desktop",
            "CFBundlePackageType": "APPL", "CFBundleShortVersionString": version,
            "CFBundleVersion": version, "CFBundleIconFile": "vodbox.icns",
            "NSHighResolutionCapable": True, "LSMinimumSystemVersion": "12.0",
        }, file)
    # libmpv.dylib 也在 MacOS 下（MpvNative 候选路径包含 ../Resources）
    identity = os.environ.get("CODESIGN_IDENTITY") or "-"
    macho = (b"\xcf\xfa\xed\xfe", b"\xce\xfa\xed\xfe", b"\xca\xfe\xba\xbe", b"\xbe\xba\xfe\xca")
    for file in sorted(contents.rglob("*"), key=lambda x: len(x.parts), reverse=True):
        if file.is_file() and not file.is_symlink():
            with file.open("rb") as content:
                if content.read(4) in macho:
                    command = ["codesign", "--force", "--sign", identity]
                    if identity != "-":
                        command += ["--options", "runtime", "--timestamp"]
                    run(*command, file)
    run("codesign", "--force", "--sign", identity, app)
    run("codesign", "--verify", "--deep", "--strict", app)
    run("ditto", "-c", "-k", "--sequesterRsrc", "--keepParent", app, output / (stem + ".zip"))
    with tempfile.TemporaryDirectory(prefix="vodbox-dmg-",dir=output) as directory:
        stage = Path(directory)
        shutil.copytree(app, stage / "VodBox.app", symlinks=True)
        (stage / "Applications").symlink_to("/Applications")
        run("hdiutil", "create", "-volname", "VodBox", "-srcfolder", stage,
            "-format", "UDZO", "-ov", output / (stem + ".dmg"))


def package_linux(rid, source: Path, output: Path, version: str):
    verify_linux_closure(source / "lib", rid)
    stem = f"vodbox_{version}_{rid}"
    output.mkdir(parents=True,exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="vodbox-pkg-",dir=output) as directory:
        stage = Path(directory)
        app = stage / "opt/vodbox"
        shutil.copytree(source, app, symlinks=True)
        (stage / "usr/bin").mkdir(parents=True)
        launcher = stage / "usr/bin/vodbox"
        launcher.write_text("#!/bin/sh\nexport LD_LIBRARY_PATH=\"/opt/vodbox/lib:/opt/vodbox${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}\"\nexec /opt/vodbox/VodBox.Desktop \"$@\"\n")
        launcher.chmod(0o755)
        apps = stage / "usr/share/applications"
        apps.mkdir(parents=True)
        (apps / "vodbox.desktop").write_text(
            "[Desktop Entry]\nType=Application\nName=VodBox\nExec=vodbox\nIcon=vodbox\n"
            "Categories=AudioVideo;Player;\nTerminal=false\n")
        for size in (16, 24, 32, 48, 64, 128, 256, 512, 1024):
            icons = stage / f"usr/share/icons/hicolor/{size}x{size}/apps"
            icons.mkdir(parents=True)
            shutil.copy2(ROOT / f"src/VodBox.Desktop/Assets/Icons/linux/{size}/vodbox.png",
                         icons / "vodbox.png")
        output.mkdir(parents=True, exist_ok=True)
        run("fpm", "-t", "deb", "-s", "dir", "-C", stage, "-n", "vodbox", "-v", version,
            "--architecture", ("amd64" if rid == "linux-x64" else "arm64"),
            "-p", output / f"{stem}.deb", "--description", "VodBox 跨平台影音应用")
        run("fpm", "-t", "rpm", "-s", "dir", "-C", stage, "-n", "vodbox", "-v", version,
            "--architecture", ("x86_64" if rid == "linux-x64" else "aarch64"),
            "-p", output / f"{stem}.rpm", "--description", "VodBox 跨平台影音应用")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("rid")
    parser.add_argument("source", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--version", required=True)
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    if args.rid.startswith("win"):
        verify_windows_closure(args.source, args.rid)
        stem = f"VodBox_{args.version}.{args.rid}"
        shutil.make_archive(str(args.output / stem), "zip", args.source)
    elif args.rid.startswith("osx"):
        package_macos(args.rid, args.source, args.output, args.version)
    else:
        package_linux(args.rid, args.source, args.output, args.version)
    print(f"packaged {args.rid}")


if __name__ == "__main__":
    main()
