#!/usr/bin/env bash
# Build the VodBox-pinned inline-hook runtimes that replace the packaged guest hook .so files.
# DOES NOT RUN IN THIS TASK. The main agent executes with the Android NDK.
#
# Ground truth read from the retained materials (not guessed):
#   * the packaged jar ships FOUR distinct hook libraries per ABI, not three:
#       android/lib/{armeabi-v7a,arm64-v8a}/{libdobby.so,libhookzz.so,libwhale.so,libxhook.so}
#     (dependency-sources/upstream-evidence/native-provenance/binary-inventory.json)
#   * libdobby.so really is Dobby (strings: DobbyHook/DobbyInstrument). libhookzz.so is a
#     DIFFERENT binary (GCC 4.9 build, libhookzz.so strings) — Dobby is NOT a drop-in
#     substitute for it, so hookzz is handled separately below and fails closed.
#   * xHook has NO CMakeLists in the pinned tree: it builds with ndk-build from
#     libxhook/jni/Android.mk (module "xhook", LOCAL_LDLIBS -llog; Application.mk APP_ABI).
#   * whale builds with CMake from the repo-root CMakeLists.txt (add_subdirectory(whale)).
#   * Dobby builds with CMake; target is SHARED "dobby" -> libdobby.so.
#
# Prereqs:
#   * ANDROID_NDK_HOME set. Pin the NDK r-number at build time and record it.
#   * Retained source archives under dependency-sources/archives/ (see hooks-pin.json).
#
# Usage: rebuild/guest-sdk-hooks/build-hooks.sh
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DEPS="$HERE/../../dependency-sources"
CACHE="${VODBOX_REBUILD_CACHE:-$HERE/.cache}"
OUT="$HERE/out"
mkdir -p "$CACHE" "$OUT"

# Commits are the ones already recorded in hooks-pin.json / dependency-sources/SHA256SUMS.
XHOOK_COMMIT="3f41ae82bc72695208377304b30dc6698621453f"
WHALE_COMMIT="6a661b27cc4cf83b7b5a3b02451597ee1ac7f264"
DOBBY_COMMIT="5dfc8546954ce3b3198132ab13fddb89ee92cdd7"

NDK="${ANDROID_NDK_HOME:?set ANDROID_NDK_HOME}"
# Host tag is derived, not hardcoded to darwin (the old script pinned darwin-x86_64 only).
case "$(uname -s)" in
  Darwin) HOST_TAG="darwin-x86_64" ;;
  Linux)  HOST_TAG="linux-x86_64" ;;
  *) echo "FATAL: unsupported build host $(uname -s)" >&2; exit 1 ;;
esac
TOOLCHAIN="$NDK/toolchains/llvm/prebuilt/$HOST_TAG"
[[ -d "$TOOLCHAIN" ]] || { echo "FATAL: NDK toolchain not found: $TOOLCHAIN" >&2; exit 1; }

# Anchored directory names: the codeload tarballs extract to <repo>-<commit>/, so we resolve
# the real root instead of globbing a name that does not exist.
extract() { # $1=archive $2=destname $3=top-level prefix check
  local a="$1" d="$CACHE/$2"
  [[ -f "$a" ]] || { echo "missing archive $a" >&2; exit 1; }
  if [[ ! -d "$d" ]]; then mkdir -p "$d" && tar -xzf "$a" -C "$d"; fi
  echo "$d"
}

XHOOK_SRC_ROOT="$(extract "$DEPS/archives/xhook-reference-${XHOOK_COMMIT}.tar.gz" xhook)"
WHALE_SRC_ROOT="$(extract "$DEPS/archives/whale-reference-${WHALE_COMMIT}.tar.gz" whale)"
DOBBY_SRC_ROOT="$(extract "$DEPS/archives/dobby-reference-${DOBBY_COMMIT}.tar.gz" dobby)"

# codeload names the single top dir <Repo>-<sha>; locate it explicitly.
top_dir() { find "$1" -mindepth 1 -maxdepth 1 -type d -name '*-*' | head -1; }
XHOOK_SRC="$(top_dir "$XHOOK_SRC_ROOT")"
WHALE_SRC="$(top_dir "$WHALE_SRC_ROOT")"
DOBBY_SRC="$(top_dir "$DOBBY_SRC_ROOT")"
[[ -d "$XHOOK_SRC/libxhook/jni" ]] || { echo "FATAL: xHook tree lacks libxhook/jni" >&2; exit 1; }
[[ -d "$WHALE_SRC/whale" ]] || { echo "FATAL: whale tree lacks whale/ subdir" >&2; exit 1; }
[[ -f "$DOBBY_SRC/CMakeLists.txt" ]] || { echo "FATAL: Dobby tree lacks top-level CMakeLists.txt" >&2; exit 1; }

# unidbg packages armeabi-v7a + arm64-v8a. NDK ABI names map as below.
build_abi() { # $1=ndk_abi  $2=api  $3=outdir
  local abi="$1" api="$2" out="$3"
  mkdir -p "$out"

  # xHook: ndk-build against libxhook/jni/Android.mk (module "xhook" -> libxhook.so).
  # APP_ABI/APP_PLATFORM are taken from the pinned Application.mk (armeabi-v7a, android-14);
  # we override only APP_PLATFORM to the API level VodBox targets and add our ABI.
  ( cd "$XHOOK_SRC" && "$TOOLCHAIN/bin/ndk-build" NDK_PROJECT_PATH=. APP_BUILD_SCRIPT=libxhook/jni/Android.mk \
      NDK_APPLICATION_MK=libxhook/jni/Application.mk \
      APP_ABI="$abi" APP_PLATFORM="android-$api" NDK_OUT="$CACHE/xhook-obj/$abi" NDK_LIBS_OUT="$CACHE/xhook-libs/$abi" -C libxhook/jni )
  find "$CACHE/xhook-libs/$abi" -name 'libxhook.so' -exec cp {} "$out/" \;

  # whale: CMake from the repo root (top CMakeLists does add_subdirectory(whale)).
  # The project uses CMAKE_SYSTEM_PROCESSOR to pick the arch source set, and NDK sets that
  # from ANDROID_ABI, so we only pass the NDK toolchain file + ABI + platform.
  cmake -S "$WHALE_SRC" -B "$CACHE/whale-build/$abi" \
    -DCMAKE_TOOLCHAIN_FILE="$TOOLCHAIN/build/cmake/android.toolchain.cmake" \
    -DANDROID_ABI="$abi" -DANDROID_PLATFORM="android-$api" \
    -DCMAKE_BUILD_TYPE=Release
  cmake --build "$CACHE/whale-build/$abi" -j
  find "$CACHE/whale-build/$abi" -name 'libwhale.so' -exec cp {} "$out/" \;

  # Dobby: CMake from its repo root; target is SHARED "dobby" -> libdobby.so.
  # This replaces the packaged libdobby.so (same artifact name and same upstream project).
  cmake -S "$DOBBY_SRC" -B "$CACHE/dobby-build/$abi" \
    -DCMAKE_TOOLCHAIN_FILE="$TOOLCHAIN/build/cmake/android.toolchain.cmake" \
    -DANDROID_ABI="$abi" -DANDROID_PLATFORM="android-$api" \
    -DCMAKE_BUILD_TYPE=Release \
    -DDOBBY_DEBUG=OFF -DDOBBY_BUILD_EXAMPLE=OFF -DDOBBY_BUILD_TEST=OFF
  cmake --build "$CACHE/dobby-build/$abi" -j --target dobby
  find "$CACHE/dobby-build/$abi" -name 'libdobby.so' -exec cp {} "$out/" \;
}

# The packaged hook set is 2 ABIs. API levels mirror unidbg's guest SDKs (sdk19 / sdk23).
build_abi "armeabi-v7a" 19 "$OUT/armeabi-v7a"
build_abi "arm64-v8a"   23 "$OUT/arm64-v8a"

# ---------------------------------------------------------------------------
# libhookzz.so is NOT rebuilt here on purpose (fail closed).
# Retained evidence shows the packaged libhookzz.so is a distinct binary from libdobby.so
# (different size/mangling/toolchain strings), and the historical HookZz source revision is
# unresolved (upstream-evidence/native-provenance/hookzz-*.json). Shipping libdobby.so under
# the name libhookzz.so would substitute an unverified ABI for an artifact the emulator loads
# by that name. Per rebuild-lock.json, hookzz must either get a pinned source of its own or be
# dropped from distribution. This script therefore leaves it untouched and reports it.
for abi in armeabi-v7a arm64-v8a; do
  echo "UNRESOLVED: $abi/libhookzz.so has no pinned source; do NOT copy libdobby.so over it."
done
# ---------------------------------------------------------------------------

echo "Hook build staged under $OUT (xhook + whale + dobby only)."
echo "Record SHA-256 of each produced .so in dependency-sources/rebuild-lock.json after review."
find "$OUT" -name '*.so' -exec shasum -a 256 {} + 2>/dev/null || find "$OUT" -name '*.so' -exec sha256sum {} +
