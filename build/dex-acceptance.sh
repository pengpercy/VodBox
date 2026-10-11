#!/usr/bin/env bash
# 新默认上游宿主的真实 .NET 单/多 DEX 验收，由主 Agent 执行（需 .NET SDK、JDK 17+）。
# 用法：bash build/dex-acceptance.sh [--work DIR] [--skip-build]
# --skip-build 只复用完整上游组件，不接受历史自研 host.jar。
# fixture 哈希与 baksmali 独立核验后，显式指定宿主运行两个真实转换用例。
# 失败、未发现用例或 Skip 均不算验收通过；不运行历史 native 改写测试。
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
work="$root/.cache/dex-acceptance"
skip_build=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --work) work="${2:?--work 需要目录}"; shift 2;;
    --skip-build) skip_build=1; shift;;
    *) echo "未知参数：$1" >&2; exit 2;;
  esac
done
mkdir -p "$work"
work="$(cd "$work" && pwd)"
host_dir="$work/JavaHost"
host_jar="$host_dir/host.jar"

echo "== 1/4 构建上游宿主与完整组件 =="
if [[ $skip_build -eq 1 ]]; then
  echo "  复用 $host_dir（接下来核验组件）"
else
  python3 "$root/build/tvbox-host.py" --output "$host_dir"
fi
# 核对来源、适配入口与组件布局；不能只凭遗留的来源标记接受旧 host.jar。
python3 -I - "$host_dir" "$root/third_party/tvbox-silicon/UPSTREAM.json" <<'PY'
import json, pathlib, sys, zipfile
host = pathlib.Path(sys.argv[1])
for name in ('host.jar', 'upstream-runtime.json', 'LICENSE-tvbox-silicon',
             'corresponding-source/pom.xml', 'corresponding-source/UPSTREAM.json',
             'corresponding-source/LICENSE'):
    if not (host / name).is_file():
        raise SystemExit(f'缺少上游组件：{host / name}')
for name in ('corresponding-source/src', 'corresponding-source/vodbox-adapter'):
    if not (host / name).is_dir():
        raise SystemExit(f'缺少对应源码：{host / name}')
expected = json.loads(pathlib.Path(sys.argv[2]).read_text())
for name in ('upstream-runtime.json', 'corresponding-source/UPSTREAM.json'):
    if json.loads((host / name).read_text()) != expected:
        raise SystemExit(f'上游来源不匹配：{host / name}')
if not list((host / 'deps').glob('*.jar')):
    raise SystemExit(f'缺少上游 Maven 依赖：{host / "deps"}')
with zipfile.ZipFile(host / 'host.jar') as archive:
    if 'vodbox/bridge/ProcessAdapter.class' not in archive.namelist():
        raise SystemExit('host.jar 缺少上游协议适配入口，不能用历史宿主验收')
PY
export VODBOX_DEX_ACCEPTANCE_HOST_JAR="$host_jar"
java_bin="${VODBOX_JAR_JAVA:-${JAVA_HOME:+$JAVA_HOME/bin/java}}"
java_bin="${java_bin:-java}"
if ! command -v "$java_bin" >/dev/null 2>&1; then
  echo "找不到 Java（$java_bin）；验收需要 JDK 17+。" >&2
  exit 2
fi
export VODBOX_JAR_JAVA="$java_bin"

echo "== 2/4 核验 .NET 实际使用的仓库 fixture（SHA-256、大小）=="
fixtures="$root/tests/VodBox.Tests/fixtures/dex"
python3 -I - "$fixtures" <<'PY'
import hashlib, json, pathlib, sys
root = pathlib.Path(sys.argv[1])
fingerprint = json.loads((root / 'fingerprint.json').read_text())
for key in ('single', 'multidex'):
    item = fingerprint[key]
    path = root / item['file']
    data = path.read_bytes()
    if len(data) != item['size'] or hashlib.sha256(data).hexdigest() != item['sha256']:
        raise SystemExit(f'fixture 大小或 SHA-256 不匹配：{path}')
    print(f'  OK {path.name}: {item["sha256"]}')
PY
# 生成器的结构自检不替代下一步独立 oracle。
python3 -I "$root/build/dex_fixture.py" --out "$fixtures" --verify-only

echo "== 3/4 baksmali 独立解析单/多 DEX =="
oracle="$work/oracle"
mkdir -p "$oracle"
python3 -I - "$fixtures/sample-multidex.zip" "$oracle" <<'PY'
import pathlib, sys, zipfile
output = pathlib.Path(sys.argv[2])
with zipfile.ZipFile(sys.argv[1]) as archive:
    for name in ('classes.dex', 'classes2.dex'):
        (output / name).write_bytes(archive.read(name))
PY
classpath="$host_jar:$host_dir/deps/*"
check_dex() {
  local input="$1" expected="$2" evidence="$3"
  "$java_bin" -cp "$classpath" org.jf.baksmali.Main list classes "$input" > "$evidence"
  if ! grep -Fqx "$expected" "$evidence"; then
    echo "baksmali 未列出 $expected；证据见 $evidence" >&2
    exit 1
  fi
}
check_dex "$fixtures/sample-single.dex" 'Ldev/vodbox/fixture/Sample;' "$oracle/single.txt"
check_dex "$oracle/classes.dex" 'Ldev/vodbox/fixture/Sample;' "$oracle/multi-first.txt"
check_dex "$oracle/classes2.dex" 'Ldev/vodbox/fixture/Second;' "$oracle/multi-second.txt"

echo "== 4/4 真实 .NET 转换验收（指定上游宿主，拒绝 Skip）=="
results="$(mktemp -d "$work/dotnet-XXXXXXXX")"
dotnet test "$root/tests/VodBox.Tests/VodBox.Tests.csproj" \
  --filter 'FullyQualifiedName~JarPluginDexConversionTests.RealConverterConvertsSingleDexFixture|FullyQualifiedName~JarPluginDexConversionTests.RealConverterConvertsMultiDexFixture' \
  --logger 'trx;LogFileName=dex-acceptance.trx' --results-directory "$results"
python3 -I - "$results" <<'PY'
import pathlib, sys, xml.etree.ElementTree as ET
expected = ('RealConverterConvertsSingleDexFixture', 'RealConverterConvertsMultiDexFixture')
results = []
for path in pathlib.Path(sys.argv[1]).rglob('*.trx'):
    results.extend(ET.parse(path).getroot().findall('.//{*}UnitTestResult'))
for name in expected:
    matches = [row for row in results if row.get('testName', '').endswith(name)]
    if len(matches) != 1 or matches[0].get('outcome') != 'Passed':
        outcomes = [row.get('outcome') for row in matches]
        raise SystemExit(f'真实验收未通过（缺失、失败或跳过）：{name}: {outcomes}')
    print(f'  Passed: {name}')
PY
printf '\n真实 .NET 单/多 DEX 验收通过；宿主：%s\n证据：%s\n' "$host_jar" "$results"
