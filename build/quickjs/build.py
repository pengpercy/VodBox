#!/usr/bin/env python3
"""Build the pinned QuickJS bridge on the matching RID host and copy the runtime library."""
import argparse
import pathlib
import platform
import shutil
import subprocess

parser = argparse.ArgumentParser()
parser.add_argument('rid', choices=['osx-x64', 'osx-arm64', 'linux-x64', 'linux-arm64', 'win-x64', 'win-arm64'])
parser.add_argument('output')
parser.add_argument('--build-dir')
args = parser.parse_args()
root = pathlib.Path(__file__).resolve().parents[2]
build = pathlib.Path(args.build_dir).resolve() if args.build_dir else root / 'artifacts' / 'quickjs' / args.rid
output = pathlib.Path(args.output).resolve()
configure = ['cmake', '-S', str(root / 'build' / 'quickjs'), '-B', str(build), '-DCMAKE_BUILD_TYPE=Release']
if args.rid.startswith('osx-'):
    configure += ['-DCMAKE_OSX_ARCHITECTURES=' + ('arm64' if args.rid.endswith('arm64') else 'x86_64')]
elif args.rid.startswith('win-'):
    configure += ['-A', 'ARM64' if args.rid.endswith('arm64') else 'x64']
else:
    arch = platform.machine().lower()
    expected = ('aarch64', 'arm64') if args.rid.endswith('arm64') else ('x86_64', 'amd64')
    if arch not in expected:
        raise SystemExit('Linux bridge must build on a matching architecture host.')
subprocess.run(configure, check=True)
subprocess.run(['cmake', '--build', str(build), '--config', 'Release', '--target', 'vodbox_quickjs', '--parallel', '2'], check=True)
name = 'vodbox_quickjs.dll' if args.rid.startswith('win-') else 'libvodbox_quickjs.dylib' if args.rid.startswith('osx-') else 'libvodbox_quickjs.so'
candidates = list(build.rglob(name))
if not candidates:
    raise SystemExit('QuickJS bridge runtime not produced: ' + name)
output.mkdir(parents=True, exist_ok=True)
shutil.copy2(candidates[0], output / name)
print('QuickJS bridge bundled for ' + args.rid)
