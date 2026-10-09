"""Verify generated native launchers without invoking platform package tools."""
import importlib.util
import pathlib
import plistlib
import tempfile
import unittest
from unittest.mock import patch

ROOT = pathlib.Path(__file__).resolve().parents[1]
(ROOT / '.alma').mkdir(exist_ok=True)
spec = importlib.util.spec_from_file_location('vodbox_package', ROOT / 'build/package.py')
package = importlib.util.module_from_spec(spec)
spec.loader.exec_module(package)

class PackageTests(unittest.TestCase):
    def test_macos_executable_and_mpv_dependency_layout(self):
        with tempfile.TemporaryDirectory(dir=ROOT / '.alma') as temp:
            root = pathlib.Path(temp)
            source = root / 'source'; source.mkdir()
            (source / 'VodBox.Desktop').write_text('fixture')
            (source / 'VodBox.Desktop.pdb').write_text('symbols fixture')
            (source / 'VodBox.Desktop.dSYM/Contents/Resources/DWARF').mkdir(parents=True)
            (source / 'VodBox.Desktop.dSYM/Contents/Resources/DWARF/VodBox.Desktop').write_text('dwarf fixture')
            (source / 'libmpv.dylib').write_text('fixture')
            (source / 'licenses/media').mkdir(parents=True)
            (source / 'licenses/media/Vulkan-Loader-LICENSE.txt').write_text('license fixture')
            (source / 'media-dependencies.json').write_text('{}')
            (source / 'lib').mkdir(); (source / 'lib/dependency.dylib').write_text('fixture')
            (source / 'lib/.gitkeep').touch()
            (source / 'lib/._.gitkeep').touch()
            (source / 'Assets/js/lib').mkdir(parents=True)
            (source / 'Assets/js/lib/cheerio.min.js').write_text('resource fixture')
            output = root / 'output'; output.mkdir()
            with patch.object(package, 'run'):
                package.package_macos('osx-x64', source, output, '0.2.1')
            app = output / 'osx-x64/VodBox.app/Contents'
            with (app / 'Info.plist').open('rb') as file:
                self.assertEqual('VodBox.Desktop', plistlib.load(file)['CFBundleExecutable'])
            self.assertTrue((app / 'MacOS/lib/dependency.dylib').exists())
            self.assertTrue((app / 'MacOS/libmpv.dylib').exists())
            self.assertTrue((app / 'Resources/Assets/js/lib/cheerio.min.js').exists())
            self.assertFalse((app / 'MacOS/Assets').exists())
            self.assertFalse((app / 'MacOS/licenses').exists())
            self.assertFalse((app / 'MacOS/media-dependencies.json').exists())
            self.assertTrue((app / 'Resources/licenses/media/Vulkan-Loader-LICENSE.txt').exists())
            self.assertTrue((app / 'Resources/media-dependencies.json').exists())
            self.assertFalse((app / 'MacOS/lib/.gitkeep').exists())
            self.assertFalse((app / 'MacOS/lib/._.gitkeep').exists())
            # Debug payloads must never reach the shipped app bundle.
            self.assertFalse((app / 'MacOS/VodBox.Desktop.dSYM').exists())
            self.assertFalse((app / 'MacOS/VodBox.Desktop.pdb').exists())

    def test_linux_launcher_and_package_architecture(self):
        for rid, deb, rpm in [('linux-x64', 'amd64', 'x86_64'), ('linux-arm64', 'arm64', 'aarch64')]:
            with tempfile.TemporaryDirectory(dir=ROOT / '.alma') as temp:
                root = pathlib.Path(temp); source = root / 'source'; source.mkdir()
                (source / 'VodBox.Desktop').write_text('fixture')
                (source / 'lib').mkdir()
                (source / 'lib/libmpv.so.2').write_bytes(b'fixture')
                commands = []
                def capture(*args, **kwargs):
                    commands.append(args)
                    stage = pathlib.Path(args[args.index('-C') + 1])
                    launcher = (stage / 'usr/bin/vodbox').read_text()
                    self.assertIn('/opt/vodbox/VodBox.Desktop', launcher)
                    self.assertIn('/opt/vodbox/lib:', launcher)
                    self.assertTrue((stage / 'opt/vodbox/lib/libmpv.so.2').exists())
                    self.assertNotIn('libmpv2', args)
                with patch.object(package, 'run', side_effect=capture), patch.object(package, 'verify_linux_closure'):
                    package.package_linux(rid, source, root / 'output', '0.2.1')
                self.assertEqual(deb, commands[0][commands[0].index('--architecture') + 1])
                self.assertEqual(rpm, commands[1][commands[1].index('--architecture') + 1])

if __name__ == '__main__':
    unittest.main()
