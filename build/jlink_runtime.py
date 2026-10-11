"""Create a Java 21 runtime on the target RID's native runner; never fall back to a full JRE."""
import json
import os
import platform
import re
import shutil
import subprocess
import tempfile
from pathlib import Path

from nativeverify import EXPECTED_ARCH, verify_executable_arch, verify_jre

# Fixed upstream Scripts/build-java-host.sh at b82a0313ceb9db652c4f1e07560a7a603eee75df.
# Keep its dynamic dependencies: Unsafe, EC TLS, zip filesystems, HTTP server and GBK.
UPSTREAM_MODULES = (
    "java.base", "java.logging", "java.xml", "java.desktop", "java.management",
    "java.naming", "jdk.unsupported", "jdk.crypto.ec", "jdk.zipfs",
    "jdk.httpserver", "jdk.charsets",
)
# java.compiler is needed by runtime compiler API users in the plugin/dependency closure;
# java.sql is used by JDBC-capable plugins and java.net.http by Java HTTP clients.
MODULES = UPSTREAM_MODULES + ("java.compiler", "java.sql", "java.net.http")
METADATA = "jlink-runtime.json"


def read_release(home: Path) -> dict[str, str]:
    release = home / "release"
    if not release.is_file():
        raise ValueError(f"{home}: missing JDK release metadata; provide target JDK 21")
    return dict(re.findall(r'^([A-Z0-9_]+)="([^"]*)"$', release.read_text(encoding="utf-8"), re.MULTILINE))


def native_rid() -> str:
    system = {"Darwin": "osx", "Linux": "linux", "Windows": "win"}.get(platform.system())
    machine = {"x86_64": "x64", "amd64": "x64", "aarch64": "arm64", "arm64": "arm64"}.get(platform.machine().lower())
    return f"{system}-{machine}"


def validate_jdk(home: Path, rid: str) -> tuple[Path, dict[str, str]]:
    if rid not in EXPECTED_ARCH:
        raise ValueError(f"unsupported runtime identifier: {rid}")
    if native_rid() != rid:
        raise ValueError(f"{rid}: jlink requires a native {rid} runner with target JDK 21; runner is {native_rid()}. No full-JRE fallback")
    home = Path(home).resolve()
    # JAVA_HOME normally points to Contents/Home, but accepting the bundle root is useful locally.
    if not (home / "bin").is_dir() and (home / "Contents/Home/bin").is_dir():
        home = home / "Contents/Home"
    release = read_release(home)
    version = release.get("JAVA_VERSION", "")
    if version.split(".")[0] != "21":
        raise ValueError(f"{rid}: target JDK 21 required, JAVA_HOME has {version!r}: {home}")
    suffix = ".exe" if rid.startswith("win") else ""
    for tool in ("java", "javac", "jlink"):
        path = home / "bin" / (tool + suffix)
        if not path.is_file():
            raise ValueError(f"{rid}: target JDK 21 is missing {path}; a JRE cannot run jlink")
        verify_executable_arch(path, rid)
    missing = [module for module in MODULES if not (home / "jmods" / (module + ".jmod")).is_file()]
    if missing:
        raise ValueError(f"{rid}: target JDK lacks required jmods: {', '.join(missing)}")
    # Check jmod native payloads as well as launchers: do not link foreign jmods using host tools.
    jmod = home / "jmods/java.base.jmod"
    import zipfile
    with zipfile.ZipFile(jmod) as archive:
        java = archive.read("bin/java" + suffix)
    scratch = Path(__file__).resolve().parents[1] / ".cache" / "jlink"
    scratch.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="jdk-machine-", dir=scratch) as temporary:
        launcher = Path(temporary) / ("java" + suffix)
        launcher.write_bytes(java)
        verify_executable_arch(launcher, rid)
    return home, release


def link_runtime(rid: str, destination: Path, jdk_home=None) -> dict:
    supplied = jdk_home or os.environ.get("JAVA_HOME")
    if not supplied:
        raise ValueError(f"{rid}: set JAVA_HOME to the native target JDK 21 (not a JRE); no full-JRE fallback")
    home, release = validate_jdk(Path(supplied), rid)
    destination = Path(destination).resolve()
    destination.parent.mkdir(parents=True, exist_ok=True)
    if destination.exists() and (not destination.is_dir() or any(destination.iterdir())):
        raise ValueError(f"{destination}: jlink output must be absent or empty")
    suffix = ".exe" if rid.startswith("win") else ""
    # Build beside the destination, so a failed link never leaves a partly populated JRE.
    with tempfile.TemporaryDirectory(prefix="jlink-", dir=destination.parent) as temporary:
        image = Path(temporary) / "jre"
        subprocess.run([str(home / "bin" / ("jlink" + suffix)),
                        "--module-path", str(home / "jmods"),
                        "--add-modules", ",".join(MODULES), "--strip-debug",
                        "--no-header-files", "--no-man-pages", "--compress=2",
                        "--output", str(image)], check=True)
        architecture = verify_jre(image, rid)
        listing = subprocess.run([str(image / "bin" / ("java" + suffix)), "--list-modules"],
                                 check=True, capture_output=True, text=True).stdout
        actual = {line.split("@", 1)[0] for line in listing.splitlines()}
        missing = set(MODULES) - actual
        if missing:
            raise ValueError(f"{rid}: linked runtime is missing modules: {', '.join(sorted(missing))}")
        # These are jlink's actual per-module redistribution notices, not synthetic placeholders.
        for module in actual:
            if not (image / "legal" / module / "LICENSE").is_file():
                raise ValueError(f"{rid}: linked runtime lacks legal/{module}/LICENSE")
        if (home / "LICENSE").is_file():
            shutil.copy2(home / "LICENSE", image / "LICENSE")
        metadata = {"kind": "jlink", "release": release["JAVA_VERSION"], "rid": rid,
                    "architecture": architecture, "modules": sorted(actual),
                    "implementor": release.get("IMPLEMENTOR", "")}
        (image / METADATA).write_text(json.dumps(metadata, indent=2) + "\n", encoding="utf-8")
        if destination.exists():
            destination.rmdir()
        image.replace(destination)
    return metadata
