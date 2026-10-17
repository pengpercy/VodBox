"""Offline release safety regressions: no credentials, network or publication."""
import hashlib
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
from urllib.parse import unquote
from zipfile import ZipFile

import release_publish as publish


def publication_fixture():
    runtime = Path("artifacts/release")
    runtime.mkdir(parents=True)
    for rid in publish.RIDS:
        with ZipFile(runtime / f"VodBox_1.0.0.{rid}.zip", "w") as archive:
            archive.writestr("VodBox/README.txt", f"Offline installer fixture for {rid}\n")


def remote_fixture(uploaded=None):
    files = sorted(Path("artifacts/release").iterdir())
    if uploaded is not None:
        names = {unquote(url.rsplit("name=", 1)[1]) for url in uploaded}
        files = [path for path in files if path.name in names]
    return [{"name": path.name, "size": path.stat().st_size,
             "digest": "sha256:" + hashlib.sha256(path.read_bytes()).hexdigest()}
            for path in files]


class ReleasePublishTests(unittest.TestCase):
    def test_existing_assets_must_match(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "package.zip"
            path.write_bytes(b"verified")
            asset = {"name": path.name, "size": 8, "digest": "sha256:" + hashlib.sha256(b"verified").hexdigest()}
            self.assertEqual([], publish.validate_assets([path], [asset]))
            self.assertEqual([path], publish.validate_assets([path], []))
            asset["digest"] = "sha256:wrong"
            with self.assertRaises(ValueError):
                publish.validate_assets([path], [asset])

    def test_missing_tag_is_created_before_release_and_verify_tag_is_required(self):
        with tempfile.TemporaryDirectory() as directory:
            old = os.getcwd()
            try:
                os.chdir(directory)
                publication_fixture()
                calls = []
                uploaded = []
                def gh(*args):
                    calls.append(args)
                    if "matching-refs" in " ".join(args):
                        return "[]"
                    if args[-1] == "repos/owner/repo/releases":
                        return json.dumps({"id": 42, "draft": True})
                    if "uploads.github.com" in args[-1]:
                        uploaded.append(args[-1])
                        return "{}"
                    if args[-1] == "repos/owner/repo/releases/42/assets?per_page=100":
                        # Remote state only shows the asset once the upload happened.
                        if not uploaded:
                            return json.dumps([[]])
                        return json.dumps([remote_fixture(uploaded)])
                    if args[-1] == "repos/owner/repo/releases/42":
                        return json.dumps({"id": 42, "draft": True})
                    return "{}"
                with patch.dict(os.environ, GITHUB_REPOSITORY="owner/repo", VERSION="1.0.0", SOURCE_SHA="abc"), patch.object(publish, "gh", gh):
                    self.assertEqual(0, publish.main())
                ref_index = next(i for i, args in enumerate(calls) if "repos/owner/repo/git/refs" in args)
                create_index = next(i for i, args in enumerate(calls) if args[-1] == "repos/owner/repo/releases")
                self.assertLess(ref_index, create_index)
                # The tag must already exist before the release is created against it.
                self.assertIn("draft=true", calls[create_index])
                self.assertIn("tag_name=v1.0.0", calls[create_index])
                self.assertFalse(any("--clobber" in args for args in calls))
            finally:
                os.chdir(old)

    def test_draft_is_resolved_by_returned_id_not_by_racing_list_lookup(self):
        """A draft missing from the immediately-following list call must still be found by id."""
        with tempfile.TemporaryDirectory() as directory:
            old = os.getcwd()
            try:
                os.chdir(directory)
                publication_fixture()
                calls = []
                uploaded = []
                def gh(*args):
                    calls.append(args)
                    if "matching-refs" in " ".join(args):
                        return "[]"
                    # Creation returns the id; every later lookup by that id succeeds.
                    if args[-1] == "repos/owner/repo/releases":
                        return json.dumps({"id": 4242, "draft": True})
                    if "uploads.github.com" in args[-1]:
                        uploaded.append(args[-1])
                        return "{}"
                    if args[-1] == "repos/owner/repo/releases/4242/assets?per_page=100":
                        if not uploaded:
                            return json.dumps([[]])
                        return json.dumps([remote_fixture(uploaded)])
                    if args[-1] == "repos/owner/repo/releases/4242":
                        return json.dumps({"id": 4242, "draft": True})
                    return "{}"
                with patch.dict(os.environ, GITHUB_REPOSITORY="owner/repo", VERSION="1.0.0", SOURCE_SHA="abc"), patch.object(publish, "gh", gh):
                    self.assertEqual(0, publish.main())
                # The stale list is only ever a rerun probe: the fresh draft is resolved and
                # driven by the id returned from creation, never by that list lookup.
                self.assertTrue(any(args[-1] == "repos/owner/repo/releases/4242" for args in calls))
                self.assertTrue(any("releases/4242/assets" in args[-1] for args in calls))
                published = [args for args in calls if args[:2] == ("release", "edit")]
                self.assertEqual(1, len(published))
                self.assertIn("--draft=false", published[0])
            finally:
                os.chdir(old)

    def test_draft_creation_survives_a_failing_notes_api(self):
        """Auto-generated notes are optional; a notes failure must not block the draft."""
        with tempfile.TemporaryDirectory() as directory:
            old = os.getcwd()
            try:
                os.chdir(directory)
                publication_fixture()
                uploaded = []
                calls = []
                def gh(*args):
                    calls.append(args)
                    if "matching-refs" in " ".join(args):
                        return "[]"
                    if args[-1] == "repos/owner/repo/releases?per_page=100":
                        return json.dumps([[]])
                    if args[-1] == "repos/owner/repo/releases":
                        if "generate_release_notes=true" in args:
                            raise publish.PublicationError("HTTP 422: notes generation unavailable")
                        return json.dumps({"id": 77, "draft": True})
                    if "uploads.github.com" in args[-1]:
                        uploaded.append(args[-1])
                        return "{}"
                    if args[-1] == "repos/owner/repo/releases/77/assets?per_page=100":
                        if not uploaded:
                            return json.dumps([[]])
                        return json.dumps([remote_fixture(uploaded)])
                    if args[-1] == "repos/owner/repo/releases/77":
                        return json.dumps({"id": 77, "draft": True})
                    return "{}"
                with patch.dict(os.environ, GITHUB_REPOSITORY="owner/repo", VERSION="1.0.0", SOURCE_SHA="abc"), patch.object(publish, "gh", gh):
                    self.assertEqual(0, publish.main())
                creates = [args for args in calls if args[-1] == "repos/owner/repo/releases"]
                self.assertEqual(2, len(creates))
                self.assertNotIn("generate_release_notes=true", creates[1])
            finally:
                os.chdir(old)

    def test_published_release_is_read_only_when_all_assets_match(self):
        with tempfile.TemporaryDirectory() as directory:
            old = os.getcwd()
            try:
                os.chdir(directory)
                publication_fixture()
                calls = []
                def gh(*args):
                    calls.append(args)
                    if "matching-refs" in " ".join(args):
                        return json.dumps([{"ref": "refs/tags/v1.0.0", "object": {"type": "commit", "sha": "abc"}}])
                    if args[-1] == "repos/owner/repo/releases?per_page=100":
                        return json.dumps([[{"tag_name": "v1.0.0", "id": 7, "draft": False}]])
                    if args[-1] == "repos/owner/repo/releases/7":
                        return json.dumps({"id": 7, "draft": False})
                    if args[-1] == "repos/owner/repo/releases/7/assets?per_page=100":
                        return json.dumps([remote_fixture()])
                    return "{}"
                with patch.dict(os.environ, GITHUB_REPOSITORY="owner/repo", VERSION="1.0.0", SOURCE_SHA="abc"), patch.object(publish, "gh", gh):
                    self.assertEqual(0, publish.main())
                self.assertFalse(any(args[0] == "release" for args in calls))
                self.assertFalse(any("--method" in args and "POST" in args for args in calls))
            finally:
                os.chdir(old)

    def test_tag_creation_failure_stops_without_release_fallback(self):
        with tempfile.TemporaryDirectory() as directory:
            old = os.getcwd()
            try:
                os.chdir(directory)
                publication_fixture()
                calls = []
                def gh(*args):
                    calls.append(args)
                    if "matching-refs" in " ".join(args):
                        return "[]"
                    raise publish.PublicationError("HTTP 403: Resource not accessible by integration")
                with patch.dict(os.environ, GITHUB_REPOSITORY="owner/repo", VERSION="1.0.0", SOURCE_SHA="abc"), patch.object(publish, "gh", gh):
                    with self.assertRaises(publish.PublicationError):
                        publish.main()
                self.assertEqual(2, len(calls))
                self.assertFalse(any(args[0] == "release" for args in calls))
            finally:
                os.chdir(old)

    def test_mismatched_tag_stops_before_publication(self):
        with tempfile.TemporaryDirectory() as directory:
            old = os.getcwd()
            try:
                os.chdir(directory)
                publication_fixture()
                calls = []
                def gh(*args):
                    calls.append(args)
                    return json.dumps([{"ref": "refs/tags/v1.0.0", "object": {"type": "commit", "sha": "wrong"}}])
                with patch.dict(os.environ, GITHUB_REPOSITORY="owner/repo", VERSION="1.0.0", SOURCE_SHA="abc"), patch.object(publish, "gh", gh):
                    with self.assertRaises(ValueError):
                        publish.main()
                self.assertEqual(1, len(calls))
            finally:
                os.chdir(old)

    def test_upload_is_idempotent_and_verified_before_publish(self):
        """Every local package must be present with matching size+digest before the draft is published."""
        with tempfile.TemporaryDirectory() as directory:
            old = os.getcwd()
            try:
                os.chdir(directory)
                publication_fixture()
                uploaded = []
                state = {"upload_calls": 0}
                def gh(*args):
                    if "matching-refs" in " ".join(args):
                        return "[]"
                    if args[-1] == "repos/owner/repo/releases":
                        return json.dumps({"id": 99, "draft": True})
                    if args[-1] == "repos/owner/repo/releases/99":
                        return json.dumps({"id": 99, "draft": True})
                    if args[-1] == "repos/owner/repo/releases/99/assets?per_page=100":
                        # First read: nothing yet. Later reads: only uploaded assets, verified.
                        if state["upload_calls"] == 0:
                            return json.dumps([[]])
                        return json.dumps([remote_fixture(uploaded)])
                    if "uploads.github.com" in args[-1]:
                        state["upload_calls"] += 1
                        uploaded.append(args[-1])
                        return "{}"
                    return "{}"
                with patch.dict(os.environ, GITHUB_REPOSITORY="owner/repo", VERSION="1.0.0", SOURCE_SHA="abc"), patch.object(publish, "gh", gh):
                    self.assertEqual(0, publish.main())
                self.assertEqual(len(publish.RIDS), len(uploaded))
                self.assertEqual({url.rsplit("name=", 1)[1] for url in uploaded},
                                 {item["name"] for item in remote_fixture()})
                self.assertTrue(any("releases/99/assets?name=VodBox_1.0.0.linux-x64.zip" in url
                                    for url in uploaded))
            finally:
                os.chdir(old)

    def test_publish_is_refused_while_a_local_package_is_still_missing_remotely(self):
        with tempfile.TemporaryDirectory() as directory:
            old = os.getcwd()
            try:
                os.chdir(directory)
                publication_fixture()
                calls = []
                def gh(*args):
                    calls.append(args)
                    if "matching-refs" in " ".join(args):
                        return "[]"
                    if args[-1] == "repos/owner/repo/releases":
                        return json.dumps({"id": 5, "draft": True})
                    if args[-1] == "repos/owner/repo/releases/5":
                        return json.dumps({"id": 5, "draft": True})
                    if args[-1] == "repos/owner/repo/releases/5/assets?per_page=100":
                        return json.dumps([[]])
                    if "uploads.github.com" in args[-1]:
                        return "{}"
                    return "{}"
                with patch.dict(os.environ, GITHUB_REPOSITORY="owner/repo", VERSION="1.0.0", SOURCE_SHA="abc"), patch.object(publish, "gh", gh):
                    with self.assertRaises(ValueError):
                        publish.main()
                self.assertFalse(any(args[0] == "release" for args in calls))
            finally:
                os.chdir(old)

if __name__ == "__main__":
    unittest.main()
