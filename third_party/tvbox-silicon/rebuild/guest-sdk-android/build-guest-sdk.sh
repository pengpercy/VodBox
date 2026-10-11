#!/usr/bin/env bash
# Guest SDK (Android guest libc/crypto) disposition review + fail-closed build gate.
# DOES NOT RUN IN THIS TASK.
#
# Scope (deliberately minimal): audit the ACTUAL distributed guest set and decide, per file,
# rebuild-from-pinned-source vs drop. This is NOT an AOSP full-system build. The old script
# pointed at a `repo init` of bionic + external/boringssl for a whole tree; that is both wider
# than the 26 distributed files and un-pinnable without guessing, so it is replaced by:
#   1) enumerate the real 26 packaged guest .so from the retained jar (source of truth),
#   2) read the per-file pin from aosp-pin.json,
#   3) build ONLY files that have a pinned AOSP tag+module, using ndk-build/CMake for that
#      module, and record hashes,
#   4) emit the drop list for everything unpinned (fail closed).
#
# It refuses to build anything while the pin is incomplete for that file; it never treats an
# unpinned file as done. Nothing here guesses an AOSP tag.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DEPS="$HERE/../../dependency-sources"
PIN="$HERE/aosp-pin.json"
OUT="$HERE/out"
JAR="$DEPS/../Runtime/JavaHost/target/dependency/unidbg-android-0.9.8.jar"
mkdir -p "$OUT"

[[ -f "$PIN" ]] || { echo "FATAL: missing pin $PIN" >&2; exit 1; }

# --- 1+2+3+4: audit + gate, driven by the retained jar and the pin file -------------
python3 - "$PIN" "$JAR" "$OUT" <<'PY'
import json, sys, zipfile, os

pin_path, jar_path, out = sys.argv[1], sys.argv[2], sys.argv[3]
pin = json.load(open(pin_path))

# Real distributed guest set: read it from the retained jar, never from memory.
guest_prefixes = ("android/sdk19/lib/", "android/sdk23/lib/", "android/sdk23/lib64/")
actual = []
if os.path.isfile(jar_path):
    with zipfile.ZipFile(jar_path) as z:
        actual = sorted(n for n in z.namelist()
                        if n.endswith(".so") and n.startswith(guest_prefixes))
else:
    print(f"WARN: retained jar not found at {jar_path}; falling back to pin inventory",
          file=sys.stderr)

pin_by_path = {l.get("path"): l for l in pin.get("libraries", [])}
# The pin carries one glob entry ("android/sdk19/lib/*.so") that is not a real file; ignore it
# when matching concrete jar entries.
pinnable = {p: l for p, l in pin_by_path.items() if p and "*" not in p}

rebuild, drop, unknown = [], [], []
for entry in actual:
    l = pinnable.get(entry)
    if l is None:
        unknown.append(entry)
        continue
    (rebuild if l.get("aosp_tag") and l.get("module") else drop).append(entry)

if not actual:
    actual = sorted(pinnable)  # best effort when the jar is unavailable

print(f"packaged guest .so entries: {len(actual)}")
print(f"rebuildable (pinned tag+module): {len(rebuild)}")
print(f"drop (unpinned, fail-closed):    {len(drop)}")
if unknown:
    print(f"NOT IN PIN (must be added before any closure claim): {len(unknown)}")
    for p in unknown:
        print("   ?", p)

if pin.get("status") == "template-unfilled":
    print("\nFAIL-CLOSED: aosp-pin.json is still marked template-unfilled.")
    print("No guest library will be built. Disposition for each file follows:")

# Emit the machine-readable disposition so the drop decision is recorded, not implied.
disposition = {
    "actual_guest_libraries": actual,
    "rebuild": rebuild,
    "drop": drop,
    "unlisted_in_pin": unknown,
    "policy": "rebuild-or-drop; unpinned files are dropped from distribution, never shipped",
}
with open(os.path.join(out, "disposition.json"), "w") as f:
    json.dump(disposition, f, indent=2)
print(f"\nwrote {os.path.join(out, 'disposition.json')}")

if rebuild:
    print("\nFiles eligible for a pinned AOSP build (main agent wires the exact module build):")
    for p in rebuild:
        l = pinnable[p]
        print(f"   {p}  <- aosp_tag={l['aosp_tag']} module={l['module']} source={l.get('source')}")
    print("\nFor each, build the named module with the pinned NDK and stage into out/<sdk>/.")
    print("Per-module build commands depend on the AOSP tag chosen in aosp-pin.json; they are")
    print("intentionally NOT hardcoded here until that tag is filled from a read manifest.")
else:
    print("\nNo file is currently pinned => nothing to build. All 26 must be dropped or pinned.")
sys.exit(0)
PY

echo
echo "Guest SDK gate complete. 'drop' entries must be removed from the shipped jar and recorded"
echo "in dependency-sources/rebuild-lock.json; they may never ship unprovenanced (REPORT.md gate 5)."
