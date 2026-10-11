# unidbg 0.9.8 native source provenance follow-up

## Result: release remains BLOCKED / source closure pending

Scope: only dependency-sources and dependency-notices. No build, test, typecheck, runtime edits, main todo edits, git commit, or license election. Network retrieval, archive inspection, SHA-256 byte comparison only.

## Established evidence

- unidbg v0.9.8 resolves to c4ced05858a7878f3c68f6df465ae02fa59f6759. Existing immutable release archive is retained. GitHub release body is only `Release 0.9.8`, assets empty; no source-to-binary attestation there (`release.json`).
- Full non-truncated release tree has zero gitlinks and no .gitmodules, .github CI, .travis or Jenkinsfile paths (`release-tree-findings.json`, ../unidbg-tree.json). This statement is limited to the release tree, not every historical branch or external build system.
- Retained release native scripts explicitly use `UNICORN_HOME=~/git/unicorn`, include its headers, and link its local libunicorn.a. The Linux clone comment names https://github.com/zhkl0228/unicorn but does not pin any ref. Actual bridge C/header/build scripts are retained in release-inputs/backend/unicorn2/src/main/native; Java source JARs are not engine source.
- Published Unicorn2 libraries match release archive bytes (prior manifest: 5/5), but platform histories are different: Linux x86-64 last changed at 5ef8f9c9e65589849bbbfe59c39007c662d27f66; macOS arm64 last changed at 99124797200d6e0c09f2fe557a418fe477194bb8; Windows x86-64 introduced at 79f59cfa5b141daf1869148825d1ad4dcbdcab2c. These are UNIDBG commits, NOT Unicorn engine commits. Retained commit API responses do not pin engine inputs.
- Public fork currently has `master`, `unicorn2`, `apple-jit-foreign-runtime` branches (`unicorn-branches.json`). Branch tips observed now, chronology, or an apparent engine version cannot identify the commits used for older published binaries. No current tip is substituted for corresponding source.
- unidbg-android contains 34 guest native files: sdk19/lib 8, sdk23/lib 9, sdk23/lib64 9, hook libraries 8. It does NOT mean SDK/API 34. All 34 indexed JAR entry SHA-256 hashes match the retained release archive (`android-release-byte-comparison.json`).
- HookZz and SDK libc histories trace through unidbg-android -> unidbg-api -> original src/main/resources to initial commit d86632f14361b27367dafc158cf6ae247d5ea63b. Whale traces to `Added whale` dfa30f044b1044571d61e16f0855381beae9a7bf. These identify import history, not upstream source versions or device images.
- xHook was explicitly upgraded to v1.1.12 at fdc2ae9ad04f410627d2414b49bd0994aa8cb161. Annotated upstream tag v1.1.12 peels to 3f41ae82bc72695208377304b30dc6698621453f (xhook-tag-refs.txt). However BOTH ABI binaries changed again at d3a4988c31681db24da2dd1ce3ee9e8f8870ae62 (`Refactor RegisterContext`). Its response gives binary changes without their source patch/build provenance. Therefore v1.1.12 is a grounded historical candidate, not a proved final corresponding-source commit.
- Dobby imported at 58c4a58cff5710371a72b0d4261f17eb1286a000 (`Add Dobby`). Embedded strings reveal a local /Users/zhkl0228/git/Dobby source tree and NDK r20-related runtime paths, not a git revision. HookZz has r18-related runtime paths; Whale has /Users/lody/... source paths. These are hints only.
- SDK binaries contain GCC/Android build hints; sdk23 SSL strings explicitly reference external/boringssl while sdk19 crypto has OpenSSL indicators. API level, compiler string, directory name or OpenSSL version string alone cannot select an Android platform manifest, vendor modifications, or all linked source.

## Actual source and licenses retained, with deliberate limitations

`reference-source-archives.json` lists immutable archives, hashes, sizes and extracted license paths:

- xHook historical v1.1.12 candidate: 3f41ae82bc72695208377304b30dc6698621453f. Full source archive, not Java source JAR. LICENSE specifies MIT plus BSD-2-Clause (tree.h) and BSD-3-Clause (queue.h); LICENSE-docs is separately preserved. Final binary attribution remains pending due to later replacement.
- Dobby current reference: 5dfc8546954ce3b3198132ab13fddb89ee92cdd7, Apache-2.0 text retained.
- Whale current reference: 6a661b27cc4cf83b7b5a3b02451597ee1ac7f264, Apache-2.0 text retained.

Dobby/Whale current references are research materials ONLY, not the historical binaries' source versions. No separate historical HookZz license/version was established. Reference repository root licenses do not prove all statically linked third-party runtime licenses. SDK component source versions and per-file license notices are not established. Neither unidbg's Apache license nor current repository licenses blanket-license the guest binaries.

## Evidence gaps and proposed formal release gate (documentation only)

Do not promote this manifest to complete merely because a release archive, Java source JAR, version string or candidate upstream archive exists. For every distributed platform/ABI native entry require:

1. Artifact/JAR entry and binary SHA-256 mapped to exact immutable upstream source commit(s), all submodule/gitlink targets, source archive SHA-256 and any local patches. For Unicorn2 obtain the engine commit separately for EACH prebuilt platform. A maintainer-provided signed provenance record or auditable build record must reference the binary hash, not just unidbg v0.9.8.
2. Build recipe and inputs: engine configuration, JNI bridge revision, compiler/JDK/NDK/toolchain versions, target/platform flags, static dependencies, generated inputs and install/link steps. For Android SDK imports require device/image fingerprint and immutable platform repo manifest (including vendor forks), then per-library source/link inventory. sdk19/sdk23 labels alone are insufficient.
3. Per-component licenses/copyright/NOTICE and linked dependency licenses, matching the selected historical source. Explicitly resolve HookZz/Dobby/Whale revisions, xHook post-v1.1.12 binary replacement, Bionic/OpenSSL/BoringSSL/zlib/libc++/compiler runtime scope as applicable. Unidbg root Apache is only its own evidence.
4. Corresponding-source package and approved distribution/relink obligations for the actual linked licenses; retained local archives are not automatically a source-delivery mechanism.
5. Gate must fail closed if any distributed native entry is unresolved. Options: obtain exact provenance/source from publisher, replace with an independently pinned and documented build in a separately authorized implementation task, or remove the unresolved binaries from the distribution after scope review. Future reproduction can verify new inputs; rebuilding a guessed commit does not retrospectively prove old publisher provenance.

No implementation of the main release gate, rebuild, or legal decision is performed here. Public evidence inspected in this bounded pass did not resolve the exact external Unicorn engine commit, exact guest SDK source manifests, or final historical hook revisions. Current status remains pending/blocked.

## Retrieval limits

API calls for xHook tags/current xHook HEAD/current Whale HEAD encountered unauthenticated GitHub rate limits. Git ls-remote supplied xHook immutable tag target and Whale current HEAD instead. Some directory-move commit API file lists can be paginated; path-specific histories and release tree/byte inventory are used for stated facts. This is not an exhaustive all-repo/all-CI historical search. Request URLs, raw responses and fixed refs are retained.
