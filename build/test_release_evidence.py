"""The pre-publish gate must verify the real downloaded packages, per RID, not a manifest flag.

The regression this guards is the one the release must never repeat: a release that goes out
because a *summary* said the obligation was closed, a receipt was synthesized, or five of six
metadata artifacts were present. Each of those is asserted here as a refusal, against real archive
bytes.
"""
import contextlib
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
    if backend or deps:
        members += [f"JavaHost/deps/{coordinate.split(':')[-2]}-{coordinate.split(':')[-1]}.jar"
                    for coordinate in native_coverage.DISTRIBUTED_SOURCE_ARTIFACTS
                    if coordinate != native_coverage.BACKEND_COORDINATE]
    if not licenses:
        members = [m for m in members if not m.startswith("licenses/")]
        members = [m for m in members if not m.endswith("legal/java.base/LICENSE")]
    return members


def write_zip(path: pathlib.Path, members: list[str], prefix: str = "") -> pathlib.Path:
    rid = next((rid for rid in evidence.RIDS
                if any(marker in path.name for marker in (f".{rid}.", f"_{rid}."))), "osx-arm64")
    contents = {member: b"payload:" + member.encode() for member in members}
    for member in members:
        if member.endswith(".jar"):
            contents[member] = jar_bytes({"Dependency.class": b"class"})
    backend = {"Backend.class": b"class"}
    if rid != "win-arm64":
        backend[native_entry_for(rid)] = NATIVE_BYTES
    if "JavaHost/deps/unidbg-unicorn2-0.9.8.jar" in contents:
        contents["JavaHost/deps/unidbg-unicorn2-0.9.8.jar"] = jar_bytes(backend)
    if "JavaHost/deps/unicorn-1.0.14.jar" in contents:
        contents["JavaHost/deps/unicorn-1.0.14.jar"] = jar_bytes({"Unicorn.class": b"class"})
    if "JavaHost/native-backend-receipt.json" in contents:
        contents["JavaHost/native-backend-receipt.json"] = json.dumps(receipt_for(rid)).encode()
    if "licenses/media/asset.json" in contents:
        contents["licenses/media/asset.json"] = b"{}"
    if "JavaHost/corresponding-source/LICENSE" in contents:
        source = "JavaHost/corresponding-source/"
        vendor = source + SOURCE_PREFIX
        contents[vendor + "dependency-sources/native-source-manifest.json"] = native_coverage.SOURCE_MANIFEST.read_bytes()
        contents[vendor + "rebuild/backend-unicorn2/engine-pin.json"] = json.dumps(
            {"engine": {"archive_sha256": SOURCE_HASH}}).encode()
        for name in ("engine", "unicorn"):
            contents[vendor + f"dependency-sources/archives/{name}.tar"] = SOURCE_BYTES
        for name, data in BRIDGE_FILES.items():
            contents[source + BRIDGE_PATH + "/" + name] = data
        entries = native_coverage._manifest_artifacts(native_coverage._source_manifest())
        notices = set()
        for coordinate, obligation in native_coverage.NATIVE_COMPONENT_OBLIGATIONS.items():
            if obligation["class"] == "permissive-notice-only":
                notices.update(native_coverage._notice_files(entries.get(coordinate, {}), obligation))
        notices.update(p for p in (native_coverage.DEPENDENCY_NOTICES / "unicorn-1.0.14").rglob("*")
                       if p.is_file() and p.name.lower().startswith(("license", "copying", "notice")))
        for notice in notices:
            contents[vendor + notice.relative_to(evidence.rebuild.SOURCE).as_posix()] = notice.read_bytes()
    with zipfile.ZipFile(path, "w") as archive:
        for member, data in contents.items():
            location = prefix + member
            if prefix.endswith("Contents/MacOS/") and member.startswith("licenses/"):
                location = prefix.removesuffix("MacOS/") + "Resources/" + member
            archive.writestr(location, data)
    return path


def jar_bytes(files: dict[str, bytes]) -> bytes:
    output = io.BytesIO()
    with zipfile.ZipFile(output, "w") as archive:
        for name, data in files.items():
            archive.writestr(zipfile.ZipInfo(name), data)
    return output.getvalue()


def source_archive() -> bytes:
    output = io.BytesIO()
    with tarfile.open(fileobj=output, mode="w") as archive:
        info = tarfile.TarInfo("source/engine.c")
        data = b"/* retained source fixture */"
        info.size = len(data)
        archive.addfile(info, io.BytesIO(data))
    return output.getvalue()


SOURCE_BYTES = source_archive()
SOURCE_HASH = hashlib.sha256(SOURCE_BYTES).hexdigest()
NATIVE_BYTES = b"rebuilt native fixture"
SOURCE_PREFIX = "third_party/tvbox-silicon/"
BRIDGE_PATH = evidence.rebuild.NATIVE_INPUTS.relative_to(ROOT).as_posix()
BRIDGE_FILES = {name: ("/* bridge " + name + " */").encode()
                for name in evidence.rebuild.BRIDGE_SOURCES + evidence.rebuild.BRIDGE_HEADERS}


def receipt_for(rid: str) -> dict:
    """A complete receipt bound to the fixture's real jars and source bytes."""
    excluded = rid == "win-arm64"
    native = None if excluded else native_entry_for(rid)
    backend_files = {"Backend.class": b"class"}
    if native:
        backend_files[native] = NATIVE_BYTES
    bridge_digest = hashlib.sha256()
    bridge_hashes = {}
    for name, data in BRIDGE_FILES.items():
        digest = hashlib.sha256(data).hexdigest()
        bridge_hashes[name] = digest
        bridge_digest.update(name.encode() + b"\0" + bytes.fromhex(digest))
    return {
        "schema": 1, "rid": rid, "sourceRebuilt": not excluded,
        "backend": {
            "coordinate": evidence.rebuild.BACKEND_COORDINATE, "excluded": excluded,
            "exclusion_reason": "no upstream native" if excluded else None,
            "engine": {"source_archive": SOURCE_PREFIX + "dependency-sources/archives/engine.tar",
                       "source_archive_sha256": SOURCE_HASH},
            "bridge": {"source_tree": BRIDGE_PATH, "files": bridge_hashes,
                       "source_digest_sha256": bridge_digest.hexdigest()},
            "toolchain": {"compiler": "fixture"},
            "output_library": None if excluded else {
                "sha256": hashlib.sha256(NATIVE_BYTES).hexdigest(), "size": len(NATIVE_BYTES)},
            "jar_integration": {
                "native_entry": native, "jar_native_entries": [] if excluded else [native],
                "post_rewrite_jar_sha256": hashlib.sha256(jar_bytes(backend_files)).hexdigest()},
        },
        "unicorn1": {
            "coordinate": evidence.rebuild.UNICORN1_COORDINATE, "kept_dependency": True,
            "removed_native_entries": ["natives/linux_64/libunicorn_java.so"],
            "post_quarantine_jar_sha256": hashlib.sha256(jar_bytes({"Unicorn.class": b"class"})).hexdigest(),
            "source_evidence": {
                "archive": SOURCE_PREFIX + "dependency-sources/archives/unicorn.tar",
                "archive_sha256": SOURCE_HASH},
        },
    }


def native_entry_for(rid: str) -> str:
    directory = {value: key for key, value in native_coverage.NATIVE_DIRECTORY_RID.items()}[rid]
    name = "unicorn.dll" if rid.startswith("win") else (
        "libunicorn.dylib" if rid.startswith("osx") else "libunicorn.so")
    return f"natives/{directory}/{name}"


def rewrite_zip(path: pathlib.Path, changes: dict[str, bytes | None]) -> None:
    with zipfile.ZipFile(path) as archive:
        contents = {name: archive.read(name) for name in archive.namelist()}
    for name, data in changes.items():
        if data is None:
            contents.pop(name, None)
        else:
            contents[name] = data
    with zipfile.ZipFile(path, "w") as archive:
        for name, data in contents.items():
            archive.writestr(name, data)


def write_linked_package(path: pathlib.Path, fixture: pathlib.Path,
                         links: dict[str, tuple[bytes, str]],
                         changes: dict[str, bytes] | None = None) -> pathlib.Path:
    """Build real tar/deb bytes with links, including forward references."""
    payload = io.BytesIO()
    with zipfile.ZipFile(fixture) as source, tarfile.open(fileobj=payload, mode="w") as output:
        for name, (kind, target) in links.items():
            info = tarfile.TarInfo(name)
            info.type = kind
            info.linkname = target
            output.addfile(info)
        contents = {name: source.read(name) for name in source.namelist() if name not in links}
        contents.update(changes or {})
        for name, data in contents.items():
            info = tarfile.TarInfo(name)
            info.size = len(data)
            output.addfile(info, io.BytesIO(data))
    data = payload.getvalue()
    if path.suffix == ".deb":
        def member(name: str, content: bytes) -> bytes:
            header = (f"{name + '/':<16}{0:<12}{0:<6}{0:<6}{'100644':<8}"
                      f"{len(content):<10}`\n").encode("ascii")
            return header + content + (b"\n" if len(content) % 2 else b"")

        data = b"!<arch>\n" + member("debian-binary", b"2.0\n") + member("data.tar", data)
    path.write_bytes(data)
    return path


class MemberInspectionTests(unittest.TestCase):
    def setUp(self):
        ALMA.mkdir(exist_ok=True)
        pin = unittest.mock.patch.object(evidence.rebuild, "_engines",
                                         return_value={"archive_sha256": SOURCE_HASH})
        pin.start()
        self.addCleanup(pin.stop)

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
        pin = unittest.mock.patch.object(evidence.rebuild, "_engines",
                                         return_value={"archive_sha256": SOURCE_HASH})
        pin.start()
        self.addCleanup(pin.stop)

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

    def test_real_macos_resources_host_and_contents_home_jre_are_verified(self):
        prefix = "VodBox.app/Contents/Resources/"
        license_member = prefix + "JavaHost/jre/Contents/Home/legal/java.base/LICENSE"
        for rid in ("osx-x64", "osx-arm64"):
            with self.subTest(rid=rid), tempfile.TemporaryDirectory(dir=ALMA) as directory:
                root = pathlib.Path(directory)
                path = write_zip(root / f"VodBox.1.{rid}.zip", host_members(), prefix=prefix)
                rewrite_zip(path, {
                    prefix + "JavaHost/jre/bin/java": None,
                    prefix + "JavaHost/jre/legal/java.base/LICENSE": None,
                    prefix + "JavaHost/jre/Contents/Home/bin/java": b"runtime executable",
                    license_member: b"runtime.*license",
                })
                verdict = evidence.verify_rid(rid, path)
                self.assertEqual(verdict["hostPrefix"], prefix + "JavaHost/")
                self.assertEqual(verdict["jreNotice"], license_member)
                receipts = root / "receipts"
                receipt_path = evidence.write_package_receipt(rid, path, receipts)
                retained = evidence._read_package_receipt(receipt_path)["subset"]
                self.assertIn(license_member, retained)
                self.assertNotIn(prefix + "JavaHost/jre/Contents/Home/bin/java", retained)
                self.assertEqual(evidence.verify_package_receipt(rid, path, receipts)["hostPrefix"],
                                 prefix + "JavaHost/")
                for data in (None, b"", b" \n"):
                    with self.subTest(license_bytes=data):
                        rewrite_zip(path, {license_member: data})
                        with self.assertRaisesRegex(evidence.ReleaseEvidenceError, "runtime.*license"):
                            evidence.verify_rid(rid, path)

    def test_windows_receipt_backslashes_bind_to_real_posix_source_members(self):
        fields = (("backend", "engine", "source_archive"),
                  ("unicorn1", "source_evidence", "archive"),
                  ("backend", "bridge", "source_tree"))
        for rid in ("win-x64", "win-arm64"):
            for selected in (*[(field,) for field in fields], fields):
                with self.subTest(rid=rid, fields=selected), tempfile.TemporaryDirectory(dir=ALMA) as directory:
                    root = pathlib.Path(directory)
                    path = write_zip(root / f"VodBox.1.{rid}.zip", host_members())
                    receipt = receipt_for(rid)
                    for section, detail, name in selected:
                        receipt[section][detail][name] = receipt[section][detail][name].replace("/", "\\")
                    rewrite_zip(path, {"JavaHost/native-backend-receipt.json": json.dumps(receipt).encode()})
                    self.assertEqual(evidence.verify_rid(rid, path)["rid"], rid)
                    receipts = root / "receipts"
                    evidence.write_package_receipt(rid, path, receipts)
                    self.assertEqual(evidence.verify_package_receipt(rid, path, receipts)["rid"], rid)
                    section, detail, name = selected[0]
                    source_member = "JavaHost/corresponding-source/" + receipt[section][detail][name].replace("\\", "/")
                    if name == "source_tree":
                        source_member += "/unicorn.c"
                    rewrite_zip(path, {source_member: b"changed source"})
                    with self.assertRaisesRegex(evidence.ReleaseEvidenceError, "source.*(hash|digest)"):
                        evidence.verify_rid(rid, path)

    def test_windows_receipt_normalization_still_refuses_absolute_and_escaping_paths(self):
        fields = (("backend", "engine", "source_archive"),
                  ("unicorn1", "source_evidence", "archive"),
                  ("backend", "bridge", "source_tree"))
        for rid in ("win-x64", "win-arm64"):
            for section, detail, name in fields:
                for spelling in ("/outside", "\\outside", "C:\\outside", "C:/outside",
                                 "\\\\server\\share\\outside", "../outside", "..\\outside",
                                 "safe/../../outside", "safe\\..\\..\\outside"):
                    with self.subTest(rid=rid, field=name, path=spelling), tempfile.TemporaryDirectory(dir=ALMA) as directory:
                        path = write_zip(pathlib.Path(directory) / f"VodBox.1.{rid}.zip", host_members())
                        receipt = receipt_for(rid)
                        receipt[section][detail][name] = spelling
                        rewrite_zip(path, {"JavaHost/native-backend-receipt.json": json.dumps(receipt).encode()})
                        expected = "source tree does not match" if name == "source_tree" else "unsafe package member"
                        with self.assertRaisesRegex(evidence.ReleaseEvidenceError, expected):
                            evidence.verify_rid(rid, path)

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


    def test_names_with_placeholder_bytes_do_not_prove_evidence(self):
        with tempfile.TemporaryDirectory(dir=ALMA) as directory:
            path = pathlib.Path(directory) / "VodBox.1.osx-arm64.zip"
            with zipfile.ZipFile(path, "w") as archive:
                for name in host_members():
                    archive.writestr(name, b"placeholder")
            with self.assertRaises(evidence.ReleaseEvidenceError):
                evidence.verify_rid("osx-arm64", path)

    def test_native_bytes_are_hashed_even_when_the_jar_hash_was_updated(self):
        with tempfile.TemporaryDirectory(dir=ALMA) as directory:
            path = write_zip(pathlib.Path(directory) / "VodBox.1.osx-arm64.zip", host_members())
            changed = jar_bytes({"Backend.class": b"class", native_entry_for("osx-arm64"): b"other native"})
            receipt = receipt_for("osx-arm64")
            receipt["backend"]["jar_integration"]["post_rewrite_jar_sha256"] = hashlib.sha256(changed).hexdigest()
            rewrite_zip(path, {"JavaHost/deps/unidbg-unicorn2-0.9.8.jar": changed,
                               "JavaHost/native-backend-receipt.json": json.dumps(receipt).encode()})
            with self.assertRaisesRegex(evidence.ReleaseEvidenceError, "output native bytes"):
                evidence.verify_rid("osx-arm64", path)

    def test_packaged_source_and_license_bytes_are_required(self):
        source = "JavaHost/corresponding-source/"
        coordinate = "net.java.dev.jna:jna:5.10.0"
        entries = native_coverage._manifest_artifacts(native_coverage._source_manifest())
        license_path = native_coverage._notice_files(
            entries[coordinate], native_coverage.NATIVE_COMPONENT_OBLIGATIONS[coordinate])[0]
        cases = {
            source + SOURCE_PREFIX + "dependency-sources/archives/engine.tar": b"changed engine",
            source + SOURCE_PREFIX + "dependency-sources/archives/unicorn.tar": b"changed unicorn",
            source + BRIDGE_PATH + "/unicorn.c": b"changed bridge",
            source + license_path.relative_to(ROOT).as_posix(): b"replacement license",
            "JavaHost/jre/legal/java.base/LICENSE": b"",
        }
        for name, data in cases.items():
            with self.subTest(member=name), tempfile.TemporaryDirectory(dir=ALMA) as directory:
                path = write_zip(pathlib.Path(directory) / "VodBox.1.osx-arm64.zip", host_members())
                rewrite_zip(path, {name: data})
                with self.assertRaises(evidence.ReleaseEvidenceError):
                    evidence.verify_rid("osx-arm64", path)
                self.assertEqual(sorted(p.name for p in path.parent.iterdir()), [path.name])

    def test_sidecar_cannot_replace_or_disagree_with_packaged_receipt(self):
        for mode in ("missing", "invalid", "wrong-rid", "different"):
            with self.subTest(mode=mode), tempfile.TemporaryDirectory(dir=ALMA) as directory:
                root = pathlib.Path(directory)
                path = write_zip(root / "VodBox.1.osx-arm64.zip", host_members())
                receipt = receipt_for("osx-arm64")
                (root / "native-backend-receipt.osx-arm64.json").write_text(json.dumps(receipt))
                if mode == "missing":
                    data = None
                elif mode == "invalid":
                    data = b"{invalid"
                else:
                    receipt["rid" if mode == "wrong-rid" else "extra"] = "linux-x64"
                    data = json.dumps(receipt).encode()
                rewrite_zip(path, {"JavaHost/native-backend-receipt.json": data})
                with self.assertRaises(evidence.ReleaseEvidenceError):
                    evidence.verify_rid("osx-arm64", path, receipts_dir=root)

    def test_win_arm64_exclusion_cannot_carry_foreign_native(self):
        with tempfile.TemporaryDirectory(dir=ALMA) as directory:
            path = write_zip(pathlib.Path(directory) / "VodBox.1.win-arm64.zip", host_members())
            rewrite_zip(path, {"JavaHost/deps/unidbg-unicorn2-0.9.8.jar": jar_bytes(
                {"Backend.class": b"class", "natives/linux_64/libunicorn.so": NATIVE_BYTES})})
            with self.assertRaises(evidence.ReleaseEvidenceError):
                evidence.verify_rid("win-arm64", path)

    def test_notice_paths_use_posix_spelling_on_every_platform(self):
        notices = (pathlib.PureWindowsPath("licenses/media/asset.json"),)
        with unittest.mock.patch.object(evidence, "MEDIA_NOTICES", notices):
            verdict = evidence.verify_licenses("win-x64", pathlib.Path("package.zip"), host_members())
            self.assertEqual(verdict["mediaNotices"], ["licenses/media/asset.json"])
            self.assertTrue(evidence._retain_member("licenses/media/asset.json"))
            with self.assertRaisesRegex(evidence.ReleaseEvidenceError, "licenses/media/asset.json"):
                evidence.verify_licenses("win-x64", pathlib.Path("package.zip"),
                                         [m for m in host_members() if m != "licenses/media/asset.json"])

    def test_tar_and_deb_links_resolve_to_actual_payload_bytes(self):
        for suffix in ("tar", "deb"):
            with self.subTest(format=suffix), tempfile.TemporaryDirectory(dir=ALMA) as directory:
                root = pathlib.Path(directory)
                fixture = write_zip(root / "VodBox.1.linux-x64.zip", host_members(),
                                    prefix="opt/vodbox/")
                links = {
                    "opt/vodbox/libmpv.so": (tarfile.SYMTYPE, "libmpv.so.2"),
                    "opt/vodbox/libmpv.so.2": (tarfile.SYMTYPE, "lib/libmpv.so.2.0"),
                    "opt/vodbox/libmpv-hard.so": (tarfile.LNKTYPE, "./opt/vodbox/libmpv.so"),
                    "opt/vodbox/lib/license": (tarfile.SYMTYPE, "../JavaHost/LICENSE-tvbox-silicon"),
                    "opt/vodbox/JavaHost/jre/legal/java.base/LICENSE":
                        (tarfile.SYMTYPE, "../../LICENSE"),
                }
                asset = write_linked_package(root / f"vodbox_1_linux-x64.{suffix}", fixture, links,
                                             {"opt/vodbox/lib/libmpv.so.2.0": b"real mpv bytes",
                                              "opt/vodbox/JavaHost/jre/LICENSE": b"runtime.*license"})
                with evidence.extracted_package(asset) as payload:
                    for name in ("libmpv.so", "libmpv.so.2", "libmpv-hard.so"):
                        path = payload / "opt/vodbox" / name
                        self.assertFalse(path.is_symlink())
                        self.assertEqual(path.read_bytes(), b"real mpv bytes")
                self.assertEqual(evidence.verify_rid("linux-x64", asset)["rid"], "linux-x64")
                receipts = root / "receipts"
                receipt = evidence.write_package_receipt("linux-x64", asset, receipts)
                data = evidence._read_package_receipt(receipt)
                self.assertEqual(data["files"]["opt/vodbox/libmpv.so"]["sha256"],
                                 hashlib.sha256(b"real mpv bytes").hexdigest())
                evidence.verify_package_receipt("linux-x64", asset, receipts)

    def test_tar_and_deb_unsafe_or_unresolvable_links_are_refused(self):
        cases = [
            {"alias": (tarfile.SYMTYPE, "/etc/passwd")},
            {"alias": (tarfile.LNKTYPE, "/JavaHost/host.jar")},
            {"alias": (tarfile.SYMTYPE, "../outside")},
            {"alias": (tarfile.LNKTYPE, "../outside")},
            {"alias": (tarfile.SYMTYPE, "C:/outside")},
            {"alias": (tarfile.SYMTYPE, "missing")},
            {"alias": (tarfile.SYMTYPE, "JavaHost")},
            {"alias": (tarfile.SYMTYPE, "alias")},
            {"alias": (tarfile.SYMTYPE, "other"), "other": (tarfile.LNKTYPE, "alias")},
            {"alias": (tarfile.SYMTYPE, "JavaHost/host.jar/../host.jar")},
            {"JavaHost": (tarfile.SYMTYPE, "elsewhere")},
        ]
        for suffix in ("tar", "deb"):
            for links in cases:
                with self.subTest(format=suffix, links=links), tempfile.TemporaryDirectory(dir=ALMA) as directory:
                    root = pathlib.Path(directory)
                    fixture = write_zip(root / "VodBox.1.linux-x64.zip", host_members())
                    asset = write_linked_package(root / f"vodbox_1_linux-x64.{suffix}", fixture, links)
                    with self.assertRaises(evidence.ReleaseEvidenceError):
                        evidence.verify_rid("linux-x64", asset)
                    self.assertEqual(sorted(p.name for p in root.iterdir()),
                                     sorted([fixture.name, asset.name]))

    def test_linked_source_native_and_license_bytes_are_still_verified(self):
        source = "JavaHost/corresponding-source/" + SOURCE_PREFIX
        members = {
            source + "dependency-sources/archives/engine.tar": b"changed source",
            "JavaHost/deps/unidbg-unicorn2-0.9.8.jar": jar_bytes({
                "Backend.class": b"class", native_entry_for("linux-x64"): b"changed native"}),
            "JavaHost/jre/legal/java.base/LICENSE": b"",
        }
        for suffix in ("tar", "deb"):
            for name, changed in members.items():
                with self.subTest(format=suffix, member=name), tempfile.TemporaryDirectory(dir=ALMA) as directory:
                    root = pathlib.Path(directory)
                    fixture = write_zip(root / "VodBox.1.linux-x64.zip", host_members())
                    target = name + ".actual"
                    links = {name: (tarfile.SYMTYPE, pathlib.PurePosixPath(target).name)}
                    asset = write_linked_package(root / f"vodbox_1_linux-x64.{suffix}", fixture,
                                                 links, {target: changed})
                    with self.assertRaises(evidence.ReleaseEvidenceError):
                        evidence.verify_rid("linux-x64", asset)

    def test_tar_payload_is_verified_and_unknown_formats_fail_closed(self):
        with tempfile.TemporaryDirectory(dir=ALMA) as directory:
            root = pathlib.Path(directory)
            path = write_zip(root / "VodBox.1.osx-arm64.zip", host_members())
            tar_path = root / "VodBox.1.osx-arm64.tar.gz"
            with zipfile.ZipFile(path) as source, tarfile.open(tar_path, "w:gz") as output:
                for name in source.namelist():
                    data = source.read(name)
                    info = tarfile.TarInfo(name)
                    info.size = len(data)
                    output.addfile(info, io.BytesIO(data))
            self.assertEqual(evidence.verify_rid("osx-arm64", tar_path)["rid"], "osx-arm64")
            unknown = root / "VodBox.1.osx-arm64.unknown"
            unknown.write_bytes(path.read_bytes())
            with self.assertRaisesRegex(evidence.ReleaseEvidenceError, "unsupported package format"):
                evidence.verify_rid("osx-arm64", unknown)

    def test_archive_traversal_and_duplicate_receipts_are_refused(self):
        for name in ("../outside", "JavaHost/native-backend-receipt.json"):
            with self.subTest(member=name), tempfile.TemporaryDirectory(dir=ALMA) as directory:
                path = write_zip(pathlib.Path(directory) / "VodBox.1.osx-arm64.zip", host_members())
                with zipfile.ZipFile(path, "a") as archive:
                    archive.writestr(name, b"invalid")
                with self.assertRaises(evidence.ReleaseEvidenceError):
                    evidence.verify_rid("osx-arm64", path)


class RpmFallbackTests(unittest.TestCase):
    """Model the documented bsdtar conversion; use real TAR bytes and the real resolver."""

    def test_rpm_is_read_once_and_all_file_and_link_bytes_are_preserved(self):
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            asset = root / "package.rpm"
            asset.write_bytes(b"RPM fixture: conversion is mocked")
            data = bytes(range(256)) * (evidence.COPY_CHUNK // 256 + 1) + b"\x00tail\xff"
            files = {f"files/{index}": f"payload {index}".encode() for index in range(64)}
            files["files/actual\nname"] = data
            links = {
                "alias": (tarfile.SYMTYPE, "files/actual\nname"),
                "chain": (tarfile.SYMTYPE, "alias"),
                "hard": (tarfile.LNKTYPE, "files/actual\nname"),
            }

            def convert(command, **kwargs):
                converted = pathlib.Path(command[3])
                self.assertEqual(converted.name, "rpm-payload.tar")
                self.assertEqual(command, ["bsdtar", "--format=pax", "-cf",
                                           str(converted), "@" + str(asset)])
                with tarfile.open(command[3], "w", format=tarfile.PAX_FORMAT) as output:
                    for name, (kind, target) in links.items():
                        info = tarfile.TarInfo(name)
                        info.type, info.linkname = kind, target
                        output.addfile(info)
                    for name, content in files.items():
                        info = tarfile.TarInfo(name)
                        info.size = len(content)
                        output.addfile(info, io.BytesIO(content))
                return evidence.subprocess.CompletedProcess(command, 0, b"", b"")

            with unittest.mock.patch.object(evidence.shutil, "which", return_value="bsdtar"), \
                    unittest.mock.patch.object(evidence.subprocess, "run", side_effect=convert) as run:
                with evidence.extracted_package(asset) as payload:
                    actual = {path.relative_to(payload).as_posix(): path.read_bytes()
                              for path in payload.rglob("*") if path.is_file()}
                    self.assertEqual(actual, {**files, **{name: data for name in links}})
                    self.assertFalse(any(path.is_symlink() for path in payload.rglob("*")))
                run.assert_called_once()
                self.assertEqual(run.call_args.args[0][-1], "@" + str(asset))
            self.assertEqual(list(root.iterdir()), [asset])

    def test_rpm_conversion_still_refuses_unsafe_members_and_links(self):
        cases = [
            ("../outside", tarfile.REGTYPE, ""),
            ("/outside", tarfile.REGTYPE, ""),
            ("alias", tarfile.SYMTYPE, "/outside"),
            ("alias", tarfile.SYMTYPE, "../outside"),
            ("alias", tarfile.SYMTYPE, "missing"),
            ("alias", tarfile.SYMTYPE, "alias"),
            ("alias", tarfile.LNKTYPE, "../outside"),
        ]
        for name, kind, target in cases:
            with self.subTest(member=name, target=target), tempfile.TemporaryDirectory() as directory:
                root = pathlib.Path(directory)
                asset = root / "package.rpm"
                asset.write_bytes(b"RPM fixture: conversion is mocked")

                def convert(command, **kwargs):
                    with tarfile.open(command[3], "w", format=tarfile.PAX_FORMAT) as output:
                        info = tarfile.TarInfo(name)
                        info.type, info.linkname = kind, target
                        output.addfile(info)
                    return evidence.subprocess.CompletedProcess(command, 0, b"", b"")

                with unittest.mock.patch.object(evidence.shutil, "which", return_value="bsdtar"), \
                        unittest.mock.patch.object(evidence.subprocess, "run", side_effect=convert) as run:
                    with self.assertRaises(evidence.ReleaseEvidenceError):
                        with evidence.extracted_package(asset):
                            self.fail("unsafe RPM payload was accepted")
                    run.assert_called_once()
                self.assertEqual(list(root.iterdir()), [asset])


class SixRidClosureTests(unittest.TestCase):
    def setUp(self):
        ALMA.mkdir(exist_ok=True)
        pin = unittest.mock.patch.object(evidence.rebuild, "_engines",
                                         return_value={"archive_sha256": SOURCE_HASH})
        pin.start()
        self.addCleanup(pin.stop)

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


class PackageReceiptTests(RidVerdictTests):
    def test_only_checked_subset_bytes_are_retained(self):
        with tempfile.TemporaryDirectory(dir=ALMA) as directory:
            root = pathlib.Path(directory)
            archive = write_zip(root / "VodBox.1.osx-arm64.zip", host_members())
            rewrite_zip(archive, {"VodBox.Desktop": b"application" * 1024,
                                  "JavaHost/jre/bin/java": b"runtime" * 1024})
            path = evidence.write_package_receipt("osx-arm64", archive, root / "receipts")
            data = evidence._read_package_receipt(path)
            self.assertEqual(data["schema"], 2)
            self.assertEqual(data["files"]["VodBox.Desktop"], {
                "size": len(b"application" * 1024),
                "sha256": hashlib.sha256(b"application" * 1024).hexdigest()})
            names = set(data["subset"])
            self.assertEqual(path.read_bytes()[:2], b"\x1f\x8b")
            self.assertNotIn("VodBox.Desktop", names)
            self.assertNotIn("JavaHost/jre/bin/java", names)
            self.assertIn("JavaHost/jre/legal/java.base/LICENSE", names)
            self.assertIn("JavaHost/deps/unidbg-unicorn2-0.9.8.jar", names)
            self.assertIn("JavaHost/corresponding-source/" + SOURCE_PREFIX +
                          "dependency-sources/archives/engine.tar", names)
            self.assertEqual(names, {name for name in data["files"] if evidence._retain_member(name)})
            evidence.verify_package_receipt("osx-arm64", archive, path.parent)

    def test_subset_and_receipt_limits_fail_without_truncating_checks(self):
        with tempfile.TemporaryDirectory(dir=ALMA) as directory:
            root = pathlib.Path(directory)
            archive = write_zip(root / "VodBox.1.osx-arm64.zip", host_members())
            receipts = root / "receipts"
            with unittest.mock.patch.object(evidence, "MAX_SUBSET_BYTES", 1):
                with self.assertRaisesRegex(evidence.ReleaseEvidenceError, "subset exceeds"):
                    evidence.write_package_receipt("osx-arm64", archive, receipts)
            self.assertEqual(list(receipts.iterdir()), [])
            path = evidence.write_package_receipt("osx-arm64", archive, receipts)
            with unittest.mock.patch.object(evidence, "MAX_SUBSET_BYTES", 1):
                with self.assertRaisesRegex(evidence.ReleaseEvidenceError, "subset exceeds"):
                    evidence.verify_package_receipt("osx-arm64", archive, receipts)
            with unittest.mock.patch.object(evidence, "MAX_RECEIPT_BYTES", 1):
                with self.assertRaisesRegex(evidence.ReleaseEvidenceError, "receipt exceeds"):
                    evidence.verify_package_receipt("osx-arm64", archive, receipts)
            self.assertTrue(path.is_file())

    def test_subset_tampering_and_missing_bytes_are_refused(self):
        for mode in ("hash", "missing", "traversal", "source", "encoding", "extra"):
            with self.subTest(mode=mode), tempfile.TemporaryDirectory(dir=ALMA) as directory:
                root = pathlib.Path(directory)
                archive = write_zip(root / "VodBox.1.osx-arm64.zip", host_members())
                path = evidence.write_package_receipt("osx-arm64", archive, root / "receipts")
                data = evidence._read_package_receipt(path)
                member = "JavaHost/corresponding-source/" + BRIDGE_PATH + "/unicorn.c"
                if mode == "missing":
                    data["subset"].pop(member)
                elif mode in ("hash", "source"):
                    changed = b"changed source"
                    data["subset"][member] = evidence.base64.b64encode(changed).decode("ascii")
                    if mode == "source":
                        data["files"][member] = {
                            "size": len(changed), "sha256": hashlib.sha256(changed).hexdigest()}
                elif mode == "encoding":
                    data["subset"][member] = "!" * len(data["subset"][member])
                else:
                    data["subset"]["../outside" if mode == "traversal" else "VodBox.Desktop"] = ""
                with gzip.open(path, "wt", encoding="utf-8") as stream:
                    json.dump(data, stream)
                with self.assertRaises(evidence.ReleaseEvidenceError):
                    evidence.verify_package_receipt("osx-arm64", archive, path.parent)

    def test_replay_checks_content_even_with_updated_manifest_hashes(self):
        source = "JavaHost/corresponding-source/"
        cases = {
            source + SOURCE_PREFIX + "dependency-sources/archives/engine.tar": b"changed source",
            source + SOURCE_PREFIX + "dependency-sources/archives/unicorn.tar": b"changed source",
            "JavaHost/jre/legal/java.base/LICENSE": b"",
            "JavaHost/deps/unidbg-unicorn2-0.9.8.jar": jar_bytes({
                "Backend.class": b"class", native_entry_for("osx-arm64"): b"changed native"}),
        }
        for member, changed in cases.items():
            with self.subTest(member=member), tempfile.TemporaryDirectory(dir=ALMA) as directory:
                root = pathlib.Path(directory)
                archive = write_zip(root / "VodBox.1.osx-arm64.zip", host_members())
                path = evidence.write_package_receipt("osx-arm64", archive, root / "receipts")
                data = evidence._read_package_receipt(path)
                data["subset"][member] = evidence.base64.b64encode(changed).decode("ascii")
                data["files"][member] = {"size": len(changed), "sha256": hashlib.sha256(changed).hexdigest()}
                with gzip.open(path, "wt", encoding="utf-8") as stream:
                    json.dump(data, stream)
                with unittest.mock.patch.object(evidence, "extracted_package", side_effect=AssertionError("must fail during replay")):
                    with self.assertRaises(evidence.ReleaseEvidenceError):
                        evidence.verify_package_receipt("osx-arm64", archive, path.parent)

    def test_duplicate_json_keys_and_decompression_overflow_are_refused(self):
        with tempfile.TemporaryDirectory(dir=ALMA) as directory:
            path = pathlib.Path(directory) / "receipt.json"
            with gzip.open(path, "wt", encoding="utf-8") as stream:
                stream.write('{"rid":"osx-arm64","rid":"linux-x64"}')
            with self.assertRaisesRegex(evidence.ReleaseEvidenceError, "duplicate"):
                evidence._read_package_receipt(path)
            with gzip.open(path, "wb") as stream:
                stream.write(b" " * 64)
            with unittest.mock.patch.object(evidence, "MAX_SUBSET_BYTES", 0), \
                    unittest.mock.patch.object(evidence, "MAX_RECEIPT_BYTES", 1), \
                    unittest.mock.patch.object(evidence, "MAX_MANIFEST_FILES", 0):
                with self.assertRaisesRegex(evidence.ReleaseEvidenceError, "decompressed byte limit"):
                    evidence._read_package_receipt(path)

    def test_unretained_payload_manifest_cannot_be_forged(self):
        with tempfile.TemporaryDirectory(dir=ALMA) as directory:
            root = pathlib.Path(directory)
            archive = write_zip(root / "VodBox.1.osx-arm64.zip", host_members())
            path = evidence.write_package_receipt("osx-arm64", archive, root / "receipts")
            data = evidence._read_package_receipt(path)
            data["files"]["VodBox.Desktop"]["sha256"] = "0" * 64
            with gzip.open(path, "wt", encoding="utf-8") as stream:
                json.dump(data, stream)
            with self.assertRaisesRegex(evidence.ReleaseEvidenceError, "manifest differs from actual asset"):
                evidence.verify_package_receipt("osx-arm64", archive, path.parent)

    def test_dmg_producer_cannot_claim_a_non_native_runner(self):
        with tempfile.TemporaryDirectory(dir=ALMA) as directory:
            root = pathlib.Path(directory)
            archive = root / "VodBox.1.osx-arm64.dmg"
            archive.write_bytes(b"not mounted")
            with unittest.mock.patch.object(evidence.sys, "platform", "linux"), \
                    unittest.mock.patch.object(evidence, "extracted_package") as extract:
                with self.assertRaisesRegex(evidence.ReleaseEvidenceError, "macOS runner"):
                    evidence.write_package_receipt("osx-arm64", archive, root / "receipts")
                extract.assert_not_called()

    def test_linux_installer_names_and_every_asset_are_discovered(self):
        with tempfile.TemporaryDirectory(dir=ALMA) as directory:
            root = pathlib.Path(directory)
            for name in ("vodbox_1.0_linux-x64.deb", "vodbox_1.0_linux-x64.rpm",
                         "VodBox_1.0.linux-x64.zip"):
                (root / name).write_bytes(b"installer")
            self.assertEqual(len(evidence.find_archives("linux-x64", root)), 3)

    def test_receipt_round_trip_and_real_zip_reverification(self):
        with tempfile.TemporaryDirectory(dir=ALMA) as directory:
            root = pathlib.Path(directory)
            archive = write_zip(root / "VodBox.1.osx-arm64.zip", host_members())
            receipts = root / "receipts"
            evidence.write_package_receipt("osx-arm64", archive, receipts)
            result = evidence.verify_release(["osx-arm64"], root, package_receipts_dir=receipts)
            self.assertEqual(len(result["verdicts"]["osx-arm64"]["assets"]), 1)
            with unittest.mock.patch.object(evidence, "verify_rid", side_effect=evidence.ReleaseEvidenceError("rechecked")):
                with self.assertRaisesRegex(evidence.ReleaseEvidenceError, "rechecked"):
                    evidence.verify_release(["osx-arm64"], root, package_receipts_dir=receipts)

    def test_receipt_mismatch_summary_and_source_tampering_fail(self):
        for mode in ("hash", "size", "summary", "source", "checks"):
            with self.subTest(mode=mode), tempfile.TemporaryDirectory(dir=ALMA) as directory:
                root = pathlib.Path(directory)
                archive = write_zip(root / "VodBox.1.osx-arm64.zip", host_members())
                path = evidence.write_package_receipt("osx-arm64", archive, root / "receipts")
                data = evidence._read_package_receipt(path)
                if mode == "hash":
                    archive.write_bytes(archive.read_bytes() + b"changed")
                elif mode == "size":
                    data["size"] += 1
                elif mode == "summary":
                    data.pop("files")
                    data["verified"] = True
                elif mode == "checks":
                    data["checks"]["sourceEvidence"]["bridgeDigest"] = "fake"
                else:
                    member = "JavaHost/corresponding-source/" + BRIDGE_PATH + "/unicorn.c"
                    data["files"][member]["sha256"] = hashlib.sha256(b"changed source").hexdigest()
                with gzip.open(path, "wt", encoding="utf-8") as stream:
                    json.dump(data, stream)
                with self.assertRaises(evidence.ReleaseEvidenceError):
                    evidence.verify_package_receipt("osx-arm64", archive, path.parent)

    def test_deb_and_rpm_receipts_do_not_skip_actual_asset_reverification(self):
        for suffix in ("deb", "rpm"):
            with self.subTest(format=suffix), tempfile.TemporaryDirectory(dir=ALMA) as directory:
                root = pathlib.Path(directory)
                fixture = write_zip(root / "VodBox.1.linux-x64.zip", host_members(), prefix="opt/vodbox/")
                asset = root / f"vodbox_1_linux-x64.{suffix}"
                asset.write_bytes(b"installer fixture")
                with evidence.extracted_package(fixture) as payload:
                    with unittest.mock.patch.object(evidence, "extracted_package", return_value=contextlib.nullcontext(payload)):
                        evidence.write_package_receipt("linux-x64", asset, root / "receipts")
                        fixture.unlink()
                        with unittest.mock.patch.object(evidence, "verify_rid", side_effect=evidence.ReleaseEvidenceError("actual installer rechecked")) as verify:
                            with self.assertRaisesRegex(evidence.ReleaseEvidenceError, "actual installer rechecked"):
                                evidence.verify_release(["linux-x64"], root, package_receipts_dir=root / "receipts")
                            verify.assert_called_once_with("linux-x64", asset, None, None)

    def test_valid_zip_does_not_hide_unverified_dmg(self):
        with tempfile.TemporaryDirectory(dir=ALMA) as directory:
            root = pathlib.Path(directory)
            write_zip(root / "VodBox.1.osx-arm64.zip", host_members())
            (root / "VodBox.1.osx-arm64.dmg").write_bytes(b"real DMG needed")
            with unittest.mock.patch.object(evidence.sys, "platform", "linux"):
                with self.assertRaisesRegex(evidence.ReleaseEvidenceError, "dmg"):
                    evidence.verify_release(["osx-arm64"], root)

    def test_dmg_uses_mac_extraction_receipt_and_never_hdiutil_on_linux(self):
        with tempfile.TemporaryDirectory(dir=ALMA) as directory:
            root = pathlib.Path(directory)
            fixture = write_zip(root / "VodBox.1.osx-arm64.zip", host_members(),
                                prefix="VodBox.app/Contents/MacOS/")
            dmg = root / "VodBox.1.osx-arm64.dmg"
            dmg.write_bytes(b"DMG fixture bytes")
            # Model files exposed by an attached DMG, not a ZIP disguised as a DMG.
            with evidence.extracted_package(fixture) as payload:
                with unittest.mock.patch.object(evidence.sys, "platform", "darwin"), \
                        unittest.mock.patch.object(evidence, "extracted_package", return_value=contextlib.nullcontext(payload)):
                    evidence.write_package_receipt("osx-arm64", dmg, root / "receipts")
            evidence.write_package_receipt("osx-arm64", fixture, root / "receipts")
            with unittest.mock.patch.object(evidence.sys, "platform", "linux"), \
                    unittest.mock.patch.object(evidence, "_command", side_effect=AssertionError("hdiutil on Linux")):
                result = evidence.verify_release(["osx-arm64"], root, package_receipts_dir=root / "receipts")
                self.assertEqual(len(result["verdicts"]["osx-arm64"]["assets"]), 2)
                path = evidence.package_receipt_path(root / "receipts", dmg)
                data = evidence._read_package_receipt(path)
                data["platform"] = "linux"
                with gzip.open(path, "wt", encoding="utf-8") as stream:
                    json.dump(data, stream)
                with self.assertRaisesRegex(evidence.ReleaseEvidenceError, "macOS runner"):
                    evidence.verify_package_receipt("osx-arm64", dmg, path.parent)


class CommandLineTests(unittest.TestCase):

    def setUp(self):
        ALMA.mkdir(exist_ok=True)
        pin = unittest.mock.patch.object(evidence.rebuild, "_engines",
                                         return_value={"archive_sha256": SOURCE_HASH})
        pin.start()
        self.addCleanup(pin.stop)

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
