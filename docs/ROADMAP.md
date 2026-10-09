# VodBox 开发路线图

> 基线：commit `9a67c2d`（clean-slate 重写）+ `ea938e4`。构建 0 错误，21 测试通过。
> 目标：把 FongMi/TV 的能力搬到 Avalonia 12 + .NET 10 NativeAOT + libmpv 桌面端。
> 本文档由能力差距分析生成，按阶段推进，每阶段有验收标准。

## 当前进度快照

**补充状态（2026-10-09，本轮）**：主测试230/230、预览54/54通过。
- NativeAOT `net10.0/osx-x64` 资产缺失阻塞已解除：必须用 Release + RID restore；新增 `build/publish-aot.sh` 固化 restore/publish 参数一致。首次本机 Desktop NativeAOT publish 成功。
- 配置/MacCMS/AppGet/App99 JSON 调用改用显式源码生成元数据，保持统一 Web/数字字符串选项；新增2条离线回归，不压制 IL2026/IL3050。
- 画面比例偏好持久化、打开媒体前恢复，菜单标识当前比例；非法存储值回退原始比例。
- 播放设置增加“不记录播放历史”偏好，切集/结束/关闭不生成新历史；不删除旧历史，不隐藏网络活动，已排队写入不会被追溯取消。
- 本地 macOS 原生 Avalonia/libmpv 渲染探针通过：32帧、position=1.90s、width=640。不是主应用全功能交互验收，也不是网络流验收。
- 构建/测试日志、产物与精确后续事项见 `.alma/`；跨平台发布及网络原生UI仍未验收。
- 画面比例菜单接mpv video-aspect-override（原始/16:9/4:3/1:1），有原生命令替身测试。
- 置顶小窗640×400，可恢复尺寸/最小尺寸/位置/置顶/窗口状态；播放浮层覆盖整个客户区。此为应用置顶窗口模式，不是系统PiP，原生桌面验收待补。
- 音量/倍速偏好持久化并在打开媒体前应用，音量按钮支持静音/恢复；虚拟行容器统一无背景模板，保留卡片交互。
- 控制条播放闲置3秒隐藏；鼠标/键盘活动恢复，暂停、拖动、菜单打开时保持显示；卸载停止计时器。
- 播放列表捕获线路/选集与解析上下文，离开详情后仍可选集；上一集/下一集使用列表边界。
- 只有mpv EOF触发自动下一集（失败/停止不触发），旧会话与重复EOF不推进；可关闭且偏好持久化。
- EOF历史写入零续播点，旧记录达到已知时长时从头播放；未知时长保留原位置。
- 进度滑杆改成独立预览位置，拖动/键盘释放提交seek；播放切换取消旧手势，播放进度更新不触发seek。
- 播放暂停图标与倍速跟随真实状态，删除固定的分辨率/编码角标。
- 已补旧SQLite迁移及详情/播放切换竞争保护；完整播放体验仍在进行中。
- 首页、点播、搜索、详情、收藏、历史海报均接入共享缓存：64MiB/128张预算、4并发、租约保护与空闲LRU淘汰；真实站点图片与原生缩放验收待补。
- 统一MDI图标，去重logo，清理时钟；标题栏/侧栏拖拽角色通过无头命中检查，原生拖动待验收。
- 首页保存默认源身份，横幅和推荐打开同一真实源；旧首页加载不覆盖新推荐/历史/横幅，显示真实站名。
- 搜索任务新增取消与代次门控；可见卡片与站点归属使用同一保留集合，修复第40条后卡片无法打开。
- 搜索结果按宽度重排并保留行虚拟化，移除结果整行hover/选中背景与重复搜索框。
- 详情来源、集数、播放/续播、收藏文字使用实际数据，倒序接入；去掉伪造已看与快捷换站结果。
- 播放设置按钮已接入mpv实际音轨/字幕菜单及关闭字幕；菜单点击前门控播放代次。多轨真实媒体验收、外部字幕加载仍待补。
- 新接播放快捷键：空格暂停、左右5秒、上下音量5、F/Enter全屏、Esc退出全屏/关闭；编辑控件保留自己的按键。原生键盘完整验收待补。
- 下文旧快照和工作量仅作历史参考，不能据其推断当前测试数或源覆盖率。


**代码量**：约 2500 行托管代码，4 个项目（Core / Infrastructure / Playback.Mpv / Desktop）+ 21 项测试。

**已跑通（A 类）**
- TVBox 配置加载：远程拉取、JS 变量包裹、`//` 注释剥离、JPEG 尾部 base64 隐写（饭太硬真实样本回归通过）
- MacCMS v10 JSON 采集：分类 / 分页 / 搜索 / 详情 / `vod_play_from$$$vod_play_url` 线路拆分
- M3U + TXT 直播解析（group-title / tvg-logo / `#genre#` / 多线路 `$`）
- SQLite 存储：历史 / 收藏 / 配置订阅 / 偏好（4 表）
- 多站点并发聚合搜索
- libmpv 传输层：loadfile / play / pause / seek / 倍速 / 音量 / UA-Referer / 断点续播位置
- 8 页面 Avalonia 外壳 + 详情选集 + 全视图设计预览数据

**关键缺口（实测验证）**

用饭太硬真实配置（`http://www.饭太硬.net/tv`，48 站点）测试：
- **94% 站点是 `csp_` Java jar 爬虫**（需 JVM，桌面端无解，长期只能靠原生 C# 适配高频站点）
- **6% 是 `.js` drpy 脚本站点**（虎牙 / 斗鱼 / 儿童启蒙）
- **0 个 MacCMS 站点** —— 当前唯一支持的运行时
- 6 个 lives 直播源、wallpaper 字段、0 parses

结论：**当前实现对真实 TVBox 配置的可用站点数 = 0**。这是最大的功能性缺口。

~~**视频渲染是空壳**~~（S1 已完成）：已移植旧栈 `MpvClient`（SafeHandle + 手工 argv）+ `MpvNative.Render.cs` + `MpvRenderContext` + `OpenGlControlBase` 版 `MpvVideoSurface`，引擎等待渲染面握手后才 loadfile。macOS 本机 JIT（37 帧）与 NativeAOT 探针（帧数见 S1 验收记录）均渲染真实画面。

**其他半成品（B 类）**
- 本地文件页能浏览不能播（`PlayRequested` 事件零订阅者）
- 历史续播丢集数上下文（不存 `LineId`/`EpisodeId`，`SourceName` 硬编码"本地"）
- 直播收藏 / 历史 Tab 是空 stub，`lives[]` 解析后被丢弃
- 海报全是 emoji 占位，无图片加载 / 缓存
- 音轨 / 字幕轨引擎支持但 UI 未接
- README 声称的 catchup 解析实际没做

## 阶段计划

### S0 · 快速修复与地基（P0，0.5–1 天）✅ 已完成 `8c91a57`，测试 21 → 48
1. 本地文件播放接线：`FilesViewModel.PlayRequested` → `PlayerViewModel.Play`
2. 历史持久化补全：存 `LineId` / `EpisodeId` / `Remarks` / 真实 `SourceName`；修 mpv 事件线程阻塞
3. 中文域名配置 URL 验证（`IdnMapping` 测试；实测 .NET HttpClient 自动转 punycode，但加回归锁定）
4. README 与实现对齐：删掉未实现的 catchup / 轨道 UI 声称
5. 接入测试源：`.cache/test-sources.md` 里的两个源加进冒烟脚本

### S0.5 · 共享地基（P0，0.5 天，S1/S3 前置）✅ 已完成 `cbd8785` `1a74a91`
已决策纳入四个低成本高收益组件（详见 `docs/SALVAGE.md` §D）：
- `BoundedContent.ReadAsync(stream,max,ct)` —— 从旧 `MacCmsProvider.cs:146` 的 private static 抽成独立文件；
  几乎所有复用文件都依赖它
- `TextEncoding.cs`(17行，DROP-IN) —— 严格 UTF8→GB18030 回退 + UTF-16 BOM，注册 `CodePagesEncodingProvider`。
  **修真实缺陷**：当前 `DefaultHttp.Decode` 只在 Windows 回退 GB18030（`DefaultHttp.cs:55`），
  macOS/Linux 上中文站点会乱码。拖入 `System.Text.Encoding.CodePages` NuGet
- `AppPaths.cs`(23行，DROP-IN) —— XDG / macOS `~/Library` / Windows `LocalApplicationData`；
  当前 `AppServices` 硬编码 `~/.vodbox`（`AppServices.cs:16-18`）
- `AtomicFile.WriteAsync`(9行，DROP-IN) —— 原子写（旧 `ConfigurationRepository` 里唯一值得捞的部分）

### S1 · 视频渲染落地（P0，2–4 天）★ 最关键 ✅ 已完成（2026-10-08，macOS x64）
- ✅ 移植 `9a67c2d^` 整套 mpv 栈：`MpvClient`（SafeHandle + 锁串行化 + 手工 NULL 结尾 UTF-8 argv）/
  `MpvNative`（保留新树 DllImportResolver 与 `VODBOX_MPV_LIB` 覆盖）/ `MpvNative.Render.cs`（5 个 render 绑定）/
  `MpvRenderContext`（UnmanagedCallersOnly 回调只置脏标记）/ `MpvVideoSurface`（Avalonia 12.1.3 `OpenGlControlBase`，零漂移编译）
- ✅ 旧 `MpvEngine` 适配新 `IPlaybackEngine`：`SeekAsync`→`SeekToAsync`+`SeekByAsync`；音量 double 0..1→int 0..100；
  `AddSubtitleAsync`/`SubtitleSource` 按决策丢弃（S8 另行设计）；`RefreshTracks` 收录视频轨（新 `TrackKind.Video`）
- ✅ `PlayerOverlay.axaml` 黑矩形+占位文字 → 真 `MpvVideoSurface`（`waitForVideoSurface` 握手：Ready 前不 loadfile）
- ✅ 保留加固选项（`config=no, load-scripts=no, ytdl=no, terminal=no, idle=yes, hwdec=auto-safe`）与非 UA/Referer 头明确拒绝（S7 媒体代理后再放行）
- ✅ 修新发现的 bug：`aid/vid/sid` 只能字符串读（DOUBLE 报 -9）
- 实测：本地合成 5s H.264 mp4 —— JIT 渲染 **37 帧**、NativeAOT **52 帧**（2.5s 窗口，position=2.23s，width=640 校验通过）；
  headless 传输层冒烟（seek/pause/resume/rate/volume/UTF-8 路径/错误/双重 dispose）全绿；Release NativeAOT 发布 0 警告
- 验证缺口（如实记录）：HLS 直播源与全屏往返未验证；Windows/Linux 未跑（无环境，靠 CI）；详情页内嵌影院模式播放器留 TODO（设计为全局浮层播放）
- 验收：本地 mp4 能看到画面、暂停、seek ✅；HLS + 全屏 ⏳ 待 S2 控制条完善后补验

**真机验收（2026-10-08，commit `11143eb`）**：
- 爬虫层 8/8：6 个爬虫（Bili/FirstAid/Trailer/Huya/Tuxiaobei/Douyu）在饭太硬+宝盒真实源各解出真实播放地址
- libmpv 真实渲染：B 站真实网络流 frames=227 / position=7.57s / h264 360x640 30fps + aac。**渲染层完好**——
  此前"卡死"是探针三层连环 bug（旧二进制 abort 的 UE 态、CDN 要 Referer、参数解析不认 --key=value），与渲染无关
- 结论：真实站点 CDN 普遍要求 Referer/UA 请求头，播放时必须带（引擎已支持透传）
- 真机验收工装：UiSmoke 支持网络地址 + Referer/UA + mpv 内部日志；PreviewTests 支持 VODBOX_PREVIEW_SHOT=1 对拍设计稿

### S2 · 播放体验（P0–P1，3–5 天）
**进行中（2026-10-08）**：PlaybackCoordinator 已接 PlayerViewModel/详情解析。
新播放与关闭取消旧解析；解析忽略取消时，迟到结果也不能打开；引擎 open/stop 转移串行；
播放事件按 SessionId 与播放意图门控。已有3条协调器离线竞争回归。
详情 Open/Resume 共用可等待任务、取消令牌与代次门控，详情/收藏/历史全部就绪后在 UI 线程回填；
新请求（含不可用站点）清空旧选集/续播上下文，离开详情取消请求，迟到成功/失败不能覆盖新状态。
切片与关闭在首次等待前截取不可变旧历史（来源、媒体、线路、选集、进度、时长、倍速、海报）；
历史写入串行且存储失败不阻断播放，切片打开新会话前等待旧写入，新播放可取消这段等待；
关闭立即取消解析，迟到关闭不会隐藏或停止新播放。异步状态回填与时钟更新显式回 UI 线程。
新增14条真实 VM + Avalonia Headless 竞争回归（忽略取消的源/存储、迟到错误、导航/站点失效、
旧历史快照、写入顺序/失败、关闭/新播放、原生打开串行、排队旧事件、后台调用的 UI 线程更新），
CI 已加入 PreviewTests。Desktop Debug build、主测试206/206、预览及 VM 测试26/26通过；
本轮使用 --no-restore，用户既有 lock 文件保持原样。
仍待：跳片头/片尾、外部字幕、无痕及真实UI连续切换/键盘/轨道/小窗验收；最近新增功能未作真机/AOT验收。
**已决策纳入**：继续适配旧 `PlaybackCoordinator.cs` 的 `SaveProgress` 完播检测与无痕，
以及 `PlaylistSession.cs`(43行)（ADAPT）；当前协调器已落实会话隔离、取消与原生转移串行，
完播续播点、播放列表及自动下一集已实现，见上方最新记录。引擎无关，单 libmpv 下照样成立。
另移植 `LiveRetryPolicy.cs`(23行，DROP-IN，填 S4 备用线路) 与
`AggregateSearch.cs`(44行，ADAPT：4路上限 `Channel` 流式 + 逐源超时，优于当前 `ConcurrentBag`+`WhenAll`)。
- 音轨 / 字幕轨 / 视频轨选择 UI（引擎已就绪）
- 快捷键：空格 / ←→ seek / ↑↓ 音量 / F 全屏 / Esc / 数字选集
- 画面比例、片头片尾跳过（字段已存在未用）、自动下一集、控制条自动隐藏
- 播放偏好持久化（音量 / 倍速 / 比例 / 内核设置）
- 验收：键盘完整操作一遍点播 + 直播

### S3 · 内容源运行时扩展（P1，最重）
**范围已决策（2026-10-08）**：

### S3a 当前验收记录（2026-10-08）
- QuickJS 引擎与 drpy 内容源已接入工作树：每站独立 VM、延迟加载、调用串行、取消/异常后重建。
- 主测试最近验证 203/203；预览 12/12。
- 原生 QuickJS 冒烟 12/12：定时器、Promise、ES modules、循环取消、释放期间回填、GBK、URL 拼接等。
- drpy 冒烟 12/12：显式 DrpySource 路径（非原生 key 兜底），兔小贝分类4/分类列表30/搜索4/详情/稳定续播身份/播放解析；实际 CDN MP4 字节读取 HTTP 206。
- 新修正：异步回调 requestId 的 C#/C ABI 使用 int 对齐；原生库搜索按当前平台/架构生成 RID。
- **尚未完成**：更多真实规则兼容、六 RID 原生库构建/打包、NativeAOT 发布验收、逐文件依赖许可与上游版本固定。
- QuickJS 不支持 Java csp_；那些仍需 C# 原生适配。以上成果不等于 S3a 全范围验收完成。

**S3a · QuickJS drpy 运行时** —— ⚠️ **不用 QuickJS.NET NuGet，复用旧自建 CMake bridge**
实测 `QuickJS.NET` v0.0.3（2021-05-24）包内 `runtimes/` 只有 win-x64/win-x86/linux-x64，
**缺 macOS 与全部 arm64（4/6 RID）**，且包装 bellard 经典 QuickJS（非 quickjs-ng）、
用反射式 `DllImport`（AOT 不友好）。改用 NuGet **省不掉 CMake 管线**（原生库仍需自编 6 份）。
故复用 `9a67c2d^` 的：`build/quickjs/CMakeLists.txt`（FetchContent 钉 commit+SHA256、CI 按 RID 编译）、
`build/quickjs/bridge.c`（53 行，64MiB 内存/1MiB 栈/15s 中断期限）、
`src/VodBox.PluginHost/QuickJsEngine.cs`（`LibraryImport`+`UnmanagedCallersOnly`，AOT 干净）。

VM 骨架可直接复用，**协议层必须重写**：旧宿主跑私有 VodBox 协议
（`globalThis.vodbox` + `VodBoxProvider.*`，宿主操作仅 `fetch`/`sha256`/`log` 且全同步），
而实测 `drpy2.min.js`（67381 字节）用 **ES modules** 且依赖整套库
（cheerio/crypto-js/node-rsa/pako/gbk/json5/jinja），需要：
- **模块解析器** + `assets://` 虚拟协议 + 随包分发 drpy 依赖库
- **约 20 个异步宿主函数**（实测调用频次：`log` 107、`request` 16、`pd` 3、`setItem`/`getItem` 各 2、
  `req`/`getProxy`/`encodeUrl`/`joinUrl` 各 1；另需 `js2Proxy`/`md5X`/`aesX`/`rsaX`/`base64*`/`setTimeout`/`console`/`getCookie`）
- **异步 req 桥接**：drpy 的 `home→detail→play` 链发大量顺序 HTTP，旧 15s 整体 eval 期限 + 同步 fetch 撑不住

收益：4 个条目（虎牙/斗鱼/儿童启蒙，两源合计）。注意旧代码已有虎牙/斗鱼/兔小贝的
**原生 C# provider**（真机 AOT 验证过），故 drpy 优先级可低于 S3b。工作量 **L**。

**S3b · 原生 C# 高频站点适配** —— 范围收窄为**头部 ~11 个入口** 🔨 进行中（1/11 完成）

✅ **已完成 `4eba5ab` `5de0726`**：
- `NativeSpiders` 注册表 + `SourceRuntime.NativeSpider` + `IResolvingContentSource`
  （签名/时效播放地址需在点播时现取，不能预置 `Episode.Uri`；直连源不实现，无破坏性变更）
- `BilibiliSource`：`csp_Bili` / `csp_BiliGuard`（覆盖排名第 1、2 的入口，两源合计 16 条目 = 16.3%）
- `ConfigLoader.ToSources` 路由 csp_；`DetailViewModel.PlayAsync` 走即时解析
- **真实端到端验证**：饭太硬存活源 0 → 10（7 个 BiliGuard），分类 130、首页 20 项、
  详情/播放解析出真实 CDN URL + cid 弹幕地址；宝盒存活 0 → 10（9 个 NativeSpider），
  分类 42、首页 20 项、聚合搜索 102 条
- **两个真实配置暴露的缺陷已修**：`TvBoxParse.Ext` 多态（宝盒整份配置曾解析失败）、
  B 站匿名搜索 `v_voucher` 风控（约 1/4 概率，已加退避重试 + 明确报错）
- **聚合搜索静默吞异常已修**：`SearchWithFailuresAsync` 返回逐源失败原因 + 每站独立 25s 超时
- 测试 71 → 90 全绿（B 站离线回归 19 项，含真实抓取的 popular/nav/spi fixture）

✅ **`csp_AppGet` 完成**（2026-10-08，AppGetSource ~470 行 + 26 项离线测试）：
- **TVBox ext 管道格式已实现**（老实现从未写过的部分）：`url|key[|version|ua]` 1~4 段；
  真实宝盒 4 条目实测全是此形态；版本段 `V119/V122` 即协议选择器
 （V119→form 签名 initV119/searchList/vodDetail；V122→JSON initV122/searchList4/vodDetail2）；
  URL 非根地址（xxx.txt）自动发现模式；JSON ext（host/key/get_type/path）同步支持
 （公开协议参考 AppGet.py/金牌APP.js 形态）
- **真实验收 2/4 全链路通过**：咕咕（V119，分类3/首页95/详情4集/真实 mp4/搜索20）、
  一碗（两字段→v119，分类5/首页159/8线路/m3u8/搜索20，**比老验证进步**——老记录失败）；
  首发（V122）发现文件解析正确但 API 服务器 111.42.67.221:8004 停机、
  蔬菜 OSS 域名本机网络不可达（DNS 劫持+TCP 拒绝，协议同构于已通的一碗）——均站点/网络侧
- 测试 140 → 166 全绿（含 OpenSSL 加密向量、双分支端点序列、外部解析器头隔离、
  并发槽位与取消、非法信封/重定向拒绝）

✅ **`csp_App99` 完成**（2026-10-08，App99Source ~460 行 + 12 项离线测试）：
- **ext JSON 对象形态已适配**（真实宝盒「双星99」「剧圈99」实测：host/appkey/name/
  versionName/buildSignature/package/buildNumber，uuid 未下发时实例内随机作 AES-256 密钥）
- **真机验收 2/2 全链路通过**（比老记录进步——旧验证仅双星 JIT）：
  双星99（分类8/列表21/3线路/ffzy m3u8/搜索21）、剧圈99（分类18/1线路/m3u8/搜索21）
- BN v2 协议全保留：AES-256-CBC 随机 IV + SHA256(body:timestamp:nonce::appkey) 签名 +
  zlib/去填充双响应 + 解析器逐个试（≤3）+ 头隔离 + 8MiB 炸弹防护

⏭️ **下一个候选**：排名 6-10 均为无旧实现的新协议（AppDrama/AppSxGuard/AppYd/
S_zpsGuard/T4Guard，各 2 条目、未验证）；或转 S3a QuickJS drpy 运行时（虎牙js 已有
原生实现兜底）。

原始范围表：
实测 69 个独立 `csp_` 入口中**仅 11 个出现 ≥2 次（覆盖 38% 条目）**，**58 个只出现 1 次**；
前 20 入口才覆盖 47%。全量移植不现实（每个入口都是独立站点协议逆向）。
按频次移植，旧代码基本都有真机验证实现可复用：

| # | 入口 | 条目 | 旧实现 | 验证状态 | 工作量 |
|---|---|---|---|---|---|
| 1 | `csp_Bili` | 9 | BilibiliProvider (329行) | **真机 JIT+AOT** | M |
| 2 | `csp_BiliGuard` | 7 | 同上（需核对 Guard 参数） | 未核实等价 | M |
| 3 | `csp_AppGet` | 4 | AppGetProvider (328行) | ✅ **已移植**（咕咕+一碗真机全通；ext 管道已实现；V122 仅 fixture） | ~~M/L~~ |
| 5 | `csp_App99` | 2 | App99Provider (282行) | ✅ **已移植**（双星+剧圈真机全通） | ~~M~~ |
| 6 | `csp_AppDrama` | 2 | 无（旧文档：公开引用是空占位，从未注册） | 未验证 | M/L |
| 7 | `csp_AppSxGuard` | 2 | 无 | 未验证 | M |
| 8 | `csp_AppYd` | 2 | 无（旧文档：源文件搜索为空） | 未验证 | M/L |
| 9 | `csp_S_zpsGuard` | 2 | 无 | 未验证 | M |
| 10 | `csp_T4Guard` | 2 | 无 | 未验证 | M |
| 11 | `csp_XBPQ` | 2 | AudioSiteProvider (205行) | **评书真机 AOT** | M |
| 12 | `csp_XPath` | 2 | 无（导航+提醒，非点播） | — | 低优先 |

长尾另有旧实现可直接捞（各 S）：**虎牙**(94行，真机AOT)、**急救教学**(99行，真机AOT)、
**荐影预告片**(106行，真机AOT)、**兔小贝**(109行，真机AOT)、**斗鱼**(85行，仅JIT，播放依赖 CDP 嗅探)。

⚠️ **必须复验**：旧爬虫解码验证**全部走 LibVLC**，libmpv 只在合成媒体上验证过。
移植后要把每个爬虫重新跑一遍 mpv 路径，**不能继承 LibVLC 的解码结论**。
先移植 `Diagnostics.cs`(246行) 真网诊断工装，它就是产出上述验证结论的工具。

未适配站点：UI 明确提示"该源需 Android 端"，不静默失败。

**S3c · 配置导入器**（S3 高杠杆，M）
新 `ConfigLoader.ToSources` 目前**直接丢弃**每个 `csp_` 站点（`ConfigLoader.cs:95`）。
旧 `ConfigLoader.Import.cs`(124行) 是范本：`type 0/1`→maccms、`csp_Bili(Guard)`→bilibili
（含导入 `ext.json` 分类≤500）、drpy 规则路径→原生、`csp_FirstAid`→firstaid、`csp_YGP`→trailers、
`lives[]`→LiveSources 带 per-source UA、`clan://` base64、对未适配站点发 `ImportWarnings`。

验收：饭太硬 + 宝盒两源加载后，头部站点能浏览 + 搜索 + 播放（经 mpv 真实解码）。

### S4 · 直播完整化（P1，1 周）
- TVBox `lives[]` 消费（多直播源、per-source UA/timeout）
- EPG / XMLTV：解析 + 缓存 + tvg-id 匹配 + 频道信息条 + 节目单时间轴
- catchup 回看（M3U `catchup-source` 模板替换）
- 直播收藏 / 历史 Tab 落地、密码分组锁
- 验收：加载饭太硬 6 个直播源，能看节目单、回看

### S5 · UI 打磨与海报（P1，3–5 天）
- 海报图片加载 + 磁盘缓存 + 占位（替换所有 emoji）——**移植旧 `PosterCache.cs`(47行，DROP-IN)**：
  SHA-256 URL 文件名、4 路下载、4MiB/响应、7 天 TTL、LRU-by-atime 淘汰至 160MiB、原子 tmp+move；
  配旧 `MainViewModel.Posters.cs`(77行) 的按可见树门控加载/取消/释放逻辑
- 筛选面板 UI（`FilterGroup` 模型已存在，MacCMS 参数已映射）
- 搜索联想 / 热搜词 / 搜索历史持久化
- 详情页：倒序选集、分段（>20集）、已看标记、快搜换站
- 主题（深/浅/跟随）、海报密度、无痕模式、壁纸
- 验收：视觉稿 `design/index.html` 8 画板逐一对照

### S6 · 设置中心与数据（P1，3–5 天）
- 配置订阅管理 UI（`IConfigStore` 已实现未接）：多配置历史、切换、设为首页源
- 站点管理：星标 / 隐藏 / 排序 / searchable-changeable 开关
- 播放 / 弹幕 / 字幕 / 界面 / 数据 / 关于 分组表单
- 备份 / 恢复（SQLite + prefs 打包）、跨设备同步（依赖 S7）
- 验收：设置 8 分组全可操作且持久化

### S7 · 本地 HTTP 服务与推送（P2，1 周）
- Kestrel 自宿主 minimal API：`/proxy` `/cache` `/action` `/media` `/upload`（AOT source-gen JSON）
- 媒体代理：请求头回环、HLS 清单/密钥转发、DASH 模板
- 推送页：二维码 + Web 遥控器 + 剪贴板推送
- 验收：手机扫码能遥控播放

### S8 · 弹幕 / 解析 / 字幕（P2，1–2 周）
- 弹幕：拉取 + 解析（XML/JSON/gzip）+ SkiaSharp 自绘层（滚动/顶部/底部）+ 时间轴同步
- VIP 解析器：`IPlayResolver` type 1/2（JSON 接口）实现 + 解析线路选择 UI
- 在线字幕（assrt）搜索 + 本地字幕加载 + 样式设置
- 验收：B 站源弹幕能看、一个需解析的源能播

### S9 · 平台与发布硬化（P2，持续）
- 跨平台媒体键、休眠抑制、多屏 DPI
- Windows 签名、Apple Developer ID / 公证
- 自动更新检查 + 签名校验
- 六 RID CI 全绿 + 安装包冒烟
- DLNA 投屏（可选，二期）

## 明确不做
- JVM / Dex 加载 `csp_` jar 爬虫（架构不可行；改为按频次用 C# 原生重写，见 S3b）
- **QuickJS.NET NuGet 包**（实测缺 macOS 与全部 arm64，4/6 RID 不可用；bellard 陈旧引擎 + 反射封送。
  改用自建 CMake bridge，见 S3a）
- **58 个单次出现的 `csp_` 长尾入口**（仅 62% 条目且各需独立协议逆向；UI 提示不支持）
- 双内核 LibVLC / `PlaybackEngineRouter` / `PlaybackEnginePolicy`（单 libmpv 决策下无可路由，约 301 行丢弃）
- Android Auto、PiP 系统级（桌面用迷你窗口替代）
- DRM（Widevine/PlayReady，mpv 不支持）
- 旧 TVBox 配置导入 / Java 插件兼容层（项目决策）

## 测试源（用户提供）
- `https://宝盒接口.top` —— 当前网络 TLS 被重置，待复测
- `http://www.饭太硬.net/tv` —— 可用，JPEG 隐写形态，48 站点（94% csp_）


### 2026-10-09 直接开发增量
- ✅ Release 严格构建清除 CS8618/MVVMTK0034/AVLN5001；最终代码串行 osx-x64 NativeAOT 发布成功、日志无警告。
- ✅ 本地外挂字幕入口（文件选择、mpv sub-add/select、切集/关闭丢弃旧选择）。实际原生字幕渲染待验收。
- ✅ 数字 1—9/小键盘选集，输入框/修饰键保护、越界忽略；Headless 键盘路由回归通过。
- ✅ 全局片头/片尾时长偏好与菜单；默认关闭，直播/短媒体/拖动/续播边界回归通过。原生 EOF/自动下一集链路待验收。
- ✅ 主测试 231/231；预览/VM 61/61。详细证据与剩余项见 `.alma/development-checkpoint.md`。


### 2026-10-09 直播与原生验收增量
- ✅ JIT/NativeAOT 真libmpv外挂字幕新增/选轨/关闭及seek末尾EOF通过（非主应用完整交互验收）。
- ✅ 直播线路循环、稳定历史键、加载取消/迟到门控、去掉虚构运行时EPG和源名。
- ✅ 有界安全XMLTV解析、本地XML/XMLTV/gzip导入、tvg-id匹配及真实节目时间轴；网络订阅、缓存、持久化与到点刷新待做。
- ✅ M3U EXT VLC/Kodi UA/Referer传递、逐频道隔离、控制字符防护。
- ✅ 主测试237/237、预览68/68；末尾全部切片串行osx-x64 AOT发布通过、日志无警告。

- ✅ 真实运行时EPG每30秒到点刷新（附着启动、卸载停止，设计器不启计时器），可注入时钟边界回归；预览69/69，最新AOT再次串行发布成功。


### 2026-10-09 播放器窗口比例增量
- ✅ 默认窗口尺寸按实际视频显示比例调整，支持竖屏/超宽与当前屏幕DPI工作区上下限。
- ✅ 小窗保比例并恢复min/max/尺寸/置顶，退出播放恢复原主界面；全屏/最大化不自动调整。
- ✅ 主307/307、预览129/129；JIT与AOT真实9:16和2.4:1视频专项验收通过。


### 2026-10-09 进度回顾后的修复与验收
- ✅ 修复GUI错误ao=auto：交由mpv选择系统音频驱动，CoreAudio正常；JIT/AOT真实自动下一集和历史集号验收通过，未伪造Ended。
- ✅ 当前直播备用线路每条有限重试，重复/旧事件隔离、全部失败可见；网络真源重试仍待验收。
- ✅ 异常关闭后窗口恢复不访问已释放屏幕owner。
- ✅ 完整主308/308、预览131/131，AOT发布成功；原生主应用47帧/暂停/seek/全屏往返/小窗恢复/连续切集/历史/关闭通过。
- ⏳ 六平台实际运行、手机扫码实机、真实直播EPG回看及ASSRT有效凭据验收仍未完成。
