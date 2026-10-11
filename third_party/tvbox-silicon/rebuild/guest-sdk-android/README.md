# guest-sdk-android — the 34 packaged guest libraries (hardest closure)

The `unidbg-android:0.9.8` jar packages 34 guest `.so` files that the emulator *loads*
(they run inside the emulated Android guest, not natively on the host):

| group            | count | example                                   |
|------------------|-------|-------------------------------------------|
| sdk19/lib        | 8     | `libc.so`, `libm.so`, `libdl.so` …        |
| sdk23/lib        | 9     | `libc.so`, `libcrypto.so`, `libssl.so` …  |
| sdk23/lib64      | 9     | 64-bit equivalents                        |
| hook libs        | 8     | `libhookzz.so`, `libwhale.so`, `libxhook.so` (v7a + v8a) |

## Why this is the hard one

`dependency-sources/upstream-evidence/native-provenance/REPORT.md` establishes, with
retained evidence, that **the exact source of these historical `.so` files is not recoverable
from public evidence**:

- They were **imported** over unidbg's history (`d86632f1…`), not built from a pinned
  manifest in the v0.9.8 release tree. The release tree has **zero gitlinks and no
  `.gitmodules`/CI**.
- API level, compiler string and `external/boringssl` vs OpenSSL strings are **leads, not
  source proof** (`sdk19-api.json`, `sdk23*.json`, `binary-inventory.json`).
- A vendor platform fork could have modified any file; a plain AOSP tag does not, on its own,
  prove the publisher's blob.

So — exactly as the report's gate item 5 requires — this component **fails closed**: any
`.so` for which VodBox cannot pin a public source/build is either (a) rebuilt from a pinned
AOSP source it can name, or (b) **removed from the distribution**.

## The two viable dispositions (VodBox chooses per file)

1. **Repin to AOSP source.** For each sdk19/sdk23 `.so`, identify the AOSP release/tag and
   the module that produces it (bionic libc, `external/boringssl`, etc.), record it in
   `aosp-pin.json`, build with the pinned NDK, and record the output hash. This yields a
   *VodBox-built* guest libc/crypto the emulator can load.
   - Risk: unidbg's emulator is tuned against specific guest libc behavior; a different
     AOSP build may change syscall/ABI behavior. The main agent must run the existing DEX
     acceptance suite (`build/dex-acceptance.sh`) to confirm behavior, and must not claim
     compatibility from the build alone.
2. **Drop the file.** If no public AOSP source can be pinned for a given `.so`, remove it
   from the shipped jar and record the removal. Any plugin path that needed it will fail
   explicitly (consistent with the existing `unsupported_native_library` contract) rather
   than ship an unprovenanced binary.

## What is intentionally left unset here

`aosp-pin.json` is a **template**. Filling in the exact tag/module per `.so` is source
research that requires reading the AOSP release manifests — deliberately not guessed in this
task to avoid exactly the "assume a commit matches the old binary" error the report warns
against. When filled in, run `build-guest-sdk.sh` (not executed here).
