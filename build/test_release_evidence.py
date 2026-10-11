"""The pre-publish gate must verify the real downloaded packages, per RID, not a manifest flag.

The regression this guards is the one the release must never repeat: a release that goes out
because a *summary* said the obligation was closed, a receipt was synthesized, or five of six
metadata artifacts were present. Each of those is asserted here as a refusal, against real archive
bytes.
"""
import contextlib
import hashlib
import io
import json
import pathlib
import sys
import tempfile
import unittest
import unittest.mock
import zipfile

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import native_coverage
import release_evidence as evidence

ROOT = pathlib.Path(__file__).resolve().parent.parent
ALMA = ROOT / ".alma"


def host_members(backend: str = "unidbg-unicorn2-0.9.8.jar", receipt: bool = True,
                 deps: tuple = ("unidbg-api-0.9.8.jar",), licenses: bool = True) -> list[str]:
    """The members a real published package carries, rooted at the platform's host prefix."""
    members = [
        "VodBox.Desktop",
        "JavaHost/host.jar",
        "JavaHost/java-host.json",
        "JavaHost/upstream-runtime.json",
        "JavaHost/LICENSE-tvbox-silicon",
        "JavaHost/corresponding-source/LICENSE",
        "JavaHost/jre/bin/java",
        "JavaHost/jre/legal/java.base/LICENSE",
        "licenses/media/asset.json",
        "licenses/media/mpv-Copyright",
    ]
    if receipt:
        members.append("JavaHost/native-backend-receipt.json")
    if backend:
        members.append(f"JavaHost/deps/{backend}")
    members += [f"JavaHost/deps/{name}" for name in deps]
    if not licenses:
        members = [m for m in members if not m.startswith("licenses/")]
        members = [m for m in members if not m.endswith("legal/java.base/LICENSE")]
    return members


def write_zip(path: pathlib.Path, members: list[str], prefix: str = "") -> pathlib.Path:
    with zipfile.ZipFile(path, "w") as archive:
        for member in members:
            archive.writestr(prefix + member, b"payload:" + member.encode())
    return path


def receipt_for(rid: str) -> dict:
    """A receipt shaped exactly like `rebuild_native_backend.py` writes, for `rid`."""
    if rid in ("win-arm64",):
        return {
            "schema": 1,
            "rid": rid,
            "sourceRebuilt": False,
            "backend": {
                "coordinate": "com.github.zhkl0228:unidbg-unicorn2:0.9.8",
                "excluded": True,
                "exclusion_reason": f"{rid} has no upstream JNI native; every natives/ entry withheld",
                "engine": {"source_archive_sha256": "a" * 64},
                "bridge": {"source_digest_sha256": "b" * 64},
                "output_library": None,
                "jar_integration": {"native_entry": None, "jar_native_entries": [],
                                    "excluded_native_entries": ["natives/linux_64/libunicorn.so"]},
            },
            "unicorn1": {"coordinate": "com.github.zhkl0228:unicorn:1.0.14", "kept_dependency": True,
                         "source_evidence": {"archive_sha256": "d" * 64}},
        }
    return {
        "schema": 1,
        "rid": rid,
        "sourceRebuilt": True,
        "backend": {
            "coordinate": "com.github.zhkl0228:unidbg-unicorn2:0.9.8",
            "excluded": False,
            "engine": {"source_archive_sha256": "a" * 64},
            "bridge": {"source_digest_sha256": "b" * 64},
            "output_library": {"sha256": "c" * 64, "size": 1234},
            "jar_integration": {"native_entry": native_entry_for(rid),
                                "jar_native_entries": [native_entry_for(rid)]},
        },
        "unicorn1": {"coordinate": "com.github.zhkl0228:unicorn:1.0.14", "kept_dependency": True,
                     "source_evidence": {"archive_sha256": "d" * 64}},
    }


def native_entry_for(rid: str) -> str:
    directory = {value: key for key, value in native_coverage.NATIVE_DIRECTORY_RID.items()}[rid]
    name = "unicorn.dll" if rid.startswith("win") else (
        "libunicorn.dylib" if rid.startswith("osx") else "libunicorn.so")
    return f"natives/{directory}/{name}"


class MemberInspectionTests(unittest.TestCase):
    def setUp(self):
        ALMA.mkdir(exist_ok=True)

    def test_java_host_prefix_is_discovered_not_assumed(self):
        # A Windows zip roots the host at JavaHost/, a macOS app nests it under
        # VodBox.app/Contents/MacOS/, a deb under opt/vodbox/. The prefix must come from the members.
        windows = ["JavaHost/host.jar", "JavaHost/deps/a.jar"]
        macos = ["VodBox.app/Contents/MacOS/JavaHost/host.jar"]
        linux = ["opt/vodbox/JavaHost/host.jar"]
        self.assertEqual(evidence.package_java_host(windows), "JavaHost/")
        self.assertEqual(evidence.package_java_host(macos), "VodBox.app/Contents/MacOS/JavaHost/")
        self.assertEqual(evidence.package_java_host(linux), "opt/vodbox/JavaHost/")
        self.assertIsNone(evidence.package_java_host(["VodBox.Desktop", "licenses/media/asset.json"]))

    def test_a_package_without_the_host_is_refused(self):
        with tempfile.TemporaryDirectory(dir=ALMA) as directory:
            archive = write_zip(pathlib.Path(directory) / "VodBox_1.0.0.linux-x64.zip",
                                ["VodBox.Desktop", "licenses/media/asset.json"])
            with self.assertRaisesRegex(evidence.ReleaseEvidenceError, "JavaHost"):
                evidence.verify_rid("linux-x64", archive)

    def test_members_of_a_real_zip_round_trip(self):
        with tempfile.TemporaryDirectory(dir=ALMA) as directory:
            archive = write_zip(pathlib.Path(directory) / "p.zip", host_members())
            members = evidence.members_of(archive)
            self.assertIn("JavaHost/host.jar", members)
            self.assertEqual(evidence.sha256_file(archive),
                             hashlib.sha256(archive.read_bytes()).hexdigest())


class RidVerdictTests(unittest.TestCase):
    def setUp(self):
        ALMA.mkdir(exist_ok=True)

    def test_a_complete_rid_package_is_accepted(self):
        with tempfile.TemporaryDirectory(dir=ALMA) as directory:
            archive = write_zip(pathlib.Path(directory) / "VodBox_1.0.0.osx-arm64.zip", host_members())
            verdict = evidence.verify_rid("osx-arm64", archive)
        self.assertEqual(verdict["rid"], "osx-arm64")
        self.assertEqual(verdict["hostPrefix"], "JavaHost/")
        self.assertEqual(verdict["backendJar"], "unidbg-unicorn2-0.9.8.jar")
        self.assertIn("native-backend-receipt.json", verdict["receipt"])

    def test_missing_packaged_receipt_is_refused(self):
        # A package whose source-rebuild receipt did not travel cannot prove where its native came
        # from; a build-tree-only receipt is not retained in the distribution.
        with tempfile.TemporaryDirectory(dir=ALMA) as directory:
            archive = write_zip(pathlib.Path(directory) / "VodBox_1.0.0.osx-arm64.zip",
                                host_members(receipt=False))
            with self.assertRaisesRegex(evidence.ReleaseEvidenceError, "native-backend-receipt"):
                evidence.verify_rid("osx-arm64", archive)

    def test_missing_staged_deps_closure_is_refused(self):
        with tempfile.TemporaryDirectory(dir=ALMA) as directory:
            archive = write_zip(pathlib.Path(directory) / "VodBox_1.0.0.linux-x64.zip",
                                host_members(deps=(), backend=""))
            with self.assertRaisesRegex(evidence.ReleaseEvidenceError, "deps"):
                evidence.verify_rid("linux-x64", archive)

    def test_a_host_without_the_source_rebuilt_backend_jar_is_refused(self):
        with tempfile.TemporaryDirectory(dir=ALMA) as directory:
            archive = write_zip(pathlib.Path(directory) / "VodBox_1.0.0.win-x64.zip",
                                host_members(backend=""))
            with self.assertRaisesRegex(evidence.ReleaseEvidenceError, "unidbg-unicorn2"):
                evidence.verify_rid("win-x64", archive)

    def test_missing_license_material_is_refused(self):
        with tempfile.TemporaryDirectory(dir=ALMA) as directory:
            archive = write_zip(pathlib.Path(directory) / "VodBox_1.0.0.linux-arm64.zip",
                                host_members(licenses=False))
            with self.assertRaises(evidence.ReleaseEvidenceError):
                evidence.verify_rid("linux-arm64", archive)

    def test_a_macos_app_layout_is_verified_by_suffix_not_by_prefix(self):
        with tempfile.TemporaryDirectory(dir=ALMA) as directory:
            archive = write_zip(pathlib.Path(directory) / "VodBox_1.0.0.osx-x64.zip",
                                host_members(), prefix="VodBox.app/Contents/MacOS/")
            verdict = evidence.verify_rid("osx-x64", archive)
        self.assertEqual(verdict["hostPrefix"], "VodBox.app/Contents/MacOS/JavaHost/")

    def test_meta_is_verified_as_a_real_file(self):
        with tempfile.TemporaryDirectory(dir=ALMA) as directory:
            root = pathlib.Path(directory)
            (root / "osx-arm64.json").write_text(json.dumps({"rid": "osx-arm64"}))
            verdict = evidence.verify_meta("osx-arm64", root / "osx-arm64.json")
            self.assertEqual(verdict["rid"], "osx-arm64")
            with self.assertRaisesRegex(evidence.ReleaseEvidenceError, "missing or empty"):
                evidence.verify_meta("osx-arm64", root / "absent.json")
            (root / "bad.json").write_text("{not json")
            with self.assertRaisesRegex(evidence.ReleaseEvidenceError, "not valid JSON"):
                evidence.verify_meta("osx-arm64", root / "bad.json")
            (root / "wrong.json").write_text(json.dumps({"rid": "linux-x64"}))
            with self.assertRaisesRegex(evidence.ReleaseEvidenceError, "metadata for"):
                evidence.verify_meta("osx-arm64", root / "wrong.json")


class SixRidClosureTests(unittest.TestCase):
    def setUp(self):
        ALMA.mkdir(exist_ok=True)

    def _release(self, root: pathlib.Path, rids, **kwargs) -> pathlib.Path:
        archives = root / "release"
        archives.mkdir(parents=True, exist_ok=True)
        for rid in rids:
            write_zip(archives / f"VodBox_1.0.0.{rid}.zip", host_members(**kwargs))
        return archives

    def test_all_six_rids_are_verified_from_their_own_artifacts(self):
        with tempfile.TemporaryDirectory(dir=ALMA) as directory:
            archives = self._release(pathlib.Path(directory), evidence.RIDS)
            result = evidence.verify_release(archives_dir=archives)
        self.assertEqual(sorted(result["verdicts"]), sorted(evidence.RIDS))

    def test_any_missing_rid_refuses_the_whole_release(self):
        # Five of six is not a distribution of this product: the release must fail, naming the RID.
        for missing in evidence.RIDS:
            with tempfile.TemporaryDirectory(dir=ALMA) as directory:
                present = [rid for rid in evidence.RIDS if rid != missing]
                archives = self._release(pathlib.Path(directory), present)
                with self.assertRaisesRegex(evidence.ReleaseEvidenceError, missing):
                    evidence.verify_release(archives_dir=archives)

    def test_a_manifest_summary_does_not_stand_in_for_the_artifacts(self):
        # The exact regression: a closed manifest summary with no real package must still refuse.
        manifest = json.loads(native_coverage.SOURCE_MANIFEST.read_text())
        edited = json.loads(json.dumps(manifest))
        edited["corresponding_source_complete"] = True
        with tempfile.TemporaryDirectory(dir=ALMA) as directory:
            archives = pathlib.Path(directory) / "release"
            archives.mkdir(parents=True)
            with unittest.mock.patch.object(native_coverage, "_source_manifest", return_value=edited):
                with self.assertRaises(evidence.ReleaseEvidenceError):
                    evidence.verify_release(archives_dir=archives)

    def test_a_synthetic_receipt_file_does_not_stand_in_for_the_package(self):
        # A hand-written receipt next to the workspace (or in another RID's meta) is not the package.
        with tempfile.TemporaryDirectory(dir=ALMA) as directory:
            root = pathlib.Path(directory)
            archives = root / "release"
            archives.mkdir(parents=True)
            (root / "native-backend-receipt.json").write_text(json.dumps(receipt_for("win-arm64")))
            with self.assertRaises(evidence.ReleaseEvidenceError):
                evidence.verify_release(archives_dir=archives)

    def test_expected_source_and_version_are_checked_against_real_metadata(self):
        with tempfile.TemporaryDirectory(dir=ALMA) as directory:
            root = pathlib.Path(directory)
            rids = list(evidence.RIDS)
            archives = self._release(root, rids)
            meta = root / "meta"
            meta.mkdir()
            for rid in rids:
                (meta / f"{rid}.json").write_text(json.dumps(
                    {"rid": rid, "version": "1.0.0", "sourceSha": "abc"}))
            result = evidence.verify_release(archives_dir=archives, meta_dir=meta,
                                             expected_sha="abc", version="1.0.0")
            self.assertEqual(len(result["verdicts"]), 6)
            (meta / "osx-arm64.json").write_text(json.dumps(
                {"rid": "osx-arm64", "version": "1.0.0", "sourceSha": "other"}))
            with self.assertRaisesRegex(evidence.ReleaseEvidenceError, "sourceSha"):
                evidence.verify_release(archives_dir=archives, meta_dir=meta,
                                        expected_sha="abc", version="1.0.0")


    def test_a_receipt_artifact_for_another_rid_is_refused(self):
        with tempfile.TemporaryDirectory(dir=ALMA) as directory:
            root = pathlib.Path(directory)
            (root / "native-backend-receipt.osx-arm64.json").write_text(
                json.dumps(receipt_for("linux-x64")))
            with self.assertRaisesRegex(evidence.ReleaseEvidenceError, "receipt for"):
                evidence.verify_receipt_artifact("osx-arm64", root)

    def test_a_synthetic_stub_receipt_is_not_a_receipt(self):
        # A receipt with no backend coordinate proves nothing about any source.
        with tempfile.TemporaryDirectory(dir=ALMA) as directory:
            root = pathlib.Path(directory)
            (root / "native-backend-receipt.linux-x64.json").write_text(json.dumps({"rid": "linux-x64"}))
            with self.assertRaisesRegex(evidence.ReleaseEvidenceError, "backend coordinate"):
                evidence.verify_receipt_artifact("linux-x64", root)
            (root / "native-backend-receipt.linux-x64.json").write_text("")
            with self.assertRaisesRegex(evidence.ReleaseEvidenceError, "no downloaded"):
                evidence.verify_receipt_artifact("linux-x64", root)

    def test_every_rid_must_carry_its_own_receipt_artifact(self):
        # Six packages but five receipts is exactly the "five of six" shortcut this refuses.
        with tempfile.TemporaryDirectory(dir=ALMA) as directory:
            root = pathlib.Path(directory)
            archives = self._release(root, evidence.RIDS)
            receipts = root / "receipts"
            receipts.mkdir()
            for rid in evidence.RIDS[:-1]:
                (receipts / f"native-backend-receipt.{rid}.json").write_text(
                    json.dumps(receipt_for(rid)))
            with self.assertRaisesRegex(evidence.ReleaseEvidenceError, evidence.RIDS[-1]):
                evidence.verify_release(archives_dir=archives, receipts_dir=receipts)
            (receipts / f"native-backend-receipt.{evidence.RIDS[-1]}.json").write_text(
                json.dumps(receipt_for(evidence.RIDS[-1])))
            result = evidence.verify_release(archives_dir=archives, receipts_dir=receipts)
        self.assertEqual(len(result["verdicts"]), 6)
        self.assertEqual(result["verdicts"]["win-arm64"]["receiptArtifact"]["excluded"], True)
        self.assertEqual(result["verdicts"]["osx-arm64"]["receiptArtifact"]["excluded"], False)


class CommandLineTests(unittest.TestCase):
    def setUp(self):
        ALMA.mkdir(exist_ok=True)

    def _run(self, *argv):
        out, err = io.StringIO(), io.StringIO()
        with unittest.mock.patch.object(sys, "argv", ["release_evidence.py", *argv]):
            with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
                status = evidence.main()
        return status, out.getvalue() + err.getvalue()

    def test_cli_refuses_an_empty_release_directory(self):
        with tempfile.TemporaryDirectory(dir=ALMA) as directory:
            status, output = self._run("--archives", directory)
        self.assertEqual(status, 1)
        self.assertIn("Refusing to publish", output)

    def test_cli_accepts_a_fully_verified_six_rid_release(self):
        with tempfile.TemporaryDirectory(dir=ALMA) as directory:
            root = pathlib.Path(directory)
            archives = root / "release"
            archives.mkdir(parents=True)
            for rid in evidence.RIDS:
                write_zip(archives / f"VodBox_1.0.0.{rid}.zip", host_members())
            status, output = self._run("--archives", str(archives))
        self.assertEqual(status, 0)
        self.assertIn("Verified 6 RIDs", output)

    def test_cli_verifies_a_single_rid(self):
        with tempfile.TemporaryDirectory(dir=ALMA) as directory:
            root = pathlib.Path(directory)
            archives = root / "release"
            archives.mkdir(parents=True)
            write_zip(archives / "VodBox_1.0.0.linux-x64.zip", host_members())
            status, output = self._run("--archives", str(archives), "--rid", "linux-x64")
        self.assertEqual(status, 0)
        self.assertIn("linux-x64", output)


if __name__ == "__main__":
    unittest.main()
