"""Native backend coverage must match the jars actually staged, and must not overstate a platform.

The regression this guards: `build/dex-deps.json` used to describe coverage from
`unidbg-dynarmic:0.9.9`, an artifact the upstream pom never resolves, while the shipped backend is
`unidbg-unicorn2:0.9.8`. Prose drifted from the jars and "Windows ARM64 supported" was claimable
with no native library behind it. These tests derive the verdict from real zip contents instead.

A second regression lives here too: conflating *binary* RID coverage with *source* closure. The five
of six RIDs that carry the backend native library is a property of the shipped binaries; the
corresponding-source obligation for those binaries is a different question, tracked in
`native-source-manifest.json` and still pending. "Source closure 5/6" is not a thing this module
will ever produce — the source side is pending for every distributed artifact, and it must be read
from the source manifest, not from the RID coverage table.
"""
import json
import pathlib
import sys
import unittest
import unittest.mock
import zipfile

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import native_coverage as coverage

ROOT = pathlib.Path(__file__).resolve().parent.parent
ALMA = ROOT / '.alma'


def backend_jar(path: pathlib.Path, directories):
    """Write a minimal jar exposing `natives/<directory>/<name>.so` for each directory."""
    with zipfile.ZipFile(path, 'w') as archive:
        for directory in directories:
            archive.writestr(f'natives/{directory}/libbackend.so', b'\x7fELF')


class CoverageTests(unittest.TestCase):
    def setUp(self):
        ALMA.mkdir(exist_ok=True)

    def test_windows_arm64_is_not_claimed(self):
        # The shipped unidbg-unicorn2:0.9.8 has no natives/windows_arm64 — the RID must not be
        # reported as supported, and the diagnostic must name the backend and the platform.
        self.assertFalse(coverage.backend_supports('win-arm64'))
        self.assertIn('win-arm64', coverage.describe('win-arm64'))
        self.assertIn('unidbg-unicorn2', coverage.describe('win-arm64'))
        self.assertIn('win-arm64', coverage.unsupported_rids())

    def test_supported_rids_are_exactly_the_five_with_natives(self):
        expected = {'osx-x64', 'osx-arm64', 'linux-x64', 'linux-arm64', 'win-x64'}
        self.assertEqual({rid for rid in coverage.RIDS if coverage.backend_supports(rid)}, expected)

    def test_unknown_rid_is_rejected(self):
        for call in (coverage.backend_supports, coverage.describe, coverage.assembler_supports):
            with self.assertRaises(coverage.NativeCoverageError):
                call('plan9-x64')

    def test_assembler_coverage_is_narrower_and_does_not_widen_backend(self):
        # capstone/keystone/demumble reach fewer platforms than the emulation backend; the backend
        # verdict is what gates the guard, so a narrow assembler must never be read as support.
        self.assertFalse(coverage.assembler_supports('linux-arm64'))
        self.assertTrue(coverage.backend_supports('linux-arm64'))

    def test_verify_deps_accepts_a_jar_covering_every_declared_rid(self):
        with self._deps(['osx_64', 'osx_arm64', 'windows_64', 'linux_64', 'linux_arm64']) as deps:
            result = coverage.verify_deps(deps)
        self.assertEqual(sorted(result['observed']), sorted(result['declared']))

    def test_verify_deps_rejects_a_jar_missing_a_declared_platform(self):
        # A dependency bump that drops windows_64 must fail loudly, not ship as "win-x64 supported".
        with self._deps(['osx_64', 'osx_arm64', 'linux_64', 'linux_arm64']) as deps:
            with self.assertRaisesRegex(coverage.NativeCoverageError, 'win-x64'):
                coverage.verify_deps(deps)

    def test_verify_deps_rejects_a_missing_backend(self):
        import tempfile
        with tempfile.TemporaryDirectory(dir=ALMA) as directory:
            with self.assertRaisesRegex(coverage.NativeCoverageError, 'not staged'):
                coverage.verify_deps(pathlib.Path(directory))

    def test_lock_table_and_module_agree_on_the_unsupported_rid(self):
        lock = json.loads((ROOT / 'build' / 'dex-deps.json').read_text())
        declared = lock['nativeCoverage']['backendRids']
        self.assertEqual(declared['unsupported'], ['win-arm64'])
        self.assertNotIn('win-arm64', declared['supported'])
        # The declared artifact must be the one the upstream pom actually pins, not a stray version.
        self.assertIn('unidbg-unicorn2:0.9.8', lock['nativePlatforms']['artifact'])

    def test_closure_state_reads_the_real_lock_table_and_reports_the_binary_gap(self):
        # The gate must not trust a written-in verdict: it reads the lock table the build uses.
        state = coverage.closure_state()
        self.assertFalse(state['complete'])
        self.assertEqual(state['missing'], ['win-arm64'])
        self.assertNotIn('win-arm64', state['supported'])

    def test_require_closure_refuses_while_any_rid_is_uncovered(self):
        # Binary coverage gate: today win-arm64 has no native library, so a six-RID release is
        # refused rather than shipped as "complete".
        with self.assertRaisesRegex(coverage.NativeCoverageError, 'win-arm64'):
            coverage.require_closure()

    def test_require_closure_accepts_a_scoped_release_that_excludes_the_uncovered_rid(self):
        # Scoping the binary-coverage gate to covered RIDs is a legitimate release decision, not a
        # bypass: the *source* obligation is a separate gate and is not escaped by scoping.
        state = coverage.require_closure(['linux-x64', 'osx-arm64'])
        self.assertNotIn('win-arm64', state['supported'])

    def test_require_closure_accepts_a_lock_table_that_covers_every_rid(self):
        full = {'backendRids': {'supported': list(coverage.RIDS), 'unsupported': []}}
        with unittest.mock.patch.object(coverage, '_declared', return_value=full):
            state = coverage.require_closure()
        self.assertTrue(state['complete'])
        self.assertEqual(state['missing'], [])

    def test_closure_state_rejects_a_self_contradictory_table(self):
        # A RID listed in both sets (or dropped from both) must fail, not silently read as complete.
        contradiction = {'backendRids': {
            'supported': list(coverage.RIDS), 'unsupported': ['win-arm64']}}
        with unittest.mock.patch.object(coverage, '_declared', return_value=contradiction):
            with self.assertRaisesRegex(coverage.NativeCoverageError, 'both supported and unsupported'):
                coverage.closure_state()
        partial = {'backendRids': {'supported': ['win-x64'], 'unsupported': []}}
        with unittest.mock.patch.object(coverage, '_declared', return_value=partial):
            with self.assertRaisesRegex(coverage.NativeCoverageError, 'exactly the six RIDs'):
                coverage.closure_state()

    def test_upstream_pom_pins_the_artifact_the_table_describes(self):
        pom = (ROOT / 'third_party/tvbox-silicon/Runtime/JavaHost/pom.xml').read_text()
        self.assertIn('<artifactId>unidbg-unicorn2</artifactId>', pom)
        self.assertIn('<version>0.9.8</version>', pom)
        # Dynarmic is not a dependency of the pinned pom: coverage must never be sourced from it.
        self.assertNotIn('unidbg-dynarmic', pom)

    def test_source_closure_is_a_different_question_than_rid_coverage(self):
        # The bug this guards: reading the five/six RID coverage table and announcing "source closure
        # 5/6". The two manifest files answer different questions; the source side must be read from
        # the source manifest, which is still pending, and must therefore be *incomplete* for a
        # six-RID release even though five RIDs have covered natives.
        coverage.require_closure(['linux-x64', 'osx-arm64'])  # scoping clears the *binary* gate
        with self.assertRaises(coverage.NativeCoverageError):
            coverage.require_source_closure()
        state = coverage.source_closure_state()
        self.assertFalse(state['complete'])
        self.assertFalse(state['summary_complete'])
        self.assertEqual(state['unclassified'], [])
        # Every distributed native artifact is pending, not just the RID that lacks a native library.
        self.assertEqual(sorted(state['pending']), sorted(coverage.DISTRIBUTED_SOURCE_ARTIFACTS))
        self.assertEqual(state['artifacts']['com.github.zhkl0228:unidbg-unicorn2:0.9.8'],
                         'pending-engine-source-commit')

    def test_source_closure_reads_the_real_source_manifest_not_the_lock_table(self):
        # The source verdict must come from native-source-manifest.json, whose overall status is the
        # pending rebuild path — never from dex-deps.json's backendRids.
        manifest = json.loads((ROOT / 'third_party/tvbox-silicon/dependency-sources'
                               / 'native-source-manifest.json').read_text())
        self.assertEqual(manifest['overall_status'], 'pending-with-rebuild-path')
        self.assertFalse(manifest['corresponding_source_complete'])
        self.assertFalse(manifest['minimal_rebuild_path']['executed'])
        # The source manifest carries no RID-split "5/6" table at all: the number is not its shape.
        self.assertNotIn('nativeCoverage', manifest)

    def test_source_closure_fails_closed_when_the_summary_flag_is_hand_edited(self):
        # Flipping corresponding_source_complete to true must not close the obligation while any
        # artifact still carries a pending status. The per-artifact evidence decides, not the flag.
        manifest = json.loads((ROOT / 'third_party/tvbox-silicon/dependency-sources'
                               / 'native-source-manifest.json').read_text())
        edited = json.loads(json.dumps(manifest))
        edited['corresponding_source_complete'] = True
        with unittest.mock.patch.object(coverage, '_source_manifest', return_value=edited):
            state = coverage.source_closure_state()
            self.assertFalse(state['complete'])
            self.assertTrue(state['summary_complete'])
            with self.assertRaises(coverage.NativeCoverageError):
                coverage.require_source_closure()

    def test_source_closure_opens_when_an_entry_is_pending_even_if_others_match(self):
        complete_entry = {'status': 'matched-release-source-collected', 'corresponding_source_complete': True}
        pending_entry = {'status': 'pending-engine-source-commit', 'corresponding_source_complete': False}
        manifest = {
            'corresponding_source_complete': True,
            'dependencies': [
                {'artifact': coordinate, 'status': pending_entry['status'],
                 'corresponding_source_complete': False}
                for coordinate in coverage.DISTRIBUTED_SOURCE_ARTIFACTS
            ],
        }
        with unittest.mock.patch.object(coverage, '_source_manifest', return_value=manifest):
            with self.assertRaisesRegex(coverage.NativeCoverageError, 'pending'):
                coverage.require_source_closure()
        # All entries closed and the summary true -> the obligation is discharged.
        manifest['dependencies'] = [
            {'artifact': coordinate, **complete_entry}
            for coordinate in coverage.DISTRIBUTED_SOURCE_ARTIFACTS
        ]
        with unittest.mock.patch.object(coverage, '_source_manifest', return_value=manifest):
            state = coverage.require_source_closure()
        self.assertTrue(state['complete'])

    def test_source_closure_fails_closed_on_a_distributed_artifact_with_no_entry(self):
        manifest = {'corresponding_source_complete': False, 'dependencies': []}
        with unittest.mock.patch.object(coverage, '_source_manifest', return_value=manifest):
            with self.assertRaisesRegex(coverage.NativeCoverageError, 'no manifest entry'):
                coverage.require_source_closure()

    def _deps(self, directories):
        import contextlib
        import tempfile

        @contextlib.contextmanager
        def manager():
            with tempfile.TemporaryDirectory(dir=ALMA) as directory:
                root = pathlib.Path(directory)
                backend_jar(root / f'{coverage.BACKEND_ARTIFACT_PREFIX}-{coverage.BACKEND_VERSION}.jar', directories)
                yield root

        return manager()


if __name__ == '__main__':
    unittest.main()
