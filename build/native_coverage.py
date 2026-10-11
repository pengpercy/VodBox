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

# NOTE: the manifest's per-artifact `status` token is *informational* here, never a verdict. A
# status of "matched-release-source-collected" on the GPL engine must not close the obligation — a
# token is not evidence of the distributed binary's source. The verdict is derived per component
# from real evidence (an executed rebuild receipt, or a retained notice file) below.

# The upstream JavaHost pom at the pinned tvbox-silicon commit. Both coordinates must be present in
# the resolved `deps/` for this component to be the one the coverage table describes.
UPSTREAM_POM = ROOT / "third_party/tvbox-silicon/Runtime/JavaHost/pom.xml"
BACKEND_ARTIFACT_PREFIX = "unidbg-unicorn2"
BACKEND_VERSION = "0.9.8"

# Where `rebuild_native_backend.py` writes its per-RID receipts. A receipt is the *evidence* a
# distributed native was rebuilt from pinned source: it records the built library hash, the engine
# source-archive hash, the bridge source digest and the jar-rewrite that removed every foreign
# native. The source gate reads these receipts and re-derives the obligation per distributed
# native — it never accepts the manifest's summary flag or a bare status token as the verdict.
RECEIPTS_DIR = ROOT / "third_party/tvbox-silicon/dependency-sources/rebuild-receipts"

# Which obligation a distributed native component carries, and what actually discharges it. This is
# the per-component audit surface the source gate walks — *not* the manifest's summary flag. Two
# obligation classes exist and they must not be merged:
#
#   * "gpl-corresponding-source"  — a GPL/copyleft native statically linked into a distributed
#     library. Only an executed from-pinned-source rebuild with a recorded output hash (a
#     `rebuild_native_backend` receipt) closes it, or removal from the distribution.
#   * "permissive-notice-only"    — a permissive (BSD/ISC/MIT/zlib/Apache) native. Redistribution
#     requires the upstream license text(s) + copyright + disclaimer be *retained*; there is no
#     source obligation. Closed by the retained notice files actually being present on disk.
#
# `notice_dir` names the retained license material for the permissive class: the upstream license /
# notice files under `dependency-notices/<notice_dir>/`. A component whose notices are *not*
# retained stays open (fail closed) — the earlier hard `corresponding_source_complete=false`
# blanket is replaced by reading the real per-component obligation. For `unidbg-android` the
# manifest additionally records the exact retained notice paths (`licenses_physically_retained`),
# and those are re-resolved to real files rather than trusted as strings.
NATIVE_COMPONENT_OBLIGATIONS = {
    "com.github.zhkl0228:unidbg-unicorn2:0.9.8": {
        "class": "gpl-corresponding-source",
        "receipts": ("backend-0.9.8",),
        "note": "Unicorn2 engine statically linked into the distributed libunicorn.{so,dylib}/"
                "unicorn.dll; the only full-source obligation. Closed by an executed rebuild receipt.",
    },
    "com.github.zhkl0228:unicorn:1.0.14": {
        "class": "gpl-corresponding-source",
        "receipts": ("backend-0.9.8",),
        "note": "Unicorn1 Java parent types keep their classes, but the GPL native natives/* is "
                "quarantined; the receipt records the removed entries + pinned source archive.",
    },
    "com.github.zhkl0228:unidbg-android:0.9.8": {
        "class": "permissive-notice-only",
        "notice_dir": "unidbg-android-0.9.8",
        "evidence": "licenses_physically_retained",
        "evidence_base": "unidbg-android-0.9.8",
        "note": "34 guest SDK/hook natives: all permissive (bionic BSD, libc++ MIT/Illinois, zlib, "
                "OpenSSL 1.0.1e, BoringSSL, xHook MIT/BSD, whale/Dobby/HookZz Apache-2.0). "
                "Notice-only; the GPL obligation belongs to the engine above, not here.",
    },
    "com.github.zhkl0228:keystone:0.9.7": {
        "class": "permissive-notice-only",
        "notice_dir": "keystone-0.9.7",
        "note": "keystone assembler native, permissive (LLVM/BSD + Apache on the Java binding). "
                "Notice-only.",
    },
    "com.github.zhkl0228:demumble:1.0.4": {
        "class": "permissive-notice-only",
        "notice_dir": "demumble-1.0.4",
        "note": "demumble demangler native, permissive (Apache-2.0/BSD); notice-only.",
    },
    "net.java.dev.jna:jna:5.10.0": {
        "class": "permissive-notice-only",
        "notice_dir": "jna-5.10.0",
        "note": "JNA is dual Apache-2.0/LGPL-2.1; distributed under Apache-2.0 (notice-only).",
    },
}

# A retained notice path in the manifest is only "retained" if the file actually resolves under
# the source tree. A recorded path is evidence, not proof; the gate re-checks existence.
DEPENDENCY_SOURCES = ROOT / "third_party/tvbox-silicon/dependency-sources"
DEPENDENCY_NOTICES = ROOT / "third_party/tvbox-silicon/dependency-notices"

# The license-file names a notice directory is expected to carry. A directory with none of these is
# not "retained license material" and must not close a notice-only obligation.
NOTICE_FILE_NAMES = ("LICENSE", "LICENSE.txt", "LICENSE.TXT", "LICENSE-COM.TXT", "COPYING",
                     "NOTICE", "LICENSE.md")


def sha256_file(path: Path) -> str:
    import hashlib

    with Path(path).open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def _backend_jar_glob() -> str:
    return f"{BACKEND_ARTIFACT_PREFIX}-*.jar"

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


def load_receipts(receipts_dir: Path | None = None) -> dict[str, dict]:
    """Every `rebuild_native_backend` receipt on disk, keyed by the key its filename carries.

    A receipt is the *evidence* a distributed native was rebuilt from pinned source. The gate reads
    the receipts rather than trusting the manifest's summary: a receipt records the built library
    hash, the engine source-archive hash, the bridge digest and the jar rewrite that removed every
    foreign native. A release with no receipt for a distributed GPL native is blocked — the default
    "old library, no receipt" case stays closed.

    Receipt files are named `<key>.json` (e.g. `backend-0.9.8.json`) or carry a `"key"` field; a
    bare `backend-<version>.json` is what `rebuild_native_backend.py --receipt` writes.
    """
    directory = Path(receipts_dir) if receipts_dir is not None else RECEIPTS_DIR
    receipts: dict[str, dict] = {}
    if not directory.is_dir():
        return receipts
    for path in sorted(directory.glob("*.json")):
        try:
            receipt = json.loads(path.read_text(encoding="utf-8"))
        except (OSError, ValueError):
            continue
        key = receipt.get("key") or path.stem
        receipts[key] = receipt
    return receipts


def receipt_problems(receipt: dict) -> list[str]:
    """The real per-native evidence a receipt must carry to close a GPL source obligation.

    This is deliberately the same evidence `rebuild_native_backend.verify_receipt` produces, read
    back for the gate: a recorded output library hash, the engine source-archive hash, the bridge
    source digest and the jar integration that keeps exactly this RID's rebuilt native. A receipt
    that records only a status or a version string does not close anything.
    """
    problems: list[str] = []
    backend = receipt.get("backend") or {}
    library = backend.get("output_library") or {}
    if not library.get("sha256"):
        problems.append("no rebuilt output library sha256")
    engine = backend.get("engine") or {}
    if not engine.get("source_archive_sha256"):
        problems.append("no engine source-archive sha256 (the corresponding source is not pinned)")
    bridge = backend.get("bridge") or {}
    if not bridge.get("source_digest_sha256"):
        problems.append("no bridge source digest")
    jar = backend.get("jar_integration") or {}
    entries = jar.get("jar_native_entries") or []
    if len(entries) != 1 or entries != [jar.get("native_entry")]:
        problems.append(
            f"the package would still carry foreign natives ({entries}); only the rebuilt native "
            f"may be distributed")
    unicorn1 = receipt.get("unicorn1")
    if unicorn1 is None:
        problems.append("no unicorn:1.0.14 quarantine record (its GPL native must be withheld)")
    else:
        if unicorn1.get("kept_dependency") is not True:
            problems.append("unicorn:1.0.14 must stay (parent types) with only its native withheld")
        if not (unicorn1.get("source_evidence") or {}).get("archive_sha256"):
            problems.append("unicorn:1.0.14 has no pinned source archive hash")
    return problems


def published_input_problems(receipt: dict, deps: Path) -> list[str]:
    """Cross-check a receipt against the *actual* staged package input (`deps/*.jar`).

    A receipt is only evidence about the binaries a release distributes if it agrees with the jars
    the release would package. This re-derives the backend jar's `natives/` entries and the
    quarantined `unicorn:1.0.14` jar from disk and compares them to what the receipt records — so a
    receipt that describes a jar the release is not actually shipping is caught, and a stale jar
    whose native changed after the rebuild is caught too. Returns the list of problems (empty is
    good). When `deps` carries no staged jar for this component, nothing is claimed and nothing is
    reported: absence of the input is not evidence either way (the caller decides that separately).
    """
    problems: list[str] = []
    backend = receipt.get("backend") or {}

    def native_entries(jar: Path) -> list[str]:
        with zipfile.ZipFile(jar) as archive:
            return sorted(n for n in archive.namelist()
                          if n.startswith("natives/") and not n.endswith("/"))

    backend_jars = sorted(Path(deps).glob(_backend_jar_glob()))
    # A receipt records at most one RID's rebuilt native; compare against the single matching jar.
    for jar in backend_jars:
        integration = backend.get("jar_integration") or {}
        recorded = integration.get("jar_native_entries") or []
        if recorded and native_entries(jar) != recorded:
            problems.append(f"{jar.name}: packaged natives/ {native_entries(jar)} do not match the "
                            f"receipt's {recorded}")
        if integration.get("post_rewrite_jar_sha256") and \
                sha256_file(jar) != integration["post_rewrite_jar_sha256"]:
            problems.append(f"{jar.name}: staged jar hash does not match the receipt (input changed "
                            f"after the rebuild)")

    unicorn1 = receipt.get("unicorn1")
    if unicorn1 is not None:
        for jar in sorted(Path(deps).glob("unicorn-1.0.14.jar")):
            leftover = [n for n in zipfile.ZipFile(jar).namelist() if n.startswith("natives/")]
            if leftover:
                problems.append(f"{jar.name}: packaged jar still carries GPL native {leftover}; the "
                                f"receipt says it was withheld")
    return problems


def _notice_files(entry: dict, obligation: dict) -> list[Path]:
    """The retained notice files for a notice-only component, resolved to real paths.

    Two sources are consulted, both read as evidence rather than trusted strings:

      * the component's `notice_dir` under `dependency-notices/` — its real license files;
      * the manifest's `license_facts.<evidence>` list where present (unidbg-android records the
        exact retained texts). Each record is "<description> - <path>", the path being relative to
        that component's notice directory.
    """
    resolved: list[Path] = []
    notice_dir = obligation.get("notice_dir")
    if notice_dir:
        root = DEPENDENCY_NOTICES / notice_dir
        for name in NOTICE_FILE_NAMES:
            for candidate in (root / "upstream" / name, root / "jar-metadata/META-INF" / name):
                if candidate.is_file():
                    resolved.append(candidate)
    evidence_field = obligation.get("evidence")
    if evidence_field:
        facts = entry.get("license_facts") or {}
        base = DEPENDENCY_NOTICES / obligation.get("evidence_base", obligation.get("notice_dir", ""))
        for item in facts.get(evidence_field) or []:
            # "<description> - <path>"; take the trailing path token after the last " - ".
            candidate = item.rsplit(" - ", 1)[-1].strip()
            resolved.extend(_expand_braces(base / candidate))
    return resolved


def _expand_braces(path: Path) -> list[Path]:
    """Expand a `{a,b}` shorthand in a recorded notice path into the real sibling paths.

    The manifest records e.g. `libc-NOTICE.android-{6.0.1_r1,4.4_r1}.txt`; both expanded files are
    real retained notices, so the shorthand must resolve to them rather than being taken literally
    (which would look like a missing file and wrongly keep the obligation open).
    """
    text = str(path)
    start = text.find("{")
    end = text.find("}", start + 1)
    if start == -1 or end == -1:
        return [path]
    prefix, body, suffix = text[:start], text[start + 1:end], text[end + 1:]
    return [Path(prefix + option + suffix) for option in body.split(",")]


def _component_verdict(coordinate: str, entry: dict | None, receipts: dict[str, dict],
                      deps: Path | None = None) -> tuple[bool, list[str], str]:
    """Decide whether one distributed native component's obligation is discharged, from real evidence.

    Returns (closed, problems, obligation_class). A component absent from the manifest is *not*
    closed: every distributed native must carry a recorded obligation, so an unlisted one fails
    closed rather than riding on the summary.
    """
    obligation = NATIVE_COMPONENT_OBLIGATIONS.get(coordinate)
    if obligation is None:
        return False, [f"no obligation is recorded for distributed native {coordinate}"], "unclassified"
    kind = obligation["class"]
    if entry is None:
        return False, [f"no manifest entry for {coordinate}"], kind

    if kind == "gpl-corresponding-source":
        problems: list[str] = []
        matched = False
        for key in obligation.get("receipts", ()):
            receipt = receipts.get(key)
            if receipt is None:
                continue
            # A receipt proves the source of the native it rewrote; the coordinate must be the one
            # the receipt wrote, or the evidence belongs to a different component.
            if receipt.get("backend", {}).get("coordinate") != coordinate and \
                    (receipt.get("unicorn1") or {}).get("coordinate") != coordinate:
                continue
            matched = True
            for problem in receipt_problems(receipt):
                problems.append(f"{key}: {problem}")
            if deps is not None:
                for problem in published_input_problems(receipt, deps):
                    problems.append(f"{key}: {problem}")
        if not matched:
            # The default blocked case: a distributed GPL native with no executed-rebuild receipt.
            problems.append(
                f"no rebuild receipt closes the corresponding-source obligation for {coordinate}; "
                f"retaining an upstream prebuilt native does not discharge it")
        return (not problems), problems, kind

    # permissive-notice-only: closed by the retained license/notice files actually being on disk.
    files = _notice_files(entry, obligation)
    if not files:
        return False, [f"{coordinate}: no retained license notice is on disk "
                       f"(dependency-notices/{obligation.get('notice_dir', '?')})"], kind
    missing = [path for path in files if not path.is_file()]
    if missing:
        return False, [f"{coordinate}: recorded notice not retained: {path}" for path in missing], kind
    return True, [], kind


def source_closure_state(receipts_dir: Path | None = None, deps: Path | None = None) -> dict:
    """The *source/license* closure verdict for every distributed native, from real evidence.

    This answers a different question than `closure_state`: not "which RIDs have a native library",
    but "is the redistribution obligation for the distributed natives actually discharged?". The
    verdict is assembled **per component** from evidence, never from the manifest's summary flag or
    a status token:

      * a **GPL corresponding-source** native is closed only by an executed from-pinned-source
        rebuild receipt (`rebuild_native_backend.py`), re-verified here to record the rebuilt
        library hash, the engine source-archive hash, the bridge digest and a jar rewrite that
        keeps exactly the rebuilt native. No receipt ⇒ blocked (the "old library without receipt"
        default). A receipt whose evidence does not actually cover the component does not close it.
      * a **permissive notice-only** native is closed by its retained upstream license/notice files
        being physically present (the guest-license facts). It carries *no* source obligation, so a
        retained notice is the whole obligation — recorded as satisfied, not hard-coded false.
      * a native with no recorded obligation, or no manifest entry, fails closed.

    The manifest's `corresponding_source_complete` is still reported (`summary_complete`) so a
    partial/hand-edited summary is visible, but `complete` is derived from the per-component
    evidence alone: flipping the summary does not close anything.

    When `deps` is supplied (a staged `JavaHost/deps` directory), each closing receipt is also
    cross-checked against the *actual* package input on disk — the backend jar's `natives/` tree and
    the quarantined `unicorn:1.0.14` jar — so a receipt for a jar the release is not shipping (or a
    jar that changed after the rebuild) does not close the obligation.

    Returns {'complete': bool, 'summary_complete': bool, 'pending': [...], 'unclassified': [...],
             'artifacts': {coordinate: status}, 'obligations': {coordinate: class},
             'problems': {coordinate: [reasons]}, 'receipts': [...keys]}.
    """
    manifest = _source_manifest()
    summary_complete = bool(manifest.get(SOURCE_MANIFEST_COMPLETE_FIELD, False))
    entries = _manifest_artifacts(manifest)
    receipts = load_receipts(receipts_dir)
    artifacts: dict[str, str] = {}
    obligations: dict[str, str] = {}
    problems: dict[str, list[str]] = {}
    pending: list[str] = []
    unclassified: list[str] = []
    for coordinate in DISTRIBUTED_SOURCE_ARTIFACTS:
        entry = entries.get(coordinate)
        artifacts[coordinate] = str((entry or {}).get("status", "no-manifest-entry"))
        closed, reasons, kind = _component_verdict(coordinate, entry, receipts, deps)
        obligations[coordinate] = kind
        if kind == "unclassified":
            unclassified.append(coordinate)
        if reasons:
            problems[coordinate] = reasons
        if not closed:
            pending.append(coordinate)
    return {
        # The verdict is derived from the per-component evidence, never from the summary flag.
        "complete": not pending and not unclassified,
        "summary_complete": summary_complete,
        "pending": pending,
        "unclassified": unclassified,
        "artifacts": artifacts,
        "obligations": obligations,
        "problems": problems,
        "receipts": sorted(receipts),
    }


def require_source_closure(receipts_dir: Path | None = None, deps: Path | None = None) -> dict:
    """Raise unless the redistribution obligation for the distributed natives is discharged.

    This is the gate that must stop a *real* release while a distributed GPL native has no executed
    from-pinned-source rebuild receipt, or while a recorded notice is not actually retained. It is
    independent of the binary-closure gate: a release can be binary-complete and still blocked here,
    and a host can decline to distribute an artifact and still pass here. The verdict is re-derived
    from the per-component evidence (`source_closure_state`), never from a hand-written summary, so
    editing `corresponding_source_complete` to `true` fails closed instead of shipping.
    """
    state = source_closure_state(receipts_dir, deps)
    if state["unclassified"]:
        raise NativeCoverageError(
            "source closure cannot be established: no recorded obligation for "
            + ", ".join(state["unclassified"])
            + " — a distributed native artifact must have a recorded obligation verdict.")
    if state["pending"]:
        detail = "; ".join(
            f"{coordinate}: {', '.join(state['problems'].get(coordinate, ['open']))}"
            for coordinate in state["pending"])
        raise NativeCoverageError(
            "source closure is incomplete — the redistribution obligation for the distributed "
            "natives is not discharged. " + detail)
    if not state["summary_complete"]:
        # Every component's evidence is closed, but the manifest's own summary still reads false: the
        # summary is derived and must be reconciled before a real publication, so stay fail-closed.
        raise NativeCoverageError(
            f"source closure is inconsistent: every distributed native's obligation is discharged "
            f"but the manifest's {SOURCE_MANIFEST_COMPLETE_FIELD} is still false; the summary is "
            f"derived and must be reconciled with the per-component evidence before publishing.")
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
    """Redistribution-obligation gate: exit non-zero unless every distributed native is discharged."""
    receipts_dir = Path(args.receipts_dir) if getattr(args, "receipts_dir", "") else None
    deps = Path(args.deps) if getattr(args, "deps", "") else None
    state = source_closure_state(receipts_dir, deps)
    print(f"source/license closure (manifest {SOURCE_MANIFEST.relative_to(ROOT)}): "
          f"{'complete' if state['complete'] else 'INCOMPLETE'}")
    print(f"  manifest {SOURCE_MANIFEST_COMPLETE_FIELD}: {state['summary_complete']} "
          f"(reported, not the verdict; the verdict is per-component evidence)")
    print(f"  receipts on disk: {', '.join(state['receipts']) or '(none)'}")
    for coordinate, status in state["artifacts"].items():
        kind = state["obligations"].get(coordinate, "unclassified")
        verdict = "closed" if coordinate not in state["pending"] else "OPEN"
        print(f"  [{verdict}] {coordinate} ({kind}): {status}")
        for problem in state["problems"].get(coordinate, []):
            print(f"      - {problem}")
    if state["unclassified"]:
        print(f"  no recorded obligation: {', '.join(state['unclassified'])}")
    if args.require:
        try:
            require_source_closure(receipts_dir, deps)
        except NativeCoverageError as error:
            print(f"FAIL source closure incomplete: {error}", file=sys.stderr)
            return 1
        print("OK redistribution obligation discharged for every distributed native artifact")
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
        "source-closure", help="打印/校验分布式原生制品的再分发义务（逐组件证据，独立于 RID 覆盖）")
    source.add_argument("--require", action="store_true",
                        help="义务未结清时以退出码 1 失败（正式发布 gate）")
    source.add_argument("--receipts-dir", default="",
                        help="rebuild_native_backend 回执目录（默认 dependency-sources/rebuild-receipts）")
    source.add_argument("--deps", default="",
                        help="已 stage 的 JavaHost/deps：用实际打包输入交叉核验回执（可选）")
    source.set_defaults(func=_command_source_closure)
    args = parser.parse_args()
    return args.func(args)


if __name__ == "__main__":
    sys.exit(main())
