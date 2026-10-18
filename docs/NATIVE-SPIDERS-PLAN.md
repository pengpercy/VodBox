# Java 插件转 C# 开发计划

分析日期：2026-10-18。基线：`v3-native-spiders`，`4455ecb`。

本文保留实现前的分阶段计划。2026-10-18 已开始首轮实现，最新进度见 [首轮记录](native-spiders/README.md)；后续按阶段实现和验收。

## 目标与完成口径

通过 REA 和已有提取工具理解订阅指向的 Java/DEX 插件，在 C# 中实现其内容、搜索和播放协议。最终桌面应用、默认构建和安装包均不依赖 JVM、JavaHost、DEX 转换器或 Android native 模拟器。

逆向分析环境可以单独使用 Java/JADX/Ghidra；这不应成为应用运行或默认发布的依赖。保留 QuickJS、libmpv 和现有 C# provider。

兼容范围按“订阅快照 + 实际插件摘要 + api + ext 形态”登记。未知 JAR 的任意代码不会因移除 Java 而自动获得支持。已承诺的目标能力必须完成等价实现；暂不支持的能力应单列为未完成项，不能通过过滤站点或伪造播放地址宣称迁移完成。

## 计划制定时的现状

- `NativeSpiders` 已有 22 个精确 api 注册名，另外有 3 个按站点 key 的映射，以及受限的 AppYs、XPath、XBPQ 适配。这些映射并不等于所有同名插件版本均兼容。
- 主应用 `AppServices` 仍以 `enableJarPluginHost: true` 创建注册表。未适配的 csp 入口会进入 Java 宿主。
- `VodBox.PluginHost.csproj` 仍复制 JavaHost 内容；CI 仍安装 Java 21、构建宿主并校验随包 JRE；`bundle.py` 默认打包 JavaHost。仅修改一个运行时开关或使用 `--skip-java-host` 不足以证明产物已经无 Java。
- 保存的饭太硬 `fty.json`：48 个条目，其中 45 个 csp 条目、36 个不同的精确 csp api。目前 12 个 csp 条目加 3 个 JS key 条目具有原生映射，共 15 个；另外 33 个条目涉及 30 个未注册 api。这里统计的是映射覆盖，不是在线可播率。
- 已有真实 payload：1,181 个 class，原始 JADX 输出和字符串/包装方法简化结果均在 scratchpad。简化结果仍有控制流混淆及无效 Java 声明，适合辅助阅读，不能直接机械翻译为 C#。
- 保存的 Oracle 对照输出包含 8 个解密输入，以及重复短串、长串、中文等加密样例。它证明已有动态观测依据，不代表 C# 通用解码器已经完成。
- `ME.KJ` 会调用 HideUtils 或反射调用原始类的 `decrypt`；T4、App99、AppSx、AppTT 均存在这条路径。加密 ext 是多个入口的共同前置依赖。
- 当前 payload 找不到 XPath 类；这需要检查外层 DEX、初始化映射和宿主提供的类，不能据此直接复用无 Guard 的导航实现。
- 旧 `ROADMAP.md`、`S3-PROTOCOL-PROGRESS.md` 的部分数量及“无 JVM”描述已落后于代码，后续随阶段验收统一更新。

## 证据保存与分析流程

本次使用用户指定的 scratchpad：

`/private/tmp/claude-501/-Users-percy-RiderProjects-VodBox/1f597b25-20cb-4d1a-ab4f-7004793e89e1/scratchpad`

已核对的 SHA-256：

- `fty-jar/fty.jar`：`798d86aba4698a1135c260880f0fe4e37952f53b197138a851c34fe0085fca02`
- `payload/payload.jar`：`218fb47aa2b48411702a7cfafa09353c71612302d4069da9f4d1f6222e7558e7`
- `so/ftyguard_v8.so`：`fac9e3937d9928e437ebff79bc5164df418a6adb212494ef21c273579b045dcb`

每个待移植协议依次保存：入口与调用链、原始方法/字节码位置、关键请求和响应、签名/加密输入输出、C# 对应实现、差分结果。REA Evidence 保留原始 ID 和摘要。仓库只纳入必要的最小 fixture、来源说明和可重复分析入口；大型 payload 与含凭据的运行缓存不直接提交。

REA 作为分析工具使用，不作为产品依赖。已阅读[官方 README](https://github.com/morluto/rea/blob/main/README.md)，本机安装版本为 4.0.1；具体操作以本机 CLI schema 为准。已有 Ghidra 12.1.4 和 JDK 21，分析命令显式设置工具路径与 JAVA_HOME；普通 Java 项目仍按全局约定使用 Java 8。

对反混淆后看似矛盾的分支，回到原始 class/DEX 或 REA 汇编核实。需要运行原插件时使用受控 Oracle，固定输入、响应、时间和随机数；优先复用已有输出，不把实时站点变化误判为 C# 协议差异。

### 制定计划时的验证

- REA `inspect-artifact` 成功核对外层 FTY JAR 的结构与 SHA-256。
- 命令级指定既有 Ghidra/JDK 后，REA 成功返回 `ftyguard_v8.so` 的 `JNI_OnLoad`（`0x1047d0`）伪代码；未修改全局配置。初次探测的本地 socket 限制已通过授权执行解决。这证明分析链可用，不代表已恢复完整加解密算法。
- 重新构建当前源码后，FTY 配置链、App99、Bttwoo、Dm84、Kanqiu 的定向测试共 **32/32 通过**。没有运行全套测试、在线站点或六平台发布验收。
- 工作区除本计划外无产品源码修改。

## 阶段 0：冻结兼容清单和验证基线

交付：订阅/插件清单、最小证据样本、可重复的分析和验收说明。

1. 以保存的饭太硬样本为首个固定目标；补齐宝盒现用快照及每站实际 JAR，记录全局 spider、站点覆盖、重定向后的 origin 和摘要。旧文档中的 AppDrama、AppYd 等单列，不能视为已由这份 FTY payload 覆盖。
2. 为每个条目记录分类、首页、筛选、分页、搜索、详情、多线路、播放解析、请求头、代理、登录和特殊媒体要求；导航类单独验收。
3. 重跑当前源码测试，区分现有缺陷、环境失败与迁移引入的回归。已有历史报告仅作为线索。
4. 固定 REA/JADX/Ghidra 的调用方法，确认 Ghidra 反编译和本地通信可用；使用命令级环境变量，不改用户全局 JDK 设置。

验收：48 个 FTY 条目都有精确状态和证据位置；宝盒新增目标单独登记；没有靠别名合并得到的虚假覆盖数。

## 阶段 1：C# 加密 ext 解码

交付：按已验证插件版本选择的解码器、配置归一化逻辑和独立算法向量测试。

1. 用 REA 追踪 native 方法注册及实际 encrypt/decrypt 实现，核对反射调用和 UTF-8/JNI 字符串语义。
2. 复现算法，而非把 Oracle 已解出的域名、密钥或配置硬编码进站点类。
3. 支持目标协议实际出现的明文 JSON、URL 和加密字符串；限制输入长度，对非法编码和未知版本明确报错。
4. 逐个满足已有 8 个解密向量，再补空串、边界长度、重复输入、中文、异常编码及跨进程确定性验证。往返测试之外必须有原插件的独立期望值。
5. 保留原始 api/ext 和订阅 origin，归一化只生成对应协议需要的输入，不全局猜测所有 Base64 都是 FTY 密文。

验收：所有已保存向量及新增边界向量逐字节匹配；C# 路径不启动 Java、不加载 guard SO。

## 阶段 2：优先复用已有 HTTP 协议

建议每个协议独立提交，顺序为 T4 → App99Guard → AppTT。

- **T4Guard（光影、奶酪，2 条目）**：解码 `siteUrl/type/categories/searchMode/playUrl`，对照 payload 的不同 type 分支。已有 `MacCmsSource` 和 `Type4Source` 可复用 JSON/XML 映射及解析逻辑，但 `csp_T4Guard` 不等于 TVBox `type:4`。先覆盖样本实际使用的 type=1 与 web 搜索行为，再扩充其他有证据的分支；验证码分支单列。
- **App99Guard（视界，1 条目）**：解码后与现有 BN v2 实现逐字段核对，包括初始化、签名、IV、压缩响应、详情和解析器；协议一致时复用 `App99Source`，有差异时显式增加版本分支。禁止只登记 Guard 别名。
- **AppTTGuard（热播，1 条目）**：按 payload 的 `/v3/type/*`、`/v3/home/*` 和签名实现新 provider，不冒用 AppGet 协议。

验收：各条目分别完成离线协议差分和在线内容链路；T4 + App99Guard 全部通过后，FTY 原生映射预期从 15 到 18，AppTT 通过后到 19。数量是条件目标，不提前记作完成。

## 阶段 3：AppSx 多版本协议

交付：AppSx provider、两个真实 ext 的初始化及播放差分样本。

- 目标是剧圈、咕咕两个条目，依据真实 init 返回选择分支。
- 核实 `getappapi`、`qijiappapi`、版本/会话/deviceId、RSA 公钥及对称密钥交换、请求签名、验证码和解析器行为。
- 用实例级会话和凭据存储承载必要状态；不引入 Android SharedPreferences、Handler 或宿主反射依赖。
- 如两个样本只覆盖其中部分分支，明确记录其余分支未验收。

验收：两个目标均跑通分类 → 列表/搜索 → 详情 → 播放解析和媒体读取；验证重新初始化、取消、过期会话及失败原因。条件覆盖目标为 FTY 21/48。

## 阶段 4：独立 HTTP、HTML、音频和直播入口

以下是分批候选顺序；每批先读完整播放依赖，再按实际复杂度调整：

1. HTML/JSON 点播：Auete、Jpys、NewCz、YCyz、JPJ。
2. 音频与直播：Music、Tingshu275、LiveGz、Alllive。
3. 其余独立协议：Appgz、Doubao。
4. DouDou 的内容导航行为及 XPathGuard 的实际来源。

复用现有 HTTP 限流、HTML 解析、播放请求与媒体代理能力。`AppgzGuard` 与已有 `GuaziTY` 名称相似不能作为同协议依据。JPJ 返回的非 HLS 地址需核对实际媒体协议；不能把所有返回字符串都视为 mpv 可播 URL。

验收：按真实功能分别核验，不要求无搜索的直播源伪造搜索；导航入口用说明/导航语义呈现。每个 provider 完成正常路径、分页/筛选、异常响应和必要的时效地址刷新，再登记精确 api。

## 阶段 5：网盘共用能力与搜索入口

2026-10-18 用户补充范围：**需要网盘账号的能力全部跳过**。不再索取或使用账号、Cookie、token。涉及登录、转存或鉴权前置的网盘线路记为“按用户要求跳过”；无需账号的站内直连、公开播放能力仍继续分析。跳过不计为已实播通过。

这批必须先做共用依赖，避免每个站点重复实现登录和文件解析。

1. 从 Pan、Cloud_* 和代理调用链提取分享链接识别、目录/文件、线路选择、登录/cookie/token、过期刷新、Range、字幕和媒体代理契约。
2. 按目标样本实际使用的网盘逐个实现，凭据按账号/站点隔离；先验收分享链接到可读媒体，再接搜索站。
3. 接入 MyDrive、WoGG、Seedhub、Libvio、S_zps（2 条目）、YpanSo、BpanSo、KkSs、UuSs、PushGuard。
4. PushGuard 在 payload 中继承 Pan，现有 `PushSource` 只覆盖部分 HTTP 行为，必须按能力拆分接入。Libvio 的站内直连与网盘线路分别验收。

验收：每个目标网盘至少有完整的分享解析与播放样本；同时验证凭据缺失、分享失效、目录分页、转存/登录前置要求、刷新与 seek。只显示搜索结果或分享页不能记为可播完成。

## 阶段 6：特殊运行时与媒体链路

- **Nmyswv**：存在 WebView/页面执行依赖。先识别可由静态 HTML/受限 JS 取代的路径；确实依赖浏览器的部分另定桌面方案，不能把 Android WebView 调用照搬过来。
- **SixV**：当前 payload 主要提取 magnet 链接。分类/详情移植不等于播放完成，磁力播放依赖必须单独实现并验收。
- **AiDj**：Java 入口会生成 Python 脚本，继承 YU，脚本中包含特殊媒体处理。需要继续分析实际执行边界和播放前置条件；不能只翻译外层 Java 类，也不能为了去 Java 又给应用增加 Python 必装依赖。
- 阶段 4/5 发现的嗅探、P2P 或特殊媒体要求进入同一待办，按实际证据评估。

验收：每条链路都取得桌面端实际播放证据。若某能力尚无可行替代，记录为迁移阻塞，不能静默删除后将全量兼容标为完成。

## 阶段 7：默认运行、构建与安装包移除 Java

进入条件：阶段 0 登记的目标能力已验收；尚未完成的特殊能力不得用“已移除依赖”掩盖兼容损失。

1. 主应用改为纯原生 provider 路由，删除 Java 进程启动、宿主定位、会话池、DEX/native guard 路径；保留未知入口的明确提示。
2. 在仍有能力缺口时，可以单独提供原生模式用于验证，但不能把它当作全量迁移完成，也不提前移除仍被依赖的默认路径。
3. 移除项目中的 JavaHost 内容复制，删除默认打包 JRE/host/deps/模拟器的逻辑、宿主校验与对应发布资产；清除旧产物后验证，避免复制残留文件。
4. CI 默认路径移除 setup-java、Maven、jlink、DEX/guard 的下载和构建。需要保留的 Oracle 分析工具放到显式、可选的开发工具路径；默认测试不调用 Java。
5. 更新文档、来源说明、许可证清单和对应源码资产逻辑。仅移除实际已不再发布的依赖声明，保留仍在使用组件的许可材料。
6. 验证收藏、历史、源 key、mediaId、episodeId 及播放身份的兼容，避免换 provider 后用户数据失联。

最终验收：

- 默认构建在没有可执行 Java/Maven 的环境成功。
- 六个既有 RID 的安装包通过扫描，无 JavaHost/JRE/JAR/DEX 转换和 Android 模拟执行组件。
- 无 JVM 的运行环境中，完成启动、配置导入、首页、搜索、详情、播放、seek、切集、关闭、历史恢复；观测不到 Java 子进程。
- 保留的 QuickJS 与 libmpv 正常工作；网络失败、取消和不可支持入口均有准确可见的原因。
- 报告安装包体积、首次出内容耗时、搜索耗时和峰值内存的同条件前后对比，不预设收益数字。

## 每个协议的统一验收标准

完成状态依次区分为：已分析、有实现、离线差分通过、在线媒体可读、桌面实播通过。注册成功或返回非空列表不能越级记为实播通过。

- 固定样本中验证请求 URL、方法、body、签名、请求头和解析结果；必要时固定时间/随机数，与原插件逐项比较。
- 对语义必需的分支补异常、取消、分页边界和时效刷新测试，避免只测与实现完全同构的内部细节。
- 在线验收分别记录站点停机、DNS/TLS、登录/验证码和协议缺陷；离线通过与在线失败分别报告。
- 播放解析后读取实际媒体数据；声称桌面实播时需取得 mpv 的解码/进度证据。保存 UA、Referer、Cookie、Range 和代理行为，跨域解析时不得串用凭据。
- 先做最小协议闭环，再注册 `NativeSpiders`、更新配置链计数；一个协议或共用能力一组可回退变更。

## 第一轮实现边界

建议首轮只执行阶段 0、阶段 1，以及阶段 2 的 T4Guard/App99Guard。先交付通用 ext 解码和 3 个条目的真实协议闭环，再推进 AppTT/AppSx；暂不同时展开网盘或删除 Java 宿主。

后续每轮根据验收结果更新此计划和覆盖清单。当前不对全部 30 个 FTY 入口或宝盒额外协议给出固定工期：网盘、验证码、浏览器和特殊媒体依赖尚未完全确定。

## 首轮实现进度（2026-10-18）

- 阶段 0：固定 FTY 48 条目、宝盒当前 50 条目和 10 个不同的有效插件引用；只读保存了载体摘要及 ZIP 清单。保存的 FTY 分析 JAR 与订阅声明 MD5 不一致，已分别记录，未把它当作已认证的相同版本。宝盒插件尚未逐个完成协议分析。
- 阶段 1：C# ext 解码器已实现，匹配原 native Oracle 的全部 20 条记录；REA 方法地址、Evidence ID 和摘要已保留。
- 阶段 2：T4Guard 的 JSON/type=1 分支和 App99Guard 的匿名 BN v2 分支已实现并按 ext 形态门控。保存样本的原生映射从 15 到 18；未知 type、密文和登录变体保留原策略。
- 主测试 674/674、预览测试 259/259 通过，均启用 TreatWarningsAsErrors；最终源码本机 osx-x64 NativeAOT 发布编译通过。未进行六平台安装包验收。
- 视界：分类 18、首页 21、详情 3 条线路，HLS 可读，并经现有媒体代理和无窗口 mpv 解码至 1.1 秒。
- 奶酪：分类 22、首页 20、详情 1 条线路，媒体返回 HTTP 403，无窗口解码超时；光影：分类请求返回 HTTP 503。这两项尚未通过在线播放验收，首轮的三个站点全链路目标仍未全部达成。
- 上述为首轮历史结果；AppTT/AppSx 的后续进展见下节。

## 第二轮核查进度（2026-10-18）

- 当前固定 FTY 样本原生映射 **22/48**；相对迁移前 15 个，新增 7 个条目。映射覆盖约 46%，新增映射占原来 33 个缺口的约 21%，均不代表实播通过率。
- 阶段 0：FTY/宝盒清单及插件摘要已固定；宝盒逐插件协议分析仍未完成。
- 阶段 1：已完成固定版本的 ext 解码和 20 条独立 Oracle 向量验证。
- 阶段 2：AppTT 已实现签名 multipart、分类、首页、分页、搜索、详情及解析；原插件 Oracle 对照和本地 Postman simulation 的真实 HTTP 链路通过。在线热播取得 10 分类、46 首页条目、12 列表条目、搜索 1 条和 2 线路。T4 两站原有 403/503 尚无新增通过证据。
- 阶段 3：AppSx 匿名 v119 已实现，两个真实 ext 均按配置门控接管；原插件九种播放组合对照及本地 Postman mock HTTP 链路通过。剧圈在线 6 分类、140 首页条目、30 列表条目、搜索 20 条和 8 线路；咕咕分类请求失败，之前捕获为 HTTP 526。其他版本/RSA/session 尚未验收。
- 阶段 4：LiveGz 已实现加密赛程、详情、直播地址刷新；Oracle 对照和 Postman simulation HTTP 链路通过。吃瓜在线 4 分类、20 首页条目、3 线路均能刷新并解析，尚无新增成功解码证据。其他独立入口仍待推进。
- 阶段 5：需要网盘账号的能力继续按用户要求跳过；无需账号的公开能力仍待分项核查。
- 阶段 6、7：特殊运行时链路尚未收口；默认运行/构建/打包去 Java 当时未完成，Java 宿主仍保留。（见下方第四轮：Java 宿主已在后续提交中移除。）
- 本轮重新运行主测试 **689/689**、预览测试 **259/259**；Release / TreatWarningsAsErrors=true。可复用探针 Release 构建零警告、零错误。
- 新增探针位于 `tools/native-spiders/Probe`，支持固定样本在线模式和本地 fixture 模式。解码须指定可用的 `VODBOX_MPV_LIB`；一次并发解码复核进程以 137 退出，不能将其记录为站点播放失败或成功。本轮 NativeAOT 发布仍在执行，未提前记为通过。

当前处于“基础解码完成、优先协议已实现、播放验收未收齐”的阶段。下一步先完成热播/剧圈/吃瓜逐进程解码验证及失败定位，再推进阶段 4 的其他公开入口；最终 Java 清理必须等兼容验收收口。

## 第三轮实现进度（2026-10-18）

本轮把阶段 4 的独立入口与阶段 6 的特殊入口收口到注册表，并落地阶段 7 的“原生默认路由”部分。

- 阶段 4：`AueteSource`、`LibvioSource`、`NewCzSource`、`YCyzSource`、`JPJSource`、`JpysSource`（`csp_Jpys`/`csp_JpysGuard`）、`AllliveSource`、`MusicSource` 全部按真实 ext 形态门控并注册；`Nmyswv`、`Doubao`、`Appgz` 一并注册。
- 阶段 6：`SixVSource` + `TorrentStreamServer`（MonoTorrent 3.0.2 / Mono.Nat 3.0.4，loopback Kestrel Range 串流）已实现并按 ext 门控注册；`AiDj` 加密/签名/CENC/本地代理链路已实现并注册。导航与提醒入口 `csp_DouDouGuard`、`csp_XPathGuard` 以信息源登记。
- 阶段 5：需要网盘账号的入口（MyDrive、WoGG、Seedhub、S_zps、YpanSo、BpanSo、KkSs、UuSs、PushGuard）继续按用户要求跳过，未计入已实播通过。
- 阶段 7（部分）：`AppServices` 默认改为纯 C# 原生路由，不再默认启动 JVM；`SourceRegistry` 对未迁移的 csp_ 入口逐项给出「该 Java/JAR 插件尚未迁移为 C# 原生实现，已跳过」的提示。本轮的实现/离线验收范围不变，网盘缺口与 SixV 在线播放在线验收仍未收口。

映射覆盖：固定 FTY 样本 48 条目中，38 个条目提升为原生源（27 个不同 csp_ api + 3 个 .js 键映射），其余 10 个条目为网盘/推送入口。

验证：主测试 739/739、预览测试 259/259 通过（Release / TreatWarningsAsErrors=true）；本机 osx-x64 NativeAOT 发布编译通过（含 MonoTorrent 与 Kestrel）。仍属“有实现/离线通过”，SixV 与新增入口的在线媒体验收、以及六平台安装包验收未完成。

## 第四轮：移除 JavaHost/JAR/DEX，落地纯原生模式（2026-10-18）

提交 `856a4fa` 把阶段 7 的“打包/运行去 Java”收口为事实，而不是开关：

- 删除 `src/VodBox.PluginHost/{JarPlugin,JavaHost}` 全部源码、JAR 宿主协议与随包内容；`SourceRuntime` 去掉 `Jar`，`Models` 去掉 `Spider`/`Jar`/`SiteJson`，`ConfigLoader` 去掉 `includeJarPlugins`。
- `SourceRegistry` 去掉 JAR 宿主分支与 `JarPluginWarnings`；`AppServices` 默认只组合原生 provider。
- 打包与 CI：`bundle.py`/`package.py`/`release_evidence.py`/`release_publish.py`/`ci_lint.py` 不再 stage JRE、`host.jar` 或 DEX 转换器，并新增 `verify_no_java_payload` 守卫——`JavaHost`/`JRE`/`JDK` 路径、`.jar`/`.dex`/`.class`/`.jmod`/`.war` 载荷以及 `java` 启动器一律拒绝发布。
- 随宿主一并删除已孤立的 Java 构建/发布工具链（`build/{jlink_runtime,fetch-dex-deps,dex-deps.json,dex_fixture,tvbox-host,verify_java_host,rebuild_native_backend,native_coverage,release_preflight,source_assets}.py`、`build/dex-acceptance.sh` 及其测试）、`tests/VodBox.Tests/fixtures/dex/`，以及 `docs/{JAR-PLUGIN-HOST,JAR-PLUGIN-DEPS,DEX-ACCEPTANCE}.md`。`third_party/tvbox-silicon/` 作为上游/REA 参考材料保留在树内，不再被任何产品/发布脚本引用。

验证：`dotnet build VodBox.slnx` 0 error / 0 warning；`VodBox.Tests` 570/570、`PreviewTests` 259/259（Release / TreatWarningsAsErrors=true）；CI 相关 Python 打包测试全部通过、`ci_lint` 通过；`osx-x64` NativeAOT 干净发布产物扫描无 `JavaHost`/Java 载荷；隔离数据目录启动冒烟经日志确认“界面初始化完成”，全程无 JVM。

## 第五轮：SixV 与阶段 4 入口在线验收（2026-10-18）

`tools/native-spiders/Probe` 通用化：可直接传任意已登记的 `csp_*` api 名（外加 `sixv`），跑
分类/首页/列表/搜索/详情/解析，`--decode` 经 libmpv 实播、`--seek` 验证 seek。新增受控 swarm 自检
（`Probe torrent --decode --seek`）：本机现场生成合成磁力并播种，确定性验证「磁力元数据 → 文件选择 →
loopback Range → 解码/seek」路径。

- 阶段 6 - SixV（新6V，`https://www.xb6v.com/`）：目录页补结尾斜杠、搜索入口改 `/e/search/so.php`
  （POST→302→result 页）；在线抓取/搜索/详情/磁力解析全部通过，真实磁力会话拿到元数据（trackers=1）；
  loopback Range → libmpv 解码/seek 由受控 swarm 自检证明（`DECODE_OK 1.1s`、`SEEK_OK 3.2s`）。
- 阶段 4 - 8 个入口在线验收：`Auete`、`JPJ`、`Music` 实播通过（libmpv 解码成功）；`Alllive` 解析通过；
  `Libvio`、`NewCz`、`Jpys`、`YCyz` 因出口 IP 地理限制 / SafeLine WAF / Cloudflare 522 受阻，抓取逻辑已按
  Java 语义与在线观察实现，记为“实现完成、环境受阻”。逐站点结论见
  `docs/native-spiders/STAGE4-ONLINE-VALIDATION.md`。

验证：主测试 570/570、预览测试 259/259（Release / TreatWarningsAsErrors=true）。

## 阶段 7 收口：原生模式合入主线与去 Java 验收（2026-10-18）

- 合入：`v3-native-spiders` 以快进方式合入 `main`（`e0fac9f..5070743`），纯原生默认路由成为主线行为，
  JavaHost/JAR/DEX 宿主与随包 JRE 在主线不再存在。
- 发布产物扫描：`osx-x64` NativeAOT 发布并经 `build/bundle.py` 补齐媒体内核后，`bundle.py` 的
  `verify_no_java_payload` 通过——133 个条目中无 `JavaHost/JRE/JDK` 路径、无 `.jar/.dex/.class/.jmod/.war`
  载荷、无 `java/javaw/javac` 启动器。
- 媒体内核自检：`build/verify_mpv_bundle.py osx-x64` 对 `av1-opus.webm`、`h264-aac.mp4`、`hevc-aac.mp4`、
  `vp9-opus.webm` 四种编解码解码/seek 全部 OK。
- 无 JVM 桌面验收：`bash build/test-desktop-smoke.sh <发布产物>` 用合成媒体跑完整 `--ui-smoke --media=`，
  退出码 0，输出 `Native main player: OK | frames=51, independent single-window/main bounds/pause/seek/
  fullscreen/compact restore/resume/automatic-next/history` 与 `Native desktop UI: OK | 7 pages, 9 settings
  sections, light/dark, graceful shutdown`；隔离数据目录日志确认 `VodBox 0.2.10.0 启动` → `界面初始化完成`，
  全程无 JVM 参与。
- 修复：`--ui-smoke` 现设 `ShutdownMode.OnMainWindowClose`。此前默认 `OnLastWindowClose`，而验收会在主窗口
  之外额外创建搜索/设置窗口，导致打印验收结论后进程不退出，`build/test-desktop-smoke.sh` 会一直挂住。
- 环境说明：本机显示器休眠（`IODisplayWrangler` 状态 0）时 Avalonia.Native 会以 `RenderTimer -6661` 失败；
  桌面验收前需唤醒显示（如 `caffeinate -u -t 600`），这不是产品缺陷。

## 后续清理：移除 Java 时代的第三方快照与工具（2026-10-18）

- 删除 `third_party/tvbox-silicon`（JavaHost 运行时、unicorn guard 后端重建脚本、Maven 源码与许可证证据，448 个受控文件）；
  `ci_reuse` 的构建指纹路径与对应单测、`.gitignore` 里的 JVM 产物规则同步移除。
- 删除依赖 JavaHost JDK 的 `tools/native-spiders/ProtocolOracle.java`、未被任何项目引用的 `Avalonia.Diagnostics` 包版本，
  以及 CI 中只为旧 guard 后端服务的 Windows MSYS2/MinGW 工具链步骤。
- 发布门禁里的 Java 载荷黑名单（`verify_no_java_payload`、`release_evidence`）保留，防止旧产物被重新打包。

## 第八轮：宝盒（宝盒接口.top）插件首批原生迁移（2026-10-18）

本轮处理用户指定的新播放源 `https://宝盒接口.top`：抓取其运行配置（50 条目 / 34 个不同 api），
对缺失入口按既有的「REA/反编译 → C# 重写 → 原生注册」流程推进。详尽的逐个协议、差异与在线观测见
[宝盒首批迁移记录](native-spiders/baohe-batch1.md)。

- 移入原生的 5 个入口：`csp_Wwys`（旺旺影视 HTML）、`csp_AppV6`（麦田 JSON）、`csp_TingBook`（六月有声 HTML）、
  `csp_HeMa`（盒马短剧，经 `hema.php` 转发层）、`csp_Feiyu`（飞鱼签名 JSON）。都在 `NativeSpiders.Entries` 中带 ext 闸门。
- 本批 5 个源均为纯 Java 实现（无 native `.so` 校验），故未使用 REA/Ghidra；`bh0822.jar` 中带 native 变体的入口
  （AppTe/AppYd 等）留待后续批次。
- 在线观测：`Wwys` 首页/分类/详情/播放页与播放器页链路 HTTP 200；`AppV6 /v6/nav` HTTP 200；`TingBook` 详情 200、
  取章节接口 500（站点侧）；`Feiyu` 后端 502。`HeMa` 转发层未发起真实请求。
- 覆盖：按该配置的 34 个 api 值，原生从 14 升到 19；仍未迁移的 19 项（含已条件覆盖的 `csp_XPath`/`csp_XBPQ`）
  已在迁移记录中列出。

验证：主测试 607/607、预览测试 265/265 通过（Release / `TreatWarningsAsErrors=true`），新增
`NativeBaoheSourceTests` 11 项离线回归。仍属「有实现/离线通过」，复杂入口（AppTe/AppYd/AppDrama/AppXY/
Czsapp/SaoHuo/Jianpian/Gz360/PianKu8/Tounimabi/KuKuMusic/Douban/FengYe/YunZhenXiang 等）未迁移。
