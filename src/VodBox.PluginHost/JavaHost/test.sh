#!/usr/bin/env bash
# 默认转到上游宿主的真实 .NET 单/多 DEX 验收；构建与执行由主 Agent 负责。
# 用法：test.sh [--work DIR] [--skip-build]
#       test.sh --legacy-unit  # 显式运行历史自研宿主的离线单元自测
# 历史自测只证明旧实现的局部行为，不证明上游宿主、native 或真实插件可用。
# 不执行旧 NativeBytecodeRewriteSelfTest；新验收入口不会再调用本脚本，避免递归。
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/../../.." && pwd)"
if [[ "${1:-}" != --legacy-unit ]]; then
  exec bash "$root/build/dex-acceptance.sh" "$@"
fi
shift
if [[ $# -ne 0 ]]; then
  echo "--legacy-unit 不接受额外参数。" >&2
  exit 2
fi
echo "历史自研宿主单元自测：结果不计入当前上游真实验收。" >&2
sources=()
while IFS= read -r -d '' source; do
  sources+=("$source")
done < <(find "$here/src" "$here/test" -name '*.java' -print0)
if [[ ${#sources[@]} -eq 0 ]]; then
  echo "no java sources under $here/src or $here/test" >&2
  exit 1
fi
work="$(mktemp -d "$here/.build-XXXXXXXX")"
trap 'rm -rf "$work"' EXIT
# 编译仅写临时 classes，不构建或覆盖 host.jar；历史依赖须事先就位。
javac --release 17 -encoding UTF-8 -cp "$here/dex-tools/*" -d "$work/classes" "${sources[@]}"
classpath="$work/classes:$here/dex-tools/*:$here/sqlite-jdbc/*"
java -cp "$classpath" dev.vodbox.jarhost.android.AndroidApiSelfTest "$work/classes"
java -cp "$classpath" dev.vodbox.jarhost.HostFtyBootstrapSelfTest
fixture="${VODBOX_DEX_FIXTURE:-$root/tests/VodBox.Tests/fixtures/dex/sample-multidex.zip}"
if [[ -f "$fixture" ]]; then
  java -cp "$classpath" dev.vodbox.jarhost.dex.DexArchiveSelfTest "$fixture"
else
  echo "跳过历史 DexArchiveSelfTest：找不到 $fixture" >&2
fi
java -cp "$classpath" dev.vodbox.jarhost.dex.CompatibilitySelfTest
