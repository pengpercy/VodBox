"""Reuse must follow individual verified jobs, version and source identity."""
import importlib.util
import subprocess
import tempfile
from pathlib import Path
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('reuse', Path(__file__).with_name('ci_reuse.py'))
reuse = importlib.util.module_from_spec(spec)
spec.loader.exec_module(reuse)

class ReuseTests(unittest.TestCase):
    def probe(self, job='success', version='0.2.1', tree='same', expired=False, event='push', path='.github/workflows/ci.yml'):
        artifact = {'created_at': '2026-10-09', 'expired': expired,
                    'workflow_run': {'id': 10, 'head_sha': 'old'}}
        def api(url):
            if '/artifacts?' in url:
                return {'artifacts': [artifact]}
            if '/jobs?' in url:
                return {'jobs': [{'name': 'build / AOT win-x64', 'conclusion': job}]}
            return {'conclusion': 'failure', 'event': event, 'path': path}
        with patch.object(reuse, 'api', side_effect=api), patch.object(reuse, 'git', return_value='head'), \
             patch.object(reuse, 'source_hash', side_effect=lambda ref: 'same' if ref=='head' else tree), \
             patch.object(reuse, 'native_build_hash', side_effect=lambda ref: 'same' if ref=='head' else tree), \
             patch.object(reuse, 'version_at', return_value=version), patch.object(reuse, 'ensure_commit', return_value=True):
            return reuse.probe('win-x64', '0.2.1', 'owner/repo')['reused']

    def test_successful_platform_survives_another_platform_failure(self):
        self.assertTrue(self.probe())

    def test_failed_or_missing_platform_is_not_reused(self):
        self.assertFalse(self.probe(job='failure'))
        self.assertFalse(self.probe(job='skipped'))

    def test_changed_version_tree_or_expired_artifact_is_not_reused(self):
        self.assertFalse(self.probe(version='0.2.0'))
        self.assertFalse(self.probe(tree='changed'))
        self.assertFalse(self.probe(expired=True))

    def test_pull_request_and_release_sources_are_not_trusted(self):
        self.assertFalse(self.probe(event='pull_request'))
        self.assertFalse(self.probe(path='.github/workflows/release.yml'))

    def test_all_tracked_tvbox_inputs_affect_fingerprint_but_generated_target_does_not(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)

            def git(*args, check=True):
                return subprocess.run(['git', '-C', directory, *args], check=check,
                                      capture_output=True, text=True).stdout

            git('init')
            inputs = ('Runtime/JavaHost/pom.xml', 'Runtime/JavaHost/src/Spider.java',
                      'Runtime/QuickJS/native.c', 'build.sh', 'LICENSE')
            for name in inputs:
                path = root / 'third_party/tvbox-silicon' / name
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_text('original')
            git('add', 'third_party/tvbox-silicon')
            git('-c', 'user.name=Test', '-c', 'user.email=test@example.com',
                'commit', '-m', 'inputs')
            with patch.object(reuse, 'git', side_effect=git):
                original = reuse.source_hash('HEAD')
                target = root / 'third_party/tvbox-silicon/Runtime/JavaHost/target/host.jar'
                target.parent.mkdir(parents=True)
                target.write_text('generated')
                self.assertNotIn('target/', git('ls-files', 'third_party/tvbox-silicon'))
                self.assertEqual(original, reuse.source_hash('HEAD'))
                for name in inputs:
                    (root / 'third_party/tvbox-silicon' / name).write_text('changed')
                    git('add', 'third_party/tvbox-silicon/' + name)
                    git('-c', 'user.name=Test', '-c', 'user.email=test@example.com',
                        'commit', '-m', name)
                    self.assertNotEqual(original, reuse.source_hash('HEAD'), name)
                    git('reset', '--hard', 'HEAD~1')
                self.assertEqual(original, reuse.source_hash('HEAD'))

    def test_lookup_failure_builds_again(self):
        with patch.object(reuse, 'git', return_value='head'), patch.object(reuse, 'source_hash', return_value='same'), \
             patch.object(reuse, 'api', side_effect=RuntimeError('offline')):
            self.assertFalse(reuse.probe('win-x64', '0.2.1', 'owner/repo')['reused'])

if __name__=='__main__':
    unittest.main()
