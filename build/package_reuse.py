"""Validate already-verified native outputs before a packaging-only retry."""
import argparse
import json
from pathlib import Path


def validate(meta, jobs, rid, version, source_sha, reuse_packages=False, run=None):
    actual_sha = meta.get('sourceSha')
    if actual_sha is None and meta.get('reused') is False and run:
        # Older fresh metadata omitted the SHA. Accept only a verified push of this exact release tag;
        # that workflow builds its tag HEAD, unlike a dispatch that may override source_sha.
        if run.get('event') == 'push' and run.get('headBranch') == 'v' + version:
            actual_sha = run.get('headSha')
    if meta.get('rid', rid) != rid or meta.get('version') != version or actual_sha != source_sha:
        raise ValueError('Build source/version mismatch: ' + rid)
    if not any(j.get('conclusion') == 'success' and j['name'].endswith('AOT ' + rid) for j in jobs):
        raise ValueError('Native verification job did not succeed: ' + rid)
    return reuse_packages and any(j.get('conclusion') == 'success' and
                                 j['name'].startswith('package / ') and
                                 j['name'].endswith(', ' + rid + ')') for j in jobs)


if __name__ == '__main__':
    p = argparse.ArgumentParser()
    p.add_argument('--meta', type=Path, required=True)
    p.add_argument('--jobs', type=Path, required=True)
    p.add_argument('--rid', required=True)
    p.add_argument('--version', required=True)
    p.add_argument('--source-sha', required=True)
    p.add_argument('--reuse-packages', action='store_true')
    a = p.parse_args()
    meta = json.loads(a.meta.read_text())
    run = json.loads(a.jobs.read_text())
    reuse = validate(meta, run['jobs'], a.rid, a.version, a.source_sha, a.reuse_packages, run)
    print('reuse_package=' + str(reuse).lower())
