#!/usr/bin/env bash
# 把运行时需要的原生库准备到开发输出目录，让 IDE / dotnet run 直接能播视频。
#
# 背景：发布包由 package.py/bundle.py 负责把 libmpv 与脚本运行库打进产物，
# 但 `bin/<Config>/net10.0` 里没有它们，于是开发运行会以
# "DllNotFoundException: Unable to load shared library 'vodbox-mpv'" 告终。
#
# 用法：
#   bash build/dev-natives.sh                 # 自动识别当前 RID
#   bash build/dev-natives.sh osx-x64         # 指定 RID
#   bash build/dev-natives.sh osx-x64 --from /path/to/native-dir
#
# 准备好的库放在 .cache/dev-natives/<rid>/，Desktop 项目在构建时会自动复制到输出目录。
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
rid="${1:-}"
shift || true
from_dir=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --from) from_dir="${2:?--from 需要目录}"; shift 2;;
    *) echo "未知参数：$1" >&2; exit 2;;
  esac
done

if [[ -z "$rid" ]]; then
  case "$(uname -s)-$(uname -m)" in
    Darwin-x86_64) rid=osx-x64;;
    Darwin-arm64)  rid=osx-arm64;;
    Linux-x86_64)  rid=linux-x64;;
    Linux-aarch64) rid=linux-arm64;;
    MINGW*|MSYS*|CYGWIN*) rid=win-x64;;
    *) echo "无法识别当前平台，请显式传入 RID。" >&2; exit 2;;
  esac
fi

case "$rid" in
  osx-*)   mpv_name=libmpv.dylib;    qjs_name=libvodbox_quickjs.dylib;;
  linux-*) mpv_name=libmpv.so.2;     qjs_name=libvodbox_quickjs.so;;
  win-*)   mpv_name=libmpv-2.dll;    qjs_name=vodbox_quickjs.dll;;
  *) echo "未知 RID：$rid" >&2; exit 2;;
esac

stage="$root/.cache/dev-natives/$rid"
mkdir -p "$stage"

# 候选来源：显式目录 → 已构建的原生目录 → 之前的开发缓存。
search_dirs=()
[[ -n "$from_dir" ]] && search_dirs+=("$from_dir")
search_dirs+=("$root/artifacts/publish/$rid" "$root/artifacts/native/$rid" "$root/artifacts/quickjs/$rid" "$root/.cache/quickjs-build" "$stage")

copy_first() {
  local name="$1" dest="$2"
  for dir in "${search_dirs[@]}"; do
    if [[ -f "$dir/$name" ]]; then
      if [[ "$dir" != "$dest" ]]; then cp -f "$dir/$name" "$dest/$name"; fi
      if [[ "$name" == "$mpv_name" && -d "$dir/lib" && "$dir" != "$dest" ]]; then
        mkdir -p "$dest/lib"
        cp -Rf "$dir/lib/." "$dest/lib/"
      fi
      echo "  $name ← $dir"
      return 0
    fi
  done
  return 1
}

echo "准备原生库：rid=$rid 目标=$stage"
missing=0
copy_first "$mpv_name" "$stage" || { echo "  ✗ 缺少 $mpv_name（可用 --from 指定目录，或先构建媒体运行时）"; missing=1; }
copy_first "$qjs_name" "$stage" || { echo "  ✗ 缺少 $qjs_name（先执行 python3 build/quickjs/build.py $rid <输出目录>）"; missing=1; }

if [[ $missing -ne 0 ]]; then
  echo
  echo "未准备完整，开发运行仍会缺少原生库。" >&2
  exit 1
fi

# 直接同步到已有的开发输出目录，省去重新构建（IDE 里改完就能跑）。
copied=0
for out in "$root"/src/VodBox.Desktop/bin/*/net10.0; do
  [[ -d "$out" ]] || continue
  cp -Rf "$stage/." "$out/"
  echo "已复制到 $out"
  copied=$((copied + 1))
done

cat <<EOF

完成。$([[ $copied -gt 0 ]] && echo "已同步到 $copied 个开发输出目录。" || echo "尚无开发输出目录，下次构建会自动复制。")
临时替代方案：设置 VODBOX_MPV_LIB 与 VODBOX_QUICKJS_LIB 指向库文件。
EOF
