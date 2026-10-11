"""Bundle pinned libmpv runtime next to the published app. Run with the target RID on its native OS.

macOS: mpv.app 主二进制重写为 libmpv.dylib（install_name_tool 把 @executable_path 依赖改为
@loader_path，逐 dylib 重签），随包分发。
Windows: 静态媒体库 libmpv-2.dll + Vulkan loader，一起随包分发。
Linux: 固定 libmpv/FFmpeg 构建 + 完整非 glibc 动态依赖闭包，随包分发。

另把 JVM 插件宿主打进 `<output>/JavaHost/`：`host.jar`（架构无关的 Java 字节码，本机 JDK 编译即可）
+ `dex-tools/`、`sqlite-jdbc/`、`runtime/`（固定版本 + sha256） + **该 RID 自己的** Temurin JRE。
JRE 绝不取构建机自带的 `java`：跨编译时那是构建机的架构，装到目标上跑不起来（`bundle_java_host`
用 verify_jre 按 RID 校验 PE/ELF/Mach-O 机器类型）。`--skip-java-host` 只打媒体内核。
"""
import argparse
import hashlib
import json
import os
import re
import platform
import shutil
import subprocess
import sys
import tarfile
import tempfile
import urllib.request
import zipfile
from pathlib import Path

from nativeverify import verify_linux_closure, verify_windows_dll, pe_imports, verify_jre, NativeVerifyError, LINUX_BASE
from native_coverage import backend_supports, describe, verify_deps, NativeCoverageError

ROOT = Path(__file__).resolve().parent.parent
ASSETS = json.loads((ROOT / "build" / "native-assets.json").read_text(encoding="utf-8"))


def download(rid: str) -> Path:
    return fetch_asset(ASSETS["runtimes"][rid])


def fetch_asset(asset: dict) -> Path:
    cache = ROOT / ".cache" / "downloads"
    cache.mkdir(parents=True, exist_ok=True)
    path = cache / (asset["sha256"][:12] + "-" + asset["url"].rsplit("/", 1)[1])
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


WINDOWS_SYSTEM_DLLS = frozenset("fontsub dxgi dxcore d3d9 d3d11 d3d12 d3dcompiler_47 msvcrt ucrtbase wintrust normaliz ncrypt kernelbase advapi32 avicap32 avrt bcrypt cfgmgr32 combase crypt32 d2d1 dbghelp dwrite dwmapi gdi32 imm32 iphlpapi kernel32 mpr msimg32 ntdll ole32 oleaut32 opengl32 powrprof propsys psapi rpcrt4 secur32 setupapi shell32 shcore shlwapi user32 userenv usp10 uxtheme version winhttp wininet winmm winspool ws2_32 wtsapi32".split())


def verify_windows_closure(directory: Path, rid: str):
    dlls = {p.name.lower(): p for p in directory.glob("*.dll")}
    if "libmpv-2.dll" not in dlls or "vulkan-1.dll" not in dlls:
        raise NativeVerifyError("Windows bundle must include both libmpv-2.dll and vulkan-1.dll")
    for name, path in dlls.items():
        verify_windows_dll(path, rid)
        for dep in pe_imports(path):
            if dep in dlls or dep.removesuffix(".dll") in WINDOWS_SYSTEM_DLLS or dep.startswith(("api-ms-win-", "ext-ms-win-")):
                continue
            raise NativeVerifyError(f"{name}: unbundled Windows dependency {dep}")


def unpack_tar(archive: Path, destination: Path):
    # Used only for pinned, hash-verified upstream source archives.
    with tarfile.open(archive) as source:
        for member in source.getmembers():
            resolved = (destination / member.name).resolve()
            if not resolved.is_relative_to(destination.resolve()):
                raise ValueError(f"Unsafe archive path: {member.name}")
        source.extractall(destination, filter="data")


def build_windows_vulkan(rid: str) -> Path:
    if os.name != "nt":
        raise RuntimeError("Vulkan loader must be built on the matching Windows runner")
    settings = ASSETS["windowsVulkan"]
    directory = ROOT / ".cache" / "vulkan" / rid / settings["version"]
    installed = directory / "install"
    result = installed / "bin" / "vulkan-1.dll"
    if result.exists():
        verify_windows_dll(result, rid)
        return result
    directory.mkdir(parents=True, exist_ok=True)
    for key in ("headers", "loader"):
        archive = fetch_asset(settings[key])
        dest = directory / key
        if not dest.exists():
            dest.mkdir(); unpack_tar(archive, dest)
    headers = next((directory / "headers").iterdir())
    loader = next((directory / "loader").iterdir())
    arch = "ARM64" if rid == "win-arm64" else "x64"
    headers_install = directory / "headers-install"
    run("cmake", "-S", headers, "-B", directory / "headers-build", "-G", "Visual Studio 17 2022", "-A", arch,
        f"-DCMAKE_INSTALL_PREFIX={headers_install}")
    run("cmake", "--install", directory / "headers-build", "--config", "Release")
    run("cmake", "-S", loader, "-B", directory / "loader-build", "-G", "Visual Studio 17 2022", "-A", arch,
        f"-DCMAKE_PREFIX_PATH={headers_install}", f"-DCMAKE_INSTALL_PREFIX={installed}",
        "-DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded", "-DBUILD_TESTS=OFF", "-DLOADER_CODEGEN=OFF")
    run("cmake", "--build", directory / "loader-build", "--config", "Release", "--parallel", "2")
    run("cmake", "--install", directory / "loader-build", "--config", "Release")
    if not result.exists():
        raise RuntimeError("Vulkan loader build did not install vulkan-1.dll")
    verify_windows_dll(result, rid)
    return result


def bundle_windows(archive: Path, output: Path, rid: str):
    with zipfile.ZipFile(archive) as source:
        dlls = [i for i in source.infolist() if i.filename.lower().endswith(".dll")]
        if not any(Path(i.filename).name.lower() == "libmpv-2.dll" for i in dlls):
            raise ValueError("Pinned Windows asset contains no libmpv-2.dll")
        for item in dlls:
            (output / Path(item.filename).name).write_bytes(source.read(item))
    shutil.copy2(build_windows_vulkan(rid), output / "vulkan-1.dll")
    verify_windows_closure(output, rid)
    print(f"{rid}: bundled static-codec libmpv plus Vulkan loader; closure verified")


def bundle_linux(archive: Path, output: Path, rid: str):
    if platform.system() != "Linux":
        raise RuntimeError("Linux media dependencies must be collected on the matching Linux runner")
    libs = output / "lib"
    libs.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="vodbox-linux-", dir=ROOT / ".cache") as temp:
        # Native GNU tar handles zstd while keeping upstream SONAME symlinks intact.
        run("tar", "-xf", archive, "-C", temp)
        for path in sorted(Path(temp).rglob("*.so*")):
            if not path.is_file():
                continue
            target = libs / path.name
            if path.is_symlink():
                target.unlink(missing_ok=True)
                target.symlink_to(path.resolve().name)
            else:
                shutil.copy2(path, target)
    copied = set()
    receipt = output / "media-dependencies.json"
    previous = json.loads(receipt.read_text(encoding="utf-8")) if receipt.exists() else {}
    previous = previous.get("libraries", previous)
    owners = {name: {key: row[key] for key in ("package", "version") if key in row}
              for name, row in previous.items() if isinstance(row, dict) and "package" in row}
    env = os.environ.copy()
    env["LD_LIBRARY_PATH"] = str(libs.resolve())
    pending = [p for p in libs.iterdir() if p.is_file() and not p.is_symlink()]
    while pending:
        path = pending.pop()
        if path.name in copied:
            continue
        copied.add(path.name)
        # The upstream Linux prefix was consumed by Flutter, which already loaded libstdc++.
        # NativeAOT/Python do not guarantee that preload. Make the private C++ runtime explicit.
        if path.name.startswith("libmpv.so"):
            run("patchelf", "--add-needed", "libstdc++.so.6", path)
        run("patchelf", "--set-rpath", "$ORIGIN", path)
        listing = subprocess.run(["ldd", str(path.resolve())], check=True, capture_output=True, text=True, env=env).stdout
        if "not found" in listing:
            raise RuntimeError(f"Unresolved runtime libraries in {path.name}: {listing}")
        for name, filename in re.findall(r"^\s*(\S+)\s+=>\s+(/\S+)", listing, re.MULTILINE):
            if name in LINUX_BASE or (libs / name).exists():
                continue
            dependency = Path(filename)
            shutil.copy2(dependency, libs / name, follow_symlinks=True)
            pending.append(libs / name)
            # Keep each distro component's own redistribution notice and version.
            query = subprocess.run(["dpkg-query", "-S", str(dependency)], capture_output=True, text=True)
            if query.returncode != 0:
                query = subprocess.run(["dpkg-query", "-S", "*/" + dependency.name], capture_output=True, text=True)
            if query.returncode != 0:
                raise RuntimeError(f"Cannot identify Debian source package for {dependency}")
            package = query.stdout.splitlines()[0].split(": ")[0]
            version = subprocess.run(["dpkg-query", "-W", "-f=${Version}", package], check=True, capture_output=True, text=True).stdout
            owners[name] = {"package": package, "version": version}
            copyright_file = Path("/usr/share/doc") / package.split(":")[0] / "copyright"
            if not copyright_file.is_file():
                raise RuntimeError(f"Missing redistribution notice for {package}")
            notice_dir = output / "licenses" / "linux"
            notice_dir.mkdir(parents=True, exist_ok=True)
            shutil.copy2(copyright_file, notice_dir / (package.replace(":", "_") + ".copyright"))
    # Every library carries its own origin-relative search path, so launching the binary directly
    # works too: the desktop launcher is not the only place that configures the library path.
    verify_linux_closure(libs, rid)
    desktop = output / "VodBox.Desktop"
    if desktop.exists():
        run("patchelf", "--set-rpath", "$ORIGIN:$ORIGIN/lib", desktop)
    records = {}
    for library in sorted(libs.iterdir()):
        if library.is_file() and not library.is_symlink():
            data = library.read_bytes()
            glibc = sorted({v.decode() for v in re.findall(rb"GLIBC_([0-9]+\.[0-9]+)", data)},
                           key=lambda v: tuple(map(int, v.split("."))))
            records[library.name] = {**owners.get(library.name, {}),
                                     "sha256": hashlib.sha256(data).hexdigest(),
                                     "maximumGlibcSymbol": glibc[-1] if glibc else None}
    (output / "media-dependencies.json").write_text(json.dumps({"asset": ASSETS["runtimes"][rid],
                                                               "libraries": records}, indent=2) + "\n")
    print(f"{rid}: bundled {len(copied)} media/runtime libraries; no system libmpv required")


def copy_media_notices(rid: str, output: Path):
    notices = output / "licenses" / "media"
    notices.mkdir(parents=True, exist_ok=True)
    shutil.copytree(ROOT / "build" / "licenses", notices, dirs_exist_ok=True)
    (notices / "asset.json").write_text(json.dumps(ASSETS["runtimes"][rid], indent=2) + "\n")


JAVA_HOST_DIRECTORY = "JavaHost"

# `deps/` is the upstream Maven closure (host.jar's runtime classpath, staged by tvbox-host.py).
# The dex-deps.json groups are the *legacy* lock table: the new default host does not stage them,
# and the native coverage question is about the upstream closure, so `deps/` must be present.
JAVA_HOST_REQUIRED_COMPONENT = "deps"
JAVA_HOST_REQUIRED_FILES = ("upstream-runtime.json", "LICENSE-tvbox-silicon")
# The per-RID source-rebuild receipt, written by `rebuild_native_backend.py` and shipped inside the
# package (it is the evidence that the distributed native was built from the pinned source).
RECEIPT_NAME = "native-backend-receipt.json"

def java_host_source() -> Path:
    return ROOT / "src" / "VodBox.PluginHost" / "JavaHost"


def stage_prebuilt_host(source: Path, destination: Path) -> Path:
    component = source.parent
    if not source.is_file() or not (component / "upstream-runtime.json").is_file():
        raise ValueError("prebuilt upstream host requires its provenance, dependencies and corresponding source")
    for name in (JAVA_HOST_REQUIRED_COMPONENT, "corresponding-source"):
        if not (component / name).is_dir():
            raise ValueError(f"prebuilt upstream component is missing {name}")
    if not any((component / JAVA_HOST_REQUIRED_COMPONENT).glob("*.jar")):
        # A component whose deps/ is empty links nothing: the host starts and then fails to find
        # the upstream classes. Reject it here rather than shipping a host that cannot load a plugin.
        raise ValueError(f"prebuilt upstream component has an empty {JAVA_HOST_REQUIRED_COMPONENT}/")
    # The source-rebuild receipt must exist beside the staged host: it is the evidence the package
    # distributes a native rebuilt from pinned source, and it has to travel with the package.
    if not (component / RECEIPT_NAME).is_file():
        raise ValueError(
            f"prebuilt upstream component is missing {RECEIPT_NAME}; the per-RID source-native "
            f"rebuild (build/rebuild_native_backend.py) must run before staging the host")
    if source.resolve() != destination.resolve():
        shutil.copy2(source, destination)
    if component.resolve() != destination.parent.resolve():
        for name in (JAVA_HOST_REQUIRED_COMPONENT, "corresponding-source"):
            shutil.copytree(component / name, destination.parent / name, dirs_exist_ok=True)
        for name in JAVA_HOST_REQUIRED_FILES + (RECEIPT_NAME,):
            shutil.copy2(component / name, destination.parent / name)
    return destination


def build_host_jar(destination: Path, rid: str) -> Path:
    """Produce `host.jar` for the bundle.

    Java bytecode is architecture-independent, so compiling with the build host's JDK is correct:
    only the *runtime* has to match the target RID. A prebuilt jar (VODBOX_JAR_HOST) wins over
    compiling, which lets CI build the host once on one runner and reuse it on every RID.

    In the in-repo (non-prebuilt) path the per-RID source rebuild runs here too, so the staged host
    carries this RID's rebuilt backend native and its receipt; a prebuilt host is expected to have
    been rebuilt already, and `stage_prebuilt_host` enforces the receipt is present.
    """
    prebuilt = os.environ.get("VODBOX_JAR_HOST")
    if prebuilt:
        source = Path(prebuilt)
        if not source.is_file():
            raise ValueError(f"VODBOX_JAR_HOST does not exist: {source}")
        return stage_prebuilt_host(source, destination)
    run(sys.executable, ROOT / "build" / "tvbox-host.py", "--output", destination.parent)
    run(sys.executable, ROOT / "build" / "rebuild_native_backend.py", "build",
        "--rid", rid, "--deps", destination.parent / "deps")
    return destination


def bundle_java_host(rid: str, output: Path, host_jar=None):
    """Stage the JVM plugin host next to the published app: host.jar + deps + the RID's JRE.

    The JRE is deliberately the RID's pinned Temurin asset, never the runner's own `java`: on a
    cross-compile leg that would be the build host's architecture, which installs fine and then
    fails to exec on the target. `verify_jre` proves the launcher matches the RID before we accept it.
    """
    host = output / JAVA_HOST_DIRECTORY
    host.mkdir(parents=True, exist_ok=True)
    jar = host / "host.jar"
    if host_jar is not None:
        source = Path(host_jar)
        if not source.is_file():
            raise ValueError(f"--java-host-jar does not exist: {source}")
        stage_prebuilt_host(source, jar)
    else:
        build_host_jar(jar, rid)
    stage = ROOT / "build" / "fetch-dex-deps.py"
    run(sys.executable, stage, "jre", "--rid", rid, "--extract", host / "jre")
    architecture = verify_jre(host / "jre", rid)
    lock = json.loads((ROOT / "build" / "dex-deps.json").read_text(encoding="utf-8"))
    # The full upstream host is staged first (host.jar + original Maven deps + corresponding source);
    # the per-RID source rebuild then rewrites the backend jar inside that staged host, and this is
    # where the *result* is verified. After a source rebuild the backend jar deliberately carries
    # only this RID's native (every foreign one was removed), so the check is per-RID: `verify_deps`
    # is asked to confirm this RID's rebuilt native is present and no foreign native rode along,
    # rather than demanding the five upstream RIDs the rebuild intentionally dropped.
    try:
        coverage = verify_deps(host / "deps", rid)
    except NativeCoverageError as error:
        # The lock table and the shipped jars disagree: treat it as a broken build, not a warning.
        raise ValueError(f"{rid}: native coverage table does not match the bundled deps: {error}") from error
    # The receipt is the evidence that ties the packaged native to its pinned source. It is written
    # by `rebuild_native_backend.py` next to the staged host and must travel *inside* the package,
    # so a later release gate can read it from the archive rather than only from the build tree.
    receipt_path = host / RECEIPT_NAME
    if not receipt_path.is_file():
        raise ValueError(
            f"{rid}: {RECEIPT_NAME} is missing from the staged Java host; the source-native rebuild "
            f"step must run before bundling so the package carries its rebuild receipt")
    receipt = json.loads(receipt_path.read_text(encoding="utf-8"))
    if receipt.get("rid") != rid:
        raise ValueError(f"{rid}: staged receipt is for {receipt.get('rid')!r}, not {rid}")
    native = {
        "backend": "unidbg-unicorn2",
        "supported": backend_supports(rid),
        "verdict": describe(rid),
        "bundledJar": coverage["jar"],
        "sourceRebuilt": bool(coverage.get("sourceRebuilt")),
        "receipt": RECEIPT_NAME,
        "receiptRid": receipt.get("rid"),
        "excluded": bool(receipt.get("backend", {}).get("excluded")),
    }
    with jar.open("rb") as handle:
        digest = hashlib.file_digest(handle, "sha256").hexdigest()
    (host / "java-host.json").write_text(json.dumps({
        "hostJar": {"name": jar.name, "sha256": digest},
        "backend": "tvbox-silicon",
        "dependencies": ["deps"],
        "jre": {"release": lock["jre"]["release"], "rid": rid, "architecture": architecture},
        "nativeBackend": native,
    }, indent=2) + "\n")
    if not native["supported"]:
        print(f"  {rid}: native ARM64 backend unavailable -> plugins needing it fail as unsupported_native_library")
    print(f"{rid}: bundled JVM plugin host (upstream host.jar + original Maven deps and corresponding source + JRE {architecture})")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("rid")
    parser.add_argument("output", type=Path)
    parser.add_argument("--java-host-jar", type=Path, default=None,
                        help="use a prebuilt host.jar instead of compiling the in-repo Java host")
    parser.add_argument("--skip-java-host", action="store_true",
                        help="bundle only the media kernel, not the JVM plugin host")
    args = parser.parse_args()
    valid={"osx-x64","osx-arm64","win-x64","win-arm64","linux-x64","linux-arm64"}
    if args.rid not in valid: parser.error("unsupported runtime identifier")
    args.output.mkdir(parents=True, exist_ok=True)
    if not args.skip_java_host:
        bundle_java_host(args.rid, args.output, args.java_host_jar)
    archive = download(args.rid)
    if args.rid.startswith("osx"):
        bundle_macos(archive, args.output)
    elif args.rid.startswith("win"):
        bundle_windows(archive, args.output, args.rid)
    else:
        bundle_linux(archive, args.output, args.rid)
    copy_media_notices(args.rid, args.output)
    print(f"bundled complete media kernel for {args.rid}")


if __name__ == "__main__":
    main()
