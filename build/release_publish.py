#!/usr/bin/env python3
"""Publish verified packages after explicitly binding the release tag to its source.

Creating a release and a missing historical tag in one API call can require workflow
write permission. Create the git ref separately with contents:write; never change an
existing tag or overwrite a published asset. Reruns verify existing SHA256 digests.
"""
from __future__ import annotations

import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys


def gh(*args: str) -> str:
    result = subprocess.run(["gh", *args], capture_output=True, text=True)
    if result.returncode:
        # Do not print process environments (they contain GH_TOKEN).
        raise RuntimeError(result.stderr.strip() or "GitHub request failed")
    return result.stdout


def validate_assets(files: list[Path], existing: list[dict]) -> list[Path]:
    by_name = {asset["name"]: asset for asset in existing}
    pending = []
    for path in files:
        asset = by_name.get(path.name)
        if asset is None:
            pending.append(path)
            continue
        digest = "sha256:" + hashlib.sha256(path.read_bytes()).hexdigest()
        if asset.get("size") != path.stat().st_size or asset.get("digest") != digest:
            raise ValueError(f"Existing asset does not match verified package: {path.name}")
    return pending


def main() -> int:
    repo = os.environ["GITHUB_REPOSITORY"]
    version = os.environ["VERSION"]
    sha = os.environ["SOURCE_SHA"]
    tag = "v" + version
    files = sorted(path for path in Path("artifacts/release").iterdir() if path.is_file())
    if not files:
        raise ValueError("No packages to publish")
    # Enumerating refs distinguishes absence from permission/network errors.
    refs = json.loads(gh("api", f"repos/{repo}/git/matching-refs/tags/{tag}"))
    ref = next((item for item in refs if item["ref"] == f"refs/tags/{tag}"), None)
    if ref is None:
        gh("api", "--method", "POST", f"repos/{repo}/git/refs", "-f", f"ref=refs/tags/{tag}", "-f", f"sha={sha}")
        print(f"Created source-bound tag {tag}")
    else:
        obj = ref["object"]
        while obj["type"] == "tag":
            obj = json.loads(gh("api", obj["url"]))["object"]
        if obj["type"] != "commit" or obj["sha"] != sha:
            raise ValueError("Existing tag does not point at the verified application source")
    releases = json.loads(gh("api", "--paginate", "--slurp", f"repos/{repo}/releases?per_page=100"))
    release = next((item for page in releases for item in page if item["tag_name"] == tag), None)
    if release is None:
        # A failed upload leaves a recoverable draft, not a half-populated public release.
        gh("release", "create", tag, "--repo", repo, "--verify-tag", "--draft", "--title", f"VodBox {version}", "--generate-notes")
        # The tags endpoint can return 404 for a newly-created draft. Enumerate
        # authenticated releases instead; drafts are keyed by id until publication.
        pages = json.loads(gh("api", "--paginate", "--slurp", f"repos/{repo}/releases?per_page=100"))
        release = next((item for page in pages for item in page if item["tag_name"] == tag), None)
        if release is None:
            raise ValueError("Created release draft could not be found")
    existing = json.loads(gh("api", "--paginate", "--slurp", release["assets_url"]))
    pending = validate_assets(files, [asset for page in existing for asset in page])
    if pending and not release["draft"]:
        raise ValueError("Published release is incomplete; refusing to alter public assets")
    if pending:
        gh("release", "upload", tag, *(str(path) for path in pending), "--repo", repo)
    if release["draft"]:
        gh("release", "edit", tag, "--repo", repo, "--draft=false")
    print(f"Release {tag} verified/published ({len(files)} packages)")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (RuntimeError, ValueError, KeyError) as error:
        print(f"Publication failed: {error}", file=sys.stderr)
        print("Check contents:write and repository rules. If GitHub still returns 403, use an explicitly approved publishing credential; do not broaden repository defaults.", file=sys.stderr)
        raise SystemExit(1)
