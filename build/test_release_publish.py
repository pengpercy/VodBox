"""Offline release safety regressions: no credentials, network or publication."""
import hashlib
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import release_publish as publish


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
                Path("artifacts/release").mkdir(parents=True)
                Path("artifacts/release/package.zip").write_bytes(b"package")
                calls = []
                def gh(*args):
                    calls.append(args)
                    if "--slurp" in args:
                        return "[[]]"
                    if "matching-refs" in " ".join(args):
                        return "[]"
                    if args[:2] == ("api", "repos/owner/repo/releases/tags/v1.0.0"):
                        return json.dumps({"draft": True, "assets_url": "assets"})
                    return "{}"
                with patch.dict(os.environ, GITHUB_REPOSITORY="owner/repo", VERSION="1.0.0", SOURCE_SHA="abc"), patch.object(publish, "gh", gh):
                    self.assertEqual(0, publish.main())
                ref_index = next(i for i, args in enumerate(calls) if "repos/owner/repo/git/refs" in args)
                create_index = next(i for i, args in enumerate(calls) if args[:2] == ("release", "create"))
                self.assertLess(ref_index, create_index)
                self.assertIn("--verify-tag", calls[create_index])
                self.assertIn("--draft", calls[create_index])
                self.assertFalse(any("--clobber" in args for args in calls))
            finally:
                os.chdir(old)

    def test_published_release_is_read_only_when_all_assets_match(self):
        with tempfile.TemporaryDirectory() as directory:
            old = os.getcwd()
            try:
                os.chdir(directory)
                Path("artifacts/release").mkdir(parents=True)
                Path("artifacts/release/package.zip").write_bytes(b"package")
                calls = []
                def gh(*args):
                    calls.append(args)
                    if "matching-refs" in " ".join(args):
                        return json.dumps([{"ref": "refs/tags/v1.0.0", "object": {"type": "commit", "sha": "abc"}}])
                    if args[-1] == "assets":
                        return json.dumps([[{"name": "package.zip", "size": 7, "digest": "sha256:" + hashlib.sha256(b"package").hexdigest()}]])
                    return json.dumps([[{"tag_name": "v1.0.0", "draft": False, "assets_url": "assets"}]])
                with patch.dict(os.environ, GITHUB_REPOSITORY="owner/repo", VERSION="1.0.0", SOURCE_SHA="abc"), patch.object(publish, "gh", gh):
                    self.assertEqual(0, publish.main())
                self.assertFalse(any(args[0] == "release" or "POST" in args for args in calls))
            finally:
                os.chdir(old)

    def test_tag_creation_failure_stops_without_release_fallback(self):
        with tempfile.TemporaryDirectory() as directory:
            old = os.getcwd()
            try:
                os.chdir(directory)
                Path("artifacts/release").mkdir(parents=True)
                Path("artifacts/release/package.zip").write_bytes(b"package")
                calls = []
                def gh(*args):
                    calls.append(args)
                    if "matching-refs" in " ".join(args):
                        return "[]"
                    raise RuntimeError("HTTP 403: Resource not accessible by integration")
                with patch.dict(os.environ, GITHUB_REPOSITORY="owner/repo", VERSION="1.0.0", SOURCE_SHA="abc"), patch.object(publish, "gh", gh):
                    with self.assertRaises(RuntimeError):
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
                Path("artifacts/release").mkdir(parents=True)
                Path("artifacts/release/package.zip").write_bytes(b"package")
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


if __name__ == "__main__":
    unittest.main()
