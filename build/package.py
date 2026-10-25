"""Native packaging: Windows ZIP, macOS app/DMG, Linux deb/rpm/AppImage."""
import argparse
import os
import plistlib
import shutil
import subprocess
import sys
import tempfile
import time
from pathlib import Path

from bundle import verify_no_java_payload, verify_windows_closure
from nativeverify import verify_linux_closure

ROOT = Path(__file__).resolve().parent.parent


def run(*args, **kwargs):
    subprocess.run([str(x) for x in args], check=True, **kwargs)


MACOS_ARCH = {"osx-x64": "x86_64", "osx-arm64": "arm64"}
MACHO_THIN = (b"\xcf\xfa\xed\xfe", b"\xce\xfa\xed\xfe", b"\xfe\xed\xfa\xcf", b"\xfe\xed\xfa\xce")
MACHO_FAT = (b"\xca\xfe\xba\xbe", b"\xbe\xba\xfe\xca", b"\xca\xfe\xba\xbf", b"\xbf\xba\xfe\xca")


def macho_kind(file: Path) -> str | None:
    """"thin" or "fat" for a Mach-O image, None for anything else."""
    with file.open("rb") as content:
        header = content.read(8)
    if header[:4] in MACHO_THIN:
        return "thin"
    if header[:4] not in MACHO_FAT or len(header) < 8:
        return None
    # Java class files share CAFEBABE with fat Mach-O, but their version
    # field is not a plausible fat architecture count.
    byteorder = "big" if header[:4] in (MACHO_FAT[0], MACHO_FAT[2]) else "little"
    architectures = int.from_bytes(header[4:8], byteorder)
    entry_size = 32 if header[:4] in MACHO_FAT[2:] else 20
    if not 1 <= architectures <= 64 or file.stat().st_size < 8 + architectures * entry_size:
        return None
    return "fat"


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
    # License/provenance text is a bundle resource, never nested code in Contents/MacOS.
    for name in ("licenses", "media-dependencies.json"):
        text_resource = contents / "MacOS" / name
        if text_resource.exists():
            shutil.move(str(text_resource), resources / name)
    # Archive round-trips (tar/zip on macOS) can leave `.DS_Store`, AppleDouble `._*` or upstream
    # `.gitkeep` files behind. They are never runtime content and they break codesign, which walks
    # sibling code directories such as lib/ and requires every entry there to be signed code.
    for detritus in sorted(contents.rglob("*"), key=lambda item: len(item.parts), reverse=True):
        if "corresponding-source" in detritus.relative_to(contents).parts:
            continue
        if detritus.name.startswith("."):
            shutil.rmtree(detritus, ignore_errors=True) if detritus.is_dir() else detritus.unlink(missing_ok=True)
    # Keep libmpv and lib/ together: native dependencies use @loader_path/lib.
    shutil.copy2(ROOT / "src/VodBox.Desktop/Assets/Icons/vodbox.icns", resources / "vodbox.icns")
    # 界面只有简体中文：只声明 zh-Hans 一种本地化，AppKit 的打开/保存面板、标准菜单项才会跟着用中文，
    # 否则没有 .lproj 的包一律回退英文（与系统语言无关）。
    for lproj in (ROOT / "build/resources/macos").glob("*.lproj"):
        shutil.copytree(lproj, resources / lproj.name)
    with (contents / "Info.plist").open("wb") as file:
        plistlib.dump({
            "CFBundleName": "VodBox", "CFBundleDisplayName": "VodBox",
            "CFBundleIdentifier": "app.vodbox.desktop", "CFBundleExecutable": "VodBox.Desktop",
            "CFBundlePackageType": "APPL", "CFBundleShortVersionString": version,
            "CFBundleVersion": version, "CFBundleIconFile": "vodbox.icns",
            "NSHighResolutionCapable": True, "LSMinimumSystemVersion": "12.0",
            "CFBundleDevelopmentRegion": "zh_CN", "CFBundleLocalizations": ["zh-Hans"],
        }, file)
    # libmpv.dylib 也在 MacOS 下（MpvNative 候选路径包含 ../Resources）
    identity = os.environ.get("CODESIGN_IDENTITY") or "-"
    # Passing a bundle's main executable to codesign signs its containing bundle.
    # Leave those executables to the explicit bundle pass after all nested code.
    bundle_executables = {contents / "MacOS/VodBox.Desktop"}
    targets = []
    for file in contents.rglob("*"):
        relative = file.relative_to(contents)
        if "corresponding-source" in relative.parts:
            continue
        if relative.parts[0] != "MacOS":
            continue
        if file.is_symlink() or not file.is_file() or file.suffix == ".class":
            continue
        kind = macho_kind(file)
        if kind is None:
            continue
        # NuGet ships SkiaSharp, HarfBuzzSharp and AvaloniaNative as universal x86_64+arm64
        # dylibs; each RID package carries only its own slice, before anything is signed.
        if kind == "fat":
            run("lipo", file, "-thin", MACOS_ARCH[rid], "-output", file)
        if file not in bundle_executables:
            targets.append(file)
    # Sign standalone code first, then seal the outer app.
    ordered_targets = sorted(targets, key=lambda item: (-len(item.parts), str(item)))
    for target in ordered_targets + [app]:
        command = ["codesign", "--force", "--sign", identity]
        if identity != "-":
            command += ["--options", "runtime", "--timestamp"]
        run(*command, target)
    run("codesign", "--verify", "--deep", "--strict", app)
    run("ditto", "-c", "-k", "--sequesterRsrc", "--keepParent", app, output / (stem + ".zip"))
    with tempfile.TemporaryDirectory(prefix="vodbox-dmg-",dir=output) as directory:
        stage = Path(directory)
        shutil.copytree(app, stage / "VodBox.app", symlinks=True)
        (stage / "Applications").symlink_to("/Applications")
        # ULMO (LZMA) needs macOS 10.15+; the app already requires 12.0.
        # hdiutil intermittently fails with "Resource busy" on CI runners; a retry clears it.
        for attempt in range(1, 4):
            try:
                run("hdiutil", "create", "-volname", "VodBox", "-srcfolder", stage,
                    "-format", "ULMO", "-ov", output / (stem + ".dmg"))
                break
            except subprocess.CalledProcessError:
                if attempt == 3:
                    raise
                print(f"hdiutil create failed, retrying in 5 seconds ({attempt}/3)", flush=True)
                time.sleep(5)


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
            "[Desktop Entry]\nType=Application\nName=VodBox\nExec=vodbox\nIcon=vodbox\nStartupWMClass=vodbox\n"
            "Categories=AudioVideo;Player;\nTerminal=false\n")
        for size in (16, 24, 32, 48, 64, 128, 256, 512, 1024):
            icons = stage / f"usr/share/icons/hicolor/{size}x{size}/apps"
            icons.mkdir(parents=True)
            shutil.copy2(ROOT / f"src/VodBox.Desktop/Assets/Icons/linux/{size}/vodbox.png",
                         icons / "vodbox.png")
        output.mkdir(parents=True, exist_ok=True)
        run("fpm", "-t", "deb", "-s", "dir", "-C", stage, "-n", "vodbox", "-v", version,
            "--architecture", ("amd64" if rid == "linux-x64" else "arm64"), "--deb-compression", "xz",
            "-p", output / f"{stem}.deb", "--description", "VodBox 跨平台影音应用")
        run("fpm", "-t", "rpm", "-s", "dir", "-C", stage, "-n", "vodbox", "-v", version,
            "--architecture", ("x86_64" if rid == "linux-x64" else "aarch64"), "--rpm-compression", "xz",
            "-p", output / f"{stem}.rpm", "--description", "VodBox 跨平台影音应用")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("rid")
    parser.add_argument("source", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--version", required=True)
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    verify_no_java_payload(args.source)
    if args.rid.startswith("win"):
        verify_windows_closure(args.source, args.rid)
        stem = f"VodBox_{args.version}.{args.rid}"
        shutil.make_archive(str(args.output / stem), "zip", args.source)
    elif args.rid.startswith("osx"):
        package_macos(args.rid, args.source, args.output, args.version)
    else:
        package_linux(args.rid, args.source, args.output, args.version)
    verify_no_java_payload(args.output)
    print(f"packaged {args.rid}")


if __name__ == "__main__":
    main()
