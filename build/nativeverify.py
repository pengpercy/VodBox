"""Pure-Python PE/ELF inspection used to prove a bundled media kernel matches its target RID.

No native tools (otool/readelf/objdump) and no third-party packages: the release pipeline runs on
three operating systems, and a verification step that silently degrades to "assume it is fine" on
one of them is worse than none. Every parser here fails loudly on malformed input.

The two things a bundle must prove before it ships:
  * architecture  - the copied DLL/ELF is the machine type the RID claims (an arm64 package that
    carries an x86-64 libmpv installs fine and then fails to load at runtime);
  * closure       - on Linux, libmpv and every bundled `.so` resolve each other plus the base
    loader/glibc, with no dependency left for the user to install.
"""
from __future__ import annotations

import struct
from pathlib import Path

PE_MACHINE = {0x8664: "x86-64", 0xAA64: "Aarch64", 0x014C: "i386", 0x01C4: "ARM"}
ELF_MACHINE = {62: "x86-64", 183: "AArch64", 3: "i386", 40: "ARM"}

# The RID's expected native architecture, as reported by the parsers above.
EXPECTED_ARCH = {
    "win-x64": "x86-64",
    "win-arm64": "Aarch64",
    "linux-x64": "x86-64",
    "linux-arm64": "AArch64",
}

# Libraries every Linux host provides: the C runtime, the dynamic loader and the C++ runtime's
# own base. These are deliberately *not* bundled (glibc is not relocatable), so a closure check
# must treat them as satisfied rather than as a missing dependency.
LINUX_BASE = frozenset({
    "libc.so.6", "libm.so.6", "libdl.so.2", "libpthread.so.0", "librt.so.1",
    "libresolv.so.2", "libutil.so.1", "libnsl.so.1", "libanl.so.1", "libthread_db.so.1",
    "ld-linux-x86-64.so.2", "ld-linux-aarch64.so.1", "linux-vdso.so.1",
})


class NativeVerifyError(RuntimeError):
    """A bundled artifact is not what its RID claims; the release must not continue."""


def _read(path: Path) -> bytes:
    return Path(path).read_bytes()


# --- PE (Windows DLL) ---------------------------------------------------------------------

def pe_machine(path: Path) -> str:
    data = _read(path)
    if data[:2] != b"MZ":
        raise NativeVerifyError(f"{path}: not a PE image (missing MZ)")
    e_lfanew = struct.unpack_from("<I", data, 0x3C)[0]
    if data[e_lfanew:e_lfanew + 4] != b"PE\0\0":
        raise NativeVerifyError(f"{path}: not a PE image (missing PE signature)")
    machine = struct.unpack_from("<H", data, e_lfanew + 4)[0]
    return PE_MACHINE.get(machine, f"unknown(0x{machine:x})")


def pe_imports(path: Path) -> list[str]:
    """Names of DLLs in the static import table (lower-cased)."""
    data = _read(path)
    e_lfanew = struct.unpack_from("<I", data, 0x3C)[0]
    coff = e_lfanew + 4
    nsec, opt_size = struct.unpack_from("<H", data, coff + 2)[0], struct.unpack_from("<H", data, coff + 16)[0]
    opt = coff + 20
    magic = struct.unpack_from("<H", data, opt)[0]
    pe32p = magic == 0x20B
    if magic not in (0x10B, 0x20B):
        raise NativeVerifyError(f"{path}: unknown optional-header magic 0x{magic:x}")
    dd_off = opt + (112 if pe32p else 96)
    imp_rva, imp_size = struct.unpack_from("<II", data, dd_off + 8)
    sections = []
    sec_off = opt + opt_size
    for i in range(nsec):
        s = sec_off + i * 40
        vsz, va, rawsz, praw = struct.unpack_from("<IIII", data, s + 8)
        sections.append((va, max(vsz, rawsz), praw))

    def rva_to_off(rva: int):
        for va, span, praw in sections:
            if va <= rva < va + span:
                return praw + (rva - va)
        return None

    imports: list[str] = []
    if imp_rva and imp_size:
        off = rva_to_off(imp_rva)
        if off is None:
            raise NativeVerifyError(f"{path}: import directory RVA outside any section")
        while True:
            descriptor = struct.unpack_from("<IIIII", data, off)
            if descriptor == (0, 0, 0, 0, 0):
                break
            name_off = rva_to_off(descriptor[3])
            if name_off is None:
                raise NativeVerifyError(f"{path}: import name RVA outside any section")
            end = data.index(b"\0", name_off)
            imports.append(data[name_off:end].decode("latin1").lower())
            off += 20
    return imports


def verify_windows_dll(path: Path, rid: str) -> str:
    """Assert a DLL matches the RID's architecture; return the machine string."""
    expected = EXPECTED_ARCH.get(rid)
    if expected is None:
        raise NativeVerifyError(f"{rid}: no expected architecture defined")
    machine = pe_machine(path)
    if machine != expected:
        raise NativeVerifyError(f"{path}: {machine} DLL in a {rid} ({expected}) bundle")
    return machine


# --- ELF (Linux shared object) ------------------------------------------------------------

class _Elf:
    def __init__(self, path: Path):
        d = _read(path)
        if d[:4] != b"\x7fELF":
            raise NativeVerifyError(f"{path}: not an ELF object")
        self.path = path
        self.data = d
        self.is64 = d[4] == 2
        if d[5] not in (1, 2):
            raise NativeVerifyError(f"{path}: unknown ELF endianness {d[5]}")
        endian = "<" if d[5] == 1 else ">"
        self.endian = endian
        if self.is64:
            self.machine = struct.unpack_from(endian + "H", d, 18)[0]
            self.shoff, = struct.unpack_from(endian + "Q", d, 0x28)
            self.shentsize, self.shnum, self.shstrndx = struct.unpack_from(endian + "HHH", d, 0x3A)
        else:
            self.machine = struct.unpack_from(endian + "H", d, 18)[0]
            self.shoff, = struct.unpack_from(endian + "I", d, 0x20)
            self.shentsize, self.shnum, self.shstrndx = struct.unpack_from(endian + "HHH", d, 0x2E)
        self._sections = []
        for i in range(self.shnum):
            base = self.shoff + i * self.shentsize
            if self.is64:
                fields = struct.unpack_from(endian + "IIQQQQIIQQ", d, base)
            else:
                fields = struct.unpack_from(endian + "IIIIIIIIII", d, base)
            # (name, type, offset, size, link, entsize)
            self._sections.append((fields[0], fields[1], fields[4], fields[5], fields[6], fields[9]))
        self._shstr = self._sections[self.shstrndx][2]

    def _string(self, table_off: int, off: int) -> str:
        end = self.data.index(b"\0", table_off + off)
        return self.data[table_off + off:end].decode("utf-8", "replace")

    def _section_name(self, off: int) -> str:
        return self._string(self._shstr, off)

    def dynamic(self) -> tuple[list[str], str | None, str | None, str | None]:
        """Return (NEEDED, RPATH, RUNPATH, SONAME)."""
        needed: list[str] = []
        rpath = runpath = soname = None
        for name, typ, off, size, link, entsize in self._sections:
            if typ != 6 or self._section_name(name) != ".dynamic":  # SHT_DYNAMIC
                continue
            strtab = self._sections[link][2]
            step = 16 if self.is64 else 8
            fmt = self.endian + ("QQ" if self.is64 else "II")
            for pos in range(off, off + size, step):
                tag, val = struct.unpack_from(fmt, self.data, pos)
                if tag == 0:
                    break
                if tag == 1:
                    needed.append(self._string(strtab, val))
                elif tag == 14:
                    soname = self._string(strtab, val)
                elif tag == 15:
                    rpath = self._string(strtab, val)
                elif tag == 29:
                    runpath = self._string(strtab, val)
        return needed, rpath, runpath, soname


def elf_arch(path: Path) -> str:
    return ELF_MACHINE.get(_Elf(path).machine, f"unknown(0x{_Elf(path).machine:x})")


def verify_linux_closure(libdir: Path, rid: str) -> dict:
    """Verify every bundled `.so` is (a) the RID's architecture and (b) resolvable from the bundle.

    `libdir` holds libmpv plus its copied dependencies. Returns a summary dict; raises
    NativeVerifyError on the first violation so a broken bundle can never look like a green build.
    """
    expected = EXPECTED_ARCH.get(rid)
    if expected is None:
        raise NativeVerifyError(f"{rid}: no expected architecture defined")
    libdir = Path(libdir)
    files = sorted(p for p in libdir.iterdir() if p.is_file() and ".so" in p.name)
    if not files:
        raise NativeVerifyError(f"{libdir}: no shared objects to verify")

    present = {p.name for p in files}
    for path in files:
        elf = _Elf(path)
        arch = ELF_MACHINE.get(elf.machine, f"unknown(0x{elf.machine:x})")
        if arch != expected:
            raise NativeVerifyError(f"{path}: {arch} object in a {rid} ({expected}) bundle")
    if not any(name.startswith("libmpv.so") for name in present):
        raise NativeVerifyError(f"{libdir}: bundled libmpv.so is missing")

    missing: dict[str, list[str]] = {}
    for path in files:
        needed, _, _, _ = _Elf(path).dynamic()
        for dep in needed:
            if dep in LINUX_BASE or dep in present:
                continue
            missing.setdefault(dep, []).append(path.name)
    if missing:
        detail = "; ".join(f"{dep} (needed by {', '.join(sorted(set(users)))})" for dep, users in sorted(missing.items()))
        raise NativeVerifyError(f"{rid}: bundled media kernel has unresolved dependencies: {detail}")
    return {"count": len(files), "arch": expected}
