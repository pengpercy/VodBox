"""Offline regression tests for the Java-free package evidence gate."""
import gzip
import hashlib
import io
import json
import pathlib
import sys
import tarfile
import tempfile
import unittest
import unittest.mock
import zipfile

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import release_evidence as evidence

ROOT = pathlib.Path(__file__).resolve().parent.parent
ALMA = ROOT / ".alma"


def members() -> list[str]:
    return [
        "VodBox.Desktop",
        "licenses/media/asset.json",
        "licenses/media/mpv-Copyright",
        "licenses/media/Vulkan-Headers/LICENSE.md",
    ]


def write_zip(path: pathlib.Path, names: list[str] | None = None,
              prefix: str = "") -> pathlib.Path:
    contents = {name: ("{}" if name.endswith(".json") else "payload:" + name).encode()
                for name in (names or members())}
    with zipfile.ZipFile(path, "w") as archive:
        for name, data in contents.items():
            location = prefix + name
            if prefix.endswith("Contents/MacOS/") and name.startswith("licenses/"):
                location = prefix.removesuffix("MacOS/") + "Resources/" + name
            archive.writestr(location, data)
    return path


def write_meta(root: pathlib.Path, rid: str, version: str = "1.0.0",
               source_sha: str = "abc") -> pathlib.Path:
    root.mkdir(parents=True, exist_ok=True)
    path = root / f"{rid}.json"
    path.write_text(json.dumps({
        "rid": rid, "version": version,
        "sourceSha": source_sha, "packageSourceSha": source_sha,
    }))
    return path


class PayloadTests(unittest.TestCase):
    def setUp(self):
        ALMA.mkdir(exist_ok=True)

    def test_a_normal_java_free_package_is_accepted(self):
        with tempfile.TemporaryDirectory(dir=ALMA) as directory:
            root = pathlib.Path(directory)
            archive = write_zip(root / "VodBox_1.0.0.linux-x64.zip")
            verdict = evidence.verify_rid("linux-x64", archive)
        self.assertEqual(verdict["rid"], "linux-x64")
        self.assertFalse(verdict["javaPayload"])
        self.assertEqual(verdict["mediaNotices"], ["licenses/media/asset.json"])

    def test_macos_resources_layout_is_accepted(self):
        with tempfile.TemporaryDirectory(dir=ALMA) as directory:
            root = pathlib.Path(directory)
            archive = write_zip(root / "VodBox.1.osx-arm64.zip",
                                prefix="VodBox.app/Contents/MacOS/")
            verdict = evidence.verify_rid("osx-arm64", archive)
        self.assertEqual(verdict["rid"], "osx-arm64")

    def test_every_java_product_payload_is_refused(self):
        names = [
            "JavaHost/host.jar",
            "opt/vodbox/JavaHost/deps/runtime.jar",
            "Contents/Home/bin/java",
            "plugin.dex",
            "dev/example/Plugin.class",
            "JavaHost/native-backend-receipt.json",
            "java-host.json",
            "upstream-runtime.json",
            "license-tvbox-silicon",
            "jre/legal/java.base/LICENSE",
            "jdk/bin/javac",
        ]
        for name in names:
            with self.subTest(member=name), tempfile.TemporaryDirectory(dir=ALMA) as directory:
                root = pathlib.Path(directory)
                archive = write_zip(root / "VodBox_1.0.0.linux-x64.zip", members() + [name])
                with self.assertRaisesRegex(evidence.ReleaseEvidenceError, "forbidden Java payload"):
                    evidence.verify_rid("linux-x64", archive)

    def test_missing_or_empty_media_notice_is_refused(self):
        with tempfile.TemporaryDirectory(dir=ALMA) as directory:
            root = pathlib.Path(directory)
            archive = write_zip(root / "VodBox_1.0.0.linux-x64.zip",
                                [name for name in members() if not name.endswith("asset.json")])
            with self.assertRaisesRegex(evidence.ReleaseEvidenceError, "media notices"):
                evidence.verify_rid("linux-x64", archive)
        with tempfile.TemporaryDirectory(dir=ALMA) as directory:
            root = pathlib.Path(directory)
            archive = root / "VodBox_1.0.0.linux-x64.zip"
            with zipfile.ZipFile(archive, "w") as output:
                for name in members():
                    output.writestr(name, b"" if name.endswith("asset.json") else b"payload")
            with self.assertRaises(evidence.ReleaseEvidenceError):
                evidence.verify_rid("linux-x64", archive)

    def test_tar_and_deb_payloads_are_verified_and_links_stay_inside(self):
        for suffix in ("tar.gz", "deb"):
            with self.subTest(format=suffix), tempfile.TemporaryDirectory(dir=ALMA) as directory:
                root = pathlib.Path(directory)
                payload = io.BytesIO()
                with tarfile.open(fileobj=payload, mode="w") as archive:
                    for name in members():
                        data = b"{}" if name.endswith(".json") else b"payload"
                        info = tarfile.TarInfo(name)
                        info.size = len(data)
                        archive.addfile(info, io.BytesIO(data))
                    link = tarfile.TarInfo("libmpv.so")
                    link.type = tarfile.SYMTYPE
                    link.linkname = "VodBox.Desktop"
                    archive.addfile(link)
                data = payload.getvalue()
                if suffix == "deb":
                    def member(name: str, content: bytes) -> bytes:
                        header = (f"{name + '/':<16}{0:<12}{0:<6}{0:<6}{'100644':<8}"
                                  f"{len(content):<10}`\n").encode("ascii")
                        return header + content + (b"\n" if len(content) % 2 else b"")
                    data = b"!<arch>\n" + member("debian-binary", b"2.0\n") + member("data.tar", data)
                archive = root / f"vodbox_1_linux-x64.{suffix}"
                archive.write_bytes(data)
                self.assertEqual(evidence.verify_rid("linux-x64", archive)["rid"], "linux-x64")

    def test_unsafe_members_are_refused(self):
        for name in ("../outside", "/outside", "C:/outside", "safe\\outside"):
            with self.subTest(member=name), tempfile.TemporaryDirectory(dir=ALMA) as directory:
                root = pathlib.Path(directory)
                archive = write_zip(root / "VodBox_1.0.0.linux-x64.zip", members() + [name])
                with self.assertRaises(evidence.ReleaseEvidenceError):
                    evidence.verify_rid("linux-x64", archive)


class ReceiptTests(unittest.TestCase):
    def setUp(self):
        ALMA.mkdir(exist_ok=True)

    def test_package_receipt_round_trip_and_tamper_refusal(self):
        with tempfile.TemporaryDirectory(dir=ALMA) as directory:
            root = pathlib.Path(directory)
            archive = write_zip(root / "VodBox_1.0.0.osx-arm64.zip")
            receipt = evidence.write_package_receipt("osx-arm64", archive, root / "receipts")
            verdict = evidence.verify_package_receipt("osx-arm64", archive, root / "receipts")
            self.assertEqual(verdict["rid"], "osx-arm64")
            data = evidence._read_package_receipt(receipt)
            self.assertEqual(data["schema"], 3)
            data["checks"]["javaPayload"] = True
            with gzip.open(receipt, "wt", encoding="utf-8") as output:
                json.dump(data, output)
            with self.assertRaisesRegex(evidence.ReleaseEvidenceError, "receipt checks differ"):
                evidence.verify_package_receipt("osx-arm64", archive, root / "receipts")

    def test_receipt_manifest_cannot_hide_java_payload(self):
        with tempfile.TemporaryDirectory(dir=ALMA) as directory:
            root = pathlib.Path(directory)
            archive = write_zip(root / "VodBox_1.0.0.linux-x64.zip")
            receipt = evidence.write_package_receipt("linux-x64", archive, root / "receipts")
            data = evidence._read_package_receipt(receipt)
            data["files"]["JavaHost/host.jar"] = {
                "size": 0, "sha256": hashlib.sha256(b"").hexdigest()}
            with gzip.open(receipt, "wt", encoding="utf-8") as output:
                json.dump(data, output)
            with self.assertRaisesRegex(evidence.ReleaseEvidenceError, "forbidden Java payload"):
                evidence.verify_package_receipt("linux-x64", archive, root / "receipts")


class ReleaseTests(unittest.TestCase):
    def setUp(self):
        ALMA.mkdir(exist_ok=True)

    def _release(self, root: pathlib.Path, rids) -> tuple[pathlib.Path, pathlib.Path]:
        archives = root / "release"
        meta = root / "meta"
        archives.mkdir(parents=True)
        for rid in rids:
            write_zip(archives / f"VodBox_1.0.0.{rid}.zip")
            write_meta(meta, rid)
        return archives, meta

    def test_all_six_rids_are_verified(self):
        with tempfile.TemporaryDirectory(dir=ALMA) as directory:
            archives, meta = self._release(pathlib.Path(directory), evidence.RIDS)
            result = evidence.verify_release(archives_dir=archives, meta_dir=meta,
                                             version="1.0.0", expected_sha="abc")
        self.assertEqual(sorted(result["verdicts"]), sorted(evidence.RIDS))

    def test_any_missing_rid_refuses_the_release(self):
        for missing in evidence.RIDS:
            with self.subTest(missing=missing), tempfile.TemporaryDirectory(dir=ALMA) as directory:
                present = [rid for rid in evidence.RIDS if rid != missing]
                archives, meta = self._release(pathlib.Path(directory), present)
                with self.assertRaisesRegex(evidence.ReleaseEvidenceError, missing):
                    evidence.verify_release(archives_dir=archives, meta_dir=meta)

    def test_metadata_version_and_source_must_match(self):
        with tempfile.TemporaryDirectory(dir=ALMA) as directory:
            archives, meta = self._release(pathlib.Path(directory), ["linux-x64"])
            with self.assertRaisesRegex(evidence.ReleaseEvidenceError, "version"):
                evidence.verify_release(["linux-x64"], archives, meta, version="2.0.0")
            with self.assertRaisesRegex(evidence.ReleaseEvidenceError, "packageSourceSha"):
                evidence.verify_release(["linux-x64"], archives, meta, expected_sha="other")

    def test_cli_accepts_a_fully_verified_release(self):
        with tempfile.TemporaryDirectory(dir=ALMA) as directory:
            archives, meta = self._release(pathlib.Path(directory), evidence.RIDS)
            with unittest.mock.patch.object(
                    sys, "argv",
                    ["release_evidence.py", "--archives", str(archives), "--meta", str(meta),
                     "--version", "1.0.0", "--expected-sha", "abc"]):
                self.assertEqual(0, evidence.main())

    def test_cli_refuses_an_empty_directory(self):
        with tempfile.TemporaryDirectory(dir=ALMA) as directory, \
                unittest.mock.patch.object(
                    sys, "argv", ["release_evidence.py", "--archives", directory]), \
                unittest.mock.patch.object(sys, "stderr", new=io.StringIO()) as stderr:
            self.assertEqual(1, evidence.main())
            self.assertIn("Refusing to publish", stderr.getvalue())


if __name__ == "__main__":
    unittest.main()
