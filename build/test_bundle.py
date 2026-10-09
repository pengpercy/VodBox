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

if __name__ == '__main__':
    unittest.main()
