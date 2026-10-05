"""Native packaging: Windows ZIP, macOS app/DMG, Linux deb/rpm/AppImage."""
import argparse
import hashlib
import os
from pathlib import Path
import plistlib
import re
import shutil
import subprocess
import sys
import tarfile
import tempfile
from bundle import download, ROOT
import json

def run(*args, **kwargs):
    subprocess.run([str(x) for x in args], check=True, **kwargs)

def package(rid, source, output, version):
    output.mkdir(parents=True, exist_ok=True)
    verify_manifest(source / "native-manifest.json", source)
    stem = f"VodBox_{version}.{rid}"
    if rid.startswith("win"):
        shutil.make_archive(str(output / stem), "zip", source)
    elif rid.startswith("osx"):
        app = output / rid / "VodBox.app"
        contents = app / "Contents"
        if app.exists(): shutil.rmtree(app)
        shutil.copytree(source, contents / "MacOS", symlinks=True, ignore=shutil.ignore_patterns("*.pdb", "*.dbg", "*.dSYM"))
        resources = contents / "Resources/vodbox"; resources.mkdir(parents=True)
        for name in ("examples", "plugins", "runtimes", "licenses", "native-manifest.json"):
            original = contents / "MacOS" / name
            if original.exists(): shutil.move(str(original), resources / name)
        shutil.move(str(contents / "MacOS/native"), resources / "native")
        shutil.copy2(ROOT / "src/VodBox.Desktop/Assets/Icons/vodbox.icns", contents / "Resources/vodbox.icns")
        (contents / "Helpers").mkdir()
        shutil.move(str(contents / "MacOS/plugin-host"), contents / "Helpers/plugin-host")
        with (contents / "Info.plist").open("wb") as file:
            plistlib.dump({"CFBundleName": "VodBox", "CFBundleDisplayName": "VodBox", "CFBundleIdentifier": "app.vodbox.desktop",
                          "CFBundleExecutable": "VodBox", "CFBundlePackageType": "APPL", "CFBundleShortVersionString": version,
                          "CFBundleVersion": version, "CFBundleIconFile": "vodbox.icns", "NSHighResolutionCapable": True, "LSMinimumSystemVersion": "12.0"}, file)
        identity = os.environ.get("CODESIGN_IDENTITY") or "-"
        macho = (b"\xcf\xfa\xed\xfe", b"\xce\xfa\xed\xfe", b"\xca\xfe\xba\xbe", b"\xbe\xba\xfe\xca")
        for file in sorted(contents.rglob("*"), key=lambda x: len(x.parts), reverse=True):
            if file == contents / "MacOS/VodBox": continue
            if file.is_file() and not file.is_symlink():
                with file.open("rb") as content:
                    native = content.read(4) in macho
                if native:
                    command = ["codesign", "--force", "--sign", identity]
                    if identity != "-": command += ["--options", "runtime", "--timestamp"]
                    run(*command, file)
        # Signing changes plugin sizes/mtimes. Rebuild VLC's cache before sealing the app.
        run(contents / "MacOS/VodBox", "--diagnostics", "--native", "--rebuild-vlc-cache", timeout=300)
        refresh_manifest(resources, contents)
        command = ["codesign", "--force", "--sign", identity]
        if identity != "-": command += ["--options", "runtime", "--timestamp"]
        run(*command, app)
        run("codesign", "--verify", "--deep", "--strict", app)
        verify_manifest(resources / "native-manifest.json", contents)
        run(sys.executable, ROOT / "build/smoke.py", rid, contents / "MacOS")
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
            for size in (16, 24, 32, 48, 64, 128, 256, 512, 1024):
                icons = stage / f"usr/share/icons/hicolor/{size}x{size}/apps"; icons.mkdir(parents=True)
                shutil.copy2(ROOT / f"src/VodBox.Desktop/Assets/Icons/linux/{size}/vodbox.png", icons / "vodbox.png")
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
            shutil.copy2(ROOT / "src/VodBox.Desktop/Assets/Icons/linux/256/vodbox.png", appdir / "vodbox.png")
            tools = json.loads((ROOT / "build/appimage-assets.json").read_text())[rpm_arch]
            tool = download(tools["tool"]); tool.chmod(0o755)
            runtime = download(tools["runtime"])
            run(tool, "--appimage-extract-and-run", "--runtime-file", runtime, appdir, output / (stem + ".AppImage"),
                env={**os.environ, "ARCH": rpm_arch, "APPIMAGE_EXTRACT_AND_RUN": "1"})
    for file in output.glob(stem + ".*"):
        with file.open("rb") as source:
            digest = hashlib.file_digest(source, "sha256").hexdigest()
        file.with_suffix(file.suffix + ".sha256").write_text(digest + "  " + file.name + "\n")

def refresh_manifest(directory, layout_root=None):
    path = directory / "native-manifest.json"
    manifest = json.loads(path.read_text())
    files = {}
    root = layout_root or directory
    candidates = root.rglob("*") if layout_root else (directory / relative for relative in manifest["files"])
    for file in candidates:
        # The outer bundle seal signs the main executable after this resource is written.
        # codesign verifies that executable; the manifest verifies stable dependency files.
        if file == path or (layout_root and file == root / "MacOS/VodBox") or not file.is_file() or file.is_symlink(): continue
        relative = file.relative_to(root).as_posix()
        if file.exists():
            with file.open("rb") as source: files[relative] = hashlib.file_digest(source, "sha256").hexdigest()
    manifest["files"] = files
    if layout_root: manifest["layout"] = "macos-contents"
    path.write_text(json.dumps(manifest, indent=2) + "\n")

def verify_manifest(path, root):
    manifest = json.loads(path.read_text())
    for relative, expected in manifest["files"].items():
        file = root / relative
        with file.open("rb") as source:
            actual = hashlib.file_digest(source, "sha256").hexdigest()
        if actual != expected: raise ValueError(f"Bundled dependency SHA256 mismatch: {relative}")
    print(f"Dependency manifest: OK ({len(manifest['files'])} files)", flush=True)

if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("rid"); parser.add_argument("source", type=Path); parser.add_argument("output", type=Path)
    parser.add_argument("--version", default=(ROOT / "VERSION").read_text().strip())
    args = parser.parse_args()
    if not re.fullmatch(r"\d+\.\d+\.\d+", args.version): parser.error("Version must have three numeric components")
    package(args.rid, args.source.resolve(), args.output.resolve(), args.version)
