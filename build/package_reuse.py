"""Validate already-verified native outputs before a packaging-only retry."""
import re
import subprocess
import argparse
import json
from pathlib import Path


def verified_checkout_sha(text):
    """Read the receipt emitted by actions/checkout, never infer a dispatch source from run HEAD."""
    text = re.sub(r'\x1b\[[0-?]*[ -/]*[@-~]', '', text)
    lines = text.splitlines()
    for index, line in enumerate(lines[:-1]):
        if '[command]' in line and re.search(r'git(?:\.exe)?"? log -1 --format=%H', line):
            value = lines[index + 1].split(' ', 1)[-1].strip()
            if re.fullmatch(r'[0-9a-f]{40}', value):
                return value
    raise ValueError('Missing actions/checkout commit receipt')


def restore_missing_source(meta, run, rid, repo):
    if meta.get('sourceSha') or meta.get('reused'):
        return meta
    job = next((j for j in run['jobs'] if j.get('conclusion') == 'success'
                and j['name'].endswith('AOT ' + rid)), None)
    if job is None:
        raise ValueError('No verified native build job: ' + rid)
    result = subprocess.run(['gh', 'api', f"repos/{repo}/actions/jobs/{job['databaseId']}/logs"],
                            capture_output=True, text=True)
    if result.returncode and 'allow-escape-sequences' in result.stderr:
        result = subprocess.run(['gh', 'api', f"repos/{repo}/actions/jobs/{job['databaseId']}/logs",
                                 '--allow-escape-sequences'], capture_output=True, text=True)
    if result.returncode:
        raise ValueError('Checkout log download failed: ' + result.stderr.strip())
    return {**meta, 'sourceSha': verified_checkout_sha(result.stdout)}


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
    p.add_argument('--repo', default='')
    a = p.parse_args()
    meta = json.loads(a.meta.read_text())
    run = json.loads(a.jobs.read_text())
    if a.repo:
        meta = restore_missing_source(meta, run, a.rid, a.repo)
    reuse = validate(meta, run['jobs'], a.rid, a.version, a.source_sha, a.reuse_packages, run)
    print('reuse_package=' + str(reuse).lower())
