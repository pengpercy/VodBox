# Native backend rebuild path (VodBox-managed, independently pinned)

Purpose: produce a **defensible corresponding-source position** for the native code that
VodBox actually distributes, without claiming that any old upstream prebuilt binary maps to
an exact public source revision it does not.

This directory is deliberately *not* part of the shipped host jar and *not* wired into
`build.yml` / the release pipeline. It is the reproducible recipe that a later, separately
authorized implementation task can execute to replace opaque upstream prebuilt natives with
binaries VodBox builds itself from pinned public sources.

## 1. What this path does and does not assert

- It does **not** assert that the upstream `unidbg-unicorn2:0.9.8` prebuilt
  `natives/*/libunicorn.{so,dylib}` / `unicorn.dll` were built from any specific public
  commit. Public evidence (see `../dependency-sources/upstream-evidence/native-provenance/REPORT.md`)
  shows the release build scripts link an *external, unpinned* `~/git/unicorn` static library
  and the engine commit is **not** recoverable from the retained release, its gitlinks, its
  CI, or its tags.
- It does **not** assert that the upstream `unidbg-android:0.9.8` guest SDK / hook `.so`
  files were built from a specific Android platform manifest or hook revision. Same reason:
  the SDK/hook binaries were imported, not built from a pinned manifest in the release tree.
- It **does** give a from-pinned-public-source recipe for each distributed native artifact,
  built by VodBox, whose inputs, toolchain and produced SHA-256 VodBox controls and records.

The correct, honest claim after running this path is therefore:

> "VodBox's distributed native backend and guest SDK/hook libraries are built by VodBox
> from the pinned public sources and toolchains recorded in `../dependency-sources/rebuild-lock.json`.
> We no longer distribute the unresolved upstream prebuilt natives."

That is a *replace*, not a *prove-the-old-binary*. `REPORT.md` §"proposed formal release gate"
already says a guessed commit cannot retrospectively prove old publisher provenance; this
path takes the second option the report lists (replace with an independently pinned build).

## 2. Two independent artifacts to satisfy

### A. `backend-unicorn2` — the ARM64 emulation backend JNI library

Distributed artifacts (currently opaque): `natives/{linux_64,linux_arm64,osx_64,osx_arm64,windows_64}/`
inside the `unidbg-unicorn2` jar. `windows_arm64` is intentionally absent (see
`build/dex-deps.json` → `nativeCoverage.backendRids.unsupported`).

Pinned rebuild inputs (all public, all license-clean):
- **JNI bridge C glue** — taken **verbatim from the unidbg v0.9.8 release tree**, which is
  already retained: `../dependency-sources/upstream-evidence/native-provenance/release-inputs/backend/unicorn2/src/main/native/`
  (`unicorn.c`, `unicorn.h`, `sample_arm.c`, `sample_arm64.c`, khash.h, the `_Unicorn.h`
  headers). These are the *bridge*, not the engine; matching the release tree here is
  legitimate because the bridge ships as source in the release.
- **Emulation engine** — `zhkl0228/unicorn`, branch **`unicorn2`**, pinned by **commit**
  (`548eda466eb9a2c69723a0f74888aa7b889b4ed9`), built statically via **CMake**
  (`cmake -B build -DBUILD_SHARED_LIBS=OFF -DUNICORN_ARCH="arm;aarch64" . && cmake --build build`).
  The pinned tree is CMake-only (no `make.sh`/`Makefile`); the `make.sh` line in the retained
  `*_build.sh` header is a stale Unicorn1 copy-paste. The bridge needs the Unicorn2 Control API
  (`uc_ctl_set_cpu_model`, `uc_ctl_remove_cache`, `uc_ctl_request_cache`, `UC_CPU_ARM_CORTEX_A15`,
  `UC_CPU_ARM64_MAX`), which the earlier Unicorn1 pin (`15cad005`, tag 1.0.14) did not provide —
  see `engine-pin.json.pin_correction`. See `rebuild/backend-unicorn2/engine-pin.json`.
  - Note the engine choice: the upstream bridge abstracts over the Unicorn C API. Building
    the bridge against a **pinned `zhkl0228/unicorn` commit** reproduces the *same ABI*
    the bridge expects. It will not bit-match the old opaque `.so`, and it is not claimed to.
- **Toolchains** — pinned per platform in `rebuild/backend-unicorn2/toolchains.json`
  (macOS: `xcrun clang` with `-mmacosx-version-min`/`-arch`; Linux: `gcc`; Windows: MinGW
  `gcc -m64 ... -static`). The retained upstream scripts are the reference for flags; the
  numbers VodBox selects are recorded, not inherited silently.

Run: `rebuild/backend-unicorn2/fetch-engine.sh` then `rebuild/backend-unicorn2/build-backend.sh`.
The build script is **not executed here** — main agent runs it. Output natives land in
`rebuild/backend-unicorn2/out/<rid>/`.

Verified against the retained materials while scripting (not guessed):
- **Static engine archive location/name.** The pinned engine is CMake-based: its archive lands
  in the CMake output dir `<engine>/build/` — `libunicorn.a` on Linux/Darwin, `unicorn.a`
  (no `lib` prefix) on MinGW. This is what the retained upstream scripts link
  (`$UNICORN_HOME/build/libunicorn.a`). `fetch-engine.sh` builds it and `build-backend.sh`
  picks whichever name exists under `build/`.
- **Output library name.** `com.github.unidbg.arm.backend.Unicorn2Factory` calls
  `NativeLoader.loadLibrary("unicorn")`, so the outputs are `libunicorn.so` / `libunicorn.dylib`
  / `unicorn.dll` (confirmed from the pinned unidbg-unicorn2:0.9.8 sources jar).
- **JNI headers.** The four `com_github_unidbg_arm_backend_unicorn_Unicorn*.h` are retained in the
  release-inputs native tree and consumed directly. `javah` was removed in JDK 10+, so the build
  no longer depends on it; regeneration via `javac -h` is documented as optional.
- **Five RIDs.** `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`, `win-x64` (matching
  `build/dex-deps.json` → `nativeCoverage.backendRids`; `win-arm64` is intentionally unsupported).

### B. `guest-sdk-hooks` — the 34 Android guest libraries

Distributed artifacts: 34 files — sdk19/lib (8), sdk23/lib (9), sdk23/lib64 (9), and 8 hook
libs. These are *guest* libcs and inline-hook runtimes that the emulator loads, not code VodBox
executes natively.

**Correction (read from the retained jar, not assumed):** the 8 hook libs are *four distinct
libraries per ABI*, not three —
`android/lib/{armeabi-v7a,arm64-v8a}/{libdobby.so,libhookzz.so,libwhale.so,libxhook.so}`.
`libdobby.so` is genuinely Dobby (it carries `DobbyHook`/`DobbyInstrument`), while `libhookzz.so`
is a **different binary** (GCC 4.9 build, `libhookzz.so` strings). Dobby is therefore the pinned
source for `libdobby.so` *only*. It is **not** a substitute for `libhookzz.so`: the historical
HookZz revision is unresolved, so `libhookzz.so` fails closed (rebuild-or-drop) and
`build-hooks.sh` will never copy `libdobby.so` over it.

`REPORT.md` establishes that:
- HookZz / SDK libc trace to unidbg's initial import (`d86632f1…`), i.e. imported binaries.
- xHook was upgraded to v1.1.12 (`3f41ae82…`) but **both ABIs changed again** at
  `d3a4988c…` with no source/build provenance — final revision not provable.
- SDK `.so` contain BoringSSL/OpenSSL/build hints that cannot select a platform manifest.

Exact per-library source closure for the *historical* binaries is therefore not reachable
from public evidence. The reproducible replacement is per-library:
- **xHook** (`libxhook.so`) → build the MIT/BSD `iqiyi/xHook` runtime from a pinned commit
  (`rebuild/guest-sdk-hooks/hooks-pin.json`). Its pinned tree has **no CMakeLists**; it builds
  with `ndk-build -C libxhook/jni` (module `xhook`, `LOCAL_LDLIBS := -llog`).
- **whale** (`libwhale.so`) → build `asLody/whale` (Apache-2.0) via its repo-root `CMakeLists.txt`
  (`add_subdirectory(whale)`) from a pinned commit.
- **Dobby** (`libdobby.so`) → build `jmpews/Dobby` (Apache-2.0) via CMake (`SHARED` target
  `dobby`) from a pinned commit.
- **HookZz** (`libhookzz.so`) → **fail closed**; no pinned source is established, and Dobby is
  not its ABI. Rebuild from a pinned HookZz revision or drop the file.
- **SDK libc / libcrypto** → these are the hard part. A defensible path is to source from a
  pinned **AOSP release tag** rather than a guessed vendor blob; see
  `rebuild/guest-sdk-android/README.md` and `aosp-pin.json` (now covering all 26 concrete files).
  If VodBox cannot pin AOSP source for a given `.so`, that library must be **dropped from
  distribution** (fail closed), per `REPORT.md` gate item 5.

Run scripts under `rebuild/guest-sdk-hooks/` and `rebuild/guest-sdk-android/`. Not executed here.
`build-guest-sdk.sh` is a read-only disposition gate: it enumerates the real 26 guest `.so` from
the retained jar, checks each against `aosp-pin.json`, writes `out/disposition.json`, and builds
nothing while the pin is incomplete.

## 3. Compatibility layer is preserved

VodBox keeps reusing the upstream **JavaHost** and the small `vodbox-adapter`
(`ProcessAdapter.java`) unchanged. Nothing in this rebuild path touches the Java host, the
Java IPC protocol, or the `dexs/` loading contract. The native artifacts are drop-in
replacements behind the same JVM-visible API (`com.github.unidbg.arm.backend.unicorn.Unicorn`),
so no compatibility shim is rewritten. Where a rebuilt native is dropped in, the only change
is the bytes on disk plus an updated hash in the lock file.

## 4. Fail-closed gate (unchanged, restated)

`../dependency-sources/native-source-manifest.json` keeps `corresponding_source_complete=false`
until **every** distributed native entry either (a) has an executed, recorded VodBox rebuild
with matching output hash, or (b) is removed from the distribution. The rebuild path here is
the *means* to reach (a); it is not itself a closure claim.

## 4a. One deliberate wiring gap (for a later, separately authorized change)

`build/tvbox-host.py` currently copies `dependency-sources` and `dependency-notices` into the
shipped `corresponding-source/`, but **not** this `rebuild/` tree (it copies only the two
names in its loop). So this recipe does not yet travel inside the distributed component.
Adding `rebuild` to that copy loop is a one-line change in **release plumbing that this task
is explicitly not allowed to touch**. It is listed here as the single follow-up needed to make
the recipe part of the shipped corresponding-source, once the swap itself is authorized.

## 5. Boundaries observed while authoring this path

- No build, test, typecheck, or compile was run.
- No changes to `build.yml`, `build/*.py`, release code, or the default runtime backend.
- No dependency version bumps; the pom still pins `unidbg-android:0.9.8` /
  `unidbg-unicorn2:0.9.8` until a separately authorized swap.
- No claim that the upstream Java sources cover native source.
 Boundaries observed while authoring this path

- No build, test, typecheck, or compile was run.
- No changes to `build.yml`, `build/*.py`, release code, or the default runtime backend.
- No dependency version bumps; the pom still pins `unidbg-android:0.9.8` /
  `unidbg-unicorn2:0.9.8` until a separately authorized swap.
- No claim that the upstream Java sources cover native source.
