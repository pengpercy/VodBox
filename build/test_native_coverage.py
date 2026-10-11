"""Native backend coverage must match the jars actually staged, and must not overstate a platform.

The regression this guards: `build/dex-deps.json` used to describe coverage from
`unidbg-dynarmic:0.9.9`, an artifact the upstream pom never resolves, while the shipped backend is
`unidbg-unicorn2:0.9.8`. Prose drifted from the jars and "Windows ARM64 supported" was claimable
with no native library behind it. These tests derive the verdict from real zip contents instead.

A second regression lives here too: conflating *binary* RID coverage with the *redistribution*
obligation of the distributed natives. The five of six RIDs that carry the backend native library is
a property of the shipped binaries; whether each distributed native's obligation is discharged is a
different question, answered per component from real evidence — an executed from-pinned-source
rebuild receipt for the GPL engine, or the retained upstream notice files for a permissive
notice-only native. "Source closure 5/6" is not a thing this module will ever produce: the two GPL
engine components stay open until a receipt exists, and a summary flag or status token never closes
them.
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
        # the source manifest's per-component evidence, which is still open for the GPL engine even
        # though five RIDs have covered natives.
        coverage.require_closure(['linux-x64', 'osx-arm64'])  # scoping clears the *binary* gate
        with self.assertRaises(coverage.NativeCoverageError):
            coverage.require_source_closure()
        state = coverage.source_closure_state()
        self.assertFalse(state['complete'])
        self.assertFalse(state['summary_complete'])
        self.assertEqual(state['unclassified'], [])
        # Only the two GPL-engine components stay open; the permissive notice-only natives are read
        # as satisfied from their retained notices, not hard-coded pending.
        self.assertEqual(sorted(state['pending']),
                         ['com.github.zhkl0228:unicorn:1.0.14',
                          'com.github.zhkl0228:unidbg-unicorn2:0.9.8'])
        self.assertEqual(state['artifacts']['com.github.zhkl0228:unidbg-unicorn2:0.9.8'],
                         'pending-engine-source-commit')

    def test_obligations_distinguish_source_from_notice_only(self):
        # The per-component audit surface: the GPL engine carries the source obligation; the guest
        # SDK/hook libs and the other permissive natives are notice-only.
        state = coverage.source_closure_state()
        self.assertEqual(state['obligations']['com.github.zhkl0228:unidbg-unicorn2:0.9.8'],
                         'gpl-corresponding-source')
        self.assertEqual(state['obligations']['com.github.zhkl0228:unicorn:1.0.14'],
                         'gpl-corresponding-source')
        for coordinate in ('com.github.zhkl0228:unidbg-android:0.9.8', 'net.java.dev.jna:jna:5.10.0',
                           'com.github.zhkl0228:keystone:0.9.7', 'com.github.zhkl0228:demumble:1.0.4'):
            self.assertEqual(state['obligations'][coordinate], 'permissive-notice-only')
            self.assertNotIn(coordinate, state['pending'])

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

    def test_gpl_engine_stays_open_with_no_rebuild_receipt(self):
        # The default blocked case: the old, un-rebuilt library has no executed-rebuild receipt, so
        # the corresponding-source obligation is open regardless of the manifest's status token.
        state = coverage.source_closure_state()
        self.assertIn('com.github.zhkl0228:unidbg-unicorn2:0.9.8', state['pending'])
        self.assertIn('no rebuild receipt', ' '.join(state['problems'][
            'com.github.zhkl0228:unidbg-unicorn2:0.9.8']))

    def test_source_closure_fails_closed_when_the_summary_flag_is_hand_edited(self):
        # Flipping corresponding_source_complete to true must not close the obligation while the GPL
        # engine still has no executed-rebuild receipt. The per-component evidence decides.
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

    def test_a_status_token_alone_does_not_close_the_gpl_obligation(self):
        # Even a manifest status of 'matched-release-source-collected' must not close the engine
        # without an executed-rebuild receipt: a status token is not evidence of the distributed
        # binary's source.
        manifest = json.loads((ROOT / 'third_party/tvbox-silicon/dependency-sources'
                               / 'native-source-manifest.json').read_text())
        edited = json.loads(json.dumps(manifest))
        edited['corresponding_source_complete'] = True
        for entry in edited['dependencies']:
            entry['status'] = 'matched-release-source-collected'
            entry['corresponding_source_complete'] = True
        for entry in edited['additional_native_dependencies']:
            entry['status'] = 'matched-release-source-collected'
            entry['corresponding_source_complete'] = True
        with unittest.mock.patch.object(coverage, '_source_manifest', return_value=edited):
            state = coverage.source_closure_state()
        self.assertFalse(state['complete'])
        self.assertIn('com.github.zhkl0228:unidbg-unicorn2:0.9.8', state['pending'])

    def test_source_closure_fails_closed_on_a_distributed_artifact_with_no_entry(self):
        manifest = {'corresponding_source_complete': False, 'dependencies': []}
        with unittest.mock.patch.object(coverage, '_source_manifest', return_value=manifest):
            with self.assertRaises(coverage.NativeCoverageError):
                coverage.require_source_closure()

    def test_source_closure_fails_closed_when_the_notice_dir_is_not_retained(self):
        # A notice-only component is closed by its retained license files actually being on disk; a
        # component whose notice directory is not retained stays open rather than being trusted.
        obligation = dict(coverage.NATIVE_COMPONENT_OBLIGATIONS)
        obligation['com.github.zhkl0228:demumble:1.0.4'] = {
            'class': 'permissive-notice-only', 'notice_dir': 'does-not-exist-9.9.9'}
        with unittest.mock.patch.dict(coverage.NATIVE_COMPONENT_OBLIGATIONS, obligation):
            state = coverage.source_closure_state()
        self.assertIn('com.github.zhkl0228:demumble:1.0.4', state['pending'])
        self.assertIn('no retained license notice', ' '.join(
            state['problems']['com.github.zhkl0228:demumble:1.0.4']))

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
