#!/usr/bin/env python3
"""Prove a release may proceed, before any release work is attempted.

Three independent gates, all cheap and all upstream of packaging:

  1. Binary native coverage. The coverage lock table (`build/dex-deps.json`) is the machine-checkable
     record of which RIDs the upstream ARM64 emulation backend actually ships a native library for.
     While it reports an uncovered RID, a release that includes that RID would publish a package
     whose native-guarded plugins fail at runtime. The verdict is re-derived from the lock table on
     every run — there is no "complete" flag to hand-edit.

     This gate is scoped to the RID set the release actually publishes, and it is about *binaries*:
     `win-arm64` shipping no ARM64 emulation library is a limitation on native-guarded plugins. It
     does not by itself block an ordinary Java release (that RID has its own JRE asset), so a release
     deliberately scoped to the covered RIDs passes here.

  2. Corresponding-source closure. A release also distributes native binaries (the ARM64 emulation
     backend, the unidbg guest SDK/hook libraries, JNA, …), and their corresponding-source
     obligation is recorded separately in
     `third_party/tvbox-silicon/dependency-sources/native-source-manifest.json`. While that manifest
     is `pending-with-rebuild-path`, the obligation is *open*, so a real publication must stop — no
     matter what the RID coverage says, and no matter which RIDs the release ships. This is the gate
     that a five/six RID coverage table must never be allowed to stand in for: RID coverage is about
     the *binary*, source closure is about the *source of the distributed binary*.

  3. Publish permission. The publish job needs `contents: write`. Without it, GitHub answers 403/404
     on both the git-refs and the releases endpoints and the failure only surfaces after the whole
     build/package chain has already run. This checks the effective permission up front by probing
     capabilities, not by trusting the YAML declaration.

Gate 2 is why a *real* release is currently refused; gate 1 stays reachable so an operator can dry
run / scope a build, but neither is a way around the other. Nothing here mutates repository state:
the git-ref write is exercised against an obviously invalid payload, so a 422 means "authorized,
rejected for content" while a 403/404 means "the token genuinely cannot write". That distinction is
the point.
"""
from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys

import native_coverage


def run(*args: str) -> tuple[int, str]:
    result = subprocess.run(["gh", *args], capture_output=True, text=True)
    return result.returncode, (result.stderr or result.stdout).strip()


def permissions_error(message: str) -> bool:
    lowered = message.lower()
    return "403" in lowered or "404" in lowered or "resource not accessible" in lowered


def check_native_coverage(rids: list[str] | None, allow_incomplete: bool) -> int:
    """Gate a release on the *binary* RID coverage, scoped to the RIDs it publishes.

    This is the RID-coverage gate only: it answers "does every RID in the release set have a shipped
    native backend library?". It is deliberately independent of `check_source_closure`, which covers
    the corresponding-source obligation. A release scoped to the covered RIDs (e.g. no `win-arm64`)
    may pass *this* gate while still being refused by the source gate — that is expected, because the
    two answer different questions.

    `--allow-incomplete` exists so a deliberately scoped, non-blocking dry run can observe the state,
    but the release workflow never passes it.
    """
    targets = rids or list(native_coverage.RIDS)
    state = native_coverage.closure_state()
    # A target is uncovered only if it is *in the release set* and the lock table says unsupported;
    # scoping a release to the covered RIDs must pass even while another RID stays uncovered.
    uncovered = [rid for rid in targets if rid in state["unsupported"]]
    print(f"Native binary coverage (from {json.dumps('build/dex-deps.json')}): "
          f"{len(state['supported'])}/{len(native_coverage.RIDS)} RIDs covered by the upstream backend")
    if not uncovered:
        print(f"Binary native coverage complete for: {', '.join(targets)}")
        return 0
    message = ("Native binary coverage incomplete: " + ", ".join(uncovered)
               + " have no upstream ARM64 emulation backend native library. Publishing a package for "
                 "them would ship one whose native-guarded plugins fail at runtime. (Ordinary Java "
                 "still runs there; this is a native-backend limitation, not a source obligation.)")
    if allow_incomplete:
        print(f"WARNING (--allow-incomplete): {message}")
        return 0
    print(f"Preflight failed: {message}", file=sys.stderr)
    print("Do not hand-edit the lock table to 'complete': fix the upstream dependency that ships the "
          "native library or scope the release to covered RIDs, then re-run.", file=sys.stderr)
    return 1


def check_source_closure(allow_incomplete: bool) -> int:
    """Gate a release on the corresponding-source closure of the distributed native binaries.

    While `native-source-manifest.json` is `pending-with-rebuild-path`, the obligation is open, so a
    real publication must stop. This gate is *independent of RID coverage*: it applies even to a
    release scoped to the five covered RIDs, because those five still distribute native binaries
    whose source is pending. `--allow-incomplete` lets an offline dry run observe the state; the
    release workflow never passes it.
    """
    try:
        state = native_coverage.source_closure_state()
    except native_coverage.NativeCoverageError as error:
        print(f"Preflight failed: {error}", file=sys.stderr)
        return 1
    print(f"Corresponding source (from "
          f"{native_coverage.SOURCE_MANIFEST.relative_to(native_coverage.ROOT)}): "
          f"{'complete' if state['complete'] else 'INCOMPLETE'}")
    if state["complete"]:
        print("Corresponding-source closure complete for every distributed native artifact")
        return 0
    message = ("Corresponding-source closure incomplete: the manifest is still pending the source of "
               "the distributed native binaries.")
    if allow_incomplete:
        print(f"WARNING (--allow-incomplete): {message}")
        return 0
    print(f"Preflight failed: {message}", file=sys.stderr)
    for coordinate, status in state["artifacts"].items():
        if coordinate in state["pending"]:
            print(f"  pending: {coordinate} ({status})", file=sys.stderr)
    for coordinate in state["unclassified"]:
        print(f"  no manifest entry: {coordinate}", file=sys.stderr)
    print("Do not hand-edit corresponding_source_complete to true: the per-artifact status must close "
          "(an executed rebuild with recorded output hashes, or the artifact removed from the "
          "distribution). Source closure is separate from and stricter than RID coverage, so a "
          "five/six RID coverage table does not stand in for it.", file=sys.stderr)
    return 1


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Release preflight: native binary coverage + corresponding source + publish permission")
    parser.add_argument("--rids", default="", help="Comma-separated RIDs a release would publish; empty means all six.")
    parser.add_argument("--allow-incomplete", action="store_true",
                        help="Report an incomplete closure without failing (dry runs only).")
    parser.add_argument("--skip-permissions", action="store_true",
                        help="Run the closure gates only (offline); the publish probe needs a token.")
    args = parser.parse_args()
    rids = [rid.strip() for rid in args.rids.split(",") if rid.strip()]
    # Both closure gates run, and both must pass: RID coverage is not a substitute for the source
    # obligation, and a scoped release does not escape it either.
    status = check_native_coverage(rids, args.allow_incomplete)
    if status:
        return status
    status = check_source_closure(args.allow_incomplete)
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
