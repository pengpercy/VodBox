# VodBox：FongMi/TV 的 Avalonia 跨平台重写方案

源码分析日期：2026-10-04；方案更新日期：2026-10-05。工程已进入实现阶段；实现与验证状态见下一节。阶段耗时是工程估算，不是实测。

## 当前实现状态（2026-10-05）

工程位于 VodBox 仓库，首版已经实现桌面壳、LibVLC 播放、版本化配置、C# JSON 目录源、分类/搜索/详情/选集、SQLite 历史与收藏、无痕播放、直播列表解析、XMLTV 解析及三种脚本宿主。Avalonia 采用编译绑定、源码生成 MVVM/JSON、Design.DataContext 与系统材质回退。下文阶段清单继续作为后续实现和验收路线，不表示每项已完成。

六个 RID 的本机 Native AOT 编译、真实 LibVLC WAV 解码、随包 QuickJS/Python/Node 协议测试及全部安装包生成均已通过 [Actions 验证](https://github.com/pengpercy/VodBox/actions/runs/37223401606)（代码提交 7105ef7）。Windows x64/ARM64 生成 ZIP，macOS Intel/Apple Silicon 生成 app/ZIP/DMG，Linux x64/ARM64 生成 deb/rpm/AppImage。打包检查实际依赖文件哈希，macOS 另外验证 ad-hoc 签名与封包后的宿主运行。尚未完成各平台有画面的交互、长时稳定性和全部安装后的真机测试；本地公开直播测试已观察到 VideoToolbox 解码，但未完成全面硬件解码验证。

当前已补充 C# MacCMS HTTP JSON/XML 适配、聚合搜索、配置仓库、偏好、收藏打开、历史重新解析、连续播放、片头片尾、直播恢复与备用重试、XMLTV 缓存、解析链、流代理和浏览器嗅探；后续增加海报缓存与可见加载、年份 / 完结筛选、单源搜索分页、桌面控制、主题、备份 / 合并导入及无窗口 XAML 预览。当前 65 项本地测试通过，macOS x64 严格 Native AOT 编译及实际备份导出导入诊断通过。CI 与真机验收分别记录在 [功能清单](implementation-progress.md)。弹幕加载、调度、透明叠层和设置已实现并完成 macOS 窗口行为验证；其余平台和全屏叠层待验收，通用筛选元数据、聚合分页和局域网扩展仍需实施。Python/Node 属于随包运行时；Node 排除 npm 和开发头文件，插件所需第三方模块须自行携带并锁定。

用户提供的饭太硬地址作为后续测试来源；它返回旧配置，不能直接作为新版配置加载。已提取公开直播列表形成新版测试示例，未导入 Java 插件；网络可达性与实际播放记录见 [播放测试](playback-testing.md)。

最新 Spider 进展：Bilibili、AppGet V119 / 显式 Qiji V122 与 App99 BN v2 已原生 C# 接入。112 项本地测试通过，App99 双星 JIT 实际解码通过；参考协议不等同于原 Java/Guard 入口全部覆盖。Python/Node 保留，继续不引入 Java 兼容层。详细范围见 [App99 Spider](app99-spider.md) 与功能清单。

## 1. 结论与范围

可以使用 Avalonia 重写为 macOS / Windows / Linux 桌面应用。采用 .NET 10 LTS、Avalonia、MVVM、SQLite，以及参考 Screenbox 的 LibVLC + LibVLCSharp 播放方案。详细播放器设计见 [Screenbox 专项分析](screenbox-playback-analysis.md)。

### 已确认的项目决策（2026-10-05）

- 基础版本锁定 .NET SDK 10.0.401、运行时 10.0.12、Avalonia 12.1.3，均为核对时的最新稳定版。LibVLCSharp 3.10.1 已支持 Avalonia 12。后续升级仍固定具体稳定版本并重新验证 Native AOT。
- 按用户最新要求保留 **LibVLC + libmpv 双内核**。计划设置提供自动、固定 libmpv、固定 LibVLC：自动模式点播/直播/本地媒体优先 libmpv，网络浏览/投屏/网络文件系统使用 LibVLC；普通播放启动失败可回退一次并提示原因，固定模式不自动切换。LibVLC 主窗口仍在使用；libmpv 原生绑定及 Avalonia OpenGL 表面已在 macOS x64 实验驱动中验证，主窗口路由、设置选择和六 RID 双内核打包尚未完成。
- 内容源支持 C# Provider、QuickJS、Python、Node.js 四种实现方式。
- Java Spider 的业务逻辑按需要用 C# Provider 重写；不实现 Java/JVM/Dex 兼容层，不随包分发 JRE。
- QuickJS-NG 通过项目内薄 C ABI 和 C# LibraryImport 源码生成绑定接入，定义 VodBox 自有宿主 API，避免反射式 CLR 互操作。
- Python 和 Node.js 明确保留，属于正式支持范围；在脚本运行时阶段交付，不因为不兼容旧配置而移除。
- 首版暂不兼容 TV 旧配置、旧插件接口和旧本地 API。使用版本化的新 JSON 配置与统一插件契约；将来若需要兼容，再作为独立需求评估。
- 构建、打包、发布结构参照 Downio，覆盖三平台六 RID；播放器和脚本运行时各自维护原生依赖清单。
- 使用最新稳定版 .NET 10，桌面程序与 QuickJS 宿主启用 Native AOT 和完整裁剪。业务代码不使用反射注册、动态程序集加载或运行时 JSON 类型发现；改用编译期注册、MVVM 与 JSON 源码生成。
- 界面配置 Design.DataContext，设计数据不能启动网络、SQLite、脚本进程或播放器。主窗口启用 ExtendClientAreaToDecorationsHint，通过 Avalonia 12 标题栏角色提供拖动区域，并保留系统窗口按钮与材质回退。
- 窗口优先请求 Mica，按 Mica → AcrylicBlur → Blur → None 自动回退。Windows 11 使用系统 Mica；macOS 与 Linux 使用可用的背景模糊，不宣称具有原生 Mica。禁用透明、缺少合成器时使用不透明深色背景。侧栏与控制栏使用半透明材质；视频区域保持黑色。设计器使用相同配色和实色底板预览。

项目不是单纯的视频播放器，而是“外部配置 → 站点/插件 → 分类与搜索 → 详情与播放解析 → 播放内核 → 历史和互通”的完整系统。成本主要在三平台视频合成、脚本宿主实现和原生依赖分发。

建议分三次交付：

1. **MVP**：三平台安装与启动、本地媒体/URL、HTTP XML/JSON 源、分类/筛选/搜索/详情/选集、字幕/音轨/倍速、历史/收藏、M3U/TXT/JSON 直播与基础 XMLTV。
2. **脚本扩展版本**：QuickJS/Python/Node 插件、JSON 解析与网页嗅探、代理、弹幕、节目表完善、配置仓库与网络规则。
3. **扩展版本**：更多 C# Provider、DLNA、局域网同步、SMB/WebDAV、特效、预加载与更新。

MVP 使用新的配置与源契约。源能力检查报告当前运行时是否可用、配置是否有效和该源支持的功能；旧配置导入与 Java 插件加载不纳入任务。

默认保留桌面鼠标和键盘交互，同时提供大屏模式；不迁移 Android Auto、Android Service、系统 PiP、手机手势原有实现。桌面悬浮播放窗口、媒体键、休眠抑制可另行实现。

## 2. 分析依据与源码结构

本次检查 FongMi/TV 的 `fongmi` 分支，固定快照为 `c616c0aa3613e87529791587a9f71b78c278c991`；Downio 的 `master` 分支快照为 `198fa617e3d1be41329b9f967dcf85a90b8b14a5`。文档网页可能继续变化；以下源码结构分析保留为参考，实施范围以“已确认的项目决策”为准。

- [TV 源码快照](https://github.com/FongMi/TV/tree/c616c0aa3613e87529791587a9f71b78c278c991)
- [Downio 工作流快照](https://github.com/pengpercy/Downio/tree/198fa617e3d1be41329b9f967dcf85a90b8b14a5/.github/workflows)

### 2.1 模块和职责

- `app/src/main`：共用业务逻辑；`app/src/leanback` 和 `app/src/mobile`：电视/手机 UI。Avalonia 应共用应用层，仅切换桌面/大屏布局和输入策略。
- `api/config/BaseConfig、VodConfig、LiveConfig`：配置加载、点播站点/解析器、直播源。迁移为配置仓库和显式加载流程，不保留全局单例。
- `api/SiteApi`：首页、分类、详情、搜索、播放和自定义操作；是建立 C# 源接口与协议测试的主要参考。
- `api/loader` 与 `catvod`：Java、JS、Python 源加载以及 Spider 契约。参考职责划分，实现 C# Provider 与 QuickJS/Python/Node 运行时管理，不迁移 Java 加载器。
- `player/PlayerManager、engine、media/PlaySpec`：播放器生命周期、播放参数和错误处理。迁移为 PlaybackCoordinator、IPlaybackEngine、PlaybackRequest。
- `player/parse/ParseJob`：网页嗅探、JSON 解析、插件扩展解析、混合解析和竞速解析。不能只做一个 HTTP 请求函数替代。
- `api/parser/LiveParser、EpgParser`：频道和节目表。核心逻辑可按格式移植，时区与编码要补测试。
- `db/AppDatabase` 与 `app/schemas`：Room 数据库。迁移为 SQLite schema 与 migration；旧 Android 数据库导入作为独立功能。
- `server`：NanoHTTPD 和路由处理；迁移为可选的本地 ASP.NET Core 服务。
- `player/extractor`：媒体地址提取和专用协议。按协议逐个判断，不能把提取器名称当作桌面可用能力。
- `player/effect`：音频均衡、稳定音量、色调等。首版保留设置模型，之后映射为 LibVLC 支持的效果或独立桌面实现。

当前 `settings.gradle` 实际包含 app、catvod、chaquo、quickjs；根目录其他组件及 AAR 不应据此假定全部是当前源码构建模块。README 也说明部分播放器 AAR 不随 clone 提供，源码并不足以复现所有媒体能力。

参考：[settings.gradle](https://github.com/FongMi/TV/blob/c616c0aa3613e87529791587a9f71b78c278c991/settings.gradle)、[SiteApi](https://github.com/FongMi/TV/blob/c616c0aa3613e87529791587a9f71b78c278c991/app/src/main/java/com/fongmi/android/tv/api/SiteApi.java)、[ParseJob](https://github.com/FongMi/TV/blob/c616c0aa3613e87529791587a9f71b78c278c991/app/src/main/java/com/fongmi/android/tv/player/parse/ParseJob.java)。

### 2.2 原项目平台边界与本项目取舍

- 原项目 HTTP 源的 type=0/1/4、Result 及分隔符格式可用于理解功能，不作为 VodBox 配置协议。需要某个外部 HTTP API 时，在对应 C# Provider 中转换响应。
- 原 JarLoader 使用 DexClassLoader，Spider.init 依赖 Android Context；本项目通过 C# 重写需要的业务功能，不加载旧 JAR/Dex，也不实现 JVM sidecar。
- QuickJS、Python、Node.js 保留运行能力，但使用新插件契约。无需复现旧 QuickJS host API、Chaquopy bridge 或 Node bundle 初始化格式。
- 原 PlayerEngine 暴露 Media3 Player/PlayerView；本项目重新定义不含平台类型的 IPlaybackEngine，以 LibVLCSharp 实现。
- TVBus、ForceTech、Thunder、JianPian 等 Android AAR/so 只作为原项目能力记录；支持其功能需要单独的桌面实现。
- DRM 不属于首版保证范围；LibVLC 接入不能自动覆盖 Widevine/PlayReady/FairPlay。

参考：[JarLoader](https://github.com/FongMi/TV/blob/c616c0aa3613e87529791587a9f71b78c278c991/app/src/main/java/com/fongmi/android/tv/api/loader/JarLoader.java)、[Spider](https://github.com/FongMi/TV/blob/c616c0aa3613e87529791587a9f71b78c278c991/catvod/src/main/java/com/github/catvod/crawler/Spider.java)。

## 3. 技术选择与工程拆分

建议使用 .NET 10 LTS；与 Downio 当前 SDK 一致。Avalonia 和 CommunityToolkit.Mvvm 选经过验证的稳定版本，锁定具体版本到 `Directory.Packages.props` 和 lock 文件，不在方案中猜测最新 NuGet 版本。

- UI：Avalonia + MVVM + 编译绑定；虚拟化海报列表、后台图片加载、响应式两栏详情、大屏焦点导航。
- 应用层：依赖注入、异步用例、CancellationToken、有界并发与结构化错误。
- DTO：System.Text.Json、版本化 schema、明确字段类型；外部源响应在 Provider 内转换为强类型领域模型。
- 存储：Microsoft.Data.Sqlite 与显式 migration；第一版无需引入完整 ORM。
- 网络：HttpClient/SocketHttpHandler，以配置/站点上下文隔离 Cookie、代理、Header 与请求超时。
- 播放：IPlaybackEngine 隔离业务，LibVLCSharp 与无反射 NativeControlHost 句柄绑定承载视频；字幕交给内核，控制和弹幕由 Avalonia 处理。
- 本地服务：ASP.NET Core，默认 loopback，局域网访问通过显式设置启用。
- 插件：独立 PluginHost 进程；QuickJS/Python/Node 分别提供运行时适配器。
- 日志：结构化日志、轮转文件、请求关联 ID、敏感 Header 和 URL query 脱敏。

建议目录：

```text
VodBox.sln
global.json
Directory.Build.props
Directory.Packages.props
VERSION
CHANGELOG.md
src/
  VodBox.Desktop/          # App、View、ViewModel、桌面入口
  VodBox.Core/             # DTO/领域模型、接口、错误与能力声明
  VodBox.Application/      # 搜索、配置、播放、直播、历史用例
  VodBox.Infrastructure/   # HTTP、SQLite、文件缓存、本地服务
  VodBox.Playback.LibVlc/  # LibVLCSharp、事件桥、视频控件
  VodBox.Providers/        # HTTP/XML/JSON/插件适配
  VodBox.PluginHost/       # QuickJS C# 绑定宿主、进程入口与 IPC
  plugins/python/         # Python SDK、worker 与示例
  plugins/node/           # Node SDK、worker 与示例
tests/
  VodBox.Core.Tests/
  VodBox.Protocol.Tests/
  VodBox.Integration.Tests/
build/
  native/                 # 按 RID 的已验证原生运行时
  scripts/                # 下载、验证、打包、签名、冒烟
  resources/              # plist、desktop、deb、rpm、图标
.github/workflows/        # ci、build、package、release
```

依赖方向：Desktop → Application → Core；Infrastructure、Providers、Playback 实现 Core 中的接口，由 Desktop 的组合根注入。Core 不引用 Avalonia、LibVLCSharp 或 Android 类型。

不要为了“未来扩展”预先搭建复杂插件市场、微服务或多播放器切换。先使一个播放器和一类源可靠。

## 4. 核心契约与播放流程

### 4.1 源接口

定义 `IContentProvider`，提供 `InitializeAsync / GetHomeAsync / GetCategoriesAsync / GetItemsAsync / GetDetailAsync / SearchAsync / ResolvePlaybackAsync / DisposeAsync`。Proxy、Action、Live 分别作为可选能力接口，不强迫所有站点实现。

`ProviderCapabilities` 声明 Search、PagedSearch、Filters、Proxy、Action、Live、Runtime、UnsupportedReason。搜索源筛选依此进行，UI 不根据字符串扩展名推测全部能力。

Provider 实例键至少包含配置 ID、站点 key、运行时、插件内容哈希。切换配置释放对应实例，避免相同 key 的站点串 Cookie 或插件状态。

### 4.2 新配置、插件契约与领域模型

配置使用 `schemaVersion`，插件使用 `apiVersion`；版本不支持时给出明确错误。字段类型固定，不沿用旧 type 数字、vod_* 字段或字符串拼接播放列表。

- 源定义包含 id、name、runtime（csharp/quickjs/python/node）、provider/entry、options 与可选运行权限声明。
- 列表结果为 items 与 pagination；未知总页数使用 null，后续页由 nextCursor/hasMore 表达，不依赖 pagecount=0。
- 详情使用 playbackLines 数组，每条线路包含 episodes 数组；多画质使用 qualities 数组，不使用 $$$/#/$ 分隔符。
- 播放结果明确声明 resolutionKind（direct/json/plugin/browser），以及 uri、headers、subtitles、qualities、startPositionMs 和可选代理上下文；不使用 parse/jx/flag 的隐式组合规则。
- Header 为字符串字典，Cookie/代理按源隔离。明确配置、源、播放请求的覆盖优先级；凭证不写入可分享配置。
- JSON 时间明确单位毫秒；内部使用 TimeSpan；画质选择使用独立 qualityId，不复用 position。
- C# Provider 与 QuickJS/Python/Node SDK 实现相同能力：init、home、categories、items、detail、search、resolvePlayback、dispose；Proxy/Action/Live 为可选扩展。
- 外部 HTTP XML/JSON 不要求符合内部 schema，由具体 Provider 显式映射。

领域模型统一为 MediaItem、Category、Filter、PlaybackLine、Episode、PlaybackRequest、LiveChannel、Programme。先定义 schema 和四种运行时的契约测试，再展开内容源实现。

### 4.3 完整播放管线

```text
用户选集
 → Provider.ResolvePlaybackAsync
 → 规范化多画质、Header、字幕、进度
 → 特殊协议提取器
 → 根据 resolutionKind 选择解析策略
 → 直链 / JSON 解析 / 插件解析 / 网页嗅探
 → 建立 PlaybackRequest
 → IPlaybackEngine 加载并报告状态（LibVLC + LibVLCSharp）
 → 更新历史、跳片头片尾、自动下一集
```

状态采用 `Idle → Resolving → Loading → Playing / Paused / Buffering → Ended / Failed`。每次操作带 session ID；切集、换源、停止都会取消旧任务，旧回调不能覆盖当前播放。

`PlaybackRequest` 至少包含 URI、请求 Header、字幕列表、起播时间、媒体格式提示、站点/视频/集数标识、备用地址、DRM 描述与代理上下文。

网络重试、解析重试、解码降级分层处理；硬解失败允许一次软解重试，线路重试有上限。禁止所有错误无限轮询。

## 5. 按顺序执行的实现步骤

### 阶段 0：冻结范围与建立样本，2–3 人日

1. 保存三个参考项目的源码快照、功能说明与来源链接，确认新配置及插件契约。
2. 收集合法可用的最小样本：HTTP XML、HTTP JSON、新配置与 C# Provider 示例、直链 MP4、HLS、DASH、M3U/TXT/JSON、XMLTV、SRT/ASS/VTT、JS/Python/Node 示例。
3. 保存可重放响应到 tests/Fixtures；移除凭证，避免 CI 依赖第三方站点在线状态。
4. 建立逐项能力清单：MVP、脚本扩展、平台扩展；明确保留 Python/Node，移除旧配置和 Java 兼容任务。
5. 记录系统基线和 CPU 架构，不把六个 RID 当作全部已测试平台。

验收：能用样本回答每种 runtime/插件/播放地址归哪个适配器；新 schema 与能力边界明确。

### 阶段 1：三平台 LibVLC 播放验证，5–8 人日，先于完整 UI

1. 创建最小 Avalonia 播放窗口和 URL/文件输入框。
2. 接入固定版本 LibVLCSharp 桌面包与 Avalonia VideoView，验证播放、暂停、跳转、音量、字幕、音轨和 EOF；不用 UWP 包。原生制品匹配目标 RID。
3. 为原生视频宿主与独立透明窗口叠层验证遮挡、裁剪、全屏、菜单与弹幕；参考 Screenbox 的 surface 生命周期，重建 Avalonia 实现。
4. 原生内核事件与 UI 更新分开；所有命令按内核要求调度，窗口销毁先解绑事件，再释放 MediaPlayer、Media、LibVLC 和视频 surface。
5. 优先实测 win-x64、osx-arm64、linux-x64；其余架构在发布前补齐制品和真机验证。
6. 验证各平台可用硬解与软解回退，以 LibVLC 日志和诊断记录为准。
7. Linux 分别验证 X11 / XWayland / 原生 Wayland 的实际支持路径，未验证路径明确标记。
8. 压测跨屏 DPI、全屏、暂停后 seek、切文件、睡眠恢复与长时间运行。

门槛：三平台画面、字幕、控制层可用，重复关闭不崩溃。按用户后续决定保留 LibVLC 与 libmpv，统一播放请求与生命周期；自动模式仅允许一次明确回退，固定模式不自动切换。主窗口双内核接入按最新开发顺序推进。

参考：[Screenbox 播放设计](screenbox-playback-analysis.md)、[LibVLCSharp Avalonia 控件](https://github.com/videolan/libvlcsharp/blob/3.x/src/LibVLCSharp.Avalonia/README.md)。

### 阶段 2：工程基础与桌面壳，3–5 人日

1. 创建工程与上述依赖方向；global.json 固定已验证 SDK，NuGet 锁定并在 CI 使用 locked restore。
2. 配置依赖注入、日志、全局错误展示、设置持久化、取消传播和调度。
3. 创建侧栏：首页、搜索、直播、历史、收藏、设置；播放器独立 ViewModel。
4. 使用 Avalonia StorageProvider 打开本地文件；URI/文件路径解析禁止手写平台分隔符。
5. Windows 数据放 LocalAppData；macOS 放 Application Support；Linux 区分 XDG_CONFIG_HOME、XDG_DATA_HOME、XDG_CACHE_HOME。
6. 实现语言、主题、快捷键、拖拽打开、全屏；macOS 使用 Command 键约定。

验收：应用不写入安装目录，普通用户可运行；窗口关闭释放播放器和子进程。

### 阶段 3：新配置与 C# 内容源，7–10 人日

1. 实现版本化 JSON schema、本地/远程配置、源切换和字段错误定位；不实现旧配置导入。
2. 相对 entry/资源路径统一以配置 URI 为基准，仅解析 schema 指定的路径字段。
3. 实现 SourceDefinition、ProviderOptions、ResolverDefinition、LiveSourceDefinition 等新 DTO。
4. 创建 C# Provider 注册表，通过 provider 标识加载应用内实现；将需要的 Java Spider 业务逻辑重写为 C#。
5. 实现实际需要的 HTTP XML/JSON 适配，使用显式参数/响应映射，不引入旧站点 type 分支。
6. 实现首页 → 分类 → 筛选 → 分页 → 详情 → 选集 → 直链播放。
7. 实现单源与聚合搜索；默认并发 4、每源独立超时、部分成功实时显示。
8. 海报磁盘缓存设总容量和淘汰策略，使用虚拟化列表；切页面取消旧请求。

验收：新配置验证和 HTTP Provider 的 fixtures 通过；单站超时不阻塞其他搜索；C# Provider 不依赖 JVM。

### 阶段 4：播放会话与个人数据，5–8 人日

1. 扩展 LibVLCSharp 封装：属性观察、轨道选择、字幕加载、速度、延迟、截图、错误分类。
2. 落地 PlaybackCoordinator 的 session ID 和状态机；一次只允许一个活动播放会话。
3. 实现进度恢复、片头片尾跳过、下一集、同集换线路；直播不写入点播恢复逻辑。
4. SQLite 表至少包含 Config、Favorite、PlaybackHistory、LiveFavorite、Settings、SchemaVersion。
5. 历史/收藏主键包含配置 ID、sourceId、mediaId；历史另外记录lineId 和 episodeId，不能仅按标题匹配。
6. 播放中约每 10 秒、暂停、换集和退出时保存进度；写入去抖、事务化，故障不阻塞播放。
7. 无痕模式关闭历史写入和相关持久化；便携备份明确提示配置中的凭据可能被包含；不包含浏览器 profile、运行时缓存或独立 Cookie 存储。

验收：切集取消正确；seek 与恢复时间单位正确；数据库升级保留收藏；崩溃后最多丢失一个保存周期的进度。

### 阶段 5：直播与 XMLTV，4–6 人日

1. 移植 M3U、TXT、JSON 解析，覆盖 group-title、tvg-id、tvg-name、logo、多地址和编码差异。
2. 实现分组、频道搜索、频道收藏、上次频道恢复和备用线路。
3. 使用 XmlReader 流式读取 XMLTV，限制下载/解压体积并缓存；后台刷新不中断播放。
4. 频道优先按 tvg-id 匹配；名称回退有明确规则和手动覆盖入口。
5. 解析 XMLTV 数字时区偏移，存 UTC，按直播配置/用户时区显示；不要给无偏移时间默认套本机时区。

验收：跨午夜、夏令时、频道同名、无节目表均正常；直播换台失败可切备选线路。

到此可以发布首个 MVP。后面阶段不应阻塞基础播放器交付。

### 阶段 6：QuickJS、Python 与 Node.js 运行时，10–18 人日

本阶段三种脚本运行时均为必交付能力，不要求兼容 TV 的旧脚本。C# Provider 在阶段 3 完成，Java/JVM/Dex 兼容层不实施。

1. 定义版本化 IPC：apiVersion、requestId、sourceId、method、params、result/error；stdout 仅传协议，日志写 stderr。能力握手检查运行时与 SDK 版本。
2. 实现运行时启动、取消、超时、崩溃恢复、销毁和配置切换；各运行时返回同一 schema，进入同一个 IContentProvider 适配入口。
3. QuickJS 选用开源 C# 绑定，验证 ES module、Promise、异常传播、内存/执行限制、GC 与原生六 RID 制品。宿主提供请求、Cookie、编码/加密、日志、缓存和受控模块解析等自有 API。
4. Python 使用受管理的 CPython worker 及隔离环境，定义 vodbox SDK。插件采用新契约，依赖锁定；不使用 Chaquopy，也不修改用户全局 Python。
5. Node.js 使用独立 Node worker 与 vodbox SDK，经统一 IPC 通信；保留 npm 生态，锁定依赖。Node 内部可以提供 HTTP 服务，但不复现 TV 的 index.js.md5 或 /config 初始化协议。
6. Proxy 支持状态、Header、流式二进制 body、Range 和取消；媒体流使用受控本地通道，不把整段视频 Base64 编码进 IPC。
7. 运行时和插件下载固定版本、来源与 SHA-256。每个源声明 runtime，报告其启动/能力状态。
8. 制作等价的 C#、QuickJS、Python、Node 示例源，共享分类、搜索、详情与播放 fixtures；添加超时、崩溃、重载和资源释放测试。
9. 官方完整发行包包含 Python/Node worker、SDK 和对应平台运行时，使其不依赖系统预装环境。先验证三个主平台，再补齐其余 RID；有纯脚本依赖与本地扩展时分别锁定/校验。
10. 插件源路径使用受控缓存；独立进程是故障隔离，真正文件/网络权限限制仍需 OS 机制或 broker。

验收：QuickJS、Python、Node 分别完成 init → 分类/搜索 → 详情 → 播放解析；每类插件崩溃不导致 UI 退出；完整包在未预装 Python/Node 的机器通过测试。发布后续脚本扩展版本前，三种运行时均不可省略。

Java 8 的全局约定继续适用于未来确有 Java 工具调用的场景；本项目没有 Java 编译或运行步骤，无需引入 JDK/JRE。

### 阶段 7：解析、网页嗅探与弹幕，7–12 人日

1. 优先实现 resolutionKind=direct/json 的直链和 JSON 解析。
2. resolutionKind=plugin 调用 C#/QuickJS/Python/Node 的新解析契约；未就绪时返回结构化错误。
3. 可选的多解析器竞速使用有界并发，成功后取消其余任务；每次结果只完成一次。
4. 网页嗅探使用独立 BrowserResolver 服务，评估 Playwright/Chromium 的网络响应与页面脚本捕获；系统 WebView 的拦截能力另做平台验证。
5. 明确安装模式：浏览器可以作为高级组件按需安装；离线完整版则需要为各 RID 打包并验证浏览器运行时。不能假定存在六架构一致的预编译浏览器。
6. 按新 ResolverDefinition 处理页面脚本、Header、Cookie、超时和被捕获媒体；不要仅靠 `.m3u8` 后缀判断，处理 Content-Type、blob/MSE 无法直接提取等限制。
7. 解析完成向播放器和分片代理传递所需请求上下文，日志脱敏。
8. 弹幕独立加载并解析为统一时间线；使用 Avalonia 自绘层、轨道调度和限量渲染，seek 时重建活动弹幕。

验收：已完成解析不能被旧页面回调覆盖；字幕与弹幕不遮挡控制入口；浏览器能力缺失时给出可操作说明。

参考：[Avalonia 网页嵌入能力](https://docs.avaloniaui.net/docs/app-development/embedding-web-content)。

### 阶段 8：局域网与平台服务，6–12 人日

1. 实现 VodBox 自有版本化本地 API，用于推送、控制、配置与同步；暂不兼容 TV 旧路由。
2. 默认 loopback；局域网模式要求 token、Origin 策略、文件访问范围和可见开关；流代理 token 与远程控制 token 分开。
3. 推送、远程控制、配置导入、收藏同步逐项验证；大文件流支持 Range。
4. DLNA 分接收器和控制点两条路径，验证 SSDP、多网卡、IPv6、防火墙和睡眠恢复。
5. 同步采用明确导出格式与版本号，冲突按稳定 ID/修改时间处理，不复制整个活跃 SQLite 文件。
6. 平台服务逐个实现：媒体键、屏幕常亮、文件关联、托盘、SMB/WebDAV、开机启动。

验收：关闭局域网功能就不监听外部地址；API 与 UI 操作进入同一播放用例；两台机器完成控制与同步场景。

## 6. 参照 Downio 的构建、打包与发布

本项目使用 LibVLC：发布携带 libvlc/libvlccore、VLC plugins 与真实链接依赖。脚本扩展完整版本还携带 QuickJS 绑定所需原生库、Python/Node 运行时、worker 和 SDK；插件依赖需独立校验。LibVLCSharp NuGet 或 dotnet publish 均不代表这些六 RID 原生制品已经齐全。

### 6.1 已确认的 Downio 行为

这是对源码的实际检查，不是对目录名称的推测：

- `build.yml` 是 workflow_call，覆盖 win-x64、win-arm64、osx-x64、osx-arm64、linux-x64、linux-arm64；设置 .NET 10；self-contained + SingleFile + 压缩；Unix tar 保存权限。
- Windows runner 为 windows-2022；Intel Mac 为 macos-15-intel，ARM Mac 为 macos-latest；Linux 在 ubuntu:20.04 容器中构建，并为 ARM64 配置交叉依赖。
- `ci.yml` 在 master push、PR、手动调用下运行 build/version/package，从 VERSION 读取版本。
- `package.yml` 下载构建产物：Windows ZIP；macOS .app ZIP 与 DMG，支持证书导入、签名和 notarytool/stapler；Linux AppImage/DEB/RPM。
- `release.yml` 在 v* tag 触发，版本去除 v，调用 build/package，从 CHANGELOG 提取说明，找不到时回退 tag message，用 gh 上传 assets。
- 包脚本校验 aria2c；在 VodBox 中要替换为 LibVLC/plugins 及脚本运行时的依赖校验。
- artifact 保存一天；Linux appimagetool 使用 continuous 下载地址。

参考：[build.yml](https://github.com/pengpercy/Downio/blob/198fa617e3d1be41329b9f967dcf85a90b8b14a5/.github/workflows/build.yml)、[package.yml](https://github.com/pengpercy/Downio/blob/198fa617e3d1be41329b9f967dcf85a90b8b14a5/.github/workflows/package.yml)、[release.yml](https://github.com/pengpercy/Downio/blob/198fa617e3d1be41329b9f967dcf85a90b8b14a5/.github/workflows/release.yml)、[打包脚本](https://github.com/pengpercy/Downio/tree/198fa617e3d1be41329b9f967dcf85a90b8b14a5/build/scripts)。

### 6.2 VodBox 产物约定

六种 RID 保留，但分清“能交叉 publish”“能打包”“已真机验证”。建议名称：

```text
VodBox_0.1.0.win-x64.zip
VodBox_0.1.0.win-arm64.zip
VodBox_0.1.0.osx-x64.zip        # 内含 VodBox.app
VodBox_0.1.0.osx-x64.dmg
VodBox_0.1.0.osx-arm64.zip
VodBox_0.1.0.osx-arm64.dmg
VodBox-0.1.0.linux.x86_64.AppImage
VodBox-0.1.0.linux.aarch64.AppImage
VodBox_0.1.0-1_amd64.deb
VodBox_0.1.0-1_arm64.deb
VodBox-0.1.0-1.x86_64.rpm
VodBox-0.1.0-1.aarch64.rpm
SHA256SUMS
THIRD-PARTY-NOTICES.txt
```

首版 Windows 使用完整目录 ZIP，后续再增加安装器。macOS 按架构独立包，不将两份 self-contained 发布目录用 lipo 直接合并。Linux 提供三种格式，但 AppImage 不是对所有发行版/glibc 的兼容保证。

### 6.3 构建参数与原生依赖

首版采用 Native AOT 目录发布，启用完整裁剪；原生 VLC、QuickJS 与外部 Python/Node 依赖保持独立文件：

```bash
dotnet restore VodBox.slnx --locked-mode
dotnet test VodBox.slnx -c Release --no-restore
dotnet publish src/VodBox.Desktop/VodBox.Desktop.csproj \
  -c Release -r osx-arm64 --self-contained true \
  -p:PublishSingleFile=false -p:PublishTrimmed=true -p:PublishAot=true \
  -p:Version=0.1.0 -o artifacts/publish/osx-arm64
```

工程与 lock 文件已经创建。publish 按 RID 需要额外 restore；不要错误沿用不含目标 RID 的 `--no-restore`。AOT 警告必须逐项验证，不能仅凭项目属性宣称支持 AOT。QuickJS 使用薄 C ABI 与 LibraryImport 源码生成绑定，不暴露反射式 CLR 对象互操作。

对每个 RID 维护 native-manifest：LibVLC/libvlccore、VLC plugins、其他实际链接依赖、QuickJS 原生库、Python/Node 运行时、版本、来源、SHA-256、许可证、最低系统要求。优先固定可复现构建，不从 latest/continuous 漂移下载。

- Windows：检查 PE machine 与 DLL 依赖，验证 VC runtime 需求。win-arm64 必须有真实 ARM64 LibVLC 与脚本运行时制品，不能靠 x64 publish job 自动获得。
- macOS：检查 Mach-O 架构；用 otool 审核依赖，使用可重定位的 @rpath/@loader_path，禁止遗留 Homebrew 绝对路径。
- Linux：检查 ELF 架构、ldd/readelf 和 glibc 符号版本。对系统基础库与可私有携带依赖分开处理，不任意捆绑 glibc。
- .NET self-contained 只包含 .NET 运行时，不自动提供完整 LibVLC/plugins、QuickJS、Python/Node、图形栈、系统字体和浏览器。

原生路径在应用启动时按 RID 显式定位；用诊断页展示加载的实际文件与版本。签名前完成路径修正，签名后不再修改库。

参考：[.NET 单文件部署](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview)。

### 6.4 工作流逐项实现

**第一步：ci.yml**

- PR 与默认分支 push：restore、build、协议测试、存储迁移测试、最小三平台构建。
- PR 不调用需要签名密钥的发布流程；fork PR 没有 secrets，不应因此失败。
- 固定 action 到审核过的 commit SHA，并保留对应版本注释。Downio 当前版本号可以作为查找起点，不能把未验证的 SHA 写入模板。

**第二步：build.yml**

- workflow_call 输入 version、commit，matrix 维护六 RID；fail-fast=false。
- checkout 固定传入 commit；检查 tag 与 VERSION 一致，版本同时注入 csproj 和 plist。
- restore/publish 后执行 stage-native → verify-native → archive。
- 每个 RID 上传 `vodbox.<rid>`，产物包含版本、commit、RID、native manifest。
- macOS 原生依赖需要匹配架构；Windows/Linux ARM64 的原生编译单独 job 或固定外部制品，不把 .NET 交叉发布当作原生交叉编译。

**第三步：package.yml**

- 下载同一次 run 的构建产物，先核对 manifest，然后运行 `package.win.ps1 / package.osx-app.sh / package.linux.sh`。
- Windows ZIP 保留目录；不签名时明确产物状态，有证书后签 exe/DLL 再压缩。
- macOS 建立 Info.plist、图标、文件类型、主程序路径与最低系统版本；MacOS 存可执行文件，Frameworks 存 dylib，Resources 存资源。
- 逐个签嵌套 dylib、helper 和 framework，最后签 app；不要仅依赖 `codesign --deep` 掩盖依赖问题。按实际运行时确定 hardened runtime entitlement，避免全量放开。
- 完成 app 验证后构建并签 DMG → notarytool → staple → validate。
- ZIP 若也宣称已公证，应使用对应 app 的公证并 staple 后再生成 ZIP；只 staple DMG 不会自动把 ZIP 内 app 也变成已 staple。
- macOS 打包 shell 直接使用 `set -euo pipefail`，不要照搬源码中拆开的 set 语句。
- Linux 为不同架构写入 deb Architecture 与 RPM target、desktop、图标、MIME、依赖声明；检查安装/卸载和普通用户启动。
- Linux 构建基线按所选 .NET/Avalonia/LibVLC 的共同最低系统决定。不能仅为了复制 Downio 就承诺 Ubuntu 20.04；必须核验 SDK、原生依赖和 action 的容器兼容。
- AppImage 构建工具架构与 payload 架构分别验证；pin 版本和校验和，不沿用 continuous URL。

**第四步：smoke.yml 或 package 后置验证 job**

- 解包最终产物，核验 manifest、版本、文件权限和原生依赖。
- 增加 `--diagnostics --headless` 路径：检查 SQLite、配置解析、Provider fixture、LibVLC 加载、脚本握手和测试音视频解码，不弹用户窗口。
- headless 不能证明 GUI、GPU 硬解、Wayland、全屏、签名启动通过；这些单列真机验收。
- x64 runner 上的 ARM64 publish 只做静态验证；用 ARM64 runner/真机执行目标二进制，不直接在 x64 容器跑 ARM 程序。

**第五步：release.yml**

- v* tag → version 校验 → tests → 六 RID build → package → smoke → hashes → draft release。
- 正式签名/公证 secrets 仅传递到需要它的受信任 package job；正式发布缺少必要证书时失败，不静默发出“正式未签名包”。
- release job 才赋予 contents:write；其他 job 默认 contents:read。
- 汇总 CHANGELOG、能力支持清单、最低系统要求、SHA256SUMS、许可证、已知限制。
- 先以 draft 集齐并校验所有预期资产，再转正式发布；避免发布中出现一半资产。
- 同 tag 重跑前检查已有资产；正式资产不默认用 --clobber 覆盖，避免已发布校验和失效。
- artifact 保留建议 CI 7 天、release 30 天；最终 Release assets 不依赖临时 artifact 的存活。

### 6.5 可直接落实的流程骨架

以下仅说明调用关系，不是完整可运行 YAML：脚本、原生制品、runner 与 action SHA 尚需发布阶段验证。

```yaml
# release.yml 中的任务依赖
jobs:
  version:     # 校验 tag、VERSION、预发布标记，输出 version 和 commit
  tests:       # 调用 ci 中的验证工作流
  build:
    needs: [version, tests]
    uses: ./.github/workflows/build.yml
    with:
      version: ${{ needs.version.outputs.version }}
      commit: ${{ needs.version.outputs.commit }}
  package:
    needs: [version, build]
    uses: ./.github/workflows/package.yml
    with:
      version: ${{ needs.version.outputs.version }}
    # 实施时按需要显式映射签名 secrets
  smoke:       # needs: [package]；最终包验证
  release:     # needs: [version, smoke]；创建 draft、上传、校验、发布
```

工作流的六 RID matrix 建议先沿用 Downio，再固定并验证 runner 镜像版本。ARM64 本机冒烟作为独立 gate 补上。

## 7. 测试、发布门槛与实施顺序

### 7.1 必要测试

- 协议：schemaVersion/apiVersion、源 runtime、XML/JSON 适配、筛选、线路/集数数组、多画质、Header 优先级、时间单位、分页、resolutionKind。
- 并发：搜索取消、切集取消、旧回调、解析竞速单次完成、插件超时/崩溃。
- 存储：schema migration、复合 ID、进度保存、无痕、断电恢复。
- 网络：Cookie、Referer、UA、重定向、Range、HLS 分片/密钥请求、代理中断。
- 播放：MP4/MKV/HLS/DASH、字幕和字体、软硬解、速度、seek、睡眠恢复、连续切源。
- 发布：最终压缩包/安装包而非仅 publish 目录；无开发环境机器、非管理员、中文用户名、带空格路径和离线启动。

纯协议测试使用 fixtures；原生播放器集成测试在明确平台执行。GPU/真机测试记录媒体、系统、架构、驱动和解码路径，不能仅报告“构建通过”。

### 7.2 推荐任务拆分

1. 工程骨架、锁定依赖、日志与 CI。
2. 三平台 LibVLC 播放原型与叠层验证。
3. 新配置 schema、DTO 与四种运行时的统一契约。
4. C# Provider 注册表、HTTP XML/JSON 适配与 fixtures。
5. 浏览/搜索/详情/选集 UI。
6. PlaybackCoordinator、历史、收藏、字幕。
7. 直播与 EPG。
8. 六 RID 原生依赖清单、三平台包脚本、首版发布。
9. PluginHost + QuickJS。
10. Python/Node 运行时。
11. JSON/扩展解析、BrowserResolver、弹幕。
12. 局域网、DLNA、更多 C# Provider 和平台扩展。

其中 1 与 2 的具体顺序可小幅交错，但完整 UI 不应抢在播放渲染验证之前。

### 7.3 时间与风险

基础 MVP 的阶段 0–5 约 26–40 人日，首版发布与六架构验证另预留 5–8 人日；单人全职约 7–10 周，包含集成与真机问题缓冲。插件/解析/互通阶段 6–8 约 23–42 人日，另外预留脚本 SDK 和原生运行时适配缓冲。

完整功能版本可按 3–5 个月级别规划，仍需具体源数量和真机验证校准。首版不含旧配置/旧插件兼容，Java 业务迁移按新增 C# Provider 数量计入；Python/Node 保留，其宿主、依赖和六 RID 分发工作不能从估算中删除。Android 专用协议、闭源 SDK 与 DRM 不在保证范围内。

首要决策门槛：

1. 原生视频宿主是否在三平台满足控制层/弹幕叠层与性能。
2. 需要实现哪些 C# Provider；QuickJS/Python/Node 新 SDK 的宿主能力是否齐全。
3. 六架构 LibVLC、QuickJS、Python/Node 及依赖是否有可验证、可分发的制品。
4. 网页嗅探是按需组件，还是每个安装包默认携带浏览器。

## 8. 来源、许可证与交付说明

TV 仓库标示 GPL-3.0。重写语言本身不会消除复制或改编代码的许可义务；实施前明确代码复用策略与分发许可。LibVLC、QuickJS 绑定、Python/Node 及插件依赖按实际制品记录许可，随发行包保留 Notices 和需要提供的源码/构建说明。未对具体闭源分发作法律判断。

官方文档入口：[配置](https://fongmi.github.io/TV/config/)、[扩展](https://fongmi.github.io/TV/spider/)、[本地 API](https://fongmi.github.io/TV/local/)、[功能](https://fongmi.github.io/TV/features/)。

本方案没有编写应用业务代码、修改 CI、发布产物或推送 GitHub。下一次进入实现，建议直接从“工程骨架 + 三平台本地视频播放原型”开始，以实测决定渲染路径，再按上面的任务顺序扩展。

## 用户测试源与 Spider 排期更新

两个用户源的统计共 98 个条目（94 个 Java Spider、4 个 drpy），精确 Java 入口 69 个。按频率先完善 `csp_Bili` 9 次 / `csp_BiliGuard` 7 次的哔哩哔哩相关适配，随后 `csp_AppGet` 4 次、App99 候选家族和其他重复内容入口。Guard 不自动合并，不等于已覆盖。详见 [移植优先级](spider-migration-priority.md)。当前公开投稿基础适配已接入并在 macOS JIT/Native AOT 实际播放、弹幕验证通过，已新增原生 BV/av 片单分类（20 项分页、四路并发、详情缓存、整页取消），本地 87 项测试通过；旧片单配置/Guard/DASH/账号待补。双内核主窗口切换排在这批 Spider 之后，CI 继续手动集中验证。


### AppGet V119 接入

已新增原生 C# `AppGetProvider` 并显式注册 `appget` / `csp_AppGet`。配置明确指定 protocol、服务根/发现文件、key 与客户端版本；支持加密分类/推荐、分页筛选、搜索、详情多线路、直接媒体和站内解析，每次播放刷新详情/令牌。最多四路 HTTP，20 秒期限、8 MiB 响应上限，无 JAR、反射或 Python 依赖。本地 95 项回归通过。真实站点初始化三项通过，一碗完成内容到地址解析；媒体解码仍失败，不能计为全部可播放。其他版本、外部解析及认证分别排期。完整配置和边界见 [AppGet Spider](appget-spider.md)。


### AppGet 播放解析与显式 Qiji V122 补齐

实际核对发现旧协议参考对 AES 密文重复 URL 编码且缺少 player_parse_type，修正后咕咕视频 5092 已在 macOS JIT 使用 VideoToolbox H.264 解码至 3.40 秒，最终 Native AOT 实际解码至 3.16 秒。加入播放器声明的外部 JSON 解析（url/data.url、准备好的解析 URL、1 MiB 上限，仅传 User-Agent），以及源码生成的 Qiji V122 JSON 分支和独立 IV。Qiji 初始化声明验证码时停止搜索；不做自动版本猜测。完整本地回归更新为 104 项通过。Qiji 真实站点发现入口 TLS 错误，首发入口仍未验收，不能计为真实覆盖。具体配置、验证和限制见 AppGet 文档与播放测试记录。


## 最新开发顺序（2026-10-05）

按用户最新决定，Spider 先按使用频率形成阶段性覆盖，完成当前 App99 与相声/评书批次后转入播放内核：补齐 mpv IPlaybackEngine、LibVLC/mpv 统一路由、用户手动选择、受限自动回退、设置持久化以及桌面视频与弹幕显示。随后回头扩大 Spider 覆盖，不等待全部 69 个 Java 入口移植完再开发播放器。123 项本地回归通过；多数原入口尤其 Guard 尚未验收，覆盖率与功能完成度分别记录，保持两种内核及 Python/Node，不增加 Java 兼容层，CI 继续批量验证。

播放内核进展：MpvEngine 已实现统一播放接口、观察事件、轨道与控制；126 项回归通过，macOS x64 JIT 与严格 Native AOT 实际验证通过。下一阶段接统一路由与主窗口，当前仍不计为双内核 GUI 已完成。见 [mpv 播放接口](mpv-engine.md)。

后续统一路由已实现：自动选择、手动固定、启动/运行失败最多回退一次、请求深拷贝、会话隔离、位置/暂停/倍速/音量/延迟迁移与取消清理。139 项本地测试和真实内核往返切换通过。下一步接主窗口设置与视频表面，随后补轨道/追加字幕迁移及六 RID 打包；Spider 暂缓扩展，没有启动 Actions。
