"""Independent source protocol regressions using real split archives."""
import hashlib
import io
import json
from pathlib import Path
import tarfile
import tempfile
import unittest
from unittest.mock import patch

import source_assets as source

VERSION = "1.0.0"
SHA = "abc"


def source_host(root):
    host = Path(root) / "JavaHost"
    closure = host / "corresponding-source"
    files = {
        "LICENSE": b"fixture source license\n",
        "src/engine.c": b"/* matching engine source */\n",
        "third_party/tvbox-silicon/dependency-notices/engine/LICENSE": b"engine license\n",
    }
    for name, data in files.items():
        path = closure / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(data)
    (host / "LICENSE-tvbox-silicon").write_bytes(files["LICENSE"])
    (host / "native-backend-receipt.json").write_bytes(b'{"schema":1}\n')
    return host


def fixture_resources(repository):
    return sorted(path.relative_to(repository).as_posix()
                  for directory in source.TARGET_RESOURCE_DIRS
                  for path in (repository / directory).rglob("*") if path.is_file())


def split_fixture(host, assets, existing=False, version=VERSION, source_sha=SHA, producer_sha=""):
    # Stub Git availability for synthetic commits; all source splitting, inventory,
    # archive bytes and runtime bindings still use the real producer implementation.
    repository = Path(__file__).resolve().parent.parent
    with patch.dict("os.environ", GITHUB_REPOSITORY="owner/repo"), \
            patch("ci_reuse.ensure_commit", return_value=True), \
            patch("ci_reuse.native_build_hash", return_value="fixture-native-input"), \
            patch.object(source, "canonicalize_tracked"), \
            patch.object(source, "target_resources", return_value=fixture_resources(repository)), \
            patch.object(source, "git_bytes", side_effect=lambda ref, name: (repository / name).read_bytes()):
        source.split(host, assets, version, source_sha, existing=existing, producer_sha=producer_sha)
    return json.loads((host / "source-offer.json").read_text())


def fixture_git_context(test):
    repository = Path(__file__).resolve().parent.parent
    contexts = (patch("ci_reuse.ensure_commit", return_value=True),
                patch("ci_reuse.native_build_hash", return_value="fixture-native-input"),
                patch.object(source, "target_resources", return_value=fixture_resources(repository)),
                patch.object(source, "git_bytes", side_effect=lambda ref, name: (repository / name).read_bytes()))
    for context in contexts:
        context.start()
        test.addCleanup(context.stop)


class SourceAssetsTests(unittest.TestCase):
    def setUp(self):
        fixture_git_context(self)
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.host = source_host(self.root / "runtime")
        self.assets = self.root / "sources"
        self.offer = split_fixture(self.host, self.assets)
        for name, value in (("ASSETS", self.assets), ("EXPECTED_VERSION", VERSION),
                            ("EXPECTED_SHA", SHA)):
            context = patch.object(source, name, value)
            context.start()
            self.addCleanup(context.stop)

    def test_split_binds_every_file_and_runtime_retains_acquisition_and_licenses(self):
        index = source.validate_index(self.assets / self.offer["index"], VERSION, SHA)
        self.assertFalse((self.host / "corresponding-source").exists())
        for name in ("LICENSE-tvbox-silicon", "dependency-notices/engine/LICENSE",
                     "SOURCE-MANIFEST.json", "source-offer.json", "SOURCE-OFFER.txt"):
            self.assertTrue((self.host / name).is_file(), name)
            self.assertGreater((self.host / name).stat().st_size, 0, name)
        instructions = (self.host / "SOURCE-OFFER.txt").read_text()
        for value in (self.offer["asset"], self.offer["index"], self.offer["assetSHA256"],
                      self.offer["manifestDigest"], "/releases/tag/v1.0.0"):
            self.assertIn(value, instructions)
        with source.matching_source(self.host) as closure:
            self.assertEqual(source.inventory(closure), index["manifest"]["files"])
            self.assertIn("build/rebuild_native_backend.py", index["manifest"]["files"])
            self.assertIn("README-build.txt", index["manifest"]["files"])
            for name, record in index["manifest"]["files"].items():
                data = (closure / name).read_bytes()
                self.assertEqual(record, {"size": len(data), "sha256": hashlib.sha256(data).hexdigest()})
        self.assertEqual((self.host / "SOURCE-MANIFEST.json").read_bytes(), source.encoded(index["manifest"]))

    def test_archive_contains_target_runtime_tools_and_raw_packaging_resources(self):
        repository = Path(__file__).resolve().parent.parent
        with source.matching_source(self.host) as closure:
            for name in source.TARGET_TOOLS:
                self.assertEqual((closure / name).read_bytes(),
                                 source.canonical_text((repository / name).read_bytes()), name)
            for name in fixture_resources(repository):
                self.assertEqual((closure / name).read_bytes(), (repository / name).read_bytes(), name)
            for name in ("build/bundle.py", "build/fetch-dex-deps.py", "build/jlink_runtime.py",
                         "build/nativeverify.py", "build/dex-deps.json", "build/native-assets.json",
                         "build/package.py", "build/licenses/mpv-Copyright",
                         "src/VodBox.Desktop/Assets/Icons/vodbox.icns"):
                self.assertTrue((closure / name).is_file(), name)
            instructions = (closure / "README-build.txt").read_text()
            for text in ("Python 3.11+", "https://github.com/owner/repo.git",
                         f"git checkout {SHA}", f"../vodbox-producer {SHA}",
                         "Git source commit objects", "does not provide Git history",
                         "preserve them", "entire app"):
                self.assertIn(text, instructions)

    def test_target_tools_and_resources_use_target_commit_not_backend_producer(self):
        repository = Path(__file__).resolve().parent.parent
        producer = "actual-producer"
        host = source_host(self.root / "producer-runtime")
        calls = []

        def git_bytes(ref, name):
            calls.append((ref, name))
            return (repository / name).read_bytes()

        with patch.dict("os.environ", GITHUB_REPOSITORY="owner/repo"), \
                patch.object(source, "canonicalize_tracked"), \
                patch.object(source, "git_bytes", side_effect=git_bytes):
            source.split(host, self.assets, VERSION, SHA, producer_sha=producer)
        for name in source.TARGET_TOOLS + tuple(fixture_resources(repository)):
            self.assertIn((SHA, name), calls)
            self.assertNotIn((producer, name), calls)
        for name in ("build/tvbox-host.py", "build/rebuild_native_backend.py"):
            self.assertIn((producer, name), calls)
        with source.matching_source(host) as closure:
            self.assertIn(f"../vodbox-producer {producer}",
                          (closure / "README-build.txt").read_text())

    def test_native_bridge_raw_bytes_survive_normalization_and_make_source_variants(self):
        import rebuild_native_backend as rebuild
        name = (rebuild.NATIVE_INPUTS.relative_to(rebuild.ROOT) / "bridge.c").as_posix()
        offers = []
        for label, data in (("lf", b"/* producer bridge */\n"),
                            ("crlf", b"/* producer bridge */\r\n")):
            host = source_host(self.root / label)
            closure = host / "corresponding-source"
            bridge = closure / name
            bridge.parent.mkdir(parents=True, exist_ok=True)
            bridge.write_bytes(data)
            listing = unittest.mock.Mock(stdout=(name + "\n").encode())
            with patch.object(source.subprocess, "run", return_value=listing), \
                    patch.object(source, "git_bytes") as blobs:
                source.canonicalize_tracked(closure, SHA)
                blobs.assert_not_called()
            self.assertEqual(bridge.read_bytes(), data)
            offer = split_fixture(host, self.assets)
            offers.append(offer)
            with source.matching_source(host) as extracted:
                self.assertEqual((extracted / name).read_bytes(), data)
                manifest = json.loads((host / "SOURCE-MANIFEST.json").read_text())
                self.assertEqual(manifest["files"][name]["sha256"],
                                 hashlib.sha256(data).hexdigest())
        self.assertNotEqual(offers[0]["manifestDigest"], offers[1]["manifestDigest"])
        self.assertNotEqual(offers[0]["asset"], offers[1]["asset"])

    def test_runtime_cannot_embed_source_or_omit_required_bindings(self):
        closure = self.host / "corresponding-source"
        closure.mkdir()
        with self.assertRaisesRegex(ValueError, "still contains corresponding-source"):
            with source.matching_source(self.host):
                self.fail("accepted embedded source")
        closure.rmdir()
        for name in ("source-offer.json", "SOURCE-MANIFEST.json", "SOURCE-OFFER.txt"):
            path = self.host / name
            original = path.read_bytes()
            path.unlink()
            try:
                with self.subTest(name=name), self.assertRaises(FileNotFoundError):
                    with source.matching_source(self.host):
                        self.fail("accepted missing acquisition evidence")
            finally:
                path.write_bytes(original)

    def test_version_and_commit_must_match_context(self):
        for field in ("EXPECTED_VERSION", "EXPECTED_SHA"):
            with self.subTest(field=field), patch.object(source, field, "wrong"):
                with self.assertRaisesRegex(ValueError, "version/commit mismatch"):
                    with source.matching_source(self.host):
                        self.fail("accepted wrong release identity")

    def test_missing_source_context_index_archive_and_closure_are_refused(self):
        with patch.object(source, "ASSETS", None):
            with self.assertRaisesRegex(ValueError, "directory is required"):
                with source.matching_source(self.host):
                    self.fail("accepted missing source context")
        for name in (self.offer["index"], self.offer["asset"]):
            path = self.assets / name
            data = path.read_bytes()
            path.unlink()
            try:
                with self.subTest(name=name), self.assertRaises((ValueError, FileNotFoundError)):
                    with source.matching_source(self.host):
                        self.fail("accepted missing source artifact")
            finally:
                path.write_bytes(data)
        with self.assertRaisesRegex(ValueError, "no corresponding source"):
            split_fixture(self.root / "missing-host", self.assets)
        host = source_host(self.root / "unmatched")
        with self.assertRaisesRegex(ValueError, "no independent source asset"):
            split_fixture(host, self.root / "empty-sources", existing=True)

    def test_runtime_offer_manifest_and_instructions_tampering_is_refused(self):
        for field in ("asset", "assetSHA256", "assetSize", "manifestDigest", "version", "sourceSha", "index"):
            with self.subTest(field=field):
                changed = dict(self.offer)
                changed[field] = changed[field] + 1 if field == "assetSize" else "wrong"
                (self.host / "source-offer.json").write_bytes(source.encoded(changed))
                with self.assertRaises((ValueError, FileNotFoundError)):
                    with source.matching_source(self.host):
                        self.fail("accepted changed binding")
                (self.host / "source-offer.json").write_bytes(source.encoded(self.offer))
        for name in ("SOURCE-MANIFEST.json", "SOURCE-OFFER.txt"):
            path = self.host / name
            data = path.read_bytes()
            path.write_bytes(b"tampered")
            try:
                with self.subTest(name=name), self.assertRaises(ValueError):
                    with source.matching_source(self.host):
                        self.fail("accepted changed runtime evidence")
            finally:
                path.write_bytes(data)

    def test_archive_tampering_is_refused_even_with_updated_outer_digest(self):
        index_path = self.assets / self.offer["index"]
        index = source.validate_index(index_path)
        archive = self.assets / index["asset"]
        archive.write_bytes(archive.read_bytes() + b"tampered")
        with self.assertRaisesRegex(ValueError, "hash/size mismatch"):
            source.validate_index(index_path)
        with tarfile.open(archive, "r:gz") as reader:
            files = {member.name: reader.extractfile(member).read() for member in reader}
        files["src/engine.c"] = b"X" * len(files["src/engine.c"])
        with tarfile.open(archive, "w:gz") as writer:
            for name, data in files.items():
                member = tarfile.TarInfo(name)
                member.size = len(data)
                writer.addfile(member, io.BytesIO(data))
        index.update(assetSize=archive.stat().st_size, assetSHA256=source.digest(archive))
        index_path.write_bytes(source.encoded(index))
        self.offer.update(assetSize=index["assetSize"], assetSHA256=index["assetSHA256"])
        (self.host / "source-offer.json").write_bytes(source.encoded(self.offer))
        instructions = (self.host / "SOURCE-OFFER.txt").read_text()
        instructions += "\n" + index["assetSHA256"]
        (self.host / "SOURCE-OFFER.txt").write_text(instructions)
        with self.assertRaisesRegex(ValueError, "content hash mismatch"):
            with source.matching_source(self.host):
                self.fail("accepted changed source file")

    def test_index_manifest_tampering_and_original_receipt_tampering_are_refused(self):
        path = self.assets / self.offer["index"]
        original = path.read_bytes()
        for field in ("version", "sourceSha", "files"):
            index = json.loads(original)
            index["manifest"][field] = {} if field == "files" else "wrong"
            path.write_bytes(source.encoded(index))
            with self.subTest(field=field), self.assertRaisesRegex(ValueError, "manifest digest"):
                source.validate_index(path, VERSION, SHA)
        path.write_bytes(original)
        (self.host / "native-backend-receipt.json").write_bytes(b"tampered receipt")
        with self.assertRaisesRegex(ValueError, "receipt differs"):
            with source.matching_source(self.host):
                self.fail("accepted changed original receipt")

    def test_independent_source_unsafe_links_paths_and_duplicates_are_refused(self):
        index = source.validate_index(self.assets / self.offer["index"])
        archive = self.assets / index["asset"]
        cases = (("../escape", tarfile.REGTYPE), ("/absolute", tarfile.REGTYPE),
                 ("C:/outside", tarfile.REGTYPE), ("dir\\outside", tarfile.REGTYPE),
                 ("src/engine.c", tarfile.SYMTYPE), ("src/engine.c", tarfile.LNKTYPE),
                 ("src/engine.c", tarfile.DIRTYPE), ("duplicate", tarfile.REGTYPE))
        for name, kind in cases:
            with self.subTest(name=name, kind=kind):
                with tarfile.open(archive, "w:gz") as writer:
                    for _ in range(2 if name == "duplicate" else 1):
                        item = tarfile.TarInfo("src/engine.c" if name == "duplicate" else name)
                        item.type = kind
                        item.linkname = "../escape" if kind in (tarfile.SYMTYPE, tarfile.LNKTYPE) else ""
                        item.size = index["manifest"]["files"]["src/engine.c"]["size"] if kind == tarfile.REGTYPE else 0
                        writer.addfile(item, io.BytesIO(b"X" * item.size) if item.size else None)
                with self.assertRaisesRegex(ValueError, "unsafe or duplicate"):
                    with source.extracted(index, self.assets):
                        self.fail("accepted unsafe source archive")

    def test_one_version_multiple_rids_deduplicate_identical_source(self):
        original = {path.name: path.read_bytes() for path in self.assets.iterdir()}
        for rid in ("linux-x64", "osx-arm64", "win-arm64"):
            with self.subTest(rid=rid):
                host = source_host(self.root / rid)
                offer = split_fixture(host, self.assets, existing=rid == "win-arm64")
                self.assertEqual(offer, self.offer)
                self.assertFalse((host / "corresponding-source").exists())
                with source.matching_source(host) as closure:
                    self.assertTrue((closure / "src/engine.c").is_file())
        self.assertEqual(len(original), 2)
        self.assertEqual({path.name: path.read_bytes() for path in self.assets.iterdir()}, original)


if __name__ == "__main__":
    unittest.main()
