#!/usr/bin/env python3
"""Prove a release may proceed, before any release work is attempted.

One release gate, plus two diagnostics, all cheap and all upstream of packaging:

  1. Redistribution obligation (the gate). A release distributes native binaries (the ARM64
     emulation backend, the unidbg guest SDK/hook libraries, JNA, …). For each one the
     redistribution obligation is verified **per component, from real evidence** — never from a
     summary flag or a status token. A GPL/copyleft native is closed only by an executed
     from-pinned-source rebuild receipt (`rebuild_native_backend.py`) recording the rebuilt library
     hash, the engine source-archive hash, the bridge source digest and a jar rewrite that keeps
     exactly the rebuilt native; no receipt ⇒ a real publication must stop. A permissive native
     carries no source obligation and is closed by its retained upstream license/notice files being
     physically present. This is why a real release is refused today, and it applies no matter which
     RIDs the release ships.

  2. Binary RID coverage (a diagnostic, never a blocker). The coverage lock table
     (`build/dex-deps.json`) records which RIDs the upstream ARM64 emulation backend ships a native
     library for. A RID like `win-arm64` with no backend native is a limitation on *native-guarded
     plugins* — an ordinary Java release still runs there (it has its own JRE asset) and the host
     fails such plugins explicitly — so this must not refuse the release. The verdict is re-derived
     from the lock table on every run; only a *malformed* table (self-contradictory or incomplete
     classification) fails here.

  3. Publish permission. The publish job needs `contents: write`. Without it, GitHub answers 403/404
     on both the git-refs and the releases endpoints and the failure only surfaces after the whole
     build/package chain has already run. This checks the effective permission up front by probing
     capabilities, not by trusting the YAML declaration.

Reading the five/six RID coverage table as the source verdict is the exact error gate 1 refuses to
make: RID coverage is about the *binary*, the redistribution obligation is about the *source of the
distributed binary*. Nothing here mutates repository state: the git-ref write is exercised against
an obviously invalid payload, so a 422 means "authorized, rejected for content" while a 403/404 means
"the token genuinely cannot write". That distinction is the point.
"""
from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
from pathlib import Path

import native_coverage


def run(*args: str) -> tuple[int, str]:
    result = subprocess.run(["gh", *args], capture_output=True, text=True)
    return result.returncode, (result.stderr or result.stdout).strip()


def permissions_error(message: str) -> bool:
    lowered = message.lower()
    return "403" in lowered or "404" in lowered or "resource not accessible" in lowered


def check_native_coverage(rids: list[str] | None, allow_incomplete: bool) -> int:
    """Report the *binary* RID coverage as a diagnostic; never block the release on it.

    A RID with no upstream ARM64 emulation backend native library is a limitation on *native-guarded
    plugins* for that RID, not a reason to refuse a release. `win-arm64` still has its own JRE asset,
    so an ordinary Java release runs there; the missing piece is the emulation library, which the
    host reports as `unsupported_native_library` rather than silently failing. Blocking the whole
    release on RID coverage conflates a per-plugin limitation with a release-wide obligation — the
    release gate's job is the redistribution/source obligation (`check_source_closure`), verified per
    distributed native component.

    So this gate emits the coverage verdict and, where a release set includes an uncovered RID,
    a warning; it returns non-zero only for a *malformed* coverage table (a self-contradictory or
    incomplete classification), which is a real error rather than a scoping choice. The `--rids`
    option still scopes the report to the RIDs a release publishes.
    """
    targets = rids or list(native_coverage.RIDS)
    try:
        state = native_coverage.closure_state()
    except native_coverage.NativeCoverageError as error:
        print(f"Preflight failed: {error}", file=sys.stderr)
        return 1
    print(f"Native binary coverage (from build/dex-deps.json): "
          f"{len(state['supported'])}/{len(native_coverage.RIDS)} RIDs covered by the upstream backend")
    uncovered = [rid for rid in targets if rid in state["unsupported"]]
    if not uncovered:
        print(f"Binary native coverage complete for: {', '.join(targets)}")
        return 0
    print(f"NOTE: native-guarded plugins are unavailable on {', '.join(uncovered)} (no upstream "
          f"ARM64 emulation backend native library). This is a per-plugin limitation, not a release "
          f"blocker: an ordinary Java package still runs there (its own JRE asset), and the host "
          f"fails such plugins explicitly as unsupported_native_library. The release gate is the "
          f"redistribution obligation verified below.")
    return 0


def check_source_closure(allow_incomplete: bool, receipts_dir: Path | None = None,
                         deps: Path | None = None) -> int:
    """Gate a release on the redistribution obligation for the distributed native components.

    This is the release gate. It walks every native the package distributes and decides, **from real
    evidence**, whether the redistribution obligation is discharged:

      * a GPL/copyleft native (the Unicorn2 engine, and Unicorn1's quarantined native) is closed
        only by an executed from-pinned-source rebuild receipt that records the rebuilt library hash,
        the engine source-archive hash, the bridge source digest and a jar rewrite that keeps exactly
        the rebuilt native. No receipt ⇒ the obligation is open, so a real publication must stop.
      * a permissive native (the guest SDK/hook libs, JNA, keystone, demumble) carries no source
        obligation; it is closed by its retained upstream license/notice files being physically
        present. A retained notice is the whole obligation, so it is read as satisfied rather than
        hard `false` — the earlier blanket summary flag is *not* the verdict.

    It is *independent of RID coverage*: it applies even to a release scope that excludes
    `win-arm64`, because the covered RIDs still distribute natives whose obligation must be settled.
    It never trusts `corresponding_source_complete` or a `status` token — the per-component evidence
    decides, so flipping the summary does not let a release through. When `deps` (the staged
    `JavaHost/deps`) is supplied, each receipt is additionally cross-checked against that actual
    package input, so a receipt for a jar the release is not shipping does not close anything.
    `--allow-incomplete` lets an offline dry run observe the state; the release workflow never
    passes it.
    """
    try:
        state = native_coverage.source_closure_state(receipts_dir, deps)
    except native_coverage.NativeCoverageError as error:
        print(f"Preflight failed: {error}", file=sys.stderr)
        return 1
    print(f"Distributed-native redistribution obligation (manifest "
          f"{native_coverage.SOURCE_MANIFEST.relative_to(native_coverage.ROOT)}, receipts "
          f"{', '.join(state['receipts']) or '(none)'}): "
          f"{'complete' if state['complete'] else 'INCOMPLETE'}")
    for coordinate, status in state["artifacts"].items():
        verdict = "closed" if coordinate not in state["pending"] else "OPEN"
        print(f"  [{verdict}] {coordinate} ({state['obligations'].get(coordinate, 'unclassified')})")
    if state["complete"]:
        print("Redistribution obligation discharged for every distributed native artifact")
        return 0
    message = ("Redistribution obligation incomplete: at least one distributed native's obligation "
               "is not discharged (a GPL native with no executed-rebuild receipt, or a notice-only "
               "native whose recorded license text is not retained).")
    if allow_incomplete:
        print(f"WARNING (--allow-incomplete): {message}")
        return 0
    print(f"Preflight failed: {message}", file=sys.stderr)
    for coordinate in state["pending"]:
        print(f"  OPEN: {coordinate} ({state['artifacts'].get(coordinate, 'unknown')}, "
              f"{state['obligations'].get(coordinate, 'unclassified')})", file=sys.stderr)
        for problem in state["problems"].get(coordinate, []):
            print(f"    - {problem}", file=sys.stderr)
    for coordinate in state["unclassified"]:
        print(f"  no recorded obligation: {coordinate}", file=sys.stderr)
    print("Do not hand-edit corresponding_source_complete to true: the per-component evidence must "
          "close (an executed rebuild receipt with recorded output hashes, or the retained license "
          "notice for a permissive native). This is separate from and stricter than RID coverage, so "
          "a five/six RID coverage table does not stand in for it, and neither does a status token.",
          file=sys.stderr)
    return 1


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Release preflight: native binary coverage + corresponding source + publish permission")
    parser.add_argument("--rids", default="", help="Comma-separated RIDs a release would publish; empty means all six.")
    parser.add_argument("--allow-incomplete", action="store_true",
                        help="Report an incomplete closure without failing (dry runs only).")
    parser.add_argument("--skip-permissions", action="store_true",
                        help="Run the closure gates only (offline); the publish probe needs a token.")
    parser.add_argument("--receipts-dir", default="",
                        help="Directory of rebuild_native_backend receipts (default: dependency-sources/rebuild-receipts).")
    parser.add_argument("--deps", default="",
                        help="Staged JavaHost/deps to cross-check receipts against the actual package input.")
    args = parser.parse_args()
    rids = [rid.strip() for rid in args.rids.split(",") if rid.strip()]
    receipts_dir = Path(args.receipts_dir) if args.receipts_dir else None
    deps = Path(args.deps) if args.deps else None
    # RID coverage is a diagnostic (a per-plugin limitation, not a release blocker); the release
    # gate is the redistribution obligation, verified per distributed native component.
    status = check_native_coverage(rids, args.allow_incomplete)
    if status:
        return status
    status = check_source_closure(args.allow_incomplete, receipts_dir, deps)
    if status or args.skip_permissions:
        return status

    repo = os.environ["GITHUB_REPOSITORY"]
    # Reading the repository is implicit for any token; a failure here is a hard error.
    status, output = run("api", f"repos/{repo}")
    if status:
        print(f"Preflight could not read {repo}: {output}", file=sys.stderr)
        return 1
    try:
        default_branch = json.loads(output).get("default_branch") or "main"
    except ValueError:
        default_branch = "main"

    # A write attempt with an empty sha is rejected on content grounds (422) when the token
    # may write refs, and with 403/404 when it may not. Nothing is created either way.
    status, output = run(
        "api", "--method", "POST", f"repos/{repo}/git/refs",
        "-f", "ref=refs/tags/vodbox-preflight-probe", "-f", "sha=",
    )
    if status and permissions_error(output):
        print("Preflight failed: GITHUB_TOKEN cannot create tags or releases. "
              "The release job requires `permissions: contents: write`.", file=sys.stderr)
        print(f"GitHub said: {output}", file=sys.stderr)
        return 1

    # Confirm the releases endpoint is reachable with the same token.
    status, output = run("api", f"repos/{repo}/releases?per_page=1")
    if status:
        print(f"Preflight could not list releases: {output}", file=sys.stderr)
        return 1
    print(f"Preflight ok: publishing capability confirmed (default branch {default_branch})")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
