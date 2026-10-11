#!/usr/bin/env python3
"""Reassemble source archives split only to satisfy GitHub's per-file size limit."""
from pathlib import Path
import hashlib, json
root = Path(__file__).resolve().parent
for entry in json.loads((root / 'archive-parts.json').read_text()):
    output = root / 'archives' / entry['archive']
    digest = hashlib.sha256()
    with output.open('wb') as stream:
        for part in entry['parts']:
            data = (root / 'archives' / part['file']).read_bytes()
            if hashlib.sha256(data).hexdigest() != part['sha256']:
                raise RuntimeError('Source archive part checksum mismatch: ' + part['file'])
            stream.write(data)
            digest.update(data)
    if digest.hexdigest() != entry['sha256']:
        output.unlink()
        raise RuntimeError('Reassembled source archive checksum mismatch')
    print('Verified source archive:', output.name)
