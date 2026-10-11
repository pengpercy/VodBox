#!/usr/bin/env bash
# 默认构建固定版本的上游 JavaHost + VodBox IPC 适配器，不编译历史 src/。
# 用法：build.sh [输出目录/host.jar]，或 build.sh --output 输出目录
# 输出目录同时包含 host.jar、deps/、来源标记、许可与对应源码。
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/../../.." && pwd)"
if [[ $# -gt 0 && "$1" != --* ]]; then
  if [[ $# -ne 1 || "$(basename "$1")" != host.jar ]]; then
    echo "旧位置参数须为 <输出目录>/host.jar；指定目录请用 --output <目录>。" >&2
    exit 2
  fi
  exec python3 "$root/build/tvbox-host.py" --output "$(dirname "$1")"
fi
exec python3 "$root/build/tvbox-host.py" "$@"
