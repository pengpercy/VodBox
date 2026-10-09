"""Symbol separation regression tests: debug payloads must leave the runtime tarball."""
import pathlib
import runpy
import sys
import tarfile
import tempfile
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[1]
(ROOT / '.alma').mkdir(exist_ok=True)


def archive(rid, source, output):
    argv = sys.argv
    sys.argv = ['archive.py', rid, str(source), str(output)]
    try:
        runpy.run_path(str(ROOT / 'build/archive.py'), run_name='__main__')
    finally:
        sys.argv = argv


class ArchiveTests(unittest.TestCase):
    def build_fixture(self, root):
        source = root / 'source'
        source.mkdir()
        (source / 'VodBox.Desktop').write_text('runtime fixture')
        (source / 'VodBox.Desktop.dSYM/Contents/Resources/DWARF').mkdir(parents=True)
        (source / 'VodBox.Desktop.dSYM/Contents/Resources/DWARF/VodBox.Desktop').write_text('dwarf')
        (source / 'VodBox.Core.pdb').write_text('pdb')
        (source / 'native/Nested.dSYM').mkdir(parents=True)
        (source / 'native/Nested.dSYM/payload').write_text('nested dwarf')
        (source / 'lib').mkdir()
        (source / 'lib/libfoo.dylib').write_text('dylib fixture')
        return source

    def test_dsym_and_pdb_leave_the_runtime_tarball(self):
        with tempfile.TemporaryDirectory(dir=ROOT / '.alma') as temp:
            root = pathlib.Path(temp)
            source = self.build_fixture(root)
            output = root / 'build'
            archive('osx-x64', source, output)

            # Symbols artifact keeps the whole dSYM bundle, including nested ones, and the pdb.
            symbols = output / 'symbols.osx-x64'
            self.assertTrue((symbols / 'VodBox.Desktop.dSYM/Contents/Resources/DWARF/VodBox.Desktop').is_file())
            self.assertTrue((symbols / 'VodBox.Core.pdb').is_file())
            self.assertTrue((symbols / 'native/Nested.dSYM/payload').is_file())
            # ... and the runtime tree no longer carries any debug payload.
            self.assertFalse((source / 'VodBox.Desktop.dSYM').exists())
            self.assertFalse((source / 'VodBox.Core.pdb').exists())
            self.assertFalse((source / 'native/Nested.dSYM').exists())

            with tarfile.open(output / 'VodBox.osx-x64.tar.gz') as tarball:
                names = tarball.getnames()
            self.assertIn('VodBox.Desktop', names)
            self.assertIn('lib/libfoo.dylib', names)
            self.assertNotIn('VodBox.Core.pdb', names)
            self.assertFalse(any('.dSYM' in name for name in names), names)


if __name__ == '__main__':
    unittest.main()
