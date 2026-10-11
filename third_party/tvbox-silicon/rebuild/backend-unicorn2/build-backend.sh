#!/usr/bin/env bash
# Build the VodBox ARM64 backend JNI library from pinned sources.
# DOES NOT RUN IN THIS TASK. The main agent executes it per RID.
#
# Everything below is traceable to retained material (read, not guessed):
#   * bridge C sources + JNI headers : ../dependency-sources/upstream-evidence/native-provenance/
#                                      release-inputs/backend/unicorn2/src/main/native/
#                                      (unicorn.c, unicorn.h, sample_arm.c, sample_arm64.c, khash.h,
#                                       com_github_unidbg_arm_backend_unicorn_Unicorn{,_NewHook,_Tuple,_UnHook}.h)
#   * per-RID flags                  : the retained upstream *_build.sh in that same directory
#   * static engine archive          : pinned engine is CMake-only (no make.sh/Makefile).
#                                      Linux/Darwin -> <engine>/build/libunicorn.a ; MinGW -> <engine>/build/unicorn.a
#                                      (CMake archive under build/; matches retained upstream scripts that
#                                       link $UNICORN_HOME/build/libunicorn.a)
#   * output library name            : com.github.unidbg.arm.backend.Unicorn2Factory does
#                                      NativeLoader.loadLibrary("unicorn") -> libunicorn.{so,dylib} / unicorn.dll
#
# Prereqs:
#   * fetch-engine.sh has run AND the static engine is built:
#       VODBOX_BUILD_ENGINE=1 rebuild/backend-unicorn2/fetch-engine.sh
#   * JAVA_HOME points at a JDK whose include/ has jni.h + jni_md.h (JDK 17+ recommended).
#     The four bridge JNI headers are retained in the tree, so no javah/javac -h step is
#     required (javah was removed in JDK 10+). If you prefer regenerating them, see
#     gen_headers() at the bottom and note it needs the compiled bridge class on a classpath.
#
# Usage: RID=osx-arm64 rebuild/backend-unicorn2/build-backend.sh
#        RID=linux-x64  rebuild/backend-unicorn2/build-backend.sh
#        RID=win-x64    rebuild/backend-unicorn2/build-backend.sh
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
NATIVE="$HERE/../../dependency-sources/upstream-evidence/native-provenance/release-inputs/backend/unicorn2/src/main/native"
CACHE="${VODBOX_REBUILD_CACHE:-$HERE/.cache}"
ENGINE="$CACHE/unicorn-548eda466eb9a2c69723a0f74888aa7b889b4ed9"
OUT="$HERE/out"

RID="${RID:?set RID to one of: linux-x64 linux-arm64 osx-x64 osx-arm64 win-x64}"

JAVA_INC="${JAVA_HOME:?set JAVA_HOME}/include"
JAVA_PLATFORM_INC="$(dirname "$(find "$JAVA_INC" -name jni_md.h | head -1)")"
[[ -f "$JAVA_INC/jni.h" && -n "$JAVA_PLATFORM_INC" ]] || {
  echo "FATAL: JAVA_HOME/include lacks jni.h / jni_md.h" >&2; exit 1; }

# Bridge sources exactly as listed in the retained upstream *_build.sh files.
BRIDGE_SRCS=(unicorn.c sample_arm.c sample_arm64.c)

cd "$NATIVE"
for f in "${BRIDGE_SRCS[@]}" \
         com_github_unidbg_arm_backend_unicorn_Unicorn.h \
         com_github_unidbg_arm_backend_unicorn_Unicorn_NewHook.h \
         com_github_unidbg_arm_backend_unicorn_Unicorn_UnHook.h \
         com_github_unidbg_arm_backend_unicorn_Unicorn_Tuple.h khash.h; do
  [[ -f "$f" ]] || { echo "FATAL: missing retained bridge input: $NATIVE/$f" >&2; exit 1; }
done

# Static engine archive: Linux/Darwin use libunicorn.a, MinGW uses unicorn.a (Makefile AR naming).
engine_static() {
  if [[ -f "$ENGINE/build/libunicorn.a" ]]; then echo "$ENGINE/build/libunicorn.a"
  elif [[ -f "$ENGINE/build/unicorn.a" ]]; then echo "$ENGINE/build/unicorn.a"
  else echo "FATAL: engine static archive not found under $ENGINE (run fetch-engine.sh with VODBOX_BUILD_ENGINE=1)" >&2; exit 1
  fi
}
[[ -d "$ENGINE/include" ]] || { echo "FATAL: engine headers missing: $ENGINE/include" >&2; exit 1; }
ENGINE_A="$(engine_static)"

mkdir -p "$OUT/$RID"
echo "Bridge: $NATIVE"
echo "Engine: $ENGINE_A"

# Flags are copied from the retained upstream scripts (source_reference in toolchains.json).
# Only additions are the explicit -arch/-mmacosx-version-min notes recorded in toolchains.json.
case "$RID" in
  linux-x64)
    # retained: linux_build.sh
    gcc -o libunicorn.so -shared -O3 -DNDEBUG \
      -I "$ENGINE/include" "${BRIDGE_SRCS[@]}" \
      -I "$JAVA_INC" -I "$JAVA_PLATFORM_INC" -fPIC \
      "$ENGINE_A" -lrt
    mv libunicorn.so "$OUT/$RID/"
    ;;
  linux-arm64)
    # retained: linux_build.sh, cross toolchain for aarch64 (target arch comes from the engine build).
    CC="${CROSS_CC:-aarch64-linux-gnu-gcc}"
    "$CC" -o libunicorn.so -shared -O3 -DNDEBUG \
      -I "$ENGINE/include" "${BRIDGE_SRCS[@]}" \
      -I "$JAVA_INC" -I "$JAVA_PLATFORM_INC" -fPIC \
      "$ENGINE_A" -lrt
    mv libunicorn.so "$OUT/$RID/"
    ;;
  osx-x64)
    # retained: osx_build.sh (uses -m64 and puts the output in osx_arm64/ upstream, which is a
    # known upstream slip). toolchains.json adds -arch x86_64 to make the target explicit.
    xcrun -sdk macosx clang -m64 -arch x86_64 -o libunicorn.dylib -shared -O3 -DNDEBUG \
      -mmacosx-version-min=11.6 \
      -I "$ENGINE/include" "${BRIDGE_SRCS[@]}" \
      -I "$JAVA_INC" -I "$JAVA_PLATFORM_INC" \
      -D_GNU_SOURCE -D_FILE_OFFSET_BITS=64 -D_LARGEFILE_SOURCE -Wall -fPIC -Wno-missing-braces \
      "$ENGINE_A"
    mv libunicorn.dylib "$OUT/$RID/"
    ;;
  osx-arm64)
    # retained: osx_build_arm64.sh
    xcrun -sdk macosx clang -m64 -o libunicorn.dylib -shared -O3 -DNDEBUG -arch arm64 \
      -I "$ENGINE/include" "${BRIDGE_SRCS[@]}" \
      -I "$JAVA_INC" -I "$JAVA_PLATFORM_INC" -fPIC \
      "$ENGINE_A"
    mv libunicorn.dylib "$OUT/$RID/"
    ;;
  win-x64)
    # retained: windows_build.sh -> MinGW gcc, links the engine static archive by its MinGW name.
    x86_64-w64-mingw32-gcc -m64 -o unicorn.dll -shared -O3 -static -DNDEBUG \
      -I "$ENGINE/include" "${BRIDGE_SRCS[@]}" \
      -I "$JAVA_INC" -I "$JAVA_PLATFORM_INC" -fPIC \
      "$ENGINE_A"
    mv unicorn.dll "$OUT/$RID/"
    ;;
  *)
    echo "unknown RID: $RID (win-arm64 is intentionally unsupported; see build/dex-deps.json)" >&2
    exit 2
    ;;
esac

echo "Built $RID -> $OUT/$RID"
shasum -a 256 "$OUT/$RID"/* 2>/dev/null || sha256sum "$OUT/$RID"/*
echo "Record these hashes in dependency-sources/rebuild-lock.json after review."

# ---------------------------------------------------------------------------
# Optional: regenerate the bridge JNI headers. Not needed for a normal build —
# the four com_github_unidbg_arm_backend_unicorn_Unicorn*.h files are retained in
# NATIVE and consumed directly. Use only if you deliberately refresh them.
#
# gen_headers() {
#   classes="$HERE/.cache/javaclasses"          # must contain com/github/unidbg/arm/backend/unicorn/Unicorn.class
#   "${JAVA_HOME}/bin/javac" -h "$NATIVE" -cp "$classes" \
#     com.github.unidbg.arm.backend.unicorn.Unicorn
# }
# ---------------------------------------------------------------------------
