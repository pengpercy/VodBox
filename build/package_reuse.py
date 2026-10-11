"""Validate already-verified native outputs before a packaging-only retry."""
import re
import subprocess
import argparse
import json
from pathlib import Path

from ci_reuse import api, ensure_commit, native_build_hash, source_hash, version_at


REQUIRED_RIDS = ('win-x64', 'win-arm64', 'osx-x64', 'osx-arm64', 'linux-x64', 'linux-arm64')
ENTRY_WORKFLOWS = ('.github/workflows/ci.yml', '.github/workflows/release.yml',
                   '.github/workflows/native-acceptance.yml')


def successful_aot(jobs, rid):
    return next((job for job in jobs if job.get('conclusion') == 'success'
                 and job.get('name', '').split(' / ')[-1] == 'AOT ' + rid), None)


def trusted_run(run, repo):
    if (run.get('path') not in ENTRY_WORKFLOWS
            or run.get('head_repository', {}).get('full_name') != repo
            or run.get('repository', {}).get('full_name') != repo
            or run.get('event') not in ('push', 'workflow_dispatch')):
        raise ValueError('Untrusted build repository or workflow entry')


def load_run(repo, run_id):
    if not re.fullmatch(r'[0-9]+', str(run_id)):
        raise ValueError('Invalid artifact/source run ID')
    run = api(f'repos/{repo}/actions/runs/{run_id}')
    trusted_run(run, repo)
    jobs = []
    page = 1
    while True:
        batch = api(f'repos/{repo}/actions/runs/{run_id}/jobs?filter=latest&per_page=100&page={page}')['jobs']
        jobs.extend(batch)
        if len(batch) < 100:
            break
        page += 1
    return {**run, 'jobs': jobs}


def validate_provenance(meta, run, rid, version, source_sha, repo, source_run):
    """The source run needs six green RIDs; the binary's original run needs only this RID."""
    trusted_run(run, repo)
    if run.get('conclusion') == 'cancelled':
        raise ValueError('Source run was cancelled')
    for required_rid in REQUIRED_RIDS:
        if successful_aot(run['jobs'], required_rid) is None:
            raise ValueError('Required native verification job did not succeed: ' + required_rid)
    artifact_run_id = str(meta.get('artifactRunId') or meta.get('sourceRunId') or source_run)
    if meta.get('sourceRunId') and str(meta['sourceRunId']) != artifact_run_id:
        raise ValueError('Conflicting artifact source run IDs: ' + rid)
    artifact_run = run if artifact_run_id == str(source_run) else load_run(repo, artifact_run_id)
    if successful_aot(artifact_run['jobs'], rid) is None:
        raise ValueError('Artifact native verification job did not succeed: ' + rid)
    # Always read the actual producer's checkout, including Release/manual dispatches.
    producer = restore_missing_source({'reused': False}, artifact_run, rid, repo)
    actual_sha = producer['sourceSha']
    if meta.get('sourceSha') not in (None, actual_sha):
        raise ValueError('Artifact producer source mismatch: ' + rid)
    if meta.get('rid', rid) != rid or meta.get('version') != version:
        raise ValueError('Build source/version mismatch: ' + rid)
    for sha in (actual_sha, source_sha):
        if not ensure_commit(sha):
            raise ValueError('Build input commit unavailable: ' + sha)
        if version_at(sha) != version:
            raise ValueError('Build source/version mismatch: ' + rid)
    fingerprint = source_hash(actual_sha)
    # Authenticate legacy metadata against the producer's original full input tree.
    # Packaging-only changes are allowed only in the separate native comparison.
    if (meta.get('fingerprint', fingerprint) != fingerprint
            or meta.get('originalFingerprint', fingerprint) != fingerprint):
        raise ValueError('Build input fingerprint mismatch: ' + rid)
    native_hash = native_build_hash(actual_sha)
    if (native_build_hash(source_sha) != native_hash
            or meta.get('nativeBuildHash', native_hash) != native_hash):
        raise ValueError('Native build input fingerprint mismatch: ' + rid)
    return {**meta, 'sourceSha': actual_sha, 'artifactRunId': artifact_run_id,
            'fingerprint': fingerprint, 'originalFingerprint': fingerprint,
            'nativeBuildHash': native_hash, 'packageSourceSha': source_sha,
            'targetPackageInput': source_hash(source_sha)}


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
    job = successful_aot(run['jobs'], rid)
    if job is None:
        raise ValueError('No verified native build job: ' + rid)
    job_id = job.get('id') or job.get('databaseId')
    result = subprocess.run(['gh', 'api', f"repos/{repo}/actions/jobs/{job_id}/logs"],
                            capture_output=True, text=True, encoding="utf-8")
    if result.returncode and 'allow-escape-sequences' in result.stderr:
        result = subprocess.run(['gh', 'api', f"repos/{repo}/actions/jobs/{job_id}/logs",
                                 '--allow-escape-sequences'], capture_output=True, text=True, encoding="utf-8")
    if result.returncode:
        raise ValueError('Checkout log download failed: ' + result.stderr.strip())
    return {**meta, 'sourceSha': verified_checkout_sha(result.stdout)}


def validate(meta, jobs, rid, version, source_sha, reuse_packages=False, run=None):
    actual_sha = meta.get('sourceSha')
    if meta.get('rid', rid) != rid or meta.get('version') != version or actual_sha != source_sha:
        raise ValueError('Build source/version mismatch: ' + rid)
    if successful_aot(jobs, rid) is None:
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
    p.add_argument('--repo', required=True)
    p.add_argument('--source-run', required=True)
    a = p.parse_args()
    original_meta = json.loads(a.meta.read_text())
    run = load_run(a.repo, a.source_run)
    meta = validate_provenance(original_meta, run, a.rid, a.version, a.source_sha,
                               a.repo, a.source_run)
    # A native binary can match the input tree across commits. An existing package/receipt
    # may be reused only for the commit that was actually packaged.
    same_package_source = original_meta.get('packageSourceSha', original_meta.get('sourceSha')) == a.source_sha
    reuse = validate(meta, run['jobs'], a.rid, a.version, meta['sourceSha'],
                     a.reuse_packages and same_package_source, run)
    a.meta.write_text(json.dumps(meta, indent=2) + '\n')
    a.jobs.write_text(json.dumps(run, indent=2) + '\n')
    print('reuse_package=' + str(reuse).lower())
