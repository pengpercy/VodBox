"""Single source of truth for "which RIDs can actually run the upstream native ARM64 backend".

The javahost ships the upstream tvbox-silicon component, whose `pom.xml` pins
`com.github.zhkl0228:unidbg-android:0.9.8` + `unidbg-unicorn2:0.9.8`. The ARM64 emulation
backend therefore comes from the native libraries *inside those resolved jars*, and the set of
RIDs it covers is a property of the jars that are actually in `deps/` — not of any entry in a
separate lock table or of a differently-versioned artifact that the pom never pulls in.

Prose drifts. This module is the machine-checkable form of the claim: it can be asked for a RID's
verdict, and `verify_deps` re-derives the verdict from the real jar contents so a lock-table edit
or a dependency bump that silently drops a platform fails loudly instead of shipping as "supported".

Two independent closures are computed here, and they are deliberately *not* the same question:

  * **Binary/native closure** (`closure_state`) — "does every release RID have a native library in
    the shipped backend jar?" This is a property of the shipped binaries alone. It is *not* source
    compliance, and it says nothing about whether the corresponding source of those binaries was
    ever established.
  * **Source closure** (`source_closure_state`) — "is the corresponding-source obligation for the
    distributed native binaries actually discharged?" This is a legal/record question, read from
    `third_party/tvbox-silicon/dependency-sources/native-source-manifest.json`, the retaining
    manifest authored by the provenance pass.

Reading `nativeCoverage.backendRids` (five covered RIDs, `win-arm64` bare) and concluding "source
closure is 5/6" is a category error the module now refuses: the five/six split describes the *binary*
backend, while a source obligation is per distributed artifact and is currently pending for every
one of them. `win-arm64` shipping no native backend library is a limitation on native-guarded
plugins only; it does not by itself block an ordinary Java release and must not be folded into the
source obligation either.

Usage as a library:
    from native_coverage import backend_supports, describe
    backend_supports("win-arm64")            # -> False
    describe("win-arm64")                    # -> human-readable diagnostic
CLI (offline, no network/tools):
    python3 build/native_coverage.py list
    python3 build/native_coverage.py verify --deps <JavaHost/deps>
    python3 build/native_coverage.py closure   [--require]
    python3 build/native_coverage.py source-closure [--require]
"""

from __future__ import annotations

import argparse
import json
import sys
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
RIDS = ("osx-x64", "osx-arm64", "win-x64", "win-arm64", "linux-x64", "linux-arm64")

# The retaining manifest that records what corresponding-source evidence is held for the native
# binaries VodBox distributes. It is a *different document* from the coverage lock table above: the
# lock table answers "which RIDs have a native library", the manifest answers "is the source
# obligation for the distributed natives actually discharged". Never read one for the other.
SOURCE_MANIFEST = (ROOT / "third_party/tvbox-silicon/dependency-sources"
                   / "native-source-manifest.json")
# The retaining manifest's own verdict field for the distributed-native source obligation.
SOURCE_MANIFEST_COMPLETE_FIELD = "corresponding_source_complete"

# Every native binary a release distributes, named by the artifact that carries it. This list is the
# audit surface: a source-closure verdict is per artifact, so adding a distributed native artifact
# without a clean verdict here must fail closed rather than ride on the manifest's summary flag.
DISTRIBUTED_SOURCE_ARTIFACTS = (
    "com.github.zhkl0228:unidbg-unicorn2:0.9.8",   # ARM64 emulation backend natives
    "com.github.zhkl0228:unidbg-android:0.9.8",    # guest SDK/hook natives
    "com.github.zhkl0228:unicorn:1.0.14",
    "com.github.zhkl0228:keystone:0.9.7",
    "com.github.zhkl0228:demumble:1.0.4",
    "net.java.dev.jna:jna:5.10.0",
)

# A per-artifact status only closes the source obligation when it is one of these. The manifest
# uses these tokens for "the case is settled" — either the release source was collected and matched,
# or the artifact is entirely out of the distributed set. Anything else ("pending-...") stays open.
SOURCE_STATUS_CLOSED = frozenset({"matched-release-source-collected", "not-distributed", "excluded"})

# The upstream JavaHost pom at the pinned tvbox-silicon commit. Both coordinates must be present in
# the resolved `deps/` for this component to be the one the coverage table describes.
UPSTREAM_POM = ROOT / "third_party/tvbox-silicon/Runtime/JavaHost/pom.xml"
BACKEND_ARTIFACT_PREFIX = "unidbg-unicorn2"
BACKEND_VERSION = "0.9.8"

# A jar's `natives/<dir>/` directory name -> the RID that reads it at runtime. This is the mapping
# the upstream loader uses; a directory here with no file is not coverage, so resolution is per file.
NATIVE_DIRECTORY_RID = {
    "osx_64": "osx-x64",
    "osx_arm64": "osx-arm64",
    "windows_64": "win-x64",
    "windows_arm64": "win-arm64",
    "linux_64": "linux-x64",
    "linux_arm64": "linux-arm64",
}


class NativeCoverageError(RuntimeError):
    """A claimed native coverage does not match what the shipped jars actually contain."""


def _declared() -> dict:
    lock = json.loads((ROOT / "build" / "dex-deps.json").read_text(encoding="utf-8"))
    return lock.get("nativeCoverage", {})


def backend_supports(rid: str) -> bool:
    """True only if the shipped ARM64 emulation backend has native libraries for `rid`."""
    if rid not in RIDS:
        raise NativeCoverageError(f"unknown runtime identifier: {rid}")
    return rid in set(_declared().get("backendRids", {}).get("supported", []))


def assembler_supports(rid: str) -> bool:
    """True only if the assembler/disassembler helpers (capstone/keystone/demumble) have natives."""
    if rid not in RIDS:
        raise NativeCoverageError(f"unknown runtime identifier: {rid}")
    return rid in set(_declared().get("assembler", {}).get("supported", []))


def describe(rid: str) -> str:
    """Human-readable diagnostic for whether `rid` can run the native backend, and why not."""
    if rid not in RIDS:
        raise NativeCoverageError(f"unknown runtime identifier: {rid}")
    if backend_supports(rid):
        return f"{rid}: upstream ARM64 emulation backend has native libraries."
    return (f"{rid}: unsupported_native_library — the upstream ARM64 emulation backend "
            f"(com.github.zhkl0228:{BACKEND_ARTIFACT_PREFIX}:{BACKEND_VERSION}) ships no native "
            f"library for this platform. JAR plugins that require the native guard fail explicitly; "
            f"a bundled JRE does not make the backend available.")


def unsupported_rids() -> list[str]:
    return [rid for rid in RIDS if not backend_supports(rid)]


def closure_state() -> dict:
    """The *binary* native-closure verdict: which RIDs have a shipped native ARM64 backend library.

    This is the per-RID coverage of the *distributed binaries* and nothing more. "Complete" here
    means the ARM64 emulation backend carries a native library for every RID the release publishes.
    The manifest is the source of truth; this reads `backendRids.supported` / `.unsupported` and
    audits that the two sets together are exactly the six RIDs, so a partial edit (a RID added to
    `supported` without being removed from `unsupported`, or a RID dropped from both) fails loudly
    instead of shipping as "complete".

    It deliberately does **not** speak to source compliance: that is `source_closure_state`, which is
    a separate obligation read from a separate manifest. Reading the five/six split here as a source
    closure is the category error this module exists to prevent.

    Returns {'complete': bool, 'supported': [...], 'unsupported': [...], 'missing': [...]}.
    `missing` is the RID list a release would be publishing without native backend coverage.
    """
    backend = _declared().get("backendRids", {})
    supported = list(backend.get("supported", []))
    unsupported = list(backend.get("unsupported", []))
    known = set(supported) | set(unsupported)
    overlap = set(supported) & set(unsupported)
    if overlap:
        raise NativeCoverageError(
            f"backendRids lists {', '.join(sorted(overlap))} as both supported and unsupported; "
            f"the coverage table is self-contradictory.")
    if known != set(RIDS):
        raise NativeCoverageError(
            f"backendRids must classify exactly the six RIDs; unclassified: "
            f"{', '.join(sorted(set(RIDS) - known)) or 'none'}, unknown: "
            f"{', '.join(sorted(known - set(RIDS))) or 'none'}.")
    return {
        "complete": not unsupported,
        "supported": supported,
        "unsupported": unsupported,
        "missing": unsupported,
    }


def require_closure(target_rids: list[str] | None = None) -> dict:
    """Raise unless the manifest declares native-backend coverage for every `target_rids` RID.

    This is the *binary* coverage gate: it does not trust a hand-written "complete" marker, it reads
    the same table bundle.py/native_coverage.py use at build time. When the binary closure is
    incomplete (today `win-arm64` has no upstream native library), publishing a package that needs
    the native backend on that RID must stop.

    Note this gate is about *native-guarded plugins*, not about the release as a whole. An ordinary
    Java release still runs on `win-arm64` (it has its own JRE asset); what is missing is the ARM64
    emulation library. That is a reason to scope the release or fail the native-guarded plugins, not
    a reason to fold `win-arm64` into the corresponding-source obligation — which is a different,
    separately-tracked question (`source_closure_state` / `require_source_closure`).

    A release set that excludes the uncovered RID passes: `require_closure(['linux-x64'])` is a
    legitimate scoping, not a bypass.
    """
    state = closure_state()
    targets = list(target_rids) if target_rids is not None else list(RIDS)
    unknown = [rid for rid in targets if rid not in RIDS]
    if unknown:
        raise NativeCoverageError(f"unknown runtime identifier(s): {', '.join(unknown)}")
    missing = [rid for rid in targets if rid in state["unsupported"]]
    if missing:
        raise NativeCoverageError(
            "native backend coverage is incomplete: " + ", ".join(missing)
            + " have no upstream ARM64 emulation backend native library, so a release that includes "
              "them would ship a package whose native-guarded plugins fail at runtime. (This is a "
              "missing *binary*, not a missing source obligation; the two gates are separate.)")
    return state


def _source_manifest() -> dict:
    """The retaining manifest for distributed-native corresponding source, parsed as JSON."""
    try:
        return json.loads(SOURCE_MANIFEST.read_text(encoding="utf-8"))
    except FileNotFoundError as error:
        raise NativeCoverageError(
            f"source manifest is missing at {SOURCE_MANIFEST.relative_to(ROOT)}; the source-closure "
            f"obligation for the distributed native binaries cannot be established without it.") from error


def _manifest_artifacts(manifest: dict) -> dict[str, dict]:
    """Every artifact entry the manifest records, keyed by its `artifact` coordinate.

    Covers both the `dependencies` list and `additional_native_dependencies`. The coordinate is the
    only key that means the same thing in both the manifest and the release set, so entries without
    one are unjoinable and are surfaced through `unclassified` rather than silently ignored.
    """
    entries: dict[str, dict] = {}
    for key in ("dependencies", "additional_native_dependencies"):
        for entry in manifest.get(key) or []:
            coordinate = entry.get("artifact")
            if coordinate:
                entries.setdefault(coordinate, entry)
    return entries


def source_closure_state() -> dict:
    """The *source* closure verdict for every distributed native artifact, read from the manifest.

    This answers a different question than `closure_state`: not "which RIDs have a native library",
    but "is the corresponding-source obligation for the distributed natives discharged?". The
    obligation is per artifact, so a verdict is assembled as:

      * the manifest's own `corresponding_source_complete` summary flag must be true;
      * every artifact in `DISTRIBUTED_SOURCE_ARTIFACTS` must have exactly one manifest entry;
      * that entry's per-case `corresponding_source_complete` must be true;
      * and the entry's `status` must be a closed token (see `SOURCE_STATUS_CLOSED`).

    A pending `status` (e.g. `pending-engine-source-commit`, `pending-embedded-native-provenance`)
    keeps the closure open even where the summary flag was hand-edited, because the summary is
    *derived* — the per-artifact status carries the actual evidence state. This is the strict,
    fail-closed reading: no single string can be flipped to "complete" while an artifact still has a
    pending reason. An artifact missing entirely from the manifest is `unclassified` and also keeps
    the closure open.

    Returns {'complete': bool, 'summary_complete': bool, 'pending': [...], 'unclassified': [...],
             'artifacts': {coordinate: status}}.
    """
    manifest = _source_manifest()
    summary_complete = bool(manifest.get(SOURCE_MANIFEST_COMPLETE_FIELD, False))
    entries = _manifest_artifacts(manifest)
    artifacts: dict[str, str] = {}
    pending: list[str] = []
    unclassified: list[str] = []
    for coordinate in DISTRIBUTED_SOURCE_ARTIFACTS:
        entry = entries.get(coordinate)
        if entry is None:
            unclassified.append(coordinate)
            continue
        status = str(entry.get("status", "unknown"))
        artifacts[coordinate] = status
        if not bool(entry.get("corresponding_source_complete", False)) or status not in SOURCE_STATUS_CLOSED:
            pending.append(coordinate)
    return {
        "complete": summary_complete and not pending and not unclassified,
        "summary_complete": summary_complete,
        "pending": pending,
        "unclassified": unclassified,
        "artifacts": artifacts,
    }


def require_source_closure() -> dict:
    """Raise unless the corresponding-source obligation for the distributed natives is discharged.

    This is the gate that must stop a *real* release while the source manifest is still
    `pending-with-rebuild-path`. It is independent of the binary-closure gate: a release can be
    binary-complete and still blocked here, and a host can decline to distribute an artifact and
    still pass here. It reads the manifest's own evidence state (`status` + per-artifact flag), never
    a hand-written summary, so editing `corresponding_source_complete` to `true` while an artifact
    still reads `pending-...` fails closed instead of shipping.
    """
    state = source_closure_state()
    if state["unclassified"]:
        raise NativeCoverageError(
            "source closure cannot be established: no manifest entry for "
            + ", ".join(state["unclassified"])
            + " — a distributed native artifact must have a recorded corresponding-source verdict.")
    if state["pending"]:
        raise NativeCoverageError(
            "source closure is incomplete: the corresponding source for " + ", ".join(state["pending"])
            + " is still pending (manifest status pending-*, not an executed rebuild or a matched "
              "release source). A release distributing these natives has an open corresponding-source "
              "obligation.")
    if not state["summary_complete"]:
        raise NativeCoverageError(
            f"source closure is incomplete: the manifest's {SOURCE_MANIFEST_COMPLETE_FIELD} is false; "
            f"the overall corresponding-source obligation is not discharged.")
    return state


def jar_native_rids(path: Path) -> set[str]:
    """RIDs for which `path` carries at least one real `natives/<dir>/<file>` native library."""
    present: set[str] = set()
    with zipfile.ZipFile(path) as archive:
        for name in archive.namelist():
            if name.endswith("/"):
                continue
            parts = name.split("/")
            if len(parts) >= 3 and parts[0] == "natives" and parts[1] in NATIVE_DIRECTORY_RID:
                present.add(NATIVE_DIRECTORY_RID[parts[1]])
    return present


def backend_jar(deps: Path) -> Path:
    """The ARM64 emulation backend jar inside a staged `deps/` directory."""
    matches = sorted(Path(deps).glob(f"{BACKEND_ARTIFACT_PREFIX}-*.jar"))
    if not matches:
        raise NativeCoverageError(
            f"{deps}: the upstream ARM64 emulation backend ({BACKEND_ARTIFACT_PREFIX}) is not "
            f"staged; native plugins cannot run on any RID.")
    return matches[0]


def verify_deps(deps: Path) -> dict:
    """Re-derive the backend coverage from the real jars and compare it to the declared table.

    Returns {'jar': name, 'observed': [...], 'declared': [...]}. Raises NativeCoverageError when
    the declared supported set overstates the jars — the exact failure that ships as
    "we support Windows ARM64" while the backend has no library for it.
    """
    deps = Path(deps)
    jar = backend_jar(deps)
    observed = jar_native_rids(jar)
    declared = set(_declared().get("backendRids", {}).get("supported", []))
    overstated = declared - observed
    if overstated:
        raise NativeCoverageError(
            f"{jar.name}: declared support for {', '.join(sorted(overstated))} but the jar has no "
            f"native library for those platforms (observed: {', '.join(sorted(observed)) or 'none'}).")
    return {"jar": jar.name, "observed": sorted(observed), "declared": sorted(declared)}


def _command_list(_args: argparse.Namespace) -> int:
    declared = _declared()
    backend = declared.get("backendRids", {})
    print(f"[ARM64 模拟后端] {BACKEND_ARTIFACT_PREFIX}:{BACKEND_VERSION}")
    for rid in RIDS:
        marker = "支持" if backend_supports(rid) else "不支持"
        print(f"  {rid:<12} {marker}")
    unsupported = unsupported_rids()
    if unsupported:
        print(f"  -> 含原生 guard 的插件在这些 RID 上以 unsupported_native_library 失败：{', '.join(unsupported)}")
    assembler = declared.get("assembler", {}).get("supported", [])
    print(f"[汇编辅助 capstone/keystone/demumble] 覆盖：{', '.join(assembler) or '（无）'}")
    return 0


def _command_verify(args: argparse.Namespace) -> int:
    result = verify_deps(args.deps)
    print(f"OK {result['jar']}：实测覆盖 {', '.join(result['observed'])}")
    print(f"   声明覆盖 {', '.join(result['declared'])}（不超出实测）")
    return 0


def _command_closure(args: argparse.Namespace) -> int:
    """Binary-coverage gate: exit non-zero unless the manifest covers every requested RID.

    Read the output carefully: a green result means the *binaries* are covered, not that the release
    may proceed. `--require` no longer gates the release on its own — the corresponding-source
    obligation is a separate gate (`source-closure`) and a real release must satisfy both.
    """
    targets = [rid.strip() for rid in args.rids.split(",") if rid.strip()] if args.rids else list(RIDS)
    state = closure_state()
    print(f"native binary coverage: {len(state['supported'])}/{len(RIDS)} RIDs have upstream backend natives")
    if state["unsupported"]:
        print(f"  no native library (must fail as unsupported_native_library): {', '.join(state['unsupported'])}")
    print("  NOTE: RID coverage is a property of the shipped binaries, not of source compliance; "
          "run `source-closure` for the corresponding-source obligation.")
    if args.require:
        try:
            require_closure(targets)
        except NativeCoverageError as error:
            print(f"FAIL binary coverage incomplete: {error}", file=sys.stderr)
            return 1
        print(f"OK binary coverage complete for: {', '.join(targets)}")
    return 0


def _command_source_closure(args: argparse.Namespace) -> int:
    """Source-eligibility gate: exit non-zero unless the corresponding-source obligation is closed."""
    state = source_closure_state()
    print(f"source closure (from {SOURCE_MANIFEST.relative_to(ROOT)}): "
          f"{'complete' if state['complete'] else 'INCOMPLETE'}")
    print(f"  manifest {SOURCE_MANIFEST_COMPLETE_FIELD}: {state['summary_complete']}")
    for coordinate, status in state["artifacts"].items():
        print(f"  {coordinate}: {status}")
    if state["unclassified"]:
        print(f"  no manifest entry: {', '.join(state['unclassified'])}")
    if args.require:
        try:
            require_source_closure()
        except NativeCoverageError as error:
            print(f"FAIL source closure incomplete: {error}", file=sys.stderr)
            return 1
        print("OK source closure complete for every distributed native artifact")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description="核验上游 ARM64 原生后端的 RID 覆盖")
    subparsers = parser.add_subparsers(dest="command", required=True)
    subparsers.add_parser("list", help="打印六 RID 的原生后端覆盖结论").set_defaults(func=_command_list)
    verify = subparsers.add_parser("verify", help="从实际 deps/*.jar 重新推导覆盖并比对声明")
    verify.add_argument("--deps", type=Path, required=True, help="已 stage 的 JavaHost/deps 目录")
    verify.set_defaults(func=_command_verify)
    closure = subparsers.add_parser(
        "closure", help="打印/校验发布所需的原生二进制覆盖（默认六 RID 全覆盖才算完整）")
    closure.add_argument("--rids", default="", help="逗号分隔的待校验 RID；空表示六 RID 全量（默认）")
    closure.add_argument("--require", action="store_true", help="覆盖不完整时以退出码 1 失败")
    closure.set_defaults(func=_command_closure)
    source = subparsers.add_parser(
        "source-closure", help="打印/校验分布式原生制品的 corresponding-source 义务（独立于 RID 覆盖）")
    source.add_argument("--require", action="store_true",
                        help="源码义务未结清时以退出码 1 失败（正式发布 gate）")
    source.set_defaults(func=_command_source_closure)
    args = parser.parse_args()
    return args.func(args)


if __name__ == "__main__":
    sys.exit(main())
