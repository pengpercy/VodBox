#!/usr/bin/env bash
# Fetch, verify and (optionally) statically build the VodBox-pinned Unicorn2 engine
# used by the backend rebuild. Compilation of the JNI bridge is a separate step
# (build-backend.sh); this script only obtains the engine source + static archive.
#
# All paths/flags below were read from the retained materials / the pinned tree, not guessed:
#   * engine pin + archive sha256 : engine-pin.json (commit 548eda46...) + rebuild-lock.json
#   * engine build system         : the pinned tree is CMake-ONLY at this commit
#                                   (CMakeLists.txt present; there is NO make.sh/Makefile).
#                                   The header comment in the retained upstream *_build.sh
#                                   ("UNICORN_ARCHS=\"arm aarch64\" UNICORN_STATIC=yes ./make.sh")
#                                   is a STALE Unicorn1 copy-paste and does NOT apply here.
#   * CMake knobs                 : pinned CMakeLists.txt line 1175 add_library(unicorn ...)
#                                   is STATIC when BUILD_SHARED_LIBS=OFF; line 61
#                                   UNICORN_ARCH cache var (semicolon list).
#   * static archive name/location: CMake output UNDER build/  ->  <src>/build/libunicorn.a
#                                   (MinGW drops the lib prefix -> <src>/build/unicorn.a).
#                                   This matches the retained upstream scripts, which link
#                                   $UNICORN_HOME/build/libunicorn.a.
#
# Usage:
#   rebuild/backend-unicorn2/fetch-engine.sh            # fetch + verify + extract
#   VODBOX_BUILD_ENGINE=1 rebuild/backend-unicorn2/fetch-engine.sh
#                                                       # also run the static engine build
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DEPS="$HERE/../../dependency-sources"

# Unicorn2 line. NOT the Unicorn1 "1.0.14" commit 15cad005 (which lacks the Control API the
# bridge needs: uc_ctl_set_cpu_model / uc_ctl_remove_cache). See engine-pin.json.pin_correction.
ENGINE_COMMIT="548eda466eb9a2c69723a0f74888aa7b889b4ed9"
ENGINE_SHA="c90a485e10674c576c2977ba84afb190277530d74fd43db1ba94afb526928a4b"
ENGINE_URL="https://codeload.github.com/zhkl0228/unicorn/tar.gz/${ENGINE_COMMIT}"
# CMake build (this tree has no make.sh). Static archive via BUILD_SHARED_LIBS=OFF.
# VODBOX_ENGINE_CMAKE_FLAGS carries per-host knobs the caller must supply, e.g. the macOS
# deployment target: the static engine is linked into the bridge library, so its own CMake build
# must share the bridge's -mmacosx-version-min (11.6) or the shipped library's minimum OS is the
# engine's default (the runner SDK) instead of the bundle's 11.6 floor.
ENGINE_CMAKE="${VODBOX_ENGINE_CMAKE:-cmake}"
ENGINE_CMAKE="${ENGINE_CMAKE//\\//}"
ENGINE_CONFIGURE=("$ENGINE_CMAKE" -B build -DCMAKE_POLICY_VERSION_MINIMUM=3.5
  -DCMAKE_BUILD_TYPE=Release -DBUILD_SHARED_LIBS=OFF '-DUNICORN_ARCH=arm;aarch64')
if [[ "${RID:-}" == "win-x64" ]]; then
  CC="${VODBOX_NATIVE_CC:-${CROSS_CC:-}}"
  if [[ -z "$CC" ]]; then
    CC="$(command -v x86_64-w64-mingw32-gcc || command -v gcc)"
  fi
  CC="${CC//\\//}"
  [[ "$("$CC" -dumpmachine)" == "x86_64-w64-mingw32" ]] || {
    echo "FATAL: $CC is not an x86_64 MinGW compiler" >&2; exit 1; }
  GENERATOR="${VODBOX_ENGINE_GENERATOR:-MinGW Makefiles}"
  MAKE="${VODBOX_ENGINE_MAKE:-}"
  if [[ -z "$MAKE" ]]; then
    if [[ "$GENERATOR" == "Ninja" ]]; then MAKE="$(command -v ninja)"
    else MAKE="$(command -v mingw32-make)"; fi
  fi
  MAKE="${MAKE//\\//}"
  ENGINE_CONFIGURE+=(-G "$GENERATOR" "-DCMAKE_C_COMPILER=$CC" "-DCMAKE_MAKE_PROGRAM=$MAKE")
fi
# Existing per-host flags are a whitespace-separated list of CMake options.
ENGINE_EXTRA_FLAGS=()
read -r -a ENGINE_EXTRA_FLAGS <<< "${VODBOX_ENGINE_CMAKE_FLAGS:-}"
ENGINE_CONFIGURE+=("${ENGINE_EXTRA_FLAGS[@]}" .)

# Symbols the bridge C source REQUIRES from the engine. If the pinned tree stops providing
# any of these, the pin is wrong (regressed back to Unicorn1) and we fail closed.
REQUIRED_HDR_SYMBOLS=(
  "include/unicorn/unicorn.h:uc_ctl_set_cpu_model"
  "include/unicorn/unicorn.h:uc_ctl_remove_cache"
  "include/unicorn/unicorn.h:uc_ctl_request_cache"
  "include/unicorn/arm.h:UC_CPU_ARM_CORTEX_A15"
  "include/unicorn/arm64.h:UC_CPU_ARM64_MAX"
)

CACHE="${VODBOX_REBUILD_CACHE:-$HERE/.cache}"
if command -v cygpath >/dev/null 2>&1; then CACHE="$(cygpath -u "$CACHE")"; fi
mkdir -p "$CACHE"
ARCHIVE="$CACHE/unicorn-${ENGINE_COMMIT}.tar.gz"
SRC="$CACHE/unicorn-${ENGINE_COMMIT}"

# Prefer the already-retained immutable archive (offline, hash-checked), else download.
RETAINED="$DEPS/archives/unicorn-${ENGINE_COMMIT}.tar.gz"
if [[ -f "$RETAINED" ]]; then
  echo "Using retained archive: $RETAINED"
  cp -f "$RETAINED" "$ARCHIVE"
else
  echo "Downloading: $ENGINE_URL"
  curl -fL --retry 3 -o "$ARCHIVE" "$ENGINE_URL"
fi

if command -v sha256sum >/dev/null 2>&1; then
  actual="$(sha256sum < "$ARCHIVE" | awk '{print $1}')"
else
  actual="$(shasum -a 256 < "$ARCHIVE" | awk '{print $1}')"
fi
if [[ "$actual" != "$ENGINE_SHA" ]]; then
  echo "FATAL: engine archive sha256 mismatch" >&2
  echo "  expected $ENGINE_SHA" >&2
  echo "  actual   $actual" >&2
  exit 1
fi
echo "OK engine archive verified: $actual"

if [[ ! -d "$SRC" ]]; then
  tar -xz -C "$CACHE" < "$ARCHIVE"
fi
[[ -d "$SRC" ]] || { echo "FATAL: expected extracted source at $SRC" >&2; exit 1; }
echo "Engine source at: $SRC"

# The bridge includes <unicorn/unicorn.h>; confirm the pinned tree actually provides it.
[[ -f "$SRC/include/unicorn/unicorn.h" ]] || {
  echo "FATAL: engine tree missing include/unicorn/unicorn.h (bridge includes it)" >&2
  exit 1
}

# Fail closed if the pinned tree is not really Unicorn2 (i.e. lost the bridge-required API).
for entry in "${REQUIRED_HDR_SYMBOLS[@]}"; do
  hdr="${entry%%:*}"; sym="${entry##*:}"
  if ! grep -q "$sym" "$SRC/$hdr" 2>/dev/null; then
    echo "FATAL: pinned engine tree lacks required symbol '$sym' in $hdr" >&2
    echo "       This tree is not the Unicorn2 line the bridge needs (see engine-pin.json)." >&2
    exit 1
  fi
done
echo "OK engine tree provides all bridge-required Unicorn2 Control-API symbols"

if [[ "${VODBOX_BUILD_ENGINE:-0}" == "1" ]]; then
  echo "Building static engine: ${ENGINE_CONFIGURE[*]}"
  ( cd "$SRC" && "${ENGINE_CONFIGURE[@]}" && "$ENGINE_CMAKE" --build build -j )
  # CMake: static archive lives in the build/ dir; MinGW names it without the lib prefix.
  if [[ -f "$SRC/build/libunicorn.a" ]]; then
    echo "Static engine: $SRC/build/libunicorn.a"
  elif [[ -f "$SRC/build/unicorn.a" ]]; then
    echo "Static engine (MinGW naming): $SRC/build/unicorn.a"
  else
    echo "FATAL: engine build produced no static archive under $SRC/build" >&2
    exit 1
  fi
else
  echo "Engine fetch only. To build the static engine, re-run with VODBOX_BUILD_ENGINE=1,"
  echo "or run manually: ( cd '$SRC' && ${ENGINE_CONFIGURE[*]} && '$ENGINE_CMAKE' --build build -j )"
fi
echo "Next: rebuild/backend-unicorn2/build-backend.sh (see README.md)."
