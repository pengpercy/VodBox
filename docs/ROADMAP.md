# VodBox 开发路线图

> 基线：commit `9a67c2d`（clean-slate 重写）+ `ea938e4`。构建 0 错误，21 测试通过。
> 目标：把 FongMi/TV 的能力搬到 Avalonia 12 + .NET 10 NativeAOT + libmpv 桌面端。
> 本文档由能力差距分析生成，按阶段推进，每阶段有验收标准。

## 当前进度快照

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

**视频渲染是空壳**：`MpvNative.cs` 无 `mpv_render_context_*` 绑定，`MpvVideoSurface` 是占位，`PlayerOverlay.axaml` 只画黑矩形 + "libmpv 渲染窗口"文字。**有传输层但看不到画面**。
好消息：git 历史（`9a67c2d^`）有可用的 `MpvRenderContext.cs` + `MpvNative.Render.cs` + `OpenGlControlBase` 版 `MpvVideoSurface.cs`，旧版曾通过 macOS JIT/AOT 真实解码验证，可捞回适配。

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

### S0.5 · 共享地基（P0，0.5 天，S1/S3 前置）
已决策纳入四个低成本高收益组件（详见 `docs/SALVAGE.md` §D）：
- `BoundedContent.ReadAsync(stream,max,ct)` —— 从旧 `MacCmsProvider.cs:146` 的 private static 抽成独立文件；
  几乎所有复用文件都依赖它
- `TextEncoding.cs`(17行，DROP-IN) —— 严格 UTF8→GB18030 回退 + UTF-16 BOM，注册 `CodePagesEncodingProvider`。
  **修真实缺陷**：当前 `DefaultHttp.Decode` 只在 Windows 回退 GB18030（`DefaultHttp.cs:55`），
  macOS/Linux 上中文站点会乱码。拖入 `System.Text.Encoding.CodePages` NuGet
- `AppPaths.cs`(23行，DROP-IN) —— XDG / macOS `~/Library` / Windows `LocalApplicationData`；
  当前 `AppServices` 硬编码 `~/.vodbox`（`AppServices.cs:16-18`）
- `AtomicFile.WriteAsync`(9行，DROP-IN) —— 原子写（旧 `ConfigurationRepository` 里唯一值得捞的部分）

### S1 · 视频渲染落地（P0，2–4 天）★ 最关键 🔨 进行中
- 捞回 `9a67c2d^` 的 `MpvRenderContext` / `MpvNative.Render.cs`，适配 Avalonia 12 `OpenGlControlBase` API
- `MpvNative` 补 `mpv_render_context_create/update/render/free/set_parameter/proc_address` 绑定
- `MpvEngine.OpenAsync` 装 render context（当前设了 `vo=libmpv` 但没回调 → 必然无画面）
- `PlayerOverlay.axaml` 黑矩形换成 `MpvVideoSurface`；DPI / 尺寸变化 / GL 丢失恢复
- 三平台验证（macOS 本机先过，Windows/Linux 靠 CI）
- 验收：本地 mp4 + 一个 HLS 直播源能看到画面、暂停、seek、全屏往返

### S2 · 播放体验（P0–P1，3–5 天）
**已决策纳入**：移植旧 `PlaybackCoordinator.cs`(88行) + `PlaylistSession.cs`(43行)（ADAPT）。
协调器提供代次计数会话隔离、取消过期解析、串行化原生状态转移、`SaveProgress` 完播检测
（不会把已看完的一集续播在最后一帧）+ 无痕；S0 那几个续播 bug 的根源正是缺协调器
（当前 `PlayerViewModel` 直接驱动引擎）。引擎无关，单 libmpv 下照样成立。
另移植 `LiveRetryPolicy.cs`(23行，DROP-IN，填 S4 备用线路) 与
`AggregateSearch.cs`(44行，ADAPT：4路上限 `Channel` 流式 + 逐源超时，优于当前 `ConcurrentBag`+`WhenAll`)。
- 音轨 / 字幕轨 / 视频轨选择 UI（引擎已就绪）
- 快捷键：空格 / ←→ seek / ↑↓ 音量 / F 全屏 / Esc / 数字选集
- 画面比例、片头片尾跳过（字段已存在未用）、自动下一集、控制条自动隐藏
- 播放偏好持久化（音量 / 倍速 / 比例 / 内核设置）
- 验收：键盘完整操作一遍点播 + 直播

### S3 · 内容源运行时扩展（P1，最重）
**范围已决策（2026-10-08）**：

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

**S3b · 原生 C# 高频站点适配** —— 范围收窄为**头部 ~11 个入口**
实测 69 个独立 `csp_` 入口中**仅 11 个出现 ≥2 次（覆盖 38% 条目）**，**58 个只出现 1 次**；
前 20 入口才覆盖 47%。全量移植不现实（每个入口都是独立站点协议逆向）。
按频次移植，旧代码基本都有真机验证实现可复用：

| # | 入口 | 条目 | 旧实现 | 验证状态 | 工作量 |
|---|---|---|---|---|---|
| 1 | `csp_Bili` | 9 | BilibiliProvider (329行) | **真机 JIT+AOT** | M |
| 2 | `csp_BiliGuard` | 7 | 同上（需核对 Guard 参数） | 未核实等价 | M |
| 3 | `csp_AppGet` | 4 | AppGetProvider (328行) | **咕咕真机 JIT+AOT**；Qiji-v122 仅 fixture | M/L |
| 5 | `csp_App99` | 2 | App99Provider (282行) | 双星**仅 JIT**，AOT 未记录 | M |
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
