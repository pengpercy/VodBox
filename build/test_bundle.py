"""Offline packaging regression tests; no network or native tools required."""
import contextlib
import importlib.util
import io
import pathlib
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('vodbox_bundle', pathlib.Path(__file__).with_name('bundle.py'))
(pathlib.Path(__file__).resolve().parent.parent / ".alma").mkdir(exist_ok=True)
bundle = importlib.util.module_from_spec(spec)
spec.loader.exec_module(bundle)

class BundleTests(unittest.TestCase):
    def test_windows_and_linux_must_download_and_bundle(self):
        for rid in ('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64'):
            with self.subTest(rid=rid), tempfile.TemporaryDirectory(dir=bundle.ROOT / '.alma') as directory:
                archive = pathlib.Path(directory) / 'asset'
                method = 'bundle_windows' if rid.startswith('win') else 'bundle_linux'
                with patch('sys.argv', ['bundle.py', rid, directory]), patch.object(bundle, 'download', return_value=archive) as download, patch.object(bundle, method) as package, patch.object(bundle, 'copy_media_notices'), patch.object(bundle, 'bundle_java_host') as java_host:
                    bundle.main()
                    download.assert_called_once_with(rid)
                    package.assert_called_once_with(archive, pathlib.Path(directory), rid)

    def test_java_host_stages_deps_and_the_rids_own_jre(self):
        with tempfile.TemporaryDirectory(dir=bundle.ROOT / '.alma') as directory:
            output = pathlib.Path(directory)
            prebuilt = output / 'prebuilt.jar'; prebuilt.write_bytes(b'host-bytes')
            (output / 'deps').mkdir(); (output / 'deps/runtime.jar').write_bytes(b'dependency')
            # The staged backend jar is the *source-rebuilt* one: only this RID's native survives.
            self._write_backend_jar(output / 'deps', 'osx-arm64')
            self._write_receipt(output, 'osx-arm64', 'natives/osx_arm64/libunicorn.dylib')
            (output / 'corresponding-source').mkdir(); (output / 'corresponding-source/pom.xml').write_text('<project/>')
            (output / 'upstream-runtime.json').write_text('{}')
            (output / 'LICENSE-tvbox-silicon').write_text('GPL-3.0-only')
            calls = []
            with patch.object(bundle, 'run', side_effect=lambda *args: calls.append([str(x) for x in args])), \
                 patch.object(bundle, 'verify_jre', return_value='AArch64') as verify:
                bundle.bundle_java_host('osx-arm64', output, prebuilt)
            host = output / 'JavaHost'
            # host.jar is placed verbatim; nothing is compiled when a prebuilt jar is supplied.
            self.assertEqual((host / 'host.jar').read_bytes(), b'host-bytes')
            staged = [call for call in calls if call[1].endswith('fetch-dex-deps.py')]
            # Dependencies come from the pinned lock table, then the target RID's JRE — never `java`.
            self.assertEqual([call[2] for call in staged], ['jre'])
            self.assertEqual((host / 'deps/runtime.jar').read_bytes(), b'dependency')
            self.assertTrue((host / 'corresponding-source/pom.xml').is_file())
            self.assertTrue((host / 'upstream-runtime.json').is_file())
            self.assertIn('osx-arm64', staged[0])
            verify.assert_called_once_with(host / 'jre', 'osx-arm64')
            notice = bundle.json.loads((host / 'java-host.json').read_text())
            self.assertEqual(notice['jre']['architecture'], 'AArch64')
            self.assertEqual(notice['dependencies'], ['deps'])

    def test_environment_prebuilt_preserves_the_complete_component(self):
        with tempfile.TemporaryDirectory(dir=bundle.ROOT / '.alma') as directory:
            root = pathlib.Path(directory)
            source = root / 'component'; source.mkdir()
            (source / 'host.jar').write_bytes(b'prebuilt')
            (source / 'deps').mkdir(); (source / 'deps/runtime.jar').write_bytes(b'dependency')
            (source / 'corresponding-source').mkdir(); (source / 'corresponding-source/pom.xml').write_text('<project/>')
            (source / 'upstream-runtime.json').write_text('{}')
            (source / 'LICENSE-tvbox-silicon').write_text('GPL-3.0-only')
            self._write_receipt(source, 'osx-arm64', 'natives/osx_arm64/libunicorn.dylib')
            target = root / 'publish'; target.mkdir()
            with patch.dict(bundle.os.environ, {'VODBOX_JAR_HOST': str(source / 'host.jar')}):
                bundle.build_host_jar(target / 'host.jar', 'osx-arm64')
            self.assertEqual((target / 'deps/runtime.jar').read_bytes(), b'dependency')
            self.assertTrue((target / 'corresponding-source/pom.xml').is_file())
            self.assertTrue((target / 'LICENSE-tvbox-silicon').is_file())
            # The source-rebuild receipt travels with the staged component.
            self.assertTrue((target / 'native-backend-receipt.json').is_file())

    def test_prebuilt_component_with_empty_deps_is_rejected(self):
        # An empty deps/ cannot link the upstream classes: the host starts and fails to load a
        # plugin. Rejecting it at staging keeps the failure at build time, where it is diagnosable.
        with tempfile.TemporaryDirectory(dir=bundle.ROOT / '.alma') as directory:
            root = pathlib.Path(directory)
            component = root / 'JavaHost'; component.mkdir()
            (component / 'host.jar').write_bytes(b'prebuilt')
            (component / 'deps').mkdir()
            (component / 'corresponding-source').mkdir()
            (component / 'upstream-runtime.json').write_text('{}')
            (component / 'LICENSE-tvbox-silicon').write_text('GPL-3.0-only')
            with self.assertRaisesRegex(ValueError, 'empty'):
                bundle.stage_prebuilt_host(component / 'host.jar', root / 'publish' / 'host.jar')

    def test_java_host_records_the_native_backend_verdict(self):
        # java-host.json must state whether the RID can run the upstream native backend. A bundled
        # JRE alone is not evidence, so the verdict is written from the bundled deps' real contents.
        for rid, supported in (('win-arm64', False), ('linux-arm64', True)):
            with self.subTest(rid=rid), tempfile.TemporaryDirectory(dir=bundle.ROOT / '.alma') as directory:
                output = pathlib.Path(directory)
                backend = output / 'component'; backend.mkdir()
                (backend / 'host.jar').write_bytes(b'host-bytes')
                (backend / 'deps').mkdir()
                # Source-rebuilt deps: the RID's own native, or none at all for an exclusion RID.
                self._write_backend_jar(backend / 'deps', rid)
                self._write_receipt(backend, rid, None if not supported else
                                    {'linux-arm64': 'natives/linux_arm64/libunicorn.so'}[rid])
                (backend / 'corresponding-source').mkdir(); (backend / 'corresponding-source/pom.xml').write_text('<project/>')
                (backend / 'upstream-runtime.json').write_text('{}')
                (backend / 'LICENSE-tvbox-silicon').write_text('GPL-3.0-only')
                with patch.object(bundle, 'run'), patch.object(bundle, 'verify_jre', return_value='AArch64'):
                    bundle.bundle_java_host(rid, output, backend / 'host.jar')
                notice = bundle.json.loads((output / 'JavaHost' / 'java-host.json').read_text())
                self.assertEqual(notice['nativeBackend']['supported'], supported)
                self.assertIn(rid, notice['nativeBackend']['verdict'])
                self.assertEqual(notice['nativeBackend']['excluded'], not supported)
                if not supported:
                    self.assertIn('unsupported_native_library', notice['nativeBackend']['verdict'])

    def test_java_host_rejects_deps_that_contradict_the_coverage_table(self):
        # If the shipped backend jar cannot serve a RID the lock table declares supported, the
        # bundle must fail rather than record a green verdict for an unrunnable platform.
        with tempfile.TemporaryDirectory(dir=bundle.ROOT / '.alma') as directory:
            output = pathlib.Path(directory)
            component = output / 'component'; component.mkdir()
            (component / 'host.jar').write_bytes(b'host-bytes')
            (component / 'deps').mkdir()
            # Only macOS natives: the staged jar cannot serve the linux-x64 bundle being built.
            self._write_backend_jar(component / 'deps', 'osx-arm64')
            self._write_receipt(component, 'osx-arm64', 'natives/osx_arm64/libunicorn.so')
            (component / 'corresponding-source').mkdir(); (component / 'corresponding-source/pom.xml').write_text('<project/>')
            (component / 'upstream-runtime.json').write_text('{}')
            (component / 'LICENSE-tvbox-silicon').write_text('GPL-3.0-only')
            with patch.object(bundle, 'run'), patch.object(bundle, 'verify_jre', return_value='x86-64'):
                with self.assertRaisesRegex(ValueError, 'native coverage'):
                    bundle.bundle_java_host('linux-x64', output, component / 'host.jar')

    def _write_backend_jar(self, deps, rid=None):
        import zipfile
        directories = {'osx-x64': ['osx_64'], 'osx-arm64': ['osx_arm64'], 'win-x64': ['windows_64'],
                       'win-arm64': [], 'linux-x64': ['linux_64'], 'linux-arm64': ['linux_arm64']}
        with zipfile.ZipFile(deps / 'unidbg-unicorn2-0.9.8.jar', 'w') as archive:
            # A source-rebuilt jar carries only the requested RID's native (win-arm64 carries none);
            # without a rid the legacy upstream jar carries all five supported natives.
            selected = directories[rid] if rid else [d for key, values in directories.items()
                                                     if key != "win-arm64" for d in values]
            for directory in selected:
                archive.writestr(f'natives/{directory}/libunicorn.so', b'\x7fELF')

    def _write_receipt(self, component, rid, native_entry):
        """A source-rebuild receipt shaped like `rebuild_native_backend.py` writes (classes-only
        when `native_entry` is None, i.e. an exclusion RID such as win-arm64)."""
        entries = [native_entry] if native_entry else []
        (component / 'native-backend-receipt.json').write_text(bundle.json.dumps({
            'schema': 1,
            'rid': rid,
            'backend': {
                'excluded': native_entry is None,
                'exclusion_reason': '' if native_entry else f'{rid} has no upstream JNI native',
                'jar_integration': {
                    'native_entry': native_entry,
                    'jar_native_entries': entries,
                },
            },
        }))

    def test_windows_exe_only_asset_must_fail(self):
        import zipfile
        with tempfile.TemporaryDirectory(dir=bundle.ROOT / '.alma') as directory:
            archive = pathlib.Path(directory) / 'asset.zip'
            with zipfile.ZipFile(archive, 'w') as source:
                source.writestr('mpv.exe', b'not a dll')
            with self.assertRaisesRegex(ValueError, 'no libmpv'):
                bundle.bundle_windows(archive, pathlib.Path(directory), 'win-x64')

    def test_windows_loader_is_not_optional(self):
        with tempfile.TemporaryDirectory(dir=bundle.ROOT / '.alma') as directory:
            (pathlib.Path(directory) / 'libmpv-2.dll').write_bytes(b'fixture')
            with self.assertRaisesRegex(bundle.NativeVerifyError, 'vulkan-1.dll'):
                bundle.verify_windows_closure(pathlib.Path(directory), 'win-x64')

    def test_unknown_rid_rejected_before_download(self):
        with patch('sys.argv', ['bundle.py', 'unknown', '.']), patch.object(bundle, 'download', side_effect=AssertionError('bad RID')), contextlib.redirect_stderr(io.StringIO()):
            with self.assertRaises(SystemExit):
                bundle.main()

    def test_shared_inode_alias_is_kept_without_duplicating_bytes(self):
        base = pathlib.Path(__file__).resolve().parent.parent / '.alma'
        with tempfile.TemporaryDirectory(dir=base) as directory:
            root = pathlib.Path(directory)
            source = root / 'source'; source.mkdir()
            (source / 'libbar.dylib').write_bytes(b'B' * 512)
            (source / 'libfoo.1.dylib').write_bytes(b'X' * 4096)
            (source / 'libfoo.dylib').symlink_to('libfoo.1.dylib')
            target = root / 'lib'
            written = bundle.copy_libraries(source, target)
            # One payload per inode: the 4096-byte library is stored once, the alias is a symlink.
            self.assertEqual(sorted(p.name for p in written), ['libbar.dylib', 'libfoo.1.dylib'])
            self.assertEqual((target / 'libfoo.1.dylib').stat().st_size, 4096)
            self.assertTrue((target / 'libfoo.dylib').is_symlink())
            self.assertEqual((target / 'libfoo.dylib').resolve().name, 'libfoo.1.dylib')
            self.assertEqual(len(list(target.iterdir())), 3)

    def test_missing_dependencies_reports_truncated_closure(self):
        class Result:
            def __init__(self, stdout): self.stdout = stdout

        listing = {
            'libmpv.dylib': 'libmpv.dylib:\n\t@loader_path/lib/a.dylib\n\t/usr/lib/libSystem.B.dylib\n',
            'a.dylib': 'a.dylib:\n\t@loader_path/b.dylib\n',
            'b.dylib': 'b.dylib:\n\t/usr/lib/libSystem.B.dylib\n',
        }
        def fake_otool(args, **kwargs):
            return Result(listing.get(pathlib.Path(args[-1]).name, f'{args[-1]}:\n'))
        base = pathlib.Path(__file__).resolve().parent.parent / '.alma'
        with tempfile.TemporaryDirectory(dir=base) as directory:
            libs = pathlib.Path(directory) / 'lib'; libs.mkdir()
            (libs / 'a.dylib').write_text('fixture')
            with patch.object(bundle.subprocess, 'run', side_effect=fake_otool):
                self.assertEqual(bundle.missing_dependencies(libs / 'libmpv.dylib', libs), ['b.dylib'])
                (libs / 'b.dylib').write_text('fixture')
                self.assertEqual(bundle.missing_dependencies(libs / 'libmpv.dylib', libs), [])

    def test_missing_dependencies_fails_when_otool_fails(self):
        class Result:
            def __init__(self, returncode): self.returncode = returncode; self.stdout = ''

        def failing_otool(args, **kwargs):
            # Mirror subprocess.run's contract: a non-zero tool only raises when check was requested.
            result = Result(returncode=1)
            if kwargs.get('check') and result.returncode:
                raise bundle.subprocess.CalledProcessError(result.returncode, args)
            return result

        base = pathlib.Path(__file__).resolve().parent.parent / '.alma'
        with tempfile.TemporaryDirectory(dir=base) as directory:
            libs = pathlib.Path(directory) / 'lib'
            with patch.object(bundle.subprocess, 'run', side_effect=failing_otool):
                # An unrunnable otool must surface as a failure, not as an empty (passing) closure.
                with self.assertRaises(bundle.subprocess.CalledProcessError):
                    bundle.missing_dependencies(libs / 'libmpv.dylib', libs)

if __name__ == '__main__':
    unittest.main()
