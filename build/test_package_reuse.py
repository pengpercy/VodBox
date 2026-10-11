import unittest
from unittest.mock import patch

import package_reuse
from package_reuse import validate, verified_checkout_sha


class ReuseTests(unittest.TestCase):
    def test_checkout_receipt_reads_actual_commit(self):
        sha = 'a' * 40
        self.assertEqual(sha, verified_checkout_sha('time [command]/usr/bin/git log -1 --format=%H\ntime ' + sha))
        self.assertEqual(sha, verified_checkout_sha('time [command]\"C:/Git/bin/git.exe\" log -1 --format=%H\ntime ' + sha))
        with self.assertRaises(ValueError):
            verified_checkout_sha('time ' + sha)
        with self.assertRaises(ValueError):
            verified_checkout_sha('time [command]/usr/bin/git log -1 --format=%H\ntime invalid')

    def setUp(self):
        self.meta = {'sourceSha': 'source', 'version': '0.2.3'}
        self.jobs = [{'name': 'build / AOT osx-arm64', 'conclusion': 'success'},
                     {'name': 'package / package (macos-15, osx-arm64)', 'conclusion': 'success'}]

    def test_valid_successful_package_reused(self):
        self.assertTrue(validate(self.meta, self.jobs, 'osx-arm64', '0.2.3', 'source', True))

    def test_failed_package_rebuilt_without_rebuilding_native(self):
        self.jobs[1]['conclusion'] = 'failure'
        self.assertFalse(validate(self.meta, self.jobs, 'osx-arm64', '0.2.3', 'source', True))

    def test_mismatched_source_rejected(self):
        with self.assertRaises(ValueError):
            validate(self.meta, self.jobs, 'osx-arm64', '0.2.3', 'other', True)

    def test_mismatched_version_rejected(self):
        with self.assertRaises(ValueError):
            validate(self.meta, self.jobs, 'osx-arm64', '0.2.4', 'source', True)

    def test_failed_native_verification_rejected(self):
        self.jobs[0]['conclusion'] = 'failure'
        with self.assertRaises(ValueError):
            validate(self.meta, self.jobs, 'osx-arm64', '0.2.3', 'source', True)

    def test_legacy_metadata_cannot_infer_source_from_run_head(self):
        m = {'version': '0.2.3', 'reused': False, 'rid': 'osx-arm64'}
        for event in ('push', 'workflow_dispatch'):
            run = {'event': event, 'headBranch': 'v0.2.3', 'headSha': 'source'}
            with self.assertRaises(ValueError):
                validate(m, self.jobs, 'osx-arm64', '0.2.3', 'source', True, run)

    def test_normal_run_never_reuses_packages(self):
        self.assertFalse(validate(self.meta, self.jobs, 'osx-arm64', '0.2.3', 'source', False))


class ProvenanceTests(unittest.TestCase):
    def setUp(self):
        self.repo = 'owner/repo'
        self.rid = 'osx-arm64'
        self.actual_sha = 'a' * 40
        self.target_sha = 'b' * 40
        self.run = {
            'path': '.github/workflows/release.yml', 'event': 'workflow_dispatch',
            'repository': {'full_name': self.repo}, 'head_repository': {'full_name': self.repo},
            'head_sha': 'c' * 40,
            'jobs': [{'id': i, 'name': 'build / AOT ' + rid, 'conclusion': 'success'}
                     for i, rid in enumerate(package_reuse.REQUIRED_RIDS, 1)],
        }
        self.artifact_run = {**self.run, 'path': '.github/workflows/ci.yml',
                             'conclusion': 'failure',
                             'jobs': [{'id': 99, 'name': 'build / AOT ' + self.rid,
                                       'conclusion': 'success'}]}
        self.meta = {'sourceSha': self.actual_sha, 'artifactRunId': '10',
                     'sourceRunId': 10, 'reused': True, 'rid': self.rid,
                     'version': '0.2.3', 'fingerprint': 'same'}

    def check(self, fingerprint='same'):
        def api(path):
            if '/jobs?' in path:
                return {'jobs': self.artifact_run['jobs']}
            self.assertEqual('repos/owner/repo/actions/runs/10', path)
            return self.artifact_run

        receipt = 'time [command]/usr/bin/git log -1 --format=%H\ntime ' + self.actual_sha
        with patch.object(package_reuse, 'api', side_effect=api), \
             patch.object(package_reuse.subprocess, 'run') as download, \
             patch.object(package_reuse, 'ensure_commit', return_value=True), \
             patch.object(package_reuse, 'version_at', return_value='0.2.3'), \
             patch.object(package_reuse, 'source_hash', return_value='same'), \
             patch.object(package_reuse, 'native_build_hash',
                          side_effect=lambda sha: 'same' if sha == self.actual_sha else fingerprint):
            download.return_value.returncode = 0
            download.return_value.stdout = receipt
            result = package_reuse.validate_provenance(self.meta, self.run, self.rid, '0.2.3',
                                                       self.target_sha, self.repo, '20')
            self.assertTrue(any('repos/owner/repo/actions/jobs/99/logs' in call.args[0]
                                for call in download.call_args_list))
            return result

    def test_single_rid_artifact_run_is_valid_and_original_sha_is_preserved(self):
        meta = self.check()
        self.assertEqual(self.actual_sha, meta['sourceSha'])
        self.assertEqual(self.target_sha, meta['packageSourceSha'])
        self.assertEqual('10', meta['artifactRunId'])

    def test_actual_entry_workflows_are_allowed(self):
        for path in package_reuse.ENTRY_WORKFLOWS:
            self.artifact_run['path'] = path
            self.assertEqual(self.actual_sha, self.check()['sourceSha'])

    def test_source_run_requires_all_six_successful_jobs(self):
        for conclusion in ('failure', 'cancelled', 'skipped', None):
            self.run['jobs'][0]['conclusion'] = conclusion
            with self.assertRaisesRegex(ValueError, 'Required native'):
                self.check()
        self.run['jobs'].pop(0)
        with self.assertRaisesRegex(ValueError, 'Required native'):
            self.check()

    def test_cancelled_source_run_is_rejected_even_with_green_jobs(self):
        self.run['conclusion'] = 'cancelled'
        with self.assertRaisesRegex(ValueError, 'cancelled'):
            self.check()

    def test_artifact_run_must_have_this_rid_successful(self):
        for conclusion in ('failure', 'cancelled', 'skipped', None):
            self.artifact_run['jobs'][0]['conclusion'] = conclusion
            with self.assertRaisesRegex(ValueError, 'Artifact native'):
                self.check()
        self.artifact_run['jobs'] = []
        with self.assertRaisesRegex(ValueError, 'Artifact native'):
            self.check()

    def test_artifact_run_and_source_run_require_trusted_repo_and_entry(self):
        for run in (self.run, self.artifact_run):
            for field, value in (('path', '.github/workflows/build.yml'),
                                 ('repository', {'full_name': 'other/repo'}),
                                 ('head_repository', {'full_name': 'fork/repo'}),
                                 ('event', 'pull_request')):
                original = run[field]
                run[field] = value
                with self.assertRaisesRegex(ValueError, 'Untrusted'):
                    self.check()
                run[field] = original

    def test_missing_repository_identity_is_rejected(self):
        del self.artifact_run['head_repository']
        with self.assertRaisesRegex(ValueError, 'Untrusted'):
            self.check()

    def test_target_tree_and_recorded_fingerprint_must_match_producer(self):
        with self.assertRaisesRegex(ValueError, 'fingerprint'):
            self.check(fingerprint='changed')
        self.meta['fingerprint'] = 'forged'
        with self.assertRaisesRegex(ValueError, 'fingerprint'):
            self.check()

    def test_source_sha_must_be_actual_checkout_not_run_head_or_target(self):
        for sha in (self.target_sha, self.artifact_run['head_sha']):
            self.meta['sourceSha'] = sha
            with self.assertRaisesRegex(ValueError, 'producer source'):
                self.check()

    def test_legacy_source_is_restored_from_producer_checkout(self):
        del self.meta['sourceSha']
        del self.meta['fingerprint']
        self.assertEqual(self.actual_sha, self.check()['sourceSha'])

    def test_conflicting_run_ids_are_rejected(self):
        self.meta['sourceRunId'] = 30
        with self.assertRaisesRegex(ValueError, 'Conflicting'):
            self.check()

    def test_jobs_are_paginated(self):
        page = [{'id': i, 'name': 'job', 'conclusion': 'success'} for i in range(100)]
        with patch.object(package_reuse, 'api', side_effect=[self.run, {'jobs': page},
                                                           {'jobs': self.artifact_run['jobs']}]) as api:
            run = package_reuse.load_run(self.repo, '10')
            self.assertEqual(101, len(run['jobs']))
            self.assertTrue(api.call_args.args[0].endswith('page=2'))


if __name__ == '__main__':
    unittest.main()
