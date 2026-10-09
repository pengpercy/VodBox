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
    def test_linux_never_requests_download_asset(self):
        with tempfile.TemporaryDirectory(dir=pathlib.Path(__file__).resolve().parent.parent / '.alma') as directory:
            for rid in ('linux-x64', 'linux-arm64'):
                with patch('sys.argv', ['bundle.py', rid, directory]), patch.object(bundle, 'download', side_effect=AssertionError('Linux must not download')), contextlib.redirect_stdout(io.StringIO()):
                    bundle.main()

    def test_windows_reports_runtime_requirement_without_downloading_or_failing(self):
        import pathlib as _p
        with _p.Path(__file__).resolve().parent.parent.joinpath('.alma').mkdir(parents=True, exist_ok=True) or tempfile.TemporaryDirectory(dir=_p.Path(__file__).resolve().parent.parent / '.alma') as directory:
            for rid in ('win-x64', 'win-arm64'):
                output = _p.Path(directory)
                with patch('sys.argv', ['bundle.py', rid, str(output)]), patch.object(bundle, 'download', side_effect=AssertionError('Windows must not download a DLL asset')):
                    stdout = io.StringIO()
                    with contextlib.redirect_stdout(stdout):
                        bundle.main()
                    self.assertIn('libmpv-2.dll', stdout.getvalue())

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
