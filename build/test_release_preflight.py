"""The release preflight must refuse an incomplete closure, derived from the manifests.

Two regressions are guarded, and they are deliberately kept apart:

  * RID coverage. A package for a RID with no upstream ARM64 backend native library would ship and
    then fail its first native-guarded plugin at runtime. This gate reads the coverage lock table
    (`build/dex-deps.json`) on every run; there is no "complete" marker to hand-edit.
  * Corresponding source. Even a release scoped to the covered RIDs still distributes native
    binaries whose source obligation is pending in `native-source-manifest.json`, so a *real*
    publication must stop. Reading the five/six RID table as "source closure 5/6" is exactly the
    error that must not let a release through.

`--allow-incomplete` is a dry-run-only escape the release workflow never passes, and it must not
close the source gate either.
"""
import contextlib
import importlib.util
import io
import pathlib
import sys
import unittest
from unittest.mock import patch

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import native_coverage

spec = importlib.util.spec_from_file_location(
    'release_preflight', pathlib.Path(__file__).with_name('release_preflight.py'))
preflight = importlib.util.module_from_spec(spec)
spec.loader.exec_module(preflight)


def run_gate(rids=None, allow_incomplete=False):
    """Capture the binary-coverage gate's stdout+stderr and return (status, printed)."""
    out, err = io.StringIO(), io.StringIO()
    with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
        status = preflight.check_native_coverage(rids, allow_incomplete)
    return status, out.getvalue() + err.getvalue()


def run_source_gate(allow_incomplete=False):
    """Capture the source-closure gate's stdout+stderr and return (status, printed)."""
    out, err = io.StringIO(), io.StringIO()
    with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
        status = preflight.check_source_closure(allow_incomplete)
    return status, out.getvalue() + err.getvalue()


class CoverageGateTests(unittest.TestCase):
    def test_real_lock_table_blocks_a_six_rid_release(self):
        # Today win-arm64 has no upstream native library, so the gate must stop a real release.
        status, output = run_gate()
        self.assertEqual(status, 1)
        self.assertIn('win-arm64', output)
        self.assertIn('incomplete', output.lower())

    def test_a_fully_covered_lock_table_passes(self):
        full = {'backendRids': {'supported': list(native_coverage.RIDS), 'unsupported': []}}
        with patch.object(native_coverage, '_declared', return_value=full):
            status, output = run_gate()
        self.assertEqual(status, 0)
        self.assertIn('complete', output.lower())

    def test_scoping_the_release_to_covered_rids_passes(self):
        # If a release were deliberately scoped to RIDs the backend covers, the *binary* gate lets it
        # through — but the source gate below is what still stops the real publication.
        status, _ = run_gate(['linux-x64', 'osx-arm64'])
        self.assertEqual(status, 0)

    def test_allow_incomplete_only_warns_never_blocks(self):
        # Dry runs may observe the gap, but this escape hatch is never used by release.yml.
        status, output = run_gate(allow_incomplete=True)
        self.assertEqual(status, 0)
        self.assertIn('WARNING', output)

    def test_unknown_rid_is_rejected_not_silently_ignored(self):
        # A target the coverage table cannot classify must fail, never pass as covered.
        full = {'backendRids': {'supported': list(native_coverage.RIDS), 'unsupported': []}}
        with patch.object(native_coverage, '_declared', return_value=full):
            with self.assertRaises(native_coverage.NativeCoverageError):
                native_coverage.require_closure(['plan9-x64'])


class SourceClosureGateTests(unittest.TestCase):
    def test_real_source_manifest_blocks_even_a_scoped_release(self):
        # The core fix: five of six RIDs being covered by the backend does NOT close the source
        # obligation. The source manifest is pending, so a release scoped to the covered RIDs still
        # fails here — the two gates are not the same question.
        status, output = run_source_gate()
        self.assertEqual(status, 1)
        self.assertIn('incomplete', output.lower())
        self.assertIn('pending', output.lower())
        self.assertEqual(run_gate(['linux-x64', 'osx-arm64'])[0], 0)  # binary gate clears...

    def test_source_gate_is_not_satisfied_by_a_covered_rid_set(self):
        # Guard against the exact bug: RID coverage must never be read as source closure.
        full = {'backendRids': {'supported': list(native_coverage.RIDS), 'unsupported': []}}
        with patch.object(native_coverage, '_declared', return_value=full):
            self.assertEqual(run_gate()[0], 0)
        self.assertEqual(run_source_gate()[0], 1)

    def test_source_gate_allow_incomplete_only_warns(self):
        status, output = run_source_gate(allow_incomplete=True)
        self.assertEqual(status, 0)
        self.assertIn('WARNING', output)

    def test_source_gate_passes_when_every_artifact_is_closed(self):
        closed = {coordinate: {'status': 'matched-release-source-collected',
                               'corresponding_source_complete': True}
                  for coordinate in native_coverage.DISTRIBUTED_SOURCE_ARTIFACTS}
        manifest = {
            'corresponding_source_complete': True,
            'dependencies': [{'artifact': coordinate, **entry}
                             for coordinate, entry in closed.items()],
        }
        with patch.object(native_coverage, '_source_manifest', return_value=manifest):
            status, output = run_source_gate()
        self.assertEqual(status, 0)
        self.assertIn('complete', output.lower())


if __name__ == '__main__':
    unittest.main()
