"""下载并校验 JVM 侧宿主依赖（见 build/dex-deps.json）。

只做三件事：**下载 → 校验 sha256 → 按宿主约定布局文件**。绝不编译、绝不运行测试，
也绝不写 /tmp（一切落在工作区 .cache/dex-deps/ 或你显式指定的目录里）。

用法：
  python3 build/fetch-dex-deps.py list
      打印锁定表（分组、许可、sha256、平台可用性）。
  python3 build/fetch-dex-deps.py fetch [--groups dex,sqlite,runtime,unidbg]
      把缺的制品下到 .cache/dex-deps/ 并逐件校验 sha256（已存在则只校验）。
  python3 build/fetch-dex-deps.py verify [--groups dex,sqlite,runtime,unidbg]
      只校验缓存（缺件即失败），适合 CI/发布前置检查。
  python3 build/fetch-dex-deps.py stage DIR [--groups dex,sqlite,runtime] [--host-jar PATH]
      按各组 stageDirectory 布局：dex→DIR/dex-tools/、sqlite→DIR/sqlite-jdbc/、runtime→DIR/runtime/、
      unidbg→DIR/unidbg/（都是宿主运行时发现的约定目录）。DIR 通常是发布输出里的 JavaHost/。
      --host-jar 一并把 host.jar 放到 DIR/host.jar。
      默认 stage 可分发组（dex,sqlite,runtime）；unidbg 组需显式 --groups unidbg 并加 --allow-non-distributable。
  python3 build/fetch-dex-deps.py jre --rid osx-arm64 [--extract DIR]
      下载锁定版 Temurin 21 JRE 到缓存并校验；--extract 摊平顶层目录解到 DIR，并按 RID 校验
      DIR/bin/java 的 PE/ELF/Mach-O 架构（跨编译时绝不放进构建机自带 java 的架构）。

说明：unidbg 组编译期传递依赖含 GPL-2.0-only（unicorn/keystone/demumble）。本脚本只陈述该许可事实；
是否随包分发是本仓库的分发策略（默认**不随包分发**该组），stage 该组需显式同意。
"""
import argparse
import hashlib
import json
import shutil
import sys
import tarfile
import urllib.request
import zipfile
from pathlib import Path

from nativeverify import verify_jre

ROOT = Path(__file__).resolve().parent.parent
LOCK = json.loads((ROOT / "build" / "dex-deps.json").read_text(encoding="utf-8"))
CACHE = ROOT / LOCK.get("cacheRoot", ".cache/dex-deps")
USER_AGENT = "vodbox-dexdeps/1.0"


def coordinates(entry: dict) -> tuple[str, str, str]:
    group, artifact, version = entry["coordinates"].split(":")
    return group, artifact, version


def url_for(entry: dict) -> str:
    group, artifact, version = coordinates(entry)
    return LOCK["artifactUrlTemplate"].format(
        repository=LOCK["mavenRepository"], groupPath=group.replace(".", "/"),
        artifact=artifact, version=version)


def cache_path(entry: dict) -> Path:
    _, artifact, version = coordinates(entry)
    return CACHE / f"{artifact}-{version}.jar"


def flatten(destination: Path) -> None:
    """Lift the single top-level directory an upstream archive ships (`jdk-<ver>-jre/`) up one level.

    Temurin (and most JDK distributions) wrap everything in a versioned root, but the host locator
    expects `<jre>/bin/java`, so the archive root must not stay nested.
    """
    children = list(destination.iterdir())
    if len(children) != 1 or not children[0].is_dir():
        return
    inner = children[0]
    for item in inner.iterdir():
        shutil.move(str(item), destination / item.name)
    inner.rmdir()


def sha256_of(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for block in iter(lambda: handle.read(1 << 20), b""):
            digest.update(block)
    return digest.hexdigest()


def download(url: str, destination: Path) -> None:
    """流式下载到 .partial 再改名，避免半截文件被当成已缓存。"""
    destination.parent.mkdir(parents=True, exist_ok=True)
    partial = destination.with_suffix(destination.suffix + ".partial")
    request = urllib.request.Request(url, headers={"User-Agent": USER_AGENT})
    with urllib.request.urlopen(request, timeout=180) as response, partial.open("wb") as output:
        shutil.copyfileobj(response, output)
    partial.replace(destination)


def selected(entries: list[dict], groups: set[str]) -> list[dict]:
    return [entry for entry in entries if entry["group"] in groups]


def ensure(entry: dict, fetch: bool) -> Path:
    path = cache_path(entry)
    if not path.exists():
        if not fetch:
            raise SystemExit(f"缺少缓存件（先跑 fetch）：{path.name}  <- {entry['coordinates']}")
        print(f"  下载 {entry['coordinates']}")
        download(url_for(entry), path)
    actual = sha256_of(path)
    if actual != entry["sha256"]:
        raise SystemExit(f"sha256 不符：{path.name}\n  期望 {entry['sha256']}\n  实际 {actual}")
    size = path.stat().st_size
    if entry.get("size") and size != entry["size"]:
        raise SystemExit(f"大小不符：{path.name} 期望 {entry['size']} 实际 {size}")
    return path


def parse_groups(values: str | None, default: list[str]) -> set[str]:
    available = set(LOCK["groups"])
    if not values:
        return set(default)
    chosen = {part.strip() for part in values.split(",") if part.strip()}
    unknown = chosen - available
    if unknown:
        raise SystemExit(f"未知分组：{','.join(sorted(unknown))}（可用：{','.join(sorted(available))}）")
    return chosen


def command_list(_args: argparse.Namespace) -> int:
    for name, group in LOCK["groups"].items():
        marker = "随包分发" if group["distribute"] else "不随包分发"
        print(f"[{name}] {marker} -> <宿主目录>/{group['stageDirectory']}/")
        print(f"    {group['description']}")
        for entry in selected(LOCK["artifacts"], {name}):
            print(f"    - {entry['coordinates']:<55} {entry['license']}")
    platform_info = LOCK["nativePlatforms"]
    print(f"\n[ARM64 模拟原生平台] {platform_info['artifact']}")
    for rid, path in platform_info["present"].items():
        print(f"    有资产 {rid:<12} {path}")
    for rid, reason in platform_info["missing"].items():
        print(f"    缺资产 {rid:<12} {reason}")
    jre = LOCK["jre"]
    print(f"\n[JRE] {jre['release']}（{jre['distribution']}） 覆盖 {len(jre['assets'])} 个 RID")
    print(f"      {jre['discovery']}")
    return 0


def command_fetch(args: argparse.Namespace) -> int:
    groups = parse_groups(args.groups, ["dex", "sqlite", "runtime", "unidbg"])
    entries = selected(LOCK["artifacts"], groups)
    print(f"拉取 {len(entries)} 个制品到 {CACHE}（分组：{','.join(sorted(groups))}）")
    for entry in entries:
        path = ensure(entry, fetch=True)
        print(f"  OK {entry['coordinates']}  {path.stat().st_size} B")
    return 0


def command_verify(args: argparse.Namespace) -> int:
    groups = parse_groups(args.groups, ["dex", "sqlite", "runtime", "unidbg"])
    entries = selected(LOCK["artifacts"], groups)
    for entry in entries:
        ensure(entry, fetch=False)
    print(f"校验通过：{len(entries)} 个制品（分组：{','.join(sorted(groups))}）")
    return 0


def command_stage(args: argparse.Namespace) -> int:
    groups = parse_groups(args.groups, ["dex", "sqlite", "runtime"])
    target = Path(args.directory).resolve()
    non_distributable = {name for name in groups if not LOCK["groups"][name]["distribute"]}
    if non_distributable and not args.allow_non_distributable:
        raise SystemExit(
            f"分组 {','.join(sorted(non_distributable))} 默认不随包分发（其闭包含 GPL-2.0-only 组件）；"
            "如确为本地开发/自担分发用途，追加 --allow-non-distributable。")
    entries = selected(LOCK["artifacts"], groups)
    for entry in entries:
        ensure(entry, fetch=True)
    for name in sorted(groups):
        stage_dir = target / LOCK["groups"][name]["stageDirectory"]
        stage_dir.mkdir(parents=True, exist_ok=True)
        count = 0
        for entry in selected(LOCK["artifacts"], {name}):
            shutil.copy2(cache_path(entry), stage_dir / cache_path(entry).name)
            count += 1
        print(f"  {name}: {count} 个 jar -> {stage_dir}")
    if args.host_jar:
        host = Path(args.host_jar).resolve()
        if not host.is_file():
            raise SystemExit(f"--host-jar 不存在：{host}")
        shutil.copy2(host, target / "host.jar")
        print(f"  host.jar -> {target / 'host.jar'}")
    print(f"已布局到 {target}（宿主据此在 host.jar 同级目录发现依赖，无需环境变量）")
    return 0


def command_jre(args: argparse.Namespace) -> int:
    jre = LOCK["jre"]
    asset = jre["assets"].get(args.rid)
    if asset is None:
        raise SystemExit(f"JRE 锁定表没有 RID {args.rid}（可用：{','.join(sorted(jre['assets']))}）")
    name = asset["url"].rsplit("/", 1)[1]
    path = CACHE / "jre" / name
    if not path.exists():
        print(f"  下载 {jre['release']} {args.rid}")
        download(asset["url"], path)
    actual = sha256_of(path)
    if actual != asset["sha256"]:
        raise SystemExit(f"JRE sha256 不符：{path.name}\n  期望 {asset['sha256']}\n  实际 {actual}")
    print(f"  OK {path}  {path.stat().st_size} B")
    if args.extract:
        destination = Path(args.extract).resolve()
        destination.mkdir(parents=True, exist_ok=True)
        if path.suffix == ".zip":
            with zipfile.ZipFile(path) as archive:
                archive.extractall(destination)
        else:
            with tarfile.open(path) as archive:
                archive.extractall(destination)
        flatten(destination)
        # Cross-compile guard: prove the launcher really is the RID's architecture instead of the
        # build host's. Shipping the runner's own `java` into an arm64 package looks fine until
        # the target tries to exec an x86-64 binary.
        arch = verify_jre(destination, args.rid)
        java = destination / "bin" / ("java.exe" if args.rid.startswith("win") else "java")
        if not java.is_file() and args.rid.startswith("osx"):
            java = destination / "Contents" / "Home" / "bin" / "java"
        print(f"  解到 {destination}；java = {java}（{arch}）")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description="下载并校验 JVM 侧宿主依赖（不编译、不运行测试）")
    subparsers = parser.add_subparsers(dest="command", required=True)

    subparsers.add_parser("list", help="打印锁定表").set_defaults(func=command_list)

    fetch = subparsers.add_parser("fetch", help="下载缺失制品并校验")
    fetch.add_argument("--groups", help="逗号分隔：dex,sqlite,runtime,unidbg（默认全部）")
    fetch.set_defaults(func=command_fetch)

    verify = subparsers.add_parser("verify", help="只校验缓存")
    verify.add_argument("--groups", help="逗号分隔：dex,sqlite,runtime,unidbg（默认全部）")
    verify.set_defaults(func=command_verify)

    stage = subparsers.add_parser("stage", help="按宿主约定布局依赖目录")
    stage.add_argument("directory", help="目标目录（通常是发布输出里的 JavaHost/）")
    stage.add_argument("--groups", help="逗号分隔：dex,sqlite,runtime,unidbg（默认 dex,sqlite,runtime）")
    stage.add_argument("--host-jar", help="一并把 host.jar 放到 DIR/host.jar")
    stage.add_argument("--allow-non-distributable", action="store_true", help="允许 stage 不随包分发的分组")
    stage.set_defaults(func=command_stage)

    jre = subparsers.add_parser("jre", help="下载锁定版 JRE")
    jre.add_argument("--rid", required=True, help="osx-x64/osx-arm64/linux-x64/linux-arm64/win-x64/win-arm64")
    jre.add_argument("--extract", help="解包目录（可选；摊平顶层并校验该 RID 的架构）")
    jre.set_defaults(func=command_jre)

    args = parser.parse_args()
    return args.func(args)


if __name__ == "__main__":
    sys.exit(main())
