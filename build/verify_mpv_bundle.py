"""Native headless decode test against the packaged libmpv, never a system fallback."""
import argparse
import ctypes as c
import os
from pathlib import Path
import time


def verify(rid: str, directory: Path):
    directory = directory.resolve()
    if rid.startswith('win'):
        search = os.add_dll_directory(str(directory))
        native = directory / 'libmpv-2.dll'
    elif rid.startswith('osx'):
        native = directory / 'libmpv.dylib'
    else:
        native = directory / 'lib/libmpv.so.2'
    if not native.is_file():
        raise RuntimeError(f'Bundled libmpv missing: {native}')
    lib = c.CDLL(str(native))
    lib.mpv_create.restype = c.c_void_p
    lib.mpv_set_option_string.argtypes = [c.c_void_p, c.c_char_p, c.c_char_p]
    lib.mpv_initialize.argtypes = [c.c_void_p]
    lib.mpv_command.argtypes = [c.c_void_p, c.POINTER(c.c_char_p)]
    lib.mpv_get_property.argtypes = [c.c_void_p, c.c_char_p, c.c_int, c.c_void_p]
    lib.mpv_terminate_destroy.argtypes = [c.c_void_p]

    def check(result):
        if result < 0:
            raise RuntimeError(f'libmpv error {result}')

    for fixture in sorted(Path(__file__).with_name('media-fixtures').glob('*')):
        if fixture.suffix not in ('.mp4', '.webm'):
            continue
        handle = lib.mpv_create()
        if not handle:
            raise RuntimeError('mpv_create failed')
        try:
            for name, value in [('vo', 'null'), ('ao', 'null'), ('hwdec', 'no'), ('config', 'no')]:
                check(lib.mpv_set_option_string(handle, name.encode(), value.encode()))
            check(lib.mpv_initialize(handle))

            def command(*args):
                data = (c.c_char_p * (len(args) + 1))(*[s.encode() for s in args], None)
                check(lib.mpv_command(handle, data))

            def number(name):
                value = c.c_double()
                code = lib.mpv_get_property(handle, name.encode(), 5, c.byref(value))
                return value.value if code >= 0 else None

            command('loadfile', str(fixture.resolve()), 'replace')
            deadline = time.monotonic() + 20
            while time.monotonic() < deadline:
                position, width = number('time-pos'), number('video-params/w')
                if position is not None and position > .3 and width is not None and width > 0:
                    break
                time.sleep(.02)
            else:
                raise RuntimeError(f'Video failed to decode: {fixture.name}')
            duration = number('duration')
            if duration is None or duration < 5:
                raise RuntimeError(f'Duration invalid: {fixture.name}')
            command('set', 'pause', 'yes')
            command('seek', '3', 'absolute+exact')
            deadline = time.monotonic() + 10
            while time.monotonic() < deadline:
                position = number('time-pos')
                if position is not None and abs(position - 3) < .15:
                    break
                time.sleep(.02)
            else:
                raise RuntimeError(f'Seek failed: {fixture.name}')
            print(f'BUNDLED MPV {rid}: {fixture.name} decode/seek OK ({int(width)}px, {duration:.2f}s)', flush=True)
        finally:
            lib.mpv_terminate_destroy(handle)
    if rid.startswith('win'):
        search.close()


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('rid')
    parser.add_argument('directory', type=Path)
    args = parser.parse_args()
    verify(args.rid, args.directory)
