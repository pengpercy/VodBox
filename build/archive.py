import argparse
import shutil
import tarfile
from pathlib import Path

parser = argparse.ArgumentParser()
parser.add_argument("rid")
parser.add_argument("source", type=Path)
parser.add_argument("output", type=Path)
args = parser.parse_args()
args.output.mkdir(parents=True, exist_ok=True)
symbols = args.output / ("symbols." + args.rid)
# Debug payloads are published as a separate `symbols.<rid>` artifact, never inside the runtime
# tarball. `.dSYM` is a directory bundle (its DWARF file carries no `.pdb`/`.dbg` suffix), so a
# file-only suffix match shipped the entire symbol bundle in the runtime package: on macOS that
# is >100 MB of DWARF that every user downloads but nothing loads at runtime.
for path in list(args.source.rglob("*")):
    if not path.exists():
        continue
    is_symbol = (path.is_dir() and path.suffix == ".dSYM") or (path.is_file() and path.suffix in (".pdb", ".dbg"))
    if is_symbol:
        target = symbols / path.relative_to(args.source)
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.move(str(path), target)
with tarfile.open(args.output / ("VodBox." + args.rid + ".tar.gz"), "w:gz") as archive:
    for file in args.source.iterdir():
        archive.add(file, arcname=file.name)
