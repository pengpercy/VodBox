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

    def test_unknown_rid_rejected_before_download(self):
        with patch('sys.argv', ['bundle.py', 'unknown', '.']), patch.object(bundle, 'download', side_effect=AssertionError('bad RID')), contextlib.redirect_stderr(io.StringIO()):
            with self.assertRaises(SystemExit):
                bundle.main()

if __name__ == '__main__':
    unittest.main()
