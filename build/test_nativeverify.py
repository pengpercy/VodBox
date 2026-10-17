"""Reject wrong-architecture binaries and unresolved media dependencies."""
import struct
from pathlib import Path
import tempfile
import unittest
import nativeverify as n
from bundle import verify_windows_closure

ROOT = Path(__file__).resolve().parent.parent


def pe(path, arch=0x8664, dependency='kernel32.dll'):
    b = bytearray(1024)
    b[:2] = b'MZ'
    struct.pack_into('<I', b, 0x3c, 0x80)
    b[0x80:0x84] = b'PE\0\0'
    struct.pack_into('<HH', b, 0x84, arch, 1)
    struct.pack_into('<H', b, 0x94, 240)
    struct.pack_into('<H', b, 0x98, 0x20b)
    struct.pack_into('<II', b, 0x98 + 112 + 8, 0x1000, 40)
    struct.pack_into('<IIII', b, 0x98 + 240 + 8, 512, 0x1000, 512, 512)
    struct.pack_into('<IIIII', b, 512, 0x1040, 0, 0, 0x1080, 0x1060)
    data = dependency.encode() + b'\0'
    b[640:640 + len(data)] = data
    path.write_bytes(b)


def elf(path, needed=(), soname=None, arch=62):
    names = b'\0.shstrtab\0.dynstr\0.dynamic\0'
    strings = bytearray(b'\0')
    dynamic = bytearray()
    for name in needed:
        dynamic += struct.pack('<QQ', 1, len(strings))
        strings += name.encode() + b'\0'
    if soname:
        dynamic += struct.pack('<QQ', 14, len(strings))
        strings += soname.encode() + b'\0'
    dynamic += struct.pack('<QQ', 0, 0)
    b = bytearray(64)
    b[:7] = b'\x7fELF\x02\x01\x01'
    struct.pack_into('<H', b, 18, arch)
    name_offset = len(b); b += names
    string_offset = len(b); b += strings
    dyn_offset = len(b); b += dynamic
    while len(b) % 8: b += b'\0'
    struct.pack_into('<Q', b, 0x28, len(b))
    struct.pack_into('<HHH', b, 0x3a, 64, 4, 1)
    b += bytes(64)
    for name, typ, off, size, link, step in [
        ('.shstrtab', 3, name_offset, len(names), 0, 0),
        ('.dynstr', 3, string_offset, len(strings), 0, 0),
        ('.dynamic', 6, dyn_offset, len(dynamic), 2, 16),
    ]:
        b += struct.pack('<IIQQQQIIQQ', names.index(name.encode()), typ, 0, 0, off, size, link, 0, 1, step)
    path.write_bytes(b)


class NativeTests(unittest.TestCase):
    def test_wrong_windows_architecture_rejected(self):
        with tempfile.TemporaryDirectory(dir=ROOT / '.alma') as t:
            p = Path(t) / 'native.dll'; pe(p, 0xaa64)
            with self.assertRaisesRegex(n.NativeVerifyError, 'Aarch64'):
                n.verify_windows_dll(p, 'win-x64')

    def test_unbundled_windows_codec_rejected(self):
        with tempfile.TemporaryDirectory(dir=ROOT / '.alma') as t:
            p = Path(t)
            pe(p / 'libmpv-2.dll', dependency='private-codec.dll')
            pe(p / 'vulkan-1.dll')
            with self.assertRaisesRegex(n.NativeVerifyError, 'private-codec.dll'):
                verify_windows_closure(p, 'win-x64')

    def test_windows_system_dependencies_allowed(self):
        with tempfile.TemporaryDirectory(dir=ROOT / '.alma') as t:
            p = Path(t)
            pe(p / 'libmpv-2.dll', dependency='vulkan-1.dll')
            pe(p / 'vulkan-1.dll')
            pe(p / 'av_libglesv2.dll', dependency='dxgi.dll')
            pe(p / 'libSkiaSharp.dll', dependency='fontsub.dll')
            verify_windows_closure(p, 'win-x64')

    def test_linux_dependency_alias_must_physically_exist(self):
        with tempfile.TemporaryDirectory(dir=ROOT / '.alma') as t:
            p = Path(t)
            elf(p / 'libmpv.so.2', ['libcodec.so.1', 'libc.so.6'])
            elf(p / 'libcodec.so.9', soname='libcodec.so.1')
            with self.assertRaisesRegex(n.NativeVerifyError, 'libcodec.so.1'):
                n.verify_linux_closure(p, 'linux-x64')
            (p / 'libcodec.so.1').symlink_to('libcodec.so.9')
            self.assertEqual(n.verify_linux_closure(p, 'linux-x64')['arch'], 'x86-64')

    def test_wrong_linux_architecture_rejected(self):
        with tempfile.TemporaryDirectory(dir=ROOT / '.alma') as t:
            p = Path(t)
            elf(p / 'libmpv.so.2', arch=183)
            with self.assertRaisesRegex(n.NativeVerifyError, 'AArch64'):
                n.verify_linux_closure(p, 'linux-x64')


if __name__ == '__main__':
    unittest.main()
