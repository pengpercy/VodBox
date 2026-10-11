> **2026-10-11：默认实现直接复用 tvbox-silicon。** 固定提交见 `third_party/tvbox-silicon/UPSTREAM.json`。上游 JavaHost 源码与 `pom.xml` 保持原样，VodBox 在 `vodbox-adapter/` 衔接 JSON IPC。宿主与 Java 包装按 GPL-3.0-only 携带许可和对应源码分发，不改变 VodBox 主项目许可。

# JAR 插件宿主：协议、生命周期与能力边界

默认宿主为固定版本的上游实现。DEX 惰性转换、Android 兼容、FTY loader、native JNI 和字节码兼容由上游承担。`src/VodBox.PluginHost/JavaHost/src/` 与 `test/` 中的自研实现保留为历史代码，不能据其单元自测推导新宿主的真实能力。

**验收尚未全部完成。** 真实 FTY 已走通解密 ClassLoader 并取得 Bili 实例，裸 Bili 首页失败，尚未成功播放。新宿主的单/多 DEX 真实验收、完整 .NET/UI 回归与目标 RID native 行为由主 Agent 执行；本次脚本和文档补丁未执行构建、测试或类型检查。

## 1. 构建与启用

```sh
python3 build/tvbox-host.py
# 兼容旧构建入口，同样直接调用上游构建脚本：
bash src/VodBox.PluginHost/JavaHost/build.sh
# 旧位置参数只接受 host.jar，构建完整组件到它的父目录：
bash src/VodBox.PluginHost/JavaHost/build.sh .cache/dex-acceptance/JavaHost/host.jar
# 推荐明确指定完整组件目录：
bash src/VodBox.PluginHost/JavaHost/build.sh --output .cache/dex-acceptance/JavaHost
```

`build.sh` 不再编译历史 `src/` 并覆盖新宿主。输出为 `host.jar`、`deps/`、`upstream-runtime.json`、`LICENSE-tvbox-silicon` 与 `corresponding-source/`；必须将整个组件一起分发。构建工具、依赖来源、许可和目标 RID JRE 约定见 [JAR-PLUGIN-DEPS.md](JAR-PLUGIN-DEPS.md)。

桌面端通过 `VODBOX_JAR_HOST` 或随包 `JavaHost/host.jar` 定位宿主，Java 可用 `VODBOX_JAR_JAVA` 覆盖。来源标记 `upstream-runtime.json` 使 .NET 选择 `vodbox.bridge.ProcessAdapter` 与同级 `deps/*`，不混入历史 `dex-tools/`。无上游标记的宿主仍存在显式历史兼容路径，但默认构建和验收不使用它。

源提升通过 `SourceRegistry` 的 `enableJarPluginHost` 与 `ConfigLoader` 的 `includeJarPlugins` 接线；开关默认值和 UI 行为以当前 C# 实现及主 Agent 的回归结果为准。配置开关不能代替真实插件可用性验收。

## 2. 协议与生命周期

类 Unix 下当前宿主入口为：

```sh
java -cp 'JavaHost/host.jar:JavaHost/deps/*' vodbox.bridge.ProcessAdapter --serve request.json
java -cp 'JavaHost/host.jar:JavaHost/deps/*' vodbox.bridge.ProcessAdapter --convert convert-request.json
```

Windows 的 classpath 分隔符为 `;`。Java 包装衔接 VodBox 的文件 IPC：

| 阶段 | 契约 |
| --- | --- |
| 启动请求 | `jar`、`cache`、`profile`、`api`、`key`、`ext`、`spider`、`globalSpider`、`origin`、`site` |
| 就绪 | `<cache>/ready.json`，带 `errorCode` 表示启动失败 |
| 命令 | stdin 逐行 `{"id","params"}`，`id` 限 `[A-Za-z0-9-]{1,80}` |
| 响应 | `<cache>/responses/<id>.json`，含结果与错误诊断 |
| 转换请求 | `input`、`output`、`result` 路径 |
| 转换响应 | `ok`、`dexFiles`、`dexVersion`、`classCount` 或错误信封 |

响应通过文件传递，插件日志不能充当协议响应。桌面侧负责请求取消/超时、终止失效进程、站点会话隔离、串行调用及空闲回收；具体默认时限以 `JarPluginHostSettings` 为准。错误保留分类与诊断，失败不能转换成可播放状态。

上游运行路径接收原始插件归档，包括 DEX 与 native 资产，不再通过历史构件装饰器提前转换或拒绝 guard；上游负责自己的加载、转换和执行。历史 C# 转换管线的内容哈希、宿主版本缓存、锁、原子提交等测试仍有各自契约价值，但不能证明新宿主实际成功执行。

## 3. 配置上下文

- 相对 `jar`/`ext` 根据订阅最终重定向后的 origin 解析。
- 全局 spider 和站点 jar 分别保留，有效 JAR 使用站点值优先的规则。
- 站点未建模字段通过 raw site JSON 透传。
- `;md5;<hash>` 是配置声明，不能因保留它就声称下载内容已核验。

这些是桌面适配层契约；其回归应与 JVM/DEX/native 实测分别记录。

## 4. 真实验收与历史单元自测

```sh
# 当前默认验收：上游构建、fixture/hash/oracle 核验、真实 .NET 单/多 DEX：
bash build/dex-acceptance.sh
# Java 测试旧入口默认转到同一验收（无互相递归）：
bash src/VodBox.PluginHost/JavaHost/test.sh
# 显式历史单元自测，需要已准备的历史依赖：
bash src/VodBox.PluginHost/JavaHost/test.sh --legacy-unit
```

当前验收设置 `VODBOX_DEX_ACCEPTANCE_HOST_JAR`，只运行 `RealConverterConvertsSingleDexFixture` 与 `RealConverterConvertsMultiDexFixture`，并要求 TRX 中两个用例均为 `Passed`。缺工具、缺组件、失败或 Skip 均不能记为通过。具体步骤、留证和 JVM 实际加载/方法调用要求见 [DEX-ACCEPTANCE.md](DEX-ACCEPTANCE.md)。

`--legacy-unit` 编译到临时 classes 目录，不生成 `host.jar`。它运行历史 `AndroidApiSelfTest`、假 FTY bootstrap、DEX 结构解析和 `CompatibilitySelfTest`；不运行 `NativeBytecodeRewriteSelfTest`。这些仅证明历史实现的局部行为，不能证明上游 native 执行、真实 FTY 首页或播放。

## 5. 当前能力边界

| 项目 | 当前证据 |
| --- | --- |
| 真实 FTY 解密 ClassLoader、取得 Bili 实例 | 已成功 |
| 裸 Bili 首页 | 失败 |
| 详情、播放解析、实际播放 | 尚未完成成功播放验证 |
| 上游单/多 DEX 真实 .NET 验收 | 入口已接线，执行结果由主 Agent 记录 |
| 完整 .NET/UI 回归 | 由主 Agent 执行并记录 |
| 各目标 RID JRE/native 能力 | 分别实测，不能从构建机结果推导 |

历史自研宿主的“最小 Android 子集”“默认拒绝 native”“不引入 GPL 上游”和“两项自研字节码修补”等描述不再代表默认方案。新宿主依赖原 pom 的 unidbg/unicorn2 及 Android 兼容实现；实际支持程度仍以各插件、各调用阶段与目标平台实测为准。
