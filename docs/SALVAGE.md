# 旧实现复用评估（git `9a67c2d^` → 当前基线）

> 旧实现 11223 行被 clean-slate 重写删掉，但不是因为坏了——大量代码经过真机验证。
> 本文评估可复用部分。**结论：约 4000–4500 行真正可复用**，其余是双内核 LibVLC/路由栈、
> god-MainViewModel、文件快照配置仓库（均已被新架构取代）。
>
> 读取方式：`git show 9a67c2d^:path/to/file.cs`（勿 checkout）。

## 0. 决定性事实：新旧 Core 契约不兼容

所以 A/D/E 组几乎没有 `DROP-IN`。差异对照：

| 关注点 | 旧 (`9a67c2d^`) | 新 (HEAD) | 影响 |
|---|---|---|---|
| 源接口 | `IContentProvider` | `IContentSource` | 每个 provider 要重新贴合 |
| 分页 | `cursor: string?` → `MediaPage(Items, NextCursor)` | `page: int` → `MediaPage(Items, Page, PageCount)` | 游标↔页码，需合成 PageCount |
| 源配置 | `SourceDefinition{Options: Dictionary<string,JsonElement>}` | `SourceInfo{Ext: string}` | TVBox→options 的管道没了 |
| MediaItem | 位置 record `(Id,Title,Poster,Remarks)` | init-only `{…,Year,Area,TypeName}` | 所有 `new(a,b,c,d)` 断 |
| Episode | `(Id,Title)` | `(Id,Title,Uri)` | |
| 播放解析 | 在源上 `ResolvePlaybackAsync` | 独立 `IPlayResolver` | provider 的播放逻辑无处安放 |
| PlaybackRequest | 有 `DanmakuUri/Subtitles/ResolverId/OriginalUri/Poster/SourceName` | 只有基础字段 | MediaProxy/解析/弹幕要补字段 |
| TrackKind | `{Audio,Subtitle}` | `{Audio,Subtitle,Video}` | |
| JSON 上下文 | `VodBoxJson` | `AppJsonContext` | 全部引用要改 |
| 组合方式 | `ProviderFactory.Create(SourceDefinition)` | `SourceRegistry.Rebuild()` 直接 new | 工厂 vs 注册表 |

**推论**：昂贵的部分（AES/WBI/MD5 加密、协议状态机、抓取正则、DASH/HLS 重写、弹幕调度）
几乎原样存活；便宜但遍布的部分（record 形状、cursor→page、工厂接线）要机械重写。

**两个必须先行落地的共享 helper**（几乎所有复用文件都依赖）：
- `BoundedContent.ReadAsync(stream,max,ct)` —— 现为 `MacCmsProvider.cs:146` 的 private static，需抽成独立文件
- `TextEncoding.Decode`（旧 `Infrastructure/TextEncoding.cs`，17 行）—— **优于当前 `DefaultHttp.Decode`**
  （后者只在 Windows 回退 GB18030）。DROP-IN，但拖入 `System.Text.Encoding.CodePages` NuGet。

## A. 爬虫 Providers（约 2000 行，其中 1400 行是加密/协议/正则宝藏）

全部实现旧 `IContentProvider`，各自建 `static HttpClient`（**故意不跟随重定向**，
不要改接到 `DefaultHttp`——后者允许 5 次重定向），`SemaphoreSlim(4,4)` + 20s 期限 + `BoundedContent` 上限。

| Provider (行数) | 实现内容 | 注册名 | 旧文档验证状态 | 工作量 |
|---|---|---|---|---|
| **BilibiliProvider** (329) | 公开投稿、WBI 签名搜索（MD5 over 置换 `wbi_img`）、view→cid 分集、`qn=16` 单段 durl、自动 cid XML 弹幕、匿名 buvid3/4、6h 会话、128/10min 详情缓存 | `bilibili`,`csp_Bili` | **真机验证**：JIT+AOT 取热门20/搜索20/一集/1200弹幕，VideoToolbox 解码 H.264（测试 `BV1WSHL66EdZ`） | M |
| **AppGetProvider** (328) | v119（表单+时间戳 AES-128-CBC/PKCS7 签名）与 qiji-v122（JSON body、独立 IV、验证码状态）双分支；分类/筛选/搜索/多线路/直接媒体/vodParse/`player_parse_type=2` 外部 JSON | `appget`,`csp_AppGet` | **咕咕真机验证**（JIT 3.40s 216/387块；AOT 3.16s 200/367块）；一碗/蔬菜/首发未过；**Qiji-v122 仅 fixture** | ✅ **已移植 AppGetSource**（2026-10-08：ext 管道新写、咕咕+一碗真机全通、166 测试全绿） |
| **App99Provider** (282) | bn-v2：AES-256-CBC（key=去连字符 UUID 前32 ASCII）、随机16B IV 前置、SHA-256 签 `body:ts:nonce::appkey`、zlib-or-raw JSON | `app99`,`csp_App99` | ✅ **已移植 App99Source**（2026-10-08：JSON ext、双星+剧圈真机全通） | M |
| **AudioSiteProvider** (205) | audio-zblog-v1（xsmp3/psmp3）：Z-Blog HTML 分页、受限 C# 读静态 APlayer 数组、专辑→分集、UA+专辑 Referer；**搜索显式 NotSupported** | `audio-site`,`csp_XBPQ` | **评书 AOT 验证**（3.21s/332音频块）；相声 AOT 未过（TLS 超时），JIT 过 | M |
| **HuyaProvider** (94) | 6 分类、`cache.php?m=LiveList`、`mp.huya.com profileRoom` 多 CDN、**匿名 FLV 签名** MD5(`{prefix}_0_{stream}_{seqid}_{wsTime}`) | `huya` | **真机 AOT 验证** | S/M |
| **DouyuProvider** (85) | 8 分类、`m.douyu.com/api/room/list`、POST 搜索、`vike_pageContext`；播放返回 `ResolutionKind.Browser`（委托 CDP 嗅探） | `douyu` | **JIT 验证**（58视频/184音频块）；**AOT 代理链路待验证** | S/M |
| **FirstAidProvider** (99) | 有来医生 `m.youlai.cn/jijiu` 8 急救分类，源码生成正则抓取 | `firstaid` | **真机 AOT 验证** | S |
| **TrailerProvider** (106) | 6huo `movlist` 目录分页+搜索+静态 ckplayer 声明（不执行脚本） | `trailers` | **真机 AOT 验证** | S |
| **TuxiaobeiProvider** (109) | 固定公开 MIP 分类、30/页、站内搜索（≤3 同源重定向）、HTML5 每次刷新 | `tuxiaobei` | **真机 AOT 验证** | S |
| **MacCmsProvider** (158) | MacCMS v10 JSON **和 XML**（`at=xml`、禁 DTD）、保留 API 专有参数、`year/isend/h/from` 筛选、per-source headers、相对 URL 解析、128 详情缓存；**内含 BoundedContent** | `maccms-json/xml` | 仅 fixture | S（新基线已有 `MacCmsSource`；可只捞 XML 模式+headers+参数保留） |
| **Providers.cs** (111) | `CatalogProvider` + `ProviderFactory` 映射 | — | — | S（换成 `SourceRegistry.Rebuild` 分支） |
| **ScriptProvider.cs** (94) | 每源一个 OS 进程；NDJSON RPC over stdin/stdout；QuickJS/Python/Node 路由 | 非 C# 运行时兜底 | 仅协议冒烟 | **REWRITE**（S3 要进程内 QuickJS，与子进程模型冲突） |

**A 组结论**：最高价值最低风险的首批移植是
**Bilibili（M，完整验证）→ Huya/FirstAid/Trailer/Tuxiaobei（各 S，AOT 验证）→ AudioSite（M）→ AppGet（M/L）→ App99（M）**，
各自 fixture 测试一并移植。

## B. QuickJS 运行时 ⚠️ 关键结论：与"用 QuickJS.NET"的预期冲突

**旧宿主是自建 CMake C bridge，不是 NuGet 包。**
- `build/quickjs/CMakeLists.txt`（14 行）`FetchContent` 钉定 quickjs-**ng** commit `3c9afc99…` + SHA256，
  `add_library(vodbox_quickjs SHARED bridge.c)` 链 `qjs`
- `build/quickjs/bridge.c`（53 行）导出 5 个 C 符号 `vb_create/vb_set_host/vb_eval/vb_free_string/vb_destroy`，
  `JS_SetMemoryLimit(64MiB)`、`JS_SetMaxStackSize(1MiB)`、单调钟中断处理器、**15s eval 期限**、一个 JS 全局 `__host(op,payload)`
- `src/VodBox.PluginHost/QuickJsEngine.cs`（53 行）用 `LibraryImport` + `UnmanagedCallersOnly` 反向回调，
  宿主操作**只有 `fetch`/`sha256`/`log`，且全同步**

**是私有协议，不是 TVBox/drpy——已从代码确认**：`PluginHost/Program.cs:7` 注入
`globalThis.vodbox = { apiVersion:1, fetchText, sha256 }`，`:19` 派发 `VodBoxProvider[method]`
（init/categories/items/search/detail/resolvePlayback）。无 `req`、无 spider `home/category/detail/play`。

### drpy2.min.js 实际需要的宿主面（旧 bridge 全都没有）

实测下载的 `drpy2.min.js`（67381 字节）用 **ES modules**：
```js
import cheerio from "assets://js/lib/cheerio.min.js"
import "assets://js/lib/crypto-js.js"
import "./node-rsa.js"; import "./pako.min.js"
import 模板 from "./模板.js"; import {gbkTool} from "./gbk.js"
import "./json5.js"; import "./jinja.js"
```
即需要**模块解析器（含 `assets://` 虚拟协议）+ 一整套 JS 库**，不是"跑个脚本"。

实测统计的宿主全局调用：`log` 107 次、`request` 16 次、`pd` 3、`setItem`/`getItem` 各 2、
`req`/`getProxy`/`encodeUrl`/`joinUrl` 各 1。

drpy 需要的 TVBox Spider 全局面：
| 类别 | 需要 | 旧 bridge 有？ |
|---|---|---|
| HTTP | `req(url,{method,headers,body,buffer,redirect})` → `{code,headers,content}`；别名 `request/http/get/post` | ❌ 只有返回裸字符串的 `fetch` |
| Spider 入口 | `init/home/homeVod/category/detail/search/play/proxy` | ❌ 用 `VodBoxProvider.*` |
| 代理路由 | `getProxy`/`local`/`js2Proxy`/`s2`/`s` | ❌ |
| 加密 | `md5/md5X/aesX/aesEncode/aesDecode/rsaX/base64Encode/Decode/sha256` | 只有 `sha256` |
| 其它 | `log/joinUrl/setTimeout/clearTimeout/console/__jsEval/getCookie/require/import` | 只有 `log` |
| 生命周期 | **异步 req 桥接** —— drpy 的 `home→detail→play` 链会发大量顺序 HTTP，旧 15s 整体 eval 期限 + 同步 fetch 撑不住 | ❌ |

### QuickJS.NET (NuGet) vs 自建 CMake bridge

**实测 NuGet 包内容（这是硬阻塞）**：
- `QuickJS.NET` **v0.0.3，最后发布 2021-05-24**，包装的是 **bellard 经典 QuickJS，不是 quickjs-ng**
- 目标 `netstandard2.0`，无依赖，用**反射式 `DllImport`/`Marshal`，不是 `LibraryImport`**
- 包内 `runtimes/` 只有 **`win-x64` / `win-x86` / `linux-x64`** —— **完全没有 macOS，没有任何 arm64**
- 项目要发 6 个 RID（win-x64/win-arm64/osx-x64/osx-arm64/linux-x64/linux-arm64），**4 个缺失**

其它候选更差：`Puerts.QuickJS`（3.0.3，游戏向重运行时，macOS 包**只有 osx-arm64**，缺 osx-x64）、
`ScriptBox`（QuickJS-in-WASM，多一层间接且 drpy 同步 `req` 语义跨 WASM 边界别扭）、
`X.Script.QuickJS`（2026 新包，但**只有 win-x64/win-arm64**，无 osx/linux）。

| 判据 | 旧 CMake bridge | QuickJS.NET 0.0.3 |
|---|---|---|
| 引擎 | quickjs-ng，commit 钉定 + SHA256 | bellard QuickJS（2021，陈旧） |
| AOT 封送 | `LibraryImport`+`UnmanagedCallersOnly`（源码生成，AOT 干净） | `DllImport`/`Marshal`（反射） |
| 6 RID 原生资产 | CI 矩阵每 RID 编译（`build.yml:54-61`） | **不提供**，仍需自备 6 RID |
| 中断/超时/内存上限 | 有（`bridge.c:19,33,41`） | 未暴露 |
| 本仓库已验证 | 是（文档记录 6-RID NativeAOT + 协议冒烟通过） | 未验证 |

**建议（与"用 QuickJS.NET"的原定方向冲突，故需决策）**：对 .NET 10 NativeAOT + 6 RID 目标，
**自建 CMake bridge 是更好的地基**。改用 QuickJS.NET 0.0.3 收益为零（原生库仍要自己编 6 RID），
却要付出引擎陈旧 + AOT 封送回退的代价。若坚持用 NuGet，必须先验证四条：
(1) quickjs-**ng** 而非 bellard；(2) `LibraryImport`/`IsAotCompatible`；
(3) 真有 6 RID 原生资产；(4) 暴露中断/期限 API。**当前没有任何包同时满足。**

务实路径：复用 `bridge.c`+`CMakeLists.txt`+`QuickJsEngine.cs`，扩展 bridge 支持 drpy，
把"QuickJS.NET"理解为"我们自己拥有的 QuickJS 绑定"。B 组整体工作量 **L**
（bridge ABI 是 S，drpy Spider shim + 异步 req + 加密套件是大头）。

## C. mpv 渲染层（S1 的方案）

**当前状态（已确认）**：`MpvEngine` 设了 `vo=libmpv`（`:30-33`）但 `MpvNative` **无 render 绑定**；
`RenderUpdate`/`VideoChanged` 事件（`:18-19`）**零消费者**；`MpvVideoSurface` 只 `FillRectangle(Black)`；
`PlayerOverlay.axaml:12` 是黑矩形 + 文字"libmpv 渲染窗口"。传输层可用，**画面不可见**。

**要移植的旧栈**：
- `MpvNative.Render.cs`（30）—— 5 个 render 绑定 + 3 个结构体
- `MpvRenderContext.cs`（84）—— unsafe，栈上建 `opengl` init params，`GCHandle` 固定 `CallbackState`，
  `[UnmanagedCallersOnly]` 的 `GetProc`/`Updated` **只设原子脏标记**（绝不进 dispatcher 或 mpv API），
  `Render(fb,w,h)` 用 `flip=1, block=0`，`Dispose` 先清回调再在**原 GL 上下文**释放
- `Desktop/MpvVideoSurface.cs`（54）—— `OpenGlControlBase` 子类：`OnOpenGlInit`→建 ctx + 16ms DispatcherTimer；
  `OnOpenGlRender`→`Render(fb, W*scale, H*scale)`；`OnOpenGlDeinit`→dispose；`OnOpenGlLost`→fail；跟踪 `RenderedFrames`
- `MpvClient.cs`（160）—— **正确的低层绑定**：`SafeHandle`(`MpvHandle`)、`lock` 串行化、
  手工构建 NULL 结尾 UTF-8 argv（`:53-70`）、`PollEvent` 拷贝原生结构、`Observe`、`AcquireRenderHandle()` 做 `DangerousAddRef`
- `MpvEngine.cs` 旧（211）—— 惰性初始化、`Observe` 驱动状态（time-pos/duration/pause/seekable/paused-for-cache/track-list）、
  `PumpAsync` 100ms/256事件批处理、加固选项（`config=no, load-scripts=no, ytdl=no, terminal=no, hwdec=auto-safe`）、
  URI scheme 校验、**非 UA/Referer 头拒绝 → 路由到 MediaProxy**（`:70-72`）
- `tests/VodBox.Mpv.Smoke`（78）+ `tests/VodBox.Mpv.UiSmoke`（71 + `DesktopProbe.cs` 210）—— 真 libmpv AOT 探针

### IntPtr vs SafeHandle 冲突（不能裸合）
1. **同名不同 ABI**：两树都定义 `class MpvNative` 但 `Library` 常量不同（旧 `"vodbox-mpv"` shim；新 `"mpv"`/`"libmpv"`/`"mpv-2"`），
   入口风格不同（旧 `Command(handle, nint argv)`；新 `mpv_command(IntPtr, string[])`），旧有 `Check()`/`MpvException`/`MpvHandle` 而新没有。
   **旧 `MpvNative.Render.cs` 直接编译不过当前 `MpvNative`。**
2. **生命周期安全**：旧 `MpvRenderContext` 调 `AcquireRenderHandle()`→`DangerousAddRef`，保证 mpv 句柄比 render context 活得久
   且 Dispose 顺序被引用计数保护。当前 `MpvEngine` 持裸 `IntPtr _ctx` 并在 `DisposeAsync` 调 `mpv_terminate_destroy(_ctx)`，
   而**专用阻塞事件线程**（`mpv_wait_event(_ctx,1000)`，`:196-218`）可能还在碰它。
   把 render context 嫁接到裸 IntPtr 引擎上会丢掉防止 render 线程与 destroy 竞争的引用计数。**这是优先移植 `MpvClient` 的真正理由。**
3. **当前引擎的死代码**：`MpvEngine.Command`（`:70-87`）用 `StringToHGlobalAnsi` 分配 `IntPtr[] array`，
   却调 `mpv_command(_ctx, args)` 传原始 `string[]`，然后释放 `array`——手工封送的指针是死代码，
   完全依赖 `LibraryImport` 的 `string[]` 封送。**（已实测：该封送确实追加 NULL 终止符，故功能正确，但 array 应删）**

**推荐路径（S1）**：**移植整套旧 mpv 栈**（`MpvClient`+`MpvNative`+`MpvNative.Render.cs`+`MpvRenderContext`+`MpvVideoSurface`），
删掉当前 `MpvEngine`/`MpvNative`/`MpvVideoSurface`，**把旧 `MpvEngine` 适配到新 `IPlaybackEngine`**
（`SeekAsync`→`SeekToAsync`+`SeekByAsync`；`SetVolumeAsync(double 0..1)`→`(int 0..100)`；
`AddSubtitleAsync`/`SubtitleSource` 删除或加进新 Core；`TrackKind.Video` 现已存在，`RefreshTracks` 别再只过滤 audio/sub）。
两处摩擦：(a) 旧 `MpvEngine` 需要 `NotifyVideoSurfaceReady/Failure` 接线——新引擎闲置的 `RenderUpdate`/`VideoChanged` 正是钩子；
(b) 旧 surface 在 **Desktop**（引用 Avalonia），但新 `VodBox.Playback.Mpv.csproj` **也已引用 Avalonia 且开了 `AllowUnsafeBlocks`**，
所以 `OpenGlControlBase` 现在可以住在 Mpv 项目里。旧树用**同样的 Avalonia 12.1.3**，API 漂移风险低，
但仍需验证 4 个 `OnOpenGl*` 签名能编译。保留旧加固选项和"头拒绝→代理"规则（当前静默丢弃非 UA/Referer 头，是退步）。**工作量 M。**

## D. 基础设施服务

| 组件 (行数) | 作用 | 结论 | 拖入依赖 | 工作量 |
|---|---|---|---|---|
| **DanmakuTimeline** (`Core/Danmaku.cs:13-109`) | 媒体时钟泳道调度：二分游标、每帧≤256候选、≤128活跃、滚动追尾碰撞+固定泳道占用、seek→重建近8s、500ms 插值 | **DROP-IN**（纯算法） | `Danmaku.cs` 模型（新 Core 无） | S |
| **DanmakuLoader.cs** (94) | XML `<d p="s,mode,size,color">`（1-3滚动/4底/5顶）+ VodBox JSON、gzip/deflate(raw+zlib)/brotli ≤3层各≤8MiB、禁 DTD、UTF-8/16/GB18030、稳定时间排序、100k/512字符上限 | **ADAPT**（`VodBoxJson`→`AppJsonContext`） | `Danmaku.cs`、`TextEncoding`、`BoundedContent` | S/M |
| **PosterCache.cs** (47) | SHA-256 URL 文件名磁盘缓存、4路下载、4MiB/响应、7天TTL、LRU-by-atime 淘汰至160MiB、原子 tmp+move | **DROP-IN**（吃 `HttpClient`） | `BoundedContent` | **S** —— 直接消灭 emoji 海报缺口（S5） |
| **EpgService.cs** (86) | XMLTV 下载+gzip、32MiB上限、6h磁盘+1h内存缓存、禁DTD流式解析、`channel/display-name`+`programme`、tvg-id→名→归一化名匹配、±窗口、≤80 | **ADAPT**（旧 `Programme(ChannelId,…)` vs 新 `Programme(ChannelKey,…,ChannelName)`） | `LiveParser.ParseTime`、`TextEncoding`、`BoundedContent` | S/M —— 填 S4 EPG |
| **MediaProxy.cs** (270) | 仅回环(127.0.0.1:随机)流式代理，处理 mpv 无法逐子资源应用的头：GET/HEAD、Range/If-Range/206、HLS 清单+`URI="…"`重写、**DASH** 嵌套 BaseURL + SegmentTemplate/List/Base 继承物化（保留 `$Number$/$Time$/$RepresentationID$`，16MiB 展开上限）、Location 重写、同源头门控、3会话/16连接、MIME 嗅探前缀 | **ADAPT**（需 `PlaybackRequest.OriginalUri`，新 Core 无） | `BoundedContent`、`TextEncoding` | M/L —— S7 骨干 |
| **PlaybackResolutionService.cs** (83) | JSON 解析链：`{url}` 模板或追加、点路径+数组下标提取（`data.0.url`）、`headersPath`、`nextResolverId` 带环检测/≤6层、8MiB、逐级超时；`_direct`/`_browser`；非 UA/Referer 头路由 MediaProxy | **ADAPT/REWRITE** 到新 `IPlayResolver` | `MediaProxy`、`BrowserMediaResolver`、`ResolverDefinition` | M |
| **BrowserMediaResolver.cs** (121) | 隔离无头 Chrome/Edge/Chromium：临时 profile、`--remote-debugging-port=0`、从 `DevToolsActivePort` 发现端口、**CDP over ClientWebSocket**、自动 `play()`、捕获首个 m3u8/mpd/mp4/webm/mp3（跳过裸分段）、杀进程树+删 profile | **ADAPT** | 无（除 Core 模型） | M —— 使能 `parses type=0` + 斗鱼播放（S8） |
| **BackupService.cs** (58)+**LibraryStore.Backup.cs** (52)+**Core/Backup.cs** (17) | `PortableBackup` v1 导出/导入、64MiB上限、校验格式/版本/身份、历史**按更新时间戳合并**于单事务、收藏并集、导入前自动备份 | **偏 REWRITE 的 ADAPT** —— 合并/校验算法可作规格，但旧 SQLite schema 与 `HistoryEntry`/`FavoriteEntry` 与新结构不同 | 旧 `LibraryStore` 等 | M（S6） |
| **ConfigurationRepository.cs** (84) | 文件快照配置仓库：`index.json`+SHA256(id).json、原子写 | **REWRITE/已被取代**（新基线用 SQLite `IConfigStore`）。只捞 **`AtomicFile.WriteAsync`**（9行，DROP-IN） | — | S |
| **ConfigLoader.Import.cs** (124) | **TVBox→provider 适配器**：`type 0/1`→maccms、`csp_Bili(Guard)`→bilibili（+导入 `ext.json` 分类≤500）、drpy `兔小贝.js/虎牙.js/斗鱼直播.js` 规则路径→原生、`csp_FirstAid`→firstaid、`csp_YGP`→trailers、`lives[]`→LiveSources 带 per-source UA、JPEG尾 `**base64`、`clan://` base64、4路、对未适配站点发 `ImportWarnings` | **ADAPT** —— 这是把真实 `csp_*` 站点路由到原生 provider 的**范本**（新 `ConfigLoader.ToSources` 目前**直接丢弃**每个 csp_ 站点，`:95`）。产出 `SourceInfo` 而非 `SourceDefinition`；映射表+bilibili分类导入是可复用规格 | `BoundedContent`、`TextEncoding`、provider 集 | **M —— S3 高杠杆** |
| **LiveParser.cs** (84) | M3U/TXT/**JSON** 直播 + **XMLTV `ParseTime`**（显式时区）+ 禁DTD | **ADAPT/择优**（新基线有自己的 `M3uParser`/`TxtLiveParser`）；可复用的是 `ParseTime`（EpgService 需要）和 JSON-live | `TextEncoding` | S |
| **TextEncoding.cs** (17) | 严格UTF8→GB18030 回退 + UTF-16 BOM，注册 `CodePagesEncodingProvider` | **DROP-IN**（替换更弱的 `DefaultHttp.Decode`） | `System.Text.Encoding.CodePages` | S |
| **AppPaths.cs** (23) | XDG/macOS(`~/Library/…`)/Windows(`LocalApplicationData`) 数据+缓存目录 | **DROP-IN**（新 `AppServices` 硬编码 `~/.vodbox`） | 无 | S |

## E. Application 层（单内核下是否保留）

| 组件 (行数) | 结论 | 理由 |
|---|---|---|
| **PlaybackCoordinator.cs** (88) | **ADAPT — 保留** | 代次计数会话隔离、取消过期 resolve、串行化原生状态转移、`SaveProgress` 带完播检测（不会把已看完的一集续播在最后一帧）+ 无痕。新基线**无协调器**（`PlayerViewModel` 直接驱动 `MpvEngine`，这正是 S0.2 那些 bug 的根源）。引擎无关 |
| **PlaylistSession.cs** (43) | **ADAPT — 保留** | 上/下集、自动连播、片头片尾跳过、完播进度 —— 正是 S2 |
| **AggregateSearch.cs** (44) | **ADAPT — 保留(升级)** | 4路上限 `Channel<SearchBatch>` 流式 + 逐源超时 + 失败隔离、`IAsyncEnumerable`。新 `SearchAllAsync` 是更简单的 `ConcurrentBag`+`WhenAll`，**无逐源超时无流式**。把模式移进去 |
| **LiveRetryPolicy.cs** (23) | **DROP-IN — 保留** | 纯 int 逻辑：每镜像试一次，手动选择重置循环。零依赖。填 S4 备用线路 |
| **PlaybackEngineRouter.cs** (265) | **REWRITE / 基本丢弃** | 双 LibVLC↔mpv 路由 + 单次自动回退 + 跨引擎状态迁移。12 处 LibVlc/Vlc/Kind 引用——它**由**已被用户否决的双内核决定定义。单 libmpv 下无可路由 |
| **PlaybackEnginePolicy.cs** (36) | **丢弃** | `RequiresVlc()` 把 `smb/ftp/sftp/nfs/upnp/dlna` 路由给 LibVLC；单内核下无意义。唯一可捞的是"每会话至多一次回退、手动选择绝不静默改变"的纪律，作为注释 |

**E 组小结**：保留 Coordinator+Playlist+AggregateSearch+LiveRetryPolicy（约198行），
按单内核决定丢弃 Router+Policy（约301行）。

## F. Desktop UI（旧 50 文件 vs 新 8 页外壳）

旧 Desktop 是**单体 god-`MainViewModel`**（拆成 12 个 partial）+ **自绘控件**；
新基线是**分页 ViewModel** + `DesignAppServices` + 原生控件。
因 `DataContext` 形状根本不同且旧 AXAML 绑 god-VM，**旧 Views 是参考/规格，不可移植**。

对照 `design/DESIGN.md` 的需求，新外壳缺的旧能力：

| 能力 | 旧来源 | 结论 | 路线图 |
|---|---|---|---|
| **弹幕自绘层**（TextLayout 渲染 + 泳道调度绑定） | `DanmakuView.cs`(104)+`MainViewModel.Danmaku.cs`(50) | **ADAPT** | S8 |
| 弹幕**透明子窗口**（LibVLC airspace 变通） | `DanmakuOverlay.cs`(68) | **丢弃** —— mpv 在树内渲染，弹幕画在同一 UI 树（`danmaku.md:49` 确认） | — |
| **EPG 时间轴/节目单** | `Views/ProgrammeView.axaml`(21)+`MainViewModel.Live.cs`(126，每分钟刷新) | **ADAPT/参考** | S4 |
| **轨道选择 UI**（音/字/视频菜单） | `Views/PlaybackControlsView.axaml`(112)+`.cs`(60) | **ADAPT/参考** | S2 |
| **筛选面板**（year/isend 接 MacCMS） | `MainViewModel.Filters.cs`(37) | **ADAPT/参考** | S5 |
| **海报加载/取消/释放**（按可见树门控） | `MainViewModel.Posters.cs`(77)+`MediaCard.cs`(29) | **ADAPT**（配 `PosterCache`） | S5 |
| **虚拟化 2-10 列海报网格** | `Views/LibraryView.axaml`(98)+`MainViewModel.Library.cs`(239) | **参考**（1000项虚拟化/分页/重排已验证，但绑 god-VM） | S5 |
| **选集浏览器**（标题搜索、倒序、>20分段、已看标记） | `Views/EpisodeBrowserView.axaml`(40)+`MainViewModel.Details.cs`(40) | **ADAPT/参考** | S5 |
| **真网解码诊断**（`--appget-smoke/--app99-smoke/--bilibili-smoke/--audio-site-smoke/--native`） | `Diagnostics.cs`(246) | **ADAPT** —— 这是**产出上面所有验证结论的工装**，移植后可重新验证复用爬虫 | 复验 |
| **推送页**（二维码+Web遥控） | **旧代码无** —— 文档明说推送/投屏"明确禁用，未伪装已有业务能力"（`desktop-layout.md:53`） | S7 是全新工作 | S7 |
| **设置 8 分组** | `SettingsWindow`+`Settings{General,Playback,Danmaku}View`（3组） | **参考**（仅 3/8） | S6 |
| 无头真 XAML 预览测试 | `DesktopPreviewTests.cs`(367) | **REWRITE** | — |

**F 组小结**：约 2500 行 Desktop 中，可复用 ≈ `DanmakuView`(104)+`Diagnostics`(246)
+`MainViewModel.{Posters,Filters,Danmaku,Live,Details}` 逻辑(约340) 作 ADAPT；
约 15 个 AXAML 视图 + god-VM 管道(约1800) 作参考/规格。全部绑旧 VM+LibVLC+旧模型，无 DROP-IN。

## G. 测试（22 文件）

| 套件 (行数) | 可移植？ | 说明 |
|---|---|---|
| **BilibiliProviderTests**(247)、**AppGetProviderTests**(299)、**App99ProviderTests**(161)、**AudioSiteProviderTests**(126)、**PublicSiteProviderTests**(191) | **ADAPT — 随各 provider 移植** | 自定义 `HttpMessageHandler` 假件（`Routes`/`Responses`）、独立 OpenSSL 加密向量、协议/身份/取消/重定向断言。**fixture 和加密向量是金子**，只有 cursor→page 和 record 形状断言要改 |
| **DanmakuTests**(154) | **近乎整体** | XML/JSON/gzip/禁DTD/取消/密集调度/seek/延迟/设计预览 |
| **DashProxyTests**(45)+**ResolutionTests**(136) | **ADAPT** | 回环 HTTP：DASH 嵌套/继承模板、HLS 密钥、Range、解析链环检测/编码 |
| **MpvEngineTests**(117) | **ADAPT** | 用假 `IMpvClient` —— 随旧 mpv 栈移植（**保留 `IMpvClient` 抽象以维持可测性**）。锁定 S1 引擎行为 |
| **Mpv.Smoke**(78)+**Mpv.UiSmoke**(71+`DesktopProbe.cs` 210) | **整体（改构造/契约）** | 真 libmpv AOT 探针；**这是 S1 的验收工装**（需 `VODBOX_MPV_PATH` + ≥3s fixture）。CI 不自动跑 |
| **BackupTests**(105) | **ADAPT** | 按新 schema 重写 |
| **PlaybackCoordinatorTests**(76)、**PlaylistTests**(66) | **ADAPT** | 随 E 组移植 |
| **InfrastructureTests**(101)、**CacheAndLibraryTests**(75)、**ConfigurationImportTests**(92)、**SearchPagingTests**(70) | **ADAPT/择优** | |
| **PlaybackEngineRouterTests**(217)、**PlaybackEnginePolicyTests**(44) | **丢弃** | 双内核，单 mpv 下过时 |
| **HomeFlowTests**(175)、**CollectionPageTests**(159)、**FeatureTests**(116)、**DesktopPreviewTests**(367) | **REWRITE** | 绑旧 god-VM/Desktop |

**整体/近乎整体**：DanmakuTests、DashProxyTests、MpvEngineTests、Mpv.Smoke、Mpv.UiSmoke。
**丢弃**：Router+Policy(261)。**重写**：desktop-flow(约817)。

## H. 构建 / CI

| 资产 (行数) | 结论 | 说明 |
|---|---|---|
| `build/quickjs/CMakeLists.txt`(14) | **DROP-IN** | 已经 6-RID（CI 矩阵）；FetchContent 钉 quickjs-ng commit+SHA256 |
| `build/quickjs/bridge.c`(53) | **ADAPT** | 保留 VM 生命周期/中断/上限 ABI；**为 drpy Spider 面扩展宿主操作** |
| `build/analyze-spiders.py`(136) | **DROP-IN** | **生成 `spider-inventory.json` 的工具**（JSON/注释/gzip/JPEG尾base64；存 SHA-256 不存原文，无 cookie/密钥/ext） |
| `build/bundle.py`(177) | **ADAPT** | 拷贝钉定原生运行时+写许可证/声明+校验依赖哈希。QuickJS 在 `:99-107`（打包 `*vodbox_quickjs.{dylib,so,dll}`+LICENSE）。**剥掉 VLC 条目；加 libmpv** |
| `build/package.py`(139) | **ADAPT** | 每 RID 安装包。丢弃 VLC 依赖 |
| `build/smoke.py`(49) | **ADAPT** | 发布后 AOT 冒烟跑 `VodBox.PluginHost demo.js`（`:24`）—— QuickJS/drpy 工装 |
| `build/archive.py`(15) | **DROP-IN** | 符号分离 + tar.gz |
| `build/native-assets.json`(83) | **ADAPT** | 钉定 python/node/**vlc** 的 6-RID URL+SHA256。**不含 quickjs 也不含 mpv** —— QuickJS 是 CI 内**按 RID 从源码编译**；mpv 运行时期望存在。移除 vlc；加 libmpv 每 RID URL+SHA256 |
| `.github/workflows/build.yml`(83) | **ADAPT** | 6-RID AOT 矩阵、`restore --locked-mode`、测试、**cmake quickjs**（`:54-61`，win-arm64 用 `-A ARM64`）、发布 Desktop+PluginHost AOT 带 `TreatWarningsAsErrors`、bundle→smoke→archive→上传。**改**：删 `libvlc-dev vlc-plugin-*` apt（`:49`），保留 `clang cmake zlib1g-dev`，加 libmpv，决定进程内 vs PluginHost |
| `ci.yml`(27，仅手动)、`package.yml`(55)、`release.yml`(43) | **ADAPT/DROP-IN** | ci.yml 手动触发（文档记录为省 Actions 额度暂停） |
| `Directory.Packages.props`(17) | **ADAPT** | 移除 `LibVLCSharp 3.10.1`；保留 Avalonia 12.1.3 等；加 `System.Text.Encoding.CodePages` |

**QuickJS 6 RID 的具体答案**：旧设计**不发布预编译 QuickJS 二进制**。
`CMakeLists.txt` FetchContent 钉定源码 tarball，CI 矩阵**按 RID 编译** `vodbox_quickjs`（`build.yml:54-61`），
`bundle.py:99-107` 把产物 + LICENSE 拷进各发布。Linux 需 `clang cmake zlib1g-dev`（`build.yml:49`）；win-arm64 用 `cmake -A ARM64`。
这就是文档能声称"六 RID 本机 Native AOT"的原因。**改用 QuickJS.NET NuGet 继承不到这些**——它不提供 RID 原生资产，
仍须自建 6 RID quickjs，即无论如何都要保留 CMake 管线。这是复用 bridge 的最强论据。

## Q1 — spider-inventory.json 摘要

2026-10-05 由 `build/analyze-spiders.py` 从两个真实配置生成（不存原文只存 SHA-256：饭太硬 `aa410c48…`、宝盒 `ede90a4f…`）。
计数单位 = **站点条目**（配置使用次数，**不是**独立站点数，**不是**可播放率）；**Guard 变体不合并**。

- **98 个站点条目**：饭太硬 48（45 java + 3 js）+ 宝盒 50（49 java + 1 js）。两者各带 **6 个直播源**。
- **70 个不同插件条目** = **69 个精确 `csp_*` Java api 名 + 1 个 JS 引擎**（`script:drpy2.min.js`）
- **实测复核（我用当前两配置重算）**：java-spider 94 条目中，**仅 11 个入口出现≥2次，覆盖 36/94 = 38%**；
  **58 个入口只出现 1 次（长尾）**。前 5 入口覆盖 25%，前 10 覆盖 36%，前 20 覆盖 47%，前 30 覆盖 58%。

| # | 入口 | 次数 | 出处 |
|---|---|---|---|
| 1 | `csp_Bili` | 9 | 宝盒 |
| 2 | `csp_BiliGuard` | 7 | 饭太硬 |
| 3 | `csp_AppGet` | 4 | 宝盒（2 个声明的 JAR 变体） |
| 4 | `script:drpy2.min.js` | 4 | 饭太硬3 + 宝盒1 |
| 5 | `csp_App99` | 2 | 宝盒 |
| 6 | `csp_AppDrama` | 2 | 宝盒 |
| 7 | `csp_AppSxGuard` | 2 | 饭太硬 |
| 8 | `csp_AppYd` | 2 | 宝盒 |
| 9 | `csp_S_zpsGuard` | 2 | 饭太硬 |
| 10 | `csp_T4Guard` | 2 | 饭太硬 |
| 11 | `csp_XBPQ` | 2 | 宝盒（xsmp3/psmp3 音频） |
| 12 | `csp_XPath` | 2 | 宝盒（导航+提醒，非点播） |
| 13-70 | 58 个入口 | 各 1 | Guard/云盘/磁力/体育/音频 混合 |

**含义**：Bili 家族 = 16 条目（16.3%）优先 → `csp_AppGet`(4) → App99 家族(3) → 其它 2 次入口 → 4 个 drpy 归 QuickJS 任务。
**旧代码已覆盖排名 1-5、11（音频 XBPQ），加长尾里的 FirstAid/YGP/tuxiaobei/huya/douyu 和 drpy 规则**——
即复用 provider 几乎精确对齐本排名头部。

⚠️ **但长尾意味着：移植全部 69 个入口不现实**（58 个单次入口，每个都是独立站点协议逆向）。
现实目标是覆盖头部 ~11 个入口（38% 条目）+ drpy（4 条目），并接受大量长尾站点不可用。

## Q2 — 真机验证 vs 仅 fixture vs 未验证

**真机验证（真实 macOS x64 网络 + 实际解码）**
- **Bilibili** —— JIT+AOT 均取得热门20/搜索20/一集/1200弹幕，VideoToolbox 解码 H.264（`spider-migration-priority.md:35`）
- **AppGet/咕咕** —— JIT+AOT 实际解码通过（`:61,:77`）
- **App99/双星** —— **仅 JIT**（187视频/470音频块）；**AOT 明确未记录**："初次首条媒体 HTTP 403…因此不记录为 App99 AOT 实际解码通过"（`app99-spider.md:25,31`）
- **AudioSite/评书** —— **AOT 验证**（3.21s/332音频块）；**相声 AOT 未过**（TLS 超时），JIT 过
- **兔小贝/虎牙/急救教学/荐影** —— **真机 AOT 验证**（`public-site-spiders.md:24`）
- **斗鱼** —— **JIT 验证**（58视频/184音频块）；**"AOT 的该代理播放链路尚待验证"**

**仅 fixture（无真实解码）**
- **AppGet Qiji-v122** —— "仅通过 fixture；参考站点发现文件 TLS 错误"（`appget-spider.md:44`）
- **App99Guard** —— "尚未核实"
- **MacCmsProvider** —— "合成 HTTP fixture 测试通过"
- **AppGet 一碗/蔬菜/首发** —— 内容过但媒体/解析失败（vodParse code=0、HLS 失败、503）

**未验证/从未注册**：`csp_AppDrama`（公开 protobuf 引用是空占位）、`csp_AppYd`（源文件搜索为空）、
所有其它 **Guard 变体**、以及 `public-site-spiders.md:28-34` 列出的 36 项剩余条目。

### ⚠️ 最重要的警告
**几乎所有爬虫解码验证走的是 LibVLC，不是 libmpv。** libmpv 的解码证明是独立的、且在**合成媒体**上
（`danmaku.md:45`："15 秒合成视频…Native AOT 驱动绘制 59 帧"；`mpv-engine.md:15`：15s 受控样本）。
**没有任何爬虫通过 mpv 渲染路径做过端到端验证** —— 所以移植 C 组之后，
**必须把 A 组爬虫重新跑一遍 mpv，不能假设 LibVLC 的解码记录可以继承。**

## Q3 — parses / IPlayResolver 是否可用

**解析引擎存在且可复用，但它不消费 TVBox `parses[]`。** 宝盒的 17 个 parses 两树都不读：
- **新 Core** 声明 `IPlayResolver`（`Contracts.cs:16`）+ `TvBoxParse`/`TvBoxConfig.Parses`（`Models.cs:14,55`），
  但 `git grep Parses HEAD -- src/*` **只返回模型声明**，零消费者
- **旧** 有 `PlaybackResolutionService.cs`(83) 实现 `IPlaybackResolver` —— JSON 解析链 + `_direct`/`_browser` + 头→MediaProxy 路由，
  加 `BrowserMediaResolver.cs`(121) 做网页嗅探。**但其解析定义来自 VodBox 私有的 `resolvers[]`**
  （`Core/Resolvers.cs`），**不是 TVBox parses**。已确认 `git grep parses 9a67c2d^ -- ConfigLoader.Import.cs` → **无匹配**
- **支持**：type-1 等价的 JSON 解析（`kind:json`、点路径提取、headers path）和 type-0 等价的网页嗅探（`kind:browser` via CDP）。
  **不实现** TVBox `parse.type` 语义（0=web/1=json/2=jsonExt/3=jsonMix/4=超级解析）和 `flags`/`flag` 匹配

**实测宝盒 17 个 parses 的 type 分布：type=0（web嗅探）10 个、type=1（json）3 个、type=3（jsonMix聚合）4 个。**
即**最需要的是 type=0 网页嗅探**（需 `BrowserMediaResolver`），type=1 JSON 解析较易。

**结论**：引擎（`PlaybackResolutionService`+`BrowserMediaResolver`+`MediaProxy`）**ADAPT 可复用**
（需 `ResolverDefinition` 重回 Core、`ResolutionKind.Browser`→`Sniff`、`PlaybackRequest.OriginalUri/ResolverId` 重加）。
**TVBox `parses[]`→resolver 的导入器必须全新写**（哪棵树都没有）。归 S8。工作量 **M**。

## Q4 — 系统代理 / DoH / per-host 头

**两树都没有显式代理或 DoH 配置——无可复用，是全新工作。**
`git grep -nE "UseProxy|WebProxy|IWebProxy|\.Proxy|doh|dns" 9a67c2d^ -- src/*` 只命中 `MediaProxy` 的回环字段（无关）。
旧文档列为未做 TODO："壁纸、DoH/代理设置仍待继续实现"（`desktop-layout.md:53`）。

**实测结论（S0 已验证）**：当前 `DefaultHttp` **不需要**显式代理配置——.NET 在 macOS 经 `MacProxy` 自动读取系统代理，
宝盒接口.top 实测成功加载（43523 字节）。这是 S6 设置项（DoH/代理），不是 S0 阻塞。
但**注意**：宝盒耗时 5.3s，而 `DefaultHttp` 有 12s 硬超时，余量偏紧；
且 tvbox 配置的 `doh`/`proxy`/`hosts`/`headers`/`ads` 字段虽解析进 `TvBoxConfig` 但从未应用。

## Q5 — IDN/中文域名处理

**旧代码没有** IDN 处理（grep 无命中）。**S0 已新增** `DefaultHttp.NormalizeIdn`（用 `IdnMapping.GetAscii`），
因为 .NET 的 `Uri.Host` 保留 Unicode 原文（只有 `Uri.IdnHost` 是 Punycode），站点 host 与 ASCII 比对会静默失配。
已在 `tests/VodBox.Tests/IdnUrlTests.cs` 锁定。
