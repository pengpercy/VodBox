"""Native packaging: Windows ZIP, macOS app/DMG, Linux deb/rpm/AppImage."""
import argparse
import hashlib
import os
from pathlib import Path
import plistlib
import re
import shutil
import subprocess
import tarfile
import tempfile
from bundle import download, ROOT
import json

def run(*args, **kwargs):
    subprocess.run([str(x) for x in args], check=True, **kwargs)

def package(rid, source, output, version):
    output.mkdir(parents=True, exist_ok=True)
    stem = f"VodBox_{version}.{rid}"
    if rid.startswith("win"):
        shutil.make_archive(str(output / stem), "zip", source)
    elif rid.startswith("osx"):
        app = output / rid / "VodBox.app"
        contents = app / "Contents"
        shutil.copytree(source, contents / "MacOS", dirs_exist_ok=True, symlinks=True)
        (contents / "Resources").mkdir(parents=True, exist_ok=True)
        with (contents / "Info.plist").open("wb") as file:
            plistlib.dump({"CFBundleName": "VodBox", "CFBundleDisplayName": "VodBox", "CFBundleIdentifier": "app.vodbox.desktop",
                          "CFBundleExecutable": "VodBox", "CFBundlePackageType": "APPL", "CFBundleShortVersionString": version,
                          "CFBundleVersion": version, "NSHighResolutionCapable": True, "LSMinimumSystemVersion": "12.0"}, file)
        identity = os.environ.get("CODESIGN_IDENTITY") or "-"
        macho = (b"\xcf\xfa\xed\xfe", b"\xce\xfa\xed\xfe", b"\xca\xfe\xba\xbe", b"\xbe\xba\xfe\xca")
        for file in (contents / "MacOS").rglob("*"):
            if file.is_file() and not file.is_symlink():
                with file.open("rb") as content:
                    native = content.read(4) in macho
                if native:
                    command = ["codesign", "--force", "--sign", identity]
                    if identity != "-": command += ["--options", "runtime", "--timestamp"]
                    run(*command, file)
        run("codesign", "--force", "--sign", identity, app)
        run("codesign", "--verify", "--deep", "--strict", app)
        run("ditto", "-c", "-k", "--sequesterRsrc", "--keepParent", app, output / (stem + ".zip"))
        with tempfile.TemporaryDirectory(prefix="vodbox-dmg-") as directory:
            stage = Path(directory)
            shutil.copytree(app, stage / "VodBox.app", symlinks=True)
            (stage / "Applications").symlink_to("/Applications")
            run("hdiutil", "create", "-volname", "VodBox", "-srcfolder", stage, "-format", "UDZO", "-ov", output / (stem + ".dmg"))
    else:
        with tempfile.TemporaryDirectory(prefix="vodbox-package-") as temp:
            stage = Path(temp)
            app = stage / "opt/vodbox"
            shutil.copytree(source, app, symlinks=True)
            bin_dir = stage / "usr/bin"; bin_dir.mkdir(parents=True)
            (bin_dir / "vodbox").write_text('#!/bin/sh\nexec /opt/vodbox/VodBox "$@"\n')
            (bin_dir / "vodbox").chmod(0o755)
            desktop_dir = stage / "usr/share/applications"; desktop_dir.mkdir(parents=True)
            desktop = "[Desktop Entry]\nType=Application\nName=VodBox\nExec=vodbox\nIcon=vodbox\nCategories=AudioVideo;Player;\nTerminal=false\n"
            (desktop_dir / "vodbox.desktop").write_text(desktop)
            icons = stage / "usr/share/icons/hicolor/scalable/apps"; icons.mkdir(parents=True)
            shutil.copy2(ROOT / "build/vodbox.svg", icons / "vodbox.svg")
            deb_arch = "arm64" if rid.endswith("arm64") else "amd64"
            control = stage / "DEBIAN"; control.mkdir()
            (control / "control").write_text(f"Package: vodbox\nVersion: {version}\nArchitecture: {deb_arch}\nMaintainer: VodBox contributors\nDepends: libc6 (>= 2.35), libx11-6, fontconfig\nDescription: Cross-platform media player and content provider host\n")
            run("dpkg-deb", "--build", "--root-owner-group", stage, output / (stem + ".deb"))
            shutil.rmtree(control)
            rpm_arch = "aarch64" if rid.endswith("arm64") else "x86_64"
            run("fpm", "-s", "dir", "-t", "rpm", "-n", "vodbox", "-v", version, "-a", rpm_arch,
                "--depends", "glibc >= 2.35", "--depends", "fontconfig", "--depends", "libX11",
                "--description", "Cross-platform media player", "--package", output / (stem + ".rpm"), "-C", stage, "opt", "usr")
            appdir = stage / "VodBox.AppDir"; appdir.mkdir()
            shutil.copytree(source, appdir / "usr/lib/vodbox", symlinks=True)
            (appdir / "AppRun").write_text('#!/bin/sh\nset -eu\nbase=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)\nexec "$base/usr/lib/vodbox/VodBox" "$@"\n')
            (appdir / "AppRun").chmod(0o755)
            (appdir / "vodbox.desktop").write_text(desktop.replace("Exec=vodbox", "Exec=AppRun"))
            shutil.copy2(ROOT / "build/vodbox.svg", appdir / "vodbox.svg")
            tools = json.loads((ROOT / "build/appimage-assets.json").read_text())[rpm_arch]
            tool = download(tools["tool"]); tool.chmod(0o755)
            runtime = download(tools["runtime"])
            run(tool, "--appimage-extract-and-run", "--runtime-file", runtime, appdir, output / (stem + ".AppImage"),
                env={**os.environ, "ARCH": rpm_arch, "APPIMAGE_EXTRACT_AND_RUN": "1"})
    for file in output.glob(stem + ".*"):
        with file.open("rb") as source:
            digest = hashlib.file_digest(source, "sha256").hexdigest()
        file.with_suffix(file.suffix + ".sha256").write_text(digest + "  " + file.name + "\n")

if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("rid"); parser.add_argument("source", type=Path); parser.add_argument("output", type=Path)
    parser.add_argument("--version", default=(ROOT / "VERSION").read_text().strip())
    args = parser.parse_args()
    if not re.fullmatch(r"\d+\.\d+\.\d+", args.version): parser.error("Version must have three numeric components")
    package(args.rid, args.source.resolve(), args.output.resolve(), args.version)
