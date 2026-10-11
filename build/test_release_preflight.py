"""The release preflight must refuse an undischarged obligation, from real evidence.

The two questions are deliberately kept apart, and the earlier code merged them wrongly:

  * RID coverage is a *diagnostic*. A RID with no upstream ARM64 backend native library (today
    `win-arm64`) limits native-guarded plugins on that RID; an ordinary Java package still runs
    there (its own JRE asset) and the host fails such plugins explicitly. It must **not** block a
    release, and the old behaviour — refusing the whole release on the five/six coverage table —
    conflated a per-plugin limitation with a release-wide obligation.

  * The redistribution obligation is the *gate*, and it is verified per distributed native from
    real evidence:
      - a GPL/copyleft native (the Unicorn engine) is closed only by an executed from-pinned-source
        rebuild receipt recording the rebuilt library hash, the engine source-archive hash and the
        bridge digest — never a summary flag or a status token. No receipt ⇒ blocked (the default
        for the old, un-rebuilt library).
      - a permissive native (the guest SDK/hook libs, JNA, keystone, demumble) is notice-only and is
        closed by its retained upstream license files actually being on disk — recorded as
        satisfied, not hard-coded false.

`--allow-incomplete` is a dry-run-only escape the release workflow never passes, and it must not
close the gate either.
"""
import contextlib
import importlib.util
import io
import json
import pathlib
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import native_coverage

spec = importlib.util.spec_from_file_location(
    'release_preflight', pathlib.Path(__file__).with_name('release_preflight.py'))
preflight = importlib.util.module_from_spec(spec)
spec.loader.exec_module(preflight)


def run_gate(rids=None, allow_incomplete=False):
    """Capture the binary-coverage diagnostic's stdout+stderr and return (status, printed)."""
    out, err = io.StringIO(), io.StringIO()
    with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
        status = preflight.check_native_coverage(rids, allow_incomplete)
    return status, out.getvalue() + err.getvalue()


def run_source_gate(allow_incomplete=False, receipts_dir=None):
    """Capture the release gate's stdout+stderr and return (status, printed)."""
    out, err = io.StringIO(), io.StringIO()
    with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
        status = preflight.check_source_closure(allow_incomplete, receipts_dir)
    return status, out.getvalue() + err.getvalue()


def valid_backend_receipt():
    """An executed-rebuild receipt shaped like `rebuild_native_backend.py` writes."""
    return {
        'schema': 1,
        'key': 'backend-0.9.8',
        'rid': 'osx-arm64',
        'backend': {
            'coordinate': 'com.github.zhkl0228:unidbg-unicorn2:0.9.8',
            'engine': {'source_archive_sha256': 'a' * 64},
            'bridge': {'source_digest_sha256': 'b' * 64},
            'output_library': {'sha256': 'c' * 64, 'size': 123},
            'jar_integration': {
                'native_entry': 'natives/osx_arm64/libunicorn.dylib',
                'jar_native_entries': ['natives/osx_arm64/libunicorn.dylib'],
            },
        },
        'unicorn1': {
            'coordinate': 'com.github.zhkl0228:unicorn:1.0.14',
            'kept_dependency': True,
            'removed_native_entries': ['natives/osx_64/libunicorn_java.dylib'],
            'source_evidence': {'archive_sha256': 'd' * 64},
        },
    }


class CoverageDiagnosticTests(unittest.TestCase):
    def test_a_six_rid_release_is_not_blocked_by_the_binary_coverage_gap(self):
        # The correction: win-arm64 has no upstream native library, but that limits native-guarded
        # plugins, not the release. Ordinary Java ships there (its own JRE), so this must not block.
        status, output = run_gate()
        self.assertEqual(status, 0)
        self.assertIn('win-arm64', output)
        self.assertIn('not a release blocker', output)

    def test_scoping_the_report_to_covered_rids_still_passes(self):
        status, _ = run_gate(['linux-x64', 'osx-arm64'])
        self.assertEqual(status, 0)

    def test_a_malformed_coverage_table_fails_the_diagnostic(self):
        # A self-contradictory table is a real error, not a scoping choice.
        contradiction = {'backendRids': {
            'supported': list(native_coverage.RIDS), 'unsupported': ['win-arm64']}}
        with patch.object(native_coverage, '_declared', return_value=contradiction):
            status, output = run_gate()
        self.assertEqual(status, 1)
        self.assertIn('both supported and unsupported', output)


class SourceGateTests(unittest.TestCase):
    def test_real_release_is_blocked_with_no_receipt_for_the_gpl_engine(self):
        # The default: an old, un-rebuilt library with no receipt stays blocked.
        status, output = run_source_gate()
        self.assertEqual(status, 1)
        self.assertIn('incomplete', output.lower())
        self.assertIn('unidbg-unicorn2:0.9.8', output)

    def test_permissive_notice_components_are_read_as_satisfied_not_hard_false(self):
        # The guest SDK/hook libs, JNA, keystone and demumble carry no source obligation; their
        # retained notices close them. They must not appear among the OPEN components.
        state = native_coverage.source_closure_state()
        for coordinate in ('com.github.zhkl0228:unidbg-android:0.9.8',
                           'net.java.dev.jna:jna:5.10.0',
                           'com.github.zhkl0228:keystone:0.9.7',
                           'com.github.zhkl0228:demumble:1.0.4'):
            self.assertEqual(state['obligations'][coordinate], 'permissive-notice-only')
            self.assertNotIn(coordinate, state['pending'])

    def test_only_the_gpl_engine_source_obligation_stays_open(self):
        state = native_coverage.source_closure_state()
        self.assertEqual(sorted(state['pending']),
                         ['com.github.zhkl0228:unicorn:1.0.14',
                          'com.github.zhkl0228:unidbg-unicorn2:0.9.8'])
        self.assertEqual(state['obligations']['com.github.zhkl0228:unidbg-unicorn2:0.9.8'],
                         'gpl-corresponding-source')

    def test_a_valid_executed_rebuild_receipt_closes_the_gate(self):
        # A receipt that records the rebuilt native + source + bridge evidence discharges it.
        with tempfile.TemporaryDirectory() as directory:
            pathlib.Path(directory, 'backend-0.9.8.json').write_text(
                json.dumps(valid_backend_receipt()))
            status, output = run_source_gate(receipts_dir=pathlib.Path(directory))
            self.assertEqual(status, 0)
            self.assertIn('discharged', output.lower())

    def test_a_status_only_receipt_does_not_close_the_gate(self):
        # The exact regression: a receipt that records no output hash / source hash / bridge digest
        # (e.g. only a status token or version string) must not be accepted as completion.
        receipt = valid_backend_receipt()
        receipt['backend']['output_library'] = {}
        receipt['backend']['engine'] = {}
        receipt['backend']['bridge'] = {}
        with tempfile.TemporaryDirectory() as directory:
            pathlib.Path(directory, 'backend-0.9.8.json').write_text(json.dumps(receipt))
            state = native_coverage.source_closure_state(pathlib.Path(directory))
            self.assertFalse(state['complete'])
            self.assertIn('com.github.zhkl0228:unidbg-unicorn2:0.9.8', state['pending'])
            self.assertEqual(run_source_gate(receipts_dir=pathlib.Path(directory))[0], 1)

    def test_a_receipt_that_leaves_foreign_natives_open_does_not_close(self):
        # A receipt whose jar rewrite still carries another RID's native proof-of-absence fails.
        receipt = valid_backend_receipt()
        receipt['backend']['jar_integration']['jar_native_entries'] = [
            'natives/osx_arm64/libunicorn.dylib', 'natives/linux_64/libunicorn.so']
        with tempfile.TemporaryDirectory() as directory:
            pathlib.Path(directory, 'backend-0.9.8.json').write_text(json.dumps(receipt))
            state = native_coverage.source_closure_state(pathlib.Path(directory))
            self.assertIn('com.github.zhkl0228:unidbg-unicorn2:0.9.8', state['pending'])

    def test_gate_is_not_satisfied_by_a_covered_rid_set(self):
        # RID coverage must never be read as the source verdict: covering all six RIDs on the
        # binary side leaves the source obligation exactly as open as before.
        full = {'backendRids': {'supported': list(native_coverage.RIDS), 'unsupported': []}}
        with patch.object(native_coverage, '_declared', return_value=full):
            self.assertEqual(run_gate()[0], 0)
        self.assertEqual(run_source_gate()[0], 1)

    def test_gate_fails_closed_when_the_summary_flag_is_hand_edited(self):
        # Flipping corresponding_source_complete to true must not close the obligation: the
        # per-component evidence decides, not the summary.
        manifest = json.loads(native_coverage.SOURCE_MANIFEST.read_text())
        edited = json.loads(json.dumps(manifest))
        edited['corresponding_source_complete'] = True
        with patch.object(native_coverage, '_source_manifest', return_value=edited):
            self.assertEqual(run_source_gate()[0], 1)

    def test_gate_allow_incomplete_only_warns(self):
        status, output = run_source_gate(allow_incomplete=True)
        self.assertEqual(status, 0)
        self.assertIn('WARNING', output)

    def test_gate_passes_when_every_component_is_closed(self):
        # All notices retained AND the GPL engine rebuilt: the gate clears.
        with tempfile.TemporaryDirectory() as directory:
            pathlib.Path(directory, 'backend-0.9.8.json').write_text(
                json.dumps(valid_backend_receipt()))
            manifest = json.loads(native_coverage.SOURCE_MANIFEST.read_text())
            manifest['corresponding_source_complete'] = True
            with patch.object(native_coverage, '_source_manifest', return_value=manifest):
                status, output = run_source_gate(receipts_dir=pathlib.Path(directory))
        self.assertEqual(status, 0)
        self.assertIn('complete', output.lower())


if __name__ == '__main__':
    unittest.main()
