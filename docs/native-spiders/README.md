# 原生爬虫首轮记录

> 本文保留首轮历史结果。当前已推进 AppTT、AppSx 和 LiveGz，原生映射为 22/48；最新状态见 [开发计划的第二轮核查进度](../NATIVE-SPIDERS-PLAN.md#第二轮核查进度2026-10-18)。

日期：2026-10-18；分支基线：`v3-native-spiders` / `4455ecb`。

## 本轮改动

- `FtyExtCodec` 从已定位的 native encrypt/decrypt 恢复 ext 解码：Base64 → RC4 → 严格 UTF-8 → 每个 UTF-16 单元减 2。配置与密文均有限长；仅在已知 FTY 协议入口调用，不全局猜测 Base64。非 BMP 变体明确拒绝。
- `FtyT4Source` 实现目标样本使用的 JSON/type=1 分支：分类限制、首页、分页列表、详情、多线路、API 搜索，以及 Web 搜索先取 ID 再向 API 补齐条目。保留原始配置、源 key 和现有 MacCMS 的分集 URL 身份；显式播放解析地址会进入现有解析链。
- App99Guard 复用匿名 BN v2 的 AES-CBC、SHA256 签名和压缩响应处理，同时读取该变体的 `ua`/`version` 字段及默认 UA。有 token、特殊登录路径或无效配置的定义不会被提升为原生。
- 原生注册先核对 ext，再决定是否接管；没有只凭 Guard 别名扩大支持范围。未适配 T4 type 与登录变体保留既有 JAR/过滤策略。

保存的 FTY 样本由 15 个原生映射增至 18 个。新增的是光影、奶酪、视界三个条目；这不是“18 个站点均已实播”的计数。

## 证据与样本

- [FTY 保存快照清单](fty-saved.json)：与仓库 `fixtures/fty-in.bmp` 的站点定义一致，48 条目。
- [宝盒当前清单](baohe-current.json)：本轮只读抓取的 50 条目、全局与站点级插件引用；没有把旧文档中的覆盖数当作当前结果。
- [插件载体清单](plugin-artifacts.json)：10 个不同引用的摘要与 ZIP 结构；大型载体留在 `.cache/native-spiders-round1-artifacts/`，未提交，也未为此执行宝盒插件。
- [REA 方法证据索引](fty-ext-evidence.json)：包含 native 摘要、Ghidra 版本、encrypt/decrypt/RC4 地址及 Evidence ID。
- `tests/VodBox.Tests/fixtures/fty-ext-v8-vectors.json`：已有 Java/native Oracle 捕获的 8 条解密、12 条加密记录。测试比较解码后 UTF-8 摘要和 UTF-16 长度，不只做 C# 自身往返测试。

保存的 FTY 分析 JAR 的 SHA-256 为 `798d86aba4698a1135c260880f0fe4e37952f53b197138a851c34fe0085fca02`，其 MD5 与保存订阅中的声明不一致。因此它是单独固定的分析样本，不能称为由该 MD5 认证的插件版本。解码结论另有 Oracle 输入输出和当前原生网络调用作为证据；没有加入忽略完整性校验的下载逻辑。

## 验证结果

- 主测试：674/674，通过；包含新协议、真实 ext、非法配置门控、跨域搜索凭据隔离、分页、取消及播放身份回归。
- 桌面预览测试：259/259，通过。
- 两套测试均使用 Release / TreatWarningsAsErrors=true。
- 最终源码的本机 osx-x64 NativeAOT 发布编译通过，TreatWarningsAsErrors=true；产物摘要已记录。未重建 QuickJS，也未做六平台安装包或 AOT 主应用 GUI 验收。
- `git diff --check` 通过。

在线结果见 [结构化验证记录](round1-validation.json)：

- **视界 / App99Guard**：18 分类、21 首页条目、3 条播放线路；HLS 请求 HTTP 206。继续通过现有媒体代理和无窗口 libmpv 解码，position=1.1s、duration=6440.3s。这是原生 provider 的无窗口播放验证，尚非 GUI 全流程验收。
- **奶酪 / T4Guard**：22 分类、20 首页条目、1 条播放线路；媒体 HTTP 403；无窗口解码探针超时。内容链路可用，播放尚未验收通过。
- **光影 / T4Guard**：分类请求 HTTP 503，后续链路未达到。Web 搜索的离线契约已验证，本站实际搜索/播放尚未验收。

这些网络结果只描述本轮观测。403/503 的根因尚未确认，不能直接归为协议无误或站点永久失效。

已做有限请求头对照：奶酪媒体使用原插件 UA 池中的 UA、不加自定义头、以及增加 API Referer，均返回 403；光影 API 使用原插件 UA 仍返回 503。没有足够证据通过改 UA/Referer 修复，故保留为待定位项。

用户更换网络出口后已重新获取内容和播放地址再试：光影分类仍为 503；奶酪仍有 22 分类、20 首页条目，新增搜索实测命中 1 条，媒体仍为 403，无窗口解码复核仍超时。三组媒体请求头对照也没有变化；原生 provider 复测使用了新进程和新取得的播放请求。

## 重复验证

默认 Java 项目环境仍为 Java 8：

```sh
export JAVA_HOME=/Library/Java/JavaVirtualMachines/jdk-1.8.jdk/Contents/Home
export PATH="$JAVA_HOME/bin:$PATH"
dotnet test tests/VodBox.Tests/VodBox.Tests.csproj --no-restore -c Release -p:TreatWarningsAsErrors=true
dotnet test tests/VodBox.PreviewTests/VodBox.PreviewTests.csproj --no-restore -c Release -p:TreatWarningsAsErrors=true
```

本机 REA/Ghidra 使用已安装的 Java 21，不改全局默认 JDK：

```sh
export GHIDRA_INSTALL_DIR=/usr/local/opt/ghidra
export JAVA_HOME="$HOME/Library/Java/JavaVirtualMachines/temurin-21.0.12.jdk/Contents/Home"
export PATH="$JAVA_HOME/bin:$PATH"
rea decompile /absolute/path/to/ftyguard_v8.so 0x10dd9c --provider ghidra --format json
```

产品的这两个新原生路径不调用 Java；整个应用仍保留尚未迁移入口所需的 Java 宿主。下一轮先核对 T4 的 403/503，再按计划处理 AppTT/AppSx；不得将这两个失败站点计为播放完成。
