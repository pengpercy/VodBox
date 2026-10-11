# unidbg-android 0.9.8 guest / hook native licensing — real facts and minimum
# distribution requirements

This document replaces the earlier "26-library AOSP template" approach with **license
facts actually observed in the retained binaries and upstream license texts**. It is a
research artifact. It performs **no license election, no release authorization, and no
legal opinion**; it states legal *facts* (which license text governs which component,
and what that license requires on redistribution) so a release decision can be made on
evidence instead of a per-file AOSP guess.

Scope: the 34 guest native files packaged inside `com.github.zhkl0228:unidbg-android:0.9.8`
(8 `sdk19/lib` + 9 `sdk23/lib` + 9 `sdk23/lib64` + 8 hook libs), plus the separately
packaged `unidbg-unicorn2` engine. It intentionally does **not** treat the permissive
guest components and the one GPL component as the same obligation.

Evidence base (all retained locally):
- `binary-inventory.json` — per-file SHA-256 and the static strings of each `.so`
  (`upstream-evidence/native-provenance/`).
- `reference-source-archives.json` + `unidbg-android-0.9.8/upstream-reference/*` — real
  upstream license texts.
- `NATIVE-LICENSE-STATUS.md`, `REPORT.md` — provenance gaps.
- Upstream license pages (citations inline).

---

## 1. The single most important distinction

Two **different kinds of obligation** are involved and must not be merged:

| class | components here | what redistribution requires |
|---|---|---|
| **Permissive, notice-only** | bionic libc/libdl/libm/liblog, libc++ (`libstdcpp`/`libcpp`), zlib, **both** the OpenSSL-1.0.1e and BoringSSL crypto/ssl libs, Dobby, whale, xHook | Reproduce the license text + copyright notices (+ disclaimer). **No source obligation.** |
| **Copyleft (GPL-2.0), corresponding-source** | the **Unicorn engine** statically linked into `unidbg-unicorn2`'s `libunicorn.{so,dylib}` / `unicorn.dll` | On binary distribution, provide the **exact corresponding source** of the GPL engine (or the engine's written offer). This is the *only* component here that triggers a full-source obligation. |

The earlier instruction is exactly right: **do not mix the permissive guest libs (BSD/
OpenSSL/ISC/MIT/zlib) with the GPL source obligation.** They are separate. Everything the
emulator *loads as a guest* (`android/…`) is permissive; the thing that is *statically
linked into a host-loaded library* (the Unicorn engine) is the GPL one.

---

## 2. Per-component license facts (grounded, not version-guessed)

### 2.1 `libc.so`, `libdl.so`, `libm.so`, `liblog.so` (sdk19/lib, sdk23/lib, sdk23/lib64)
**License: Android bionic — BSD-family (2-/3-clause) + a few others, permissive.**
Evidence — observed strings in the retained binaries:
- `sdk19/lib/libc.so`: `__bionic_libgcc_compat_hooks`, `ANDROID_PROPERTY_WORKSPACE`,
  `ANDROID_DNS_MODE`, `%s(3) is not implemented on Android` → bionic markers.
- `sdk23/lib/libc.so`: `__bionic_libgcc_compat_symbols`, `jemalloc-1`, `.note.android.ident`,
  `libgcc_compat.c`, `bionic_time_conversions.cpp` → bionic.
- `libdl.so`/`libm.so`/`liblog.so` are bionic linker/math/log shims.

bionic's `libc/NOTICE` carries SPDX `BSD-3-Clause` plus two-clause BSD material
(`.../platform/bionic/+/refs/heads/main/libc/NOTICE`). BSD notice obligations are
satisfied by reproducing the copyright notice, the two/three conditions and the
disclaimer in the distribution — **bionic BSD does NOT require shipping full source.**
This directly answers the "Android bionic BSD is not all full-source" point: only the
notice/disclaimer conditions apply.

### 2.2 `libstdcpp.so` (sdk19, sdk23) and `libcpp.so` (sdk23)
**License: Android bionic libc++ — dual MIT / University-of-Illinois BSD-like, permissive.**
Evidence: `android_set_abort_message`, `Android`, `.note.android.ident`. libc++ is "dual
licensed under both the University of Illinois 'BSD-Like' license and the MIT license …
you may choose to use it under either license" (`android/…/libcxx/LICENSE.TXT`). Notice-only.

### 2.3 `libz.so` (all three groups)
**License: zlib — permissive, notice-only.** Evidence: `zlibVersion`, `incompatible
version`. zlib's license requires the copyright notice + the (binary-form) acknowledgment
that the software is based on the work of Jean-loup Gailly and Mark Adler.

### 2.4 `libcrypto.so` + `libssl.so` — **two genuinely different upstreams**
This is where a version/name assumption would be wrong. The retained binaries show
**two different crypto libraries**, and each is permissive but **not the same text**:

- **sdk23/lib + sdk23/lib64** → **BoringSSL.**
  Evidence: dozens of `external/boringssl/src/crypto/...` and `external/boringssl/src/ssl/...`
  strings plus the literal string `BoringSSL`.
  License: **OpenSSL-license (ISC-style) + Google ISC for new files** — "BoringSSL is a
  fork of OpenSSL. As such, large parts of it fall under OpenSSL licensing. Files that are
  completely new have a Google copyright and an ISC license" (`boringssl/LICENSE`). Permissive;
  reproduce the BoringSSL `LICENSE` text and copyright notices.

- **sdk19/lib** → **classic OpenSSL 1.0.1e**, NOT BoringSSL.
  Evidence: `external/openssl/crypto/...` and `external/openssl/ssl/...` strings, plus the
  explicit version stamps `ASN.1 part of OpenSSL 1.0.1e 11 Feb 2013`, `OpenSSL 1.0.1e
  11 Feb 2013`, `dtls1_version_str`, `ssl2_version_str`, `OPENSSL_config`, `OpenSSLDie`.
  License: **the old dual OpenSSL + SSLeay license** ("The OpenSSL toolkit stays under a
  double license, i.e. both the conditions of the OpenSSL License and the original SSLeay
  license apply"). Both are BSD-style/permissive with an advertising-style acknowledgment,
  **not** GPL. Reproduce the OpenSSL LICENSE text (bundling both the OpenSSL and SSLeay
  notices).

> Consequence for the manifest: **do not** label the sdk19 crypto/ssl "external/boringssl".
> The sdk19 libs are OpenSSL 1.0.1e; only sdk23 is BoringSSL. Both are permissive but the
> retained license text must match the actual library.

### 2.5 Hook libs — four distinct libraries per ABI (8 total)
- `libxhook.so` → **iqiyi/xHook, MIT + BSD-2 (tree.h) + BSD-3 (queue.h); docs CC-BY-4.0.**
  Retained at `upstream-reference/xhook/LICENSE` and `…/xhook/LICENSE-docs`. The historical
  version is **not** proven to be v1.1.12 (both ABIs changed again at `d3a4988c…`), so the
  shipped revision is unresolved — but **whichever revision it is, the license family is
  MIT/BSD and notice-only**, so the *obligation* class is known even though the *exact
  revision* is not.
- `libwhale.so` → **asLody/whale, Apache-2.0.** Retained at `upstream-reference/whale/LICENSE`.
  (The retained text is the current HEAD; historical binary source is not proven, but the
  project license is Apache-2.0.)
- `libdobby.so` → **jmpews/Dobby, Apache-2.0.** Retained at `upstream-reference/dobby/LICENSE`.
- `libhookzz.so` → **HookZz — Apache-2.0 (license now established; name correspondence
  verified, not assumed).** The repository `jmpews/HookZz` was **renamed to
  `jmpews/Dobby`** (README commit `6e44803096cf`, 2019-12-21, "[dobby] hookzz => dobby").
  The original project's `LICENSE` (present from its master root commit `400ad4f2384c`,
  2018-10-10) and every mirror fork (zzscc/killvxk/panhongwei/loveezu/3kyo0) are
  **Apache-2.0**, byte-identical (11356 bytes; SHA-256 `43070e2d…`). Retained at
  `upstream-reference/hookzz/LICENSE`. The packaged `libhookzz.so` is NDK-r18 built and
  byte-distinct from `libdobby.so` (which carries `DobbyHook`/`DobbyInstrument`). **Dobby
  must NOT be assumed to be HookZz's license or its ABI** — they are different binaries —
  but HookZz's own license is Apache-2.0. Exact historical HookZz *source revision* remains
  unpinned (a provenance/source question, not a license question).

### 2.6 `unidbg-unicorn2` engine (`libunicorn.so/dylib`, `unicorn.dll`)
**License: GPL-2.0.** The Unicorn engine (both the 1.0.14 line and the `unicorn2` line)
is GPL-2.0; the JNI bridge on top is Apache-2.0 (unidbg). See
`rebuild/backend-unicorn2/engine-pin.json` (`engine.license = GPL-2.0`) and the retained
`dependency-notices/unicorn-1.0.14/upstream/COPYING`, `COPYING.LGPL2`. This is the
component with a real corresponding-source obligation. Its exact engine commit is
unrecoverable from public evidence (`REPORT.md`), which is why the release is blocked on
*source*, not on notice.

---

## 3. Minimum distribution requirements per class (what actually closes)

### 3.1 Permissive guest libs (all of §2.1–§2.5, i.e. the 26 SDK libs + xHook/whale/Dobby)
Minimum, and *sufficient for the notice obligation*: ship, in the distribution's
third-party notice / corresponding-source bundle, the **upstream license text(s)** for each
component plus its **copyright notice**, and (BSD/OpenSSL) the **disclaimer**. Concretely:
- bionic: BSD-2/BSD-3 `NOTICE` text (reproduce conditions + disclaimer).
- libc++: the Illinois-BSD-or-MIT `LICENSE.TXT`.
- zlib: zlib license text (with the Gailly/Adler acknowledgment).
- OpenSSL 1.0.1e (sdk19): the dual OpenSSL + SSLeay license text.
- BoringSSL (sdk23): the BoringSSL `LICENSE` text.
- xHook: MIT + BSD-2 (tree.h) + BSD-3 (queue.h).
- whale, Dobby: Apache-2.0 (Apache also requires the NOTICE file if the upstream ships one).
**No source delivery is required for these.** This is the honest, closable minimum.

### 3.2 The GPL Unicorn engine
Distinct and stricter: binary distribution of a GPL-2.0 engine statically linked into
`libunicorn.*`/`unicorn.dll` requires **corresponding source of that engine** for the exact
binary distributed (Unicorn's GPL-2.0, no linking exception that erases the obligation for
the statically linked engine). Because the publisher's exact engine commit is not
recoverable, the only paths that close are the ones `REPORT.md` already lists: obtain the
exact source from the publisher, **or** replace with a VodBox-built engine and ship *its*
source (`rebuild/backend-unicorn2/`). Retaining a version string or a guessed commit does
**not** close it.

### 3.3 `libhookzz.so`
License is **Apache-2.0** (established above; text retained at
`upstream-reference/hookzz/LICENSE`), so it *is* satisfiable by notice — reproduce the
Apache-2.0 text (+ any upstream `NOTICE`) and attribute HookZz. The **binary**
nevertheless stays on the rebuild-or-drop provenance path, because the exact historical
HookZz *source revision* that built the packaged `.so` is not pinned. That is a
source/provenance question, not a license question, and does not change the notice
obligation. Do **not** satisfy the binary by copying `libdobby.so` into its place.

---

## 4. Is the license text physically present? (what is retained today)

Already retained under `dependency-notices/`:
- `unidbg-android-0.9.8/upstream-reference/{xhook,whale,dobby}/LICENSE(+LICENSE-docs)` ✅
- `unidbg-android-0.9.8/upstream-reference/{bionic,libcxx,zlib,openssl-1.0.1e,boringssl,hookzz}/`
  — the six texts below ✅ (added; upstream original text + pinned source + SHA-256, see
  `upstream-reference/NOTICE-SOURCES.md`)
- `unidbg-unicorn2-0.9.8/upstream/LICENSE` (Apache bridge) ✅
- `unicorn-1.0.14/upstream/COPYING{,LGPL2}` (GPL engine) ✅

**NOW retained** (previously the six open items):
- bionic BSD `NOTICE` text (libc/libdl/libm/liblog), per SDK era —
  `upstream-reference/bionic/libc-NOTICE.android-6.0.1_r1.txt` (sdk23) and
  `…android-4.4_r1.txt` (sdk19); source aosp-mirror `platform_bionic` tags.
- libc++ `LICENSE.TXT` (libstdcpp/libcpp) — `upstream-reference/libcxx/LICENSE.TXT`
  (dual MIT / Illinois-BSD); source upstream libc++ `llvmorg-3.5.0`.
- zlib license text — `upstream-reference/zlib/zlib-license.txt` (verbatim from `zlib.h` 1.2.8).
- OpenSSL 1.0.1e dual OpenSSL+SSLeay `LICENSE` text —
  `upstream-reference/openssl-1.0.1e/LICENSE` (from the openssl.org 1.0.1e release tarball).
- BoringSSL `LICENSE` text — `upstream-reference/boringssl/LICENSE`
  (AOSP `platform/external/boringssl` tag `android-6.0.1_r1`, `src/LICENSE`: the
  OpenSSL+SSLeay+Google-ISC era text that actually governs the sdk23 binary; the current
  BoringSSL HEAD text, now Apache-2.0, is intentionally NOT substituted).
- HookZz license — `upstream-reference/hookzz/LICENSE` = **Apache-2.0**. The historical
  "license unknown" is **resolved**: `jmpews/HookZz` renamed to `jmpews/Dobby`; the
  original project's `LICENSE` (present from its master root commit `400ad4f2384c`,
  2018-10-10) and every mirror fork are Apache-2.0, byte-identical (11356 bytes). The
  packaged `libhookzz.so` is NDK-r18 built and byte-distinct from `libdobby.so`. Exact
  historical source revision remains unpinned, but the license is established.

Every permissive guest/hook component therefore now has its upstream notice text on disk
with a pinned source and SHA-256 (`upstream-reference/NOTICE-SOURCES.md`). This is the
concrete "licensing material" — **not** a per-file AOSP tag/module guess and not 26 templates.

---

## 5. What this does and does not change

- It **does not** stamp anything as complete. `native-source-manifest.json` keeps
  `corresponding_source_complete=false` for the GPL engine (unresolved source) and the
  rebuild-or-drop provenance for `libhookzz.so` (historical source revision unresolved).
- It **does** correct the *license facts* the manifest records for the guest SDK: sdk19
  crypto/ssl is **OpenSSL 1.0.1e**, not BoringSSL; the permissive-notice class is
  separated from the GPL-engine source class; and `libhookzz.so` was **Apache-2.0**, not
  "unknown".
- It **does** convert the "26-library AOSP template" into a **notice-text checklist** that
  can actually be closed, and the six texts on that checklist are now retained
  (`upstream-reference/NOTICE-SOURCES.md`), leaving the GPL engine as the real blocker.

### Open items (genuine, not hand-waved)
1. Unicorn engine exact corresponding source → GPL closure (blocks; see `REPORT.md`).
2. HookZz exact historical **source revision** → `libhookzz.so` rebuild-or-drop. (Its
   **license** is now established as Apache-2.0; only the source revision is unresolved.)
3. ~~Obtain + retain the six permissive license texts listed in §4.~~ **DONE** — all six
   retained in `upstream-reference/{bionic,libcxx,zlib,openssl-1.0.1e,boringssl,hookzz}/`
   with pinned source + SHA-256 in `upstream-reference/NOTICE-SOURCES.md`.
4. Historical-vs-current revision of xHook/whale/Dobby/HookZz remains unproven (notice
   class known, exact revision not) — record the retained reference revision and the fact
   it is a reference, not a proof.
