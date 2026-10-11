#!/usr/bin/env python3
"""Publish verified packages, driving every step from a stable release id.

Creating a release and a missing historical tag in one call can require workflow
write permission, so the tag is created separately with contents:write. All
follow-up work is keyed on the release **id** returned by the create call: the
`releases/tags/{tag}` endpoint returns 404 for a draft, and the release *list*
endpoint is eventually consistent, so re-scanning it can miss a draft that was
created moments earlier. Never change an existing tag and never overwrite a
published asset. Reruns verify existing SHA256 digests and upload only what is
missing, then publish once every remote asset matches the local package.
"""
from __future__ import annotations

import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
from urllib.parse import quote


class PublicationError(Exception):
    """A release could not be published; the caller reports it without leaking secrets."""


def gh(*args: str) -> str:
    result = subprocess.run(["gh", *args], capture_output=True, text=True)
    if result.returncode:
        # Do not print process environments (they contain GH_TOKEN).
        raise PublicationError(result.stderr.strip() or "GitHub request failed")
    return result.stdout


def api(path: str, *extra: str) -> str:
    return gh("api", *extra, path.lstrip("/"))


def digest_of(path: Path) -> str:
    return "sha256:" + hashlib.sha256(path.read_bytes()).hexdigest()


def validate_assets(files: list[Path], existing: list[dict]) -> list[Path]:
    """Return the files still missing remotely; reject any name that already exists with different bytes."""
    by_name = {asset["name"]: asset for asset in existing}
    pending = []
    for path in files:
        asset = by_name.get(path.name)
        if asset is None:
            pending.append(path)
            continue
        # GitHub reports digests as `sha256:<hex>`; treat a missing digest as unverifiable.
        if asset.get("size") != path.stat().st_size or asset.get("digest") != digest_of(path):
            raise ValueError(f"Existing asset does not match verified package: {path.name}")
    return pending


def remote_assets(repo: str, release_id: int) -> list[dict]:
    pages = json.loads(api(f"repos/{repo}/releases/{release_id}/assets?per_page=100", "--paginate", "--slurp"))
    return [asset for page in pages for asset in page]


def ensure_tag(repo: str, tag: str, sha: str) -> None:
    """Create the tag only when absent; an existing tag must already point at the verified source."""
    refs = json.loads(api(f"repos/{repo}/git/matching-refs/tags/{tag}"))
    ref = next((item for item in refs if item["ref"] == f"refs/tags/{tag}"), None)
    if ref is None:
        api(f"repos/{repo}/git/refs", "--method", "POST",
            "-f", f"ref=refs/tags/{tag}", "-f", f"sha={sha}")
        print(f"Created source-bound tag {tag}")
        return
    obj = ref["object"]
    while obj["type"] == "tag":
        obj = json.loads(api(obj["url"]))["object"]
    if obj["type"] != "commit" or obj["sha"] != sha:
        raise ValueError("Existing tag does not point at the verified application source")


def resolve_release(repo: str, tag: str, version: str) -> int:
    """Return the release id, preferring the API-returned id over any list lookup."""
    # The list endpoint is eventually consistent; it is a rerun fallback for a draft
    # an earlier attempt already created.
    pages = json.loads(api(f"repos/{repo}/releases?per_page=100", "--paginate", "--slurp"))
    found = next((item for page in pages for item in page if item["tag_name"] == tag), None)
    if found is not None:
        return int(found["id"])
    # A failed upload leaves a recoverable draft, not a half-populated public release.
    # The create response carries the id directly, so no racy lookup is needed.
    # Auto-generated notes are a nicety; never let them block creating the draft.
    for extra in (("-F", "generate_release_notes=true"), ()):
        try:
            created = json.loads(api(
                f"repos/{repo}/releases", "--method", "POST",
                "-f", f"tag_name={tag}", "-f", f"name=VodBox {version}",
                "-F", "draft=true", *extra,
            ))
            return int(created["id"])
        except PublicationError:
            # A concurrent or half-observed create surfaces as already_exists/422; re-resolve
            # by tag on the last attempt instead of failing a draft that actually exists.
            if not extra:
                pages = json.loads(api(f"repos/{repo}/releases?per_page=100", "--paginate", "--slurp"))
                existing = next((item for page in pages for item in page if item["tag_name"] == tag), None)
                if existing is not None:
                    return int(existing["id"])
                raise
    raise PublicationError("Could not create or resolve the release draft")


def upload_pending(repo: str, release_id: int, pending: list[Path]) -> None:
    for path in pending:
        api(f"https://uploads.github.com/repos/{repo}/releases/{release_id}/assets?name={quote(path.name)}",
            "--method", "POST", "-H", "Content-Type: application/octet-stream",
            "--input", str(path))
        print(f"Uploaded {path.name}")


def publish(repo: str, version: str, sha: str) -> int:
    tag = "v" + version
    files = sorted(path for path in Path("artifacts/release").iterdir() if path.is_file())
    if not files:
        raise ValueError("No packages to publish")
    ensure_tag(repo, tag, sha)
    release_id = resolve_release(repo, tag, version)
    release = json.loads(api(f"repos/{repo}/releases/{release_id}"))
    pending = validate_assets(files, remote_assets(repo, release_id))
    if pending and not release["draft"]:
        raise ValueError("Published release is incomplete; refusing to alter public assets")
    upload_pending(repo, release_id, pending)
    # Re-read remote state: publish only once every local package is present and identical.
    missing = validate_assets(files, remote_assets(repo, release_id))
    if missing:
        raise ValueError("Remote assets still missing after upload: " + ", ".join(path.name for path in missing))
    if release["draft"]:
        gh("release", "edit", tag, "--repo", repo, "--draft=false")
    print(f"Release {tag} verified/published ({len(files)} packages)")
    return 0


def main() -> int:
    return publish(os.environ["GITHUB_REPOSITORY"], os.environ["VERSION"], os.environ["SOURCE_SHA"])


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (PublicationError, ValueError, KeyError) as error:
        print(f"Publication failed: {error}", file=sys.stderr)
        print("Check contents:write and repository rules. If GitHub still returns 403, fix the workflow "
              "permissions; do not publish from a local machine or broaden repository defaults.", file=sys.stderr)
        raise SystemExit(1)
