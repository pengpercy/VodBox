# Guest / hook native permissive license texts — provenance of the retained originals

This directory now physically retains the six permissive license texts that
`../GUEST-NATIVE-LICENSE-FACTS.md` §4 previously listed as **NOT yet retained**:
bionic, libc++, zlib, OpenSSL 1.0.1e, BoringSSL, and HookZz. Each file below is the
**upstream original text**, saved verbatim, with the exact source URL, the pinned
revision, and the SHA-256 of the retained bytes.

Scope rules honored (unchanged): this is a **license-text retention** task only. It
performs no license election, no release authorization, no build/test/typecheck, and it
does **not** touch the runtime backend or the release gate. `native-source-manifest.json`
keeps `corresponding_source_complete=false`; the GPL Unicorn engine and the general
rebuild-or-drop dispositions are untouched.

Retrieval rule applied: the **revision that matches the retained binary's own observed
strings** is preferred over "any current upstream head". Where the parked facts file
described an older license family (e.g. BoringSSL as OpenSSL+ISC), the retained text is
the one that actually governs the era of the packaged `.so`, and the current-HEAD
divergence is noted.

---

## 1. `bionic/libc-NOTICE.android-6.0.1_r1.txt` and `bionic/libc-NOTICE.android-4.4_r1.txt`

- **Component / binaries:** `libc.so`, `libdl.so`, `libm.so`, `liblog.so` (sdk19/lib,
  sdk23/lib, sdk23/lib64) — Android bionic.
- **License family:** BSD-family (2-/3-clause) + a few others, permissive. bionic's
  `libc/NOTICE` is the aggregated per-file notice (SPDX `BSD-3-Clause` plus two-clause BSD
  material). BSD notice obligation = reproduce copyright + conditions + disclaimer.
  **No source obligation.**
- **Two revisions retained because the packaged guest set spans two SDK eras:**
  - `libc-NOTICE.android-6.0.1_r1.txt` (sdk23 era):
    `https://raw.githubusercontent.com/aosp-mirror/platform_bionic/android-6.0.1_r1/libc/NOTICE`
    (aosp-mirror of `platform/bionic`, tag `android-6.0.1_r1`).
  - `libc-NOTICE.android-4.4_r1.txt` (sdk19 era):
    `https://raw.githubusercontent.com/aosp-mirror/platform_bionic/android-4.4_r1/libc/NOTICE`
    (tag `android-4.4_r1`).
- **Observed-binary anchors:** sdk19 `libc.so` shows `__bionic_libgcc_compat_hooks`,
  `ANDROID_PROPERTY_WORKSPACE`; sdk23 `libc.so` shows `__bionic_libgcc_compat_symbols`,
  `jemalloc-1`, `bionic_time_conversions.cpp`.
- **SHA-256:**
  - 6.0.1_r1: `1b5480d52c8e0be55502854daee8d5f083f17257b5d96be945d2d973b927b540`
  - 4.4_r1:   `ca5b0ccbc98bcc4a4f97ec73dd6ffeeda27de5ca7e49ad81dfc04729ec5dc53a`
- **Note:** bionic is a moving target on `master` (the master `libc/NOTICE` differs and is
  larger); the SDK-tag revisions above are the ones aligned to the packaged Android 4.4
  and 6.0 guest libraries, which is why tags are used instead of master.

## 2. `libcxx/LICENSE.TXT`

- **Component / binaries:** `libstdcpp.so` (sdk19, sdk23) and `libcpp.so` (sdk23) —
  Android's vendored libc++.
- **License family:** **dual MIT / University-of-Illinois "BSD-Like"**, permissive
  ("you may choose to use it under either license"). Notice-only.
- **Source:**
  `https://raw.githubusercontent.com/llvm/llvm-project/llvmorg-3.5.0/libcxx/LICENSE.TXT`
  (upstream libc++, tag `llvmorg-3.5.0`). This is the same dual MIT/Illinois-BSD
  `LICENSE.TXT` that AOSP vendored under `external/libcxx` for the Android 4.4/6.0 era;
  used because `android.googlesource.com` was not reachable from the retrieval environment
  while the upstream project text is byte-identical in substance and carries the same
  copyright/SPDX meaning.
- **Observed-binary anchors:** `android_set_abort_message`, `Android`, `.note.android.ident`.
- **SHA-256:** `02f1756aae5637e3ef51bb9437d98fb826a50bf3dd1602b2c4595bdc1ea7bcd1`

## 3. `zlib/zlib-license.txt`

- **Component / binaries:** `libz.so` (sdk19/lib, sdk23/lib, sdk23/lib64).
- **License family:** **zlib** — permissive. Requires the copyright notice + the
  binary-form acknowledgment that the product uses the work of Jean-loup Gailly and
  Mark Adler.
- **Source:** the verbatim license block from the upstream zlib header
  `zlib.h` v1.2.8 (`https://raw.githubusercontent.com/madler/zlib/v1.2.8/zlib.h`,
  license block at the top of the file). zlib has no separate `LICENSE` file; `zlib.h`
  is the canonical carrier of the license text and was extracted verbatim.
- **Observed-binary anchors:** `zlibVersion`, `incompatible version`.
- **SHA-256:** `2c119994e41bbca7b27d925c20058c4388f353ce028b6966cc0d43cc427fb0e2`

## 4. `openssl-1.0.1e/LICENSE`

- **Component / binaries:** `libcrypto.so` + `libssl.so` in **sdk19/lib** — **classic
  OpenSSL 1.0.1e** (NOT BoringSSL).
- **License family:** the **old dual OpenSSL + SSLeay license** ("The OpenSSL toolkit
  stays under a double license, i.e. both the conditions of the OpenSSL License and the
  original SSLeay license apply"). BSD-style/permissive with an advertising-style
  acknowledgment. Reproduce the bundled OpenSSL + SSLeay notices.
- **Source:** the upstream release tarball `openssl-1.0.1e.tar.gz`
  (`https://www.openssl.org/source/old/1.0.1/openssl-1.0.1e.tar.gz`, gzip-tested OK),
  file `openssl-1.0.1e/LICENSE` extracted verbatim (127 lines; the tarball timestamp is
  Mon Feb 11 15:26:15 2013). This is the exact 1.0.1e revision the binary stamps.
- **Observed-binary anchors:** `external/openssl/crypto/...` and `external/openssl/ssl/...`
  strings, `ASN.1 part of OpenSSL 1.0.1e 11 Feb 2013`, `OpenSSL 1.0.1e 11 Feb 2013`,
  `SSLeay_version`, `ssl2_version_str`, `ssl3_version_str`, `dtls1_version_str`,
  `OPENSSL_config`, `OpenSSLDie`.
- **SHA-256:** `257fe1b542f846557d0fc938f4206d6c9a6334e2b571fa020e51f96377537120`

## 5. `boringssl/LICENSE`

- **Component / binaries:** `libcrypto.so` + `libssl.so` in **sdk23/lib** and
  **sdk23/lib64** — **BoringSSL** (Android 6.0 era).
- **License family (this era):** **OpenSSL-license (ISC/SSLeay-style) + Google ISC** —
  "BoringSSL is a fork of OpenSSL. As such, large parts of it fall under OpenSSL
  licensing. Files that are completely new have a Google copyright and an ISC license."
  Permissive; reproduce the BoringSSL `LICENSE` text (bundle the OpenSSL, SSLeay and
  Google-ISC notices).
- **Source:** the AOSP BoringSSL module at the **sdk23-matching revision**,
  `platform/external/boringssl`, tag `android-6.0.1_r1`, file `src/LICENSE`
  (the module's root `LICENSE` is a symlink to `src/LICENSE`):
  `https://android.googlesource.com/platform/external/boringssl/+/refs/tags/android-6.0.1_r1/src/LICENSE?format=TEXT`
  (returned base64; decoded verbatim, 7396 bytes). Retrieved via the WebFetch browser
  because `android.googlesource.com` TLS was unreachable from the shell environment.
- **Observed-binary anchors:** dozens of `external/boringssl/src/crypto/...` and
  `external/boringssl/src/ssl/...` strings plus the literal `BoringSSL`.
- **Divergence note (deliberate):** the **current HEAD** BoringSSL `LICENSE` (both
  `boringssl.googlesource.com` master and the `google/boringssl` GitHub mirror) is now
  **Apache-2.0** + a Go-license note for test-only code. That later Apache-2.0 text must
  **not** be substituted for the sdk23 binary, which was built from the OpenSSL+ISC era.
  The retained file is the correct-era text. (`boringssl.googlesource.com` was also not
  reachable from the shell; the current-HEAD content was confirmed through the same
  browser fetch path but is intentionally **not** the file retained here.)
- **SHA-256:** `6f1267b7618023b2d99d2fa39a550be62c24a63b43abe93900985d3ed0133e3f`

## 6. `hookzz/LICENSE`

- **Component / binary:** `libhookzz.so` (armeabi-v7a and arm64-v8a) — **HookZz**, the
  predecessor of Dobby. **This is a separate binary from `libdobby.so`** and must not be
  satisfied by the Dobby license/binary.
- **License family — RESOLVED AS Apache-2.0 (was recorded "unknown"):**
  - **Project identity / name correspondence (verified, not assumed):** the GitHub
    repository `jmpews/HookZz` now **redirects to `jmpews/Dobby`** — i.e. the project was
    renamed (`README` history contains commit `6e44803096cf`, 2019-12-21,
    "[dobby] hookzz => dobby"). Forks that mirror the original HookZz (e.g.
    `zzscc/HookZz`, `killvxk/HookZz`, `panhongwei/HookZz`, `loveezu/HookZz`, `3kyo0/HookZz`)
    all declare **Apache-2.0**.
  - **LICENSE file history:** the file `LICENSE` in the original project appears at its
    current-master **root commit** `400ad4f2384c` (2018-10-10, "v2 x_x"; 201 additions,
    no parents) and is the **Apache License, Version 2.0**. Both `master` and `dev`
    branches carry the Apache-2.0 `LICENSE`.
  - **Binary-era corroboration:** the packaged `libhookzz.so` strings show it was built
    with **NDK r18** (`ndk-release-r18`, Sept 2018 era) and carries `libhookzz.so` /
    `.note.android.ident` but **no Dobby strings** — consistent with the Apache-2.0 v2
    HookZz tree, and confirming it is byte-distinct from `libdobby.so` (which shows
    `DobbyHook`/`DobbyInstrument` and was built by `zhkl0228`).
  - **Source (byte-verified):** fetched from the original project repo's `LICENSE`
    (`https://api.github.com/repos/jmpews/Dobby/contents/LICENSE`, which is `jmpews/HookZz`)
    and cross-checked byte-for-byte against the mirror
    (`https://api.github.com/repos/killvxk/HookZz/contents/LICENSE`). **Both decode to the
    identical 11356-byte Apache-2.0 text.**
- **Honest residual:** the *exact historical HookZz source revision* that built the
  packaged `libhookzz.so` is still not pinned (the pre-2018 "v1" lineage is not in the
  current repo history). The **license itself is now established as Apache-2.0**, so the
  obligation class is notice-only (Apache-2.0 requires the license text + any upstream
  `NOTICE`); the rebuild-or-drop disposition for the *binary* is a provenance/source
  question and is **not** changed by this file.
- **SHA-256:** `43070e2d4e532684de521b885f385d0841030efa2b1a20bafb76133a5e1379c1`

---

## What this closes and what it does not

- **Closes** the "six missing license texts" item: every permissive guest/hook component
  now has its upstream notice text retained on disk with a pinned source and SHA-256.
- **Does not** close the GPL source obligation (Unicorn engine) — unchanged, still
  pending/blocked per `../../../dependency-sources/upstream-evidence/native-provenance/REPORT.md`.
- **Does not** claim the retained reference revisions prove the historical binary's exact
  source. Per the stated rule, a permissive license needs no precise binary-source
  reconstruction, only a complete, evidenced notice — which is what is retained here.
- **Does not** perform build/test/typecheck, and does not modify the runtime backend, the
  gate, or `native-source-manifest.json`.
