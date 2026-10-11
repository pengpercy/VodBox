"""Offline jlink contracts for the main agent to run; no real Java tools or network."""
import json
import pathlib
import struct
import subprocess
import tempfile
import unittest
import zipfile
from unittest.mock import patch

import jlink_runtime as runtime
from nativeverify import NativeVerifyError

ROOT = pathlib.Path(__file__).resolve().parents[1]


def launcher(rid):
    arm = rid.endswith('arm64')
    if rid.startswith('osx'):
        return struct.pack('<IIIIIIII', 0xFEEDFACF, 0x0100000C if arm else 0x01000007,
                           0, 2, 0, 0, 0, 0)
    if rid.startswith('linux'):
        # _Elf parses section headers even when only the machine type is requested.
        # Include the null section and a real section-name string table.
        data = bytearray(208)
        data[:7] = b'\x7fELF\x02\x01\x01'
        struct.pack_into('<HHI', data, 16, 2, 183 if arm else 62, 1)
        struct.pack_into('<Q', data, 0x28, 80)
        struct.pack_into('<H', data, 0x34, 64)
        struct.pack_into('<HHH', data, 0x3A, 64, 2, 1)
        data[64:75] = b'\0.shstrtab\0'
        struct.pack_into('<IIQQQQIIQQ', data, 144, 1, 3, 0, 0, 64, 11, 0, 0, 1, 0)
        return bytes(data)
    data = bytearray(128)
    data[:2] = b'MZ'
    struct.pack_into('<I', data, 0x3C, 64)
    data[64:68] = b'PE\0\0'
    struct.pack_into('<H', data, 68, 0xAA64 if arm else 0x8664)
    return bytes(data)


class JlinkRuntimeTests(unittest.TestCase):
    def fixture(self, root, rid, version='21.0.11'):
        home = root / 'jdk'
        (home / 'bin').mkdir(parents=True)
        (home / 'jmods').mkdir()
        (home / 'release').write_text(f'JAVA_VERSION="{version}"\nIMPLEMENTOR="Eclipse Adoptium"\n')
        (home / 'LICENSE').write_text('real vendor license fixture')
        suffix = '.exe' if rid.startswith('win') else ''
        for tool in ('java', 'javac', 'jlink'):
            (home / 'bin' / (tool + suffix)).write_bytes(launcher(rid))
        for module in runtime.MODULES:
            with zipfile.ZipFile(home / 'jmods' / (module + '.jmod'), 'w') as archive:
                if module == 'java.base':
                    archive.writestr('bin/java' + suffix, launcher(rid))
        return home

    def fake_link(self, rid, missing_module=None, missing_license=None):
        def run(args, **kwargs):
            if '--output' in args:
                image = pathlib.Path(args[args.index('--output') + 1])
                (image / 'bin').mkdir(parents=True)
                suffix = '.exe' if rid.startswith('win') else ''
                (image / 'bin' / ('java' + suffix)).write_bytes(launcher(rid))
                for module in runtime.MODULES:
                    if module != missing_license:
                        legal = image / 'legal' / module
                        legal.mkdir(parents=True)
                        (legal / 'LICENSE').write_text('actual jlink license fixture')
                return subprocess.CompletedProcess(args, 0)
            listing = '\n'.join(module + '@21.0.11' for module in runtime.MODULES if module != missing_module)
            return subprocess.CompletedProcess(args, 0, stdout=listing)
        return run

    def test_all_six_native_rids_link_modules_licenses_and_bin_java(self):
        for rid in runtime.EXPECTED_ARCH:
            with self.subTest(rid=rid), tempfile.TemporaryDirectory(dir=ROOT) as temporary:
                root = pathlib.Path(temporary)
                home = self.fixture(root, rid)
                image = root / 'jre'
                with patch.object(runtime, 'native_rid', return_value=rid), \
                     patch.object(runtime.subprocess, 'run', side_effect=self.fake_link(rid)) as run:
                    metadata = runtime.link_runtime(rid, image, home)
                args = run.call_args_list[0].args[0]
                self.assertEqual(args[args.index('--module-path') + 1], str(home / 'jmods'))
                self.assertEqual(set(args[args.index('--add-modules') + 1].split(',')), set(runtime.MODULES))
                self.assertTrue(set(runtime.UPSTREAM_MODULES) <= set(metadata['modules']))
                self.assertIn('java.compiler', metadata['modules'])
                self.assertEqual(metadata['release'], '21.0.11')
                self.assertEqual(json.loads((image / runtime.METADATA).read_text()), metadata)
                self.assertEqual((image / 'LICENSE').read_text(), 'real vendor license fixture')
                self.assertEqual((image / 'legal/java.base/LICENSE').read_text(), 'actual jlink license fixture')
                self.assertFalse((image / 'Contents').exists())

    def test_cross_architecture_runner_fails_before_invoking_tools(self):
        with patch.object(runtime, 'native_rid', return_value='osx-x64'), \
             patch.object(runtime.subprocess, 'run') as run:
            with self.assertRaisesRegex(ValueError, 'native osx-arm64 runner'):
                runtime.link_runtime('osx-arm64', ROOT / 'unused-jre', ROOT / 'absent-jdk')
            run.assert_not_called()

    def test_jdk24_and_jre_only_are_rejected(self):
        with tempfile.TemporaryDirectory(dir=ROOT) as temporary:
            root = pathlib.Path(temporary)
            home = self.fixture(root, 'linux-x64', '24.0.1')
            with patch.object(runtime, 'native_rid', return_value='linux-x64'):
                with self.assertRaisesRegex(ValueError, 'JDK 21 required'):
                    runtime.validate_jdk(home, 'linux-x64')
                (home / 'release').write_text('JAVA_VERSION="21.0.11"\n')
                (home / 'bin/jlink').unlink()
                with self.assertRaisesRegex(ValueError, 'JRE cannot run jlink'):
                    runtime.validate_jdk(home, 'linux-x64')

    def test_foreign_launcher_or_jmods_are_rejected(self):
        for rid in runtime.EXPECTED_ARCH:
            foreign_rid = rid.rsplit('-', 1)[0] + ('-x64' if rid.endswith('arm64') else '-arm64')
            suffix = '.exe' if rid.startswith('win') else ''
            for foreign_jmods in (False, True):
                with self.subTest(rid=rid, jmods=foreign_jmods), tempfile.TemporaryDirectory(dir=ROOT) as temporary:
                    home = self.fixture(pathlib.Path(temporary), rid)
                    if foreign_jmods:
                        with zipfile.ZipFile(home / 'jmods/java.base.jmod', 'w') as archive:
                            archive.writestr('bin/java' + suffix, launcher(foreign_rid))
                    else:
                        (home / 'bin' / ('jlink' + suffix)).write_bytes(launcher(foreign_rid))
                    with patch.object(runtime, 'native_rid', return_value=rid):
                        with self.assertRaisesRegex(NativeVerifyError, 'image in a ' + rid):
                            runtime.validate_jdk(home, rid)

    def test_missing_required_jmod_fails_before_linking(self):
        with tempfile.TemporaryDirectory(dir=ROOT) as temporary:
            home = self.fixture(pathlib.Path(temporary), 'linux-x64')
            (home / 'jmods/java.compiler.jmod').unlink()
            with patch.object(runtime, 'native_rid', return_value='linux-x64'):
                with self.assertRaisesRegex(ValueError, 'required jmods: java.compiler'):
                    runtime.validate_jdk(home, 'linux-x64')

    def test_missing_output_modules_or_real_license_do_not_publish_image(self):
        for kwargs in ({'missing_module': 'jdk.crypto.ec'}, {'missing_license': 'java.base'}):
            with self.subTest(kwargs=kwargs), tempfile.TemporaryDirectory(dir=ROOT) as temporary:
                root = pathlib.Path(temporary)
                home = self.fixture(root, 'linux-x64')
                image = root / 'jre'
                with patch.object(runtime, 'native_rid', return_value='linux-x64'), \
                     patch.object(runtime.subprocess, 'run', side_effect=self.fake_link('linux-x64', **kwargs)):
                    with self.assertRaises(ValueError):
                        runtime.link_runtime('linux-x64', image, home)
                self.assertFalse(image.exists())
                self.assertFalse(list(root.glob('jlink-*')))

    def test_java_home_is_used_and_absence_is_explicit(self):
        with patch.dict(runtime.os.environ, {}, clear=True):
            with self.assertRaisesRegex(ValueError, 'set JAVA_HOME'):
                runtime.link_runtime('win-arm64', ROOT / 'unused-jre')
        with tempfile.TemporaryDirectory(dir=ROOT) as temporary:
            root = pathlib.Path(temporary)
            home = self.fixture(root, 'win-arm64')
            with patch.dict(runtime.os.environ, {'JAVA_HOME': str(home)}), \
                 patch.object(runtime, 'native_rid', return_value='win-arm64'), \
                 patch.object(runtime.subprocess, 'run', side_effect=self.fake_link('win-arm64')):
                runtime.link_runtime('win-arm64', root / 'jre')


if __name__ == '__main__':
    unittest.main()
