import unittest
from package_reuse import validate


class ReuseTests(unittest.TestCase):
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

    def test_legacy_fresh_metadata_requires_exact_tag_push(self):
        m = {'version': '0.2.3', 'reused': False, 'rid': 'osx-arm64'}
        run = {'event': 'push', 'headBranch': 'v0.2.3', 'headSha': 'source'}
        self.assertTrue(validate(m, self.jobs, 'osx-arm64', '0.2.3', 'source', True, run))
        run['headSha'] = 'other'
        with self.assertRaises(ValueError):
            validate(m, self.jobs, 'osx-arm64', '0.2.3', 'source', True, run)
        run.update(headSha='source', event='workflow_dispatch')
        with self.assertRaises(ValueError):
            validate(m, self.jobs, 'osx-arm64', '0.2.3', 'source', True, run)

    def test_normal_run_never_reuses_packages(self):
        self.assertFalse(validate(self.meta, self.jobs, 'osx-arm64', '0.2.3', 'source', False))


if __name__ == '__main__':
    unittest.main()
