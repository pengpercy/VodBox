# unidbg-android embedded native licensing: pending

This directory's `upstream-reference/` contains extracted texts from immutable SOURCE archives listed in dependency-sources/upstream-evidence/native-provenance/reference-source-archives.json.

- xHook v1.1.12 / 3f41ae82bc72695208377304b30dc6698621453f: MIT + BSD-2-Clause (tree.h) + BSD-3-Clause (queue.h); documentation license retained separately. Unidbg later replaced both ABI binaries; final source attribution is NOT established.
- Dobby current reference 5dfc8546954ce3b3198132ab13fddb89ee92cdd7: Apache-2.0. This is NOT a claim that this current revision built the historical libdobby.so.
- Whale current reference 6a661b27cc4cf83b7b5a3b02451597ee1ac7f264: Apache-2.0. This is NOT a claim that this current revision built the historical libwhale.so.

Historical HookZz **source revision**, Android SDK platform/component exact tags, static runtime licenses and exact source closure remain unresolved. Do not blanket-apply unidbg Apache-2.0 or these reference licenses to all 34 guest binaries. See the provenance REPORT.md for evidence, gaps and proposed fail-closed release requirements. No license election or release authorization is made.

### Added license texts (bionic / libc++ / zlib / OpenSSL 1.0.1e / BoringSSL / HookZz)

`upstream-reference/` now also retains the six previously-missing permissive license texts,
each as upstream original bytes with pinned source + SHA-256 (see
`upstream-reference/NOTICE-SOURCES.md`):
bionic `libc/NOTICE` (AOSP tags `android-6.0.1_r1` + `android-4.4_r1`),
libc++ `LICENSE.TXT` (dual MIT/Illinois-BSD, `llvmorg-3.5.0`),
zlib license (verbatim from `zlib.h` 1.2.8),
OpenSSL 1.0.1e `LICENSE` (from the openssl.org 1.0.1e tarball),
BoringSSL `LICENSE` (AOSP `external/boringssl` tag `android-6.0.1_r1` `src/LICENSE` —
the OpenSSL+SSLeay+ISC era text that governs the sdk23 binary),
and **HookZz `LICENSE` = Apache-2.0** (the resolved historical "unknown").

## Update: per-component license FACTS (not version guesses)

`GUEST-NATIVE-LICENSE-FACTS.md` (same directory) states, from the strings actually observed
in the retained binaries, the LICENSE that governs each of the 34 guest files and separates
two different obligation classes:

- **Permissive, notice-only** (26 SDK libs + xHook/whale/Dobby): bionic libc/libdl/libm/liblog
  (BSD-family), libc++ (MIT/Illinois-BSD), zlib, OpenSSL 1.0.1e (sdk19 crypto/ssl — the
  retained strings read `external/openssl/...` + `OpenSSL 1.0.1e`, NOT BoringSSL), BoringSSL
  (sdk23 crypto/ssl), MIT/BSD/Apache hooks. → reproduce license text + copyright + disclaimer;
  **no source obligation**. Android bionic BSD does NOT require shipping full source.
- **GPL-2.0, corresponding-source** (the Unicorn engine inside unidbg-unicorn2 only). → the
  actual source obligation. Do not conflate it with the permissive guest libs.

`libhookzz.so` **license is now established as Apache-2.0** (the repo `jmpews/HookZz`
renamed to `jmpews/Dobby`; the project `LICENSE` and every mirror fork are byte-identical
Apache-2.0; text retained at `upstream-reference/hookzz/LICENSE`) — so it is satisfiable by
notice. The exact historical HookZz **source revision** that built the packaged binary is
still unresolved → the binary stays on rebuild-or-drop for provenance, not for license.

Still unresolved: **exact** AOSP tag/module per SDK `.so` (a provenance/rebuild question,
not a license question, since the licenses above are established). The license TEXTS that
were missing are now physically retained; see `upstream-reference/NOTICE-SOURCES.md` and
GUEST-NATIVE-LICENSE-FACTS.md §4.
