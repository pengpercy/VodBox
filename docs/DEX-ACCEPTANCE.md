# 上游 JavaHost：DEX 与真实插件验收

默认宿主直接复用 [tvbox-silicon](https://github.com/jo32/tvbox-silicon/tree/b82a0313ceb9db652c4f1e07560a7a603eee75df) 的原 JavaHost 源码与 `pom.xml`，固定提交为 `b82a0313ceb9db652c4f1e07560a7a603eee75df`。DEX 转换、Android 兼容和 native 执行由上游实现承担；VodBox 保留 Java 协议包装，不再另造这些运行时。

**验收尚未全部完成。** 真实 FTY 已成功完成解密 ClassLoader 路径并拿到 Bili 实例，但裸 Bili 首页调用失败，尚未成功播放。主 Agent 正在执行单 DEX、多 DEX 真实验收及完整 .NET/UI 回归；在结果和证据落盘前，不能将这些项目标为通过。本次文档更新未运行构建、测试或类型检查。

## 1. 构建和组件来源

从仓库根目录构建默认宿主：

```sh
python3 build/tvbox-host.py
# 验收使用独立输出目录：
python3 build/tvbox-host.py --output .cache/dex-acceptance/JavaHost
```

构建输入为 `third_party/tvbox-silicon/Runtime/JavaHost/{src,pom.xml}` 与 `third_party/tvbox-silicon/vodbox-adapter/`。上游源与 pom 保持原样，包装层只衔接 VodBox 的 IPC。

需要 JDK 17+ 和 Maven，依赖从官方 Maven Central（`https://repo.maven.apache.org/maven2`）解析。工具自动发现/下载是构建交付要求：当前脚本从 `JAVA_HOME` 或 `PATH` 发现 JDK，自动发现 Maven，缺失 Maven 时下载官方发行并核对 SHA-512；**缺失 JDK 时的自动下载仍需主 Agent 确认实现与验收**。

输出必须同时包含 `host.jar`、`deps/*`、`upstream-runtime.json`、`LICENSE-tvbox-silicon` 和 `corresponding-source/`。GPL 许可与对应源码随宿主组件分发，主项目许可不修改。详见 [依赖与分发约定](JAR-PLUGIN-DEPS.md)。

## 2. 单 DEX、多 DEX 的真实验收

主 Agent 执行统一入口（本次补丁未执行）：

```sh
bash build/dex-acceptance.sh
# 复用独立工作区中的完整上游组件：
bash build/dex-acceptance.sh --work .cache/dex-acceptance --skip-build
# Java 旧测试入口默认转到上述验收，不互相递归：
bash src/VodBox.PluginHost/JavaHost/test.sh
```

脚本直接调用 `python3 build/tvbox-host.py --output <工作区>/JavaHost`，核验组件布局、来源与 `vodbox.bridge.ProcessAdapter` 入口，然后设置绝对路径 `VODBOX_DEX_ACCEPTANCE_HOST_JAR`。它校验 .NET 实际使用的仓库 fixture 的 SHA-256/大小，执行生成器 `--verify-only`，并用上游 `deps/*` 中的 baksmali 独立解析单 DEX 和多 DEX 的两个条目；不改写仓库 fixture，不再 stage 历史 `dex-tools/`。

最后执行 `dotnet test`，只选择 `JarPluginDexConversionTests.RealConverterConvertsSingleDexFixture` 与 `RealConverterConvertsMultiDexFixture`。TRX 必须包含这两个用例且均为 `Passed`；未发现、Skip 和失败都不能算验收通过。每次执行使用独立结果目录，避免旧 TRX 冒充本次结果。宿主、oracle 输出与 TRX 留在工作区；转换产物的留证和进一步 JVM 加载/方法调用由主 Agent 补齐。

需要单独诊断 Java 转换协议时，类 Unix 可使用下面的命令；这不能替代 .NET 接线验收。Windows 的 classpath 分隔符改为 `;`：

```sh
java -cp '.cache/dex-acceptance/JavaHost/host.jar:.cache/dex-acceptance/JavaHost/deps/*' \
  vodbox.bridge.ProcessAdapter --convert .cache/dex-acceptance/single-request.json
java -cp '.cache/dex-acceptance/JavaHost/host.jar:.cache/dex-acceptance/JavaHost/deps/*' \
  vodbox.bridge.ProcessAdapter --convert .cache/dex-acceptance/multi-request.json
```

每份请求包含 `input`、`output`、`result` 的路径，例如：

```json
{
  "input": "/abs/path/fixtures/sample-single.dex",
  "output": "/abs/path/evidence/single.jar",
  "result": "/abs/path/evidence/single.json"
}
```

通过标准：

1. 用独立 oracle（baksmali）确认输入是合法 DEX，并列出预期类。
2. 单 DEX 返回 `ok=true`、`classCount>0`，产物含 `dev/vodbox/fixture/Sample.class`。
3. 多 DEX 返回 `ok=true`、`dexFiles>=2`，产物同时含 `Sample.class` 和 `Second.class`，忽略非 DEX 条目。
4. 用 JVM 实际加载转换后的类并调用 fixture 方法，确认返回值；仅检查 JAR 条目不足以证明可执行。
5. 保存请求、结果信封、oracle 输出、产物及宿主来源信息，失败或跳过必须单独记录。

`build/dex-acceptance.sh` 已接到上游构建与真实 .NET 用例，补丁本身不代表验收已通过。当前 C# 用例检查转换结果、JAR 可读性和预期类条目；上述实际 JVM 加载/方法调用及转换产物完整留证仍需主 Agent 确认。`test.sh --legacy-unit` 是历史自研宿主单元自测，不运行旧 native 字节码改写自测，结果不计入新方案证明。

## 3. Fixture 与回归

`build/dex_fixture.py` 生成真实 DEX，fixture 位于 `tests/VodBox.Tests/fixtures/dex/`：

| 文件 | 内容 | 验收用途 |
| --- | --- | --- |
| `sample-single.dex` | `dev.vodbox.fixture.Sample`，含构造函数、`answer()`、`greet()` | 单 DEX 转换、加载和调用 |
| `sample-multidex.zip` | `classes.dex`（Sample）、`classes2.dex`（Second）、`res/raw/keep.txt` | 多 DEX 与非 DEX 条目处理 |
| `fingerprint.json` | SHA-256、大小、类名 | 输入身份与证据核对 |

`JarPluginDexConversionTests` 的真实转换用例必须对齐新宿主及 `deps/*`，并实际执行成功；缺少运行环境导致的 Skip 不算通过。缓存、并发锁、原子提交、损坏回退等管线测试与真实转换验收分别记录。完整 .NET/UI 回归由主 Agent 执行，本页不提前声明结果。

## 4. 真实 FTY 的验收边界

| 阶段 | 当前结果 |
| --- | --- |
| 解密 ClassLoader 并取得 Bili 实例 | 已成功 |
| 裸 Bili 首页调用 | 失败 |
| 详情、播放解析、实际播放 | 尚未完成成功播放验证 |
| 新默认宿主单 DEX、多 DEX 真实验收 | 主 Agent 正在执行，待结果 |
| 完整 .NET/UI 回归 | 主 Agent 正在执行，待结果 |

解密成功和实例创建成功只证明对应加载路径已走通。首页、搜索、详情、播放解析与实际播放仍需分别留证，不能从初始化成功推导出插件完整可用。平台 native 能力也必须在目标 RID 上实测，JRE 架构验证不能替代 native 后端验证。


2026-10-11 最新主 Agent 验收：正式 `bash build/dex-acceptance.sh` 两项 Passed、零 Skip；完整核心 597/597、UI 238/238、打包闭包 9/9 通过。真实饭太硬 AppTT guard/loader/Spider 初始化成功，首页 10 分类和 47 视频，详情 2 线路、首线 71 集、播放解析 parse=0。被选首个媒体远端 m3u8 HTTP 404，不能把该视频记为成功播放。证据汇总 `.cache/upstream-host-check/acceptance.json`。
