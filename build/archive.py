import argparse
from pathlib import Path
import shutil
import tarfile

parser = argparse.ArgumentParser(); parser.add_argument("rid"); parser.add_argument("source", type=Path); parser.add_argument("output", type=Path)
args = parser.parse_args()
args.output.mkdir(parents=True, exist_ok=True)
for file in list(args.source.rglob("*")):
    if file.exists() and (file.suffix in (".pdb", ".dbg") or file.name.endswith(".dSYM")):
        target = args.output / ("symbols." + args.rid) / file.relative_to(args.source)
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.move(str(file), target)
with tarfile.open(args.output / ("VodBox." + args.rid + ".tar.gz"), "w:gz") as archive:
    for file in args.source.iterdir(): archive.add(file, arcname=file.name)
