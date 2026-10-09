#!/usr/bin/env python3
"""Reuse an existing green build instead of rebuilding an identical source tree.

The reuse key is the *build input tree* (git blob hashes of tracked paths that affect the binaries),
not the commit id or the version string: a rebuild happens only when something that can change the
output changed. Every candidate artifact is verified against the commit that produced it, so a stale
artifact can never be shipped just because the version happened to match.

Failures always fall back to building (never to skipping).
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import subprocess
import sys
from pathlib import Path

ARTIFACT_NAME = "vodbox.{rid}"
# Tracked paths that can change the published binaries. Docs/logs are deliberately excluded.
SOURCE_PATHS = (
    "src", "tests", "build", "Directory.Build.props", "Directory.Packages.props",
    "global.json", "VodBox.slnx", "NuGet.Config", "VERSION",
)


def git(*args: str, check: bool = True) -> str:
    return subprocess.run(["git", *args], check=check, capture_output=True, text=True).stdout


def api(path: str) -> dict:
    out = subprocess.run(["gh", "api", path], check=True, capture_output=True, text=True).stdout
    return json.loads(out)


def source_hash(ref: str) -> str:
    listing = git("ls-tree", "-r", ref, "--", *SOURCE_PATHS)
    blobs = "\n".join(line for line in listing.splitlines() if line.strip())
    return hashlib.sha256(blobs.encode()).hexdigest()


def version_at(ref: str) -> str:
    return git("show", f"{ref}:VERSION").strip()


def ensure_commit(sha: str) -> bool:
    if subprocess.run(["git", "cat-file", "-e", f"{sha}^{{commit}}"], capture_output=True).returncode == 0:
        return True
    fetch = subprocess.run(["git", "fetch", "--depth=1", "origin", sha], capture_output=True, text=True)
    return fetch.returncode == 0


def probe(rid: str, version: str, repo: str) -> dict:
    head = git("rev-parse", "HEAD").strip()
    trees = {head: source_hash(head)}
    try:
        artifacts = api(f"repos/{repo}/actions/artifacts?name={ARTIFACT_NAME.format(rid=rid)}&per_page=100")
    except Exception as error:  # noqa: BLE001 -任何 API 问题都必须回退到重新构建
        return {"reused": False, "reason": f"artifact lookup failed: {error}"}
    candidates = sorted(artifacts.get("artifacts") or [], key=lambda item: item["created_at"], reverse=True)
    for artifact in candidates:
        if artifact.get("expired"):
            continue
        run_id = artifact["workflow_run"]["id"]
        sha = artifact["workflow_run"]["head_sha"]
        try:
            run = api(f"repos/{repo}/actions/runs/{run_id}")
        except Exception:  # noqa: BLE001
            continue
        if run.get("conclusion") != "success":
            continue
        if not ensure_commit(sha):
            continue
        try:
            # 版本号会编译进程序集，任何情况下都必须匹配，同 commit 也不能例外。
            if version_at(sha) != version:
                continue
        except subprocess.CalledProcessError:
            continue
        if sha not in trees:
            trees[sha] = source_hash(sha)
        if trees[sha] != trees[head]:
            continue
        return {
            "reused": True, "reason": "identical source tree already built successfully",
            "rid": rid, "version": version, "sourceSha": sha, "sourceRunId": run_id,
            "artifactName": ARTIFACT_NAME.format(rid=rid),
        }
    return {"reused": False, "reason": "no successful artifact for this source tree"}


def main() -> int:
    parser = argparse.ArgumentParser(description="CI build reuse helper")
    parser.add_argument("command", choices=["probe", "finalize", "artifact-run"])
    parser.add_argument("--rid", default="")
    parser.add_argument("--version", default="")
    parser.add_argument("--repo", default=os.environ.get("GITHUB_REPOSITORY", ""))
    parser.add_argument("--meta", default="")
    parser.add_argument("--current-run", default="")
    args = parser.parse_args()

    if args.command == "probe":
        if not (args.rid and args.version and args.repo):
            parser.error("probe needs --rid, --version and --repo")
        result = probe(args.rid, args.version, args.repo)
        if args.meta:
            Path(args.meta).parent.mkdir(parents=True, exist_ok=True)
            Path(args.meta).write_text(json.dumps({"rid": args.rid, "version": args.version, **result}, indent=2))
        print(f"skip={'true' if result['reused'] else 'false'}")
        print(f"reason={result['reason']}", file=sys.stderr)
        return 0

    if args.command == "finalize":
        if not args.meta:
            parser.error("finalize needs --meta")
        data = json.loads(Path(args.meta).read_text())
        data["artifactRunId"] = str(data.get("sourceRunId") or args.current_run)
        Path(args.meta).write_text(json.dumps(data, indent=2))
        return 0

    if not args.meta:
        parser.error("artifact-run needs --meta")
    data = json.loads(Path(args.meta).read_text())
    print(data.get("artifactRunId") or args.current_run)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
