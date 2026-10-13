"""Verify generated native launchers without invoking platform package tools."""
import importlib.util
import os
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

    def test_macos_nested_jre_signature_order_and_source_preservation(self):
        for identity in ('-', 'Developer ID Application: fixture'):
            with self.subTest(identity=identity), tempfile.TemporaryDirectory(dir=ROOT / '.alma') as temp:
                root = pathlib.Path(temp)
                source = root / 'source'; source.mkdir()
                macho = b'\xcf\xfa\xed\xfe' + b'runtime fixture'
                (source / 'VodBox.Desktop').write_bytes(macho)
                jre = source / 'JavaHost/jre'
                (jre / 'Contents/Home/bin').mkdir(parents=True)
                (jre / 'Contents/Home/bin/java').write_bytes(macho)
                (jre / 'Contents/Home/lib/server').mkdir(parents=True)
                (jre / 'Contents/Home/lib/server/libjvm.dylib').write_bytes(macho)
                (jre / 'Contents/Home/lib/Fixture.class').write_bytes(b'\xca\xfe\xba\xbe\x00\x00\x00\x3d')
                (jre / 'Contents/Home/lib/libjvm.dylib').symlink_to('server/libjvm.dylib')
                (jre / 'Contents/Home/legal/java.base').mkdir(parents=True)
                (jre / 'Contents/Home/legal/java.base/LICENSE').write_text('JRE license')
                metadata = plistlib.dumps({'CFBundleExecutable': 'libjli.dylib',
                                          'CFBundlePackageType': 'BNDL'})
                (jre / 'Contents/MacOS').mkdir(parents=True)
                (jre / 'Contents/MacOS/libjli.dylib').write_bytes(macho)
                (jre / 'Contents/Info.plist').write_bytes(metadata)
                (jre / 'Contents/_CodeSignature').mkdir()
                (jre / 'Contents/_CodeSignature/CodeResources').write_bytes(b'old seal')
                corresponding = source / 'JavaHost/corresponding-source'
                (corresponding / 'native').mkdir(parents=True)
                (corresponding / 'native/historical.dylib').write_bytes(macho)
                (corresponding / 'LICENSE').write_text('source license')
                (corresponding / '.source-metadata').write_text('source metadata')
                output = root / 'output'; output.mkdir()
                app = output / 'osx-arm64/VodBox.app'
                copied_jre = app / 'Contents/Resources/JavaHost/jre'
                commands = []
                def capture(*args, **kwargs):
                    commands.append(args)
                    if args[0] == 'codesign' and '--sign' in args:
                        self.assertFalse((copied_jre / 'Contents/_CodeSignature').exists())
                        self.assertEqual(metadata, (copied_jre / 'Contents/Info.plist').read_bytes())
                with patch.dict(package.os.environ, {'CODESIGN_IDENTITY': identity}), \
                        patch.object(package, 'run', side_effect=capture):
                    package.package_macos('osx-arm64', source, output, '0.2.1')
                signing = [args for args in commands if args[0] == 'codesign' and '--sign' in args]
                targets = [args[-1] for args in signing]
                self.assertEqual(4, len(targets))
                for relative in ('Contents/Home/bin/java', 'Contents/Home/lib/server/libjvm.dylib'):
                    self.assertLess(targets.index(copied_jre / relative), targets.index(copied_jre))
                desktop = app / 'Contents/MacOS/VodBox.Desktop'
                self.assertNotIn(desktop, targets)
                self.assertNotIn(copied_jre / 'Contents/MacOS/libjli.dylib', targets)
                self.assertNotIn(copied_jre / 'Contents/Home/lib/Fixture.class', targets)
                self.assertFalse((app / 'Contents/MacOS/JavaHost').exists())
                self.assertLess(targets.index(copied_jre), targets.index(app))
                self.assertEqual(app, targets[-1])
                for args in signing:
                    self.assertEqual(identity, args[args.index('--sign') + 1])
                    self.assertEqual(identity != '-', '--timestamp' in args)
                    self.assertEqual(identity != '-', '--options' in args)
                verify = ('codesign', '--verify', '--deep', '--strict', app)
                self.assertEqual(verify, commands[len(signing)])
                copied_source = app / 'Contents/Resources/JavaHost/corresponding-source'
                for relative in ('native/historical.dylib', 'LICENSE', '.source-metadata'):
                    self.assertEqual((corresponding / relative).read_bytes(),
                                     (copied_source / relative).read_bytes())
                self.assertEqual('JRE license',
                                 (copied_jre / 'Contents/Home/legal/java.base/LICENSE').read_text())
                self.assertEqual(b'old seal', (jre / 'Contents/_CodeSignature/CodeResources').read_bytes())
                self.assertEqual(metadata, (jre / 'Contents/Info.plist').read_bytes())

    @unittest.skipIf(os.name == 'nt', 'macOS jlink links need POSIX symlink semantics')
    def test_macos_jlink_license_links_materialized_only_in_copy(self):
        with tempfile.TemporaryDirectory(dir=ROOT / '.alma') as temp:
            root = pathlib.Path(temp)
            source = root / 'source'; source.mkdir()
            jre = source / 'JavaHost/jre'
            (jre / 'legal/java.base').mkdir(parents=True)
            (jre / 'legal/java.logging').mkdir()
            content = b'JRE license\n'
            original = jre / 'legal/java.base/LICENSE'
            original.write_bytes(content)
            link = jre / 'legal/java.logging/LICENSE'
            link.symlink_to('../java.base/LICENSE')
            (jre / 'lib').mkdir()
            (jre / 'lib/runtime').write_bytes(b'runtime')
            (jre / 'lib/runtime-link').symlink_to('runtime')
            output = root / 'output'; output.mkdir()
            with patch.object(package, 'run'):
                package.package_macos('osx-arm64', source, output, '0.2.1')
            copied = output / 'osx-arm64/VodBox.app/Contents/Resources/JavaHost/jre'
            self.assertFalse((copied / 'legal/java.logging/LICENSE').is_symlink())
            self.assertEqual(content, (copied / 'legal/java.logging/LICENSE').read_bytes())
            self.assertEqual(content, (copied / 'legal/java.base/LICENSE').read_bytes())
            self.assertTrue((copied / 'lib/runtime-link').is_symlink())
            self.assertTrue(link.is_symlink())
            self.assertEqual(pathlib.Path('../java.base/LICENSE'), link.readlink())
            self.assertEqual(content, original.read_bytes())

    @unittest.skipIf(os.name == 'nt', 'macOS jlink links need POSIX symlink semantics')
    def test_macos_rejects_invalid_jlink_license_links(self):
        for case in ('missing', 'outside', 'directory', 'empty', 'whitespace', 'loop'):
            with self.subTest(case=case), tempfile.TemporaryDirectory(dir=ROOT / '.alma') as temp:
                root = pathlib.Path(temp)
                source = root / 'source'; source.mkdir()
                jre = source / 'JavaHost/jre'
                legal = jre / 'legal/java.base'; legal.mkdir(parents=True)
                link = legal / 'LICENSE'
                target = legal / 'target'
                if case == 'outside':
                    target = root / 'outside-license'
                    target.write_text('external license')
                elif case == 'directory':
                    target.mkdir()
                elif case in ('empty', 'whitespace'):
                    target.write_bytes(b'' if case == 'empty' else b' \n\t')
                elif case == 'loop':
                    target.symlink_to('LICENSE')
                link.symlink_to(target if case == 'outside' else 'target')
                output = root / 'output'; output.mkdir()
                with patch.object(package, 'run') as run:
                    with self.assertRaises(ValueError):
                        package.package_macos('osx-arm64', source, output, '0.2.1')
                run.assert_not_called()
                copied = output / 'osx-arm64/VodBox.app/Contents/Resources/JavaHost/jre/legal/java.base/LICENSE'
                self.assertTrue(copied.is_symlink())
                self.assertTrue(link.is_symlink())
                self.assertEqual(target if case == 'outside' else pathlib.Path('target'), link.readlink())

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
