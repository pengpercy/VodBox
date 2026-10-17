# SixV 磁力串流实现证据

2026-10-18；分支 `v3-native-spiders`。本文记录本切块交付的代码设计，不代表已完成在线/实播验收。未执行 build/test/typecheck。

## 交付文件

- `src/VodBox.Infrastructure/SixVSource.cs`：`IResolvingContentSource` 实现（首页/分类/分页/搜索/详情，磁力分集身份）。
- `src/VodBox.Infrastructure/TorrentStreamServer.cs`：loopback BitTorrent 串流后端（magnet → 元数据 → 文件选择 → 可 seek HTTP Range）。
- `src/VodBox.Infrastructure/VodBox.Infrastructure.csproj`：新增 `MonoTorrent` 3.0.2 / `Mono.Nat` 3.0.4 引用。
- `Directory.Packages.props`：集中版本登记（与本机临时 MonoTorrent 探针一致；探针用完已清理）。

原 Java 来源：`scratchpad/payload/clean/com/github/catvod/spider/SixV.java`（`payload.jar` 反混淆结果）。站点 ext 为首页 URL（真实 ext = `https://www.xb6v.com/`）。

## SixV provider 设计

- `Supports(ext)`：要求 `ext` 解析后是 HTTP(S) 站点首页（scheme http/https、无 userinfo/query/fragment、路径为 `/`）。既接受明文 URL，也接受 FTY v8 加密后的同一明文（走 `FtyExtCodec.JsonConfig`）。空 ext 直接拒绝，不臆造默认站点。
- 分类：Java `homeContent` 的 12 个固定目录（`dianshiju/guoju`…`donghuapian`），`GetHomeAsync` 与 Java 一样只返回空列表，不伪造卡片。
- 分页：`/{typeId}`（第 1 页）/ `/{typeId}/index_{page}.html`（第 N 页）。卡片来自 `#post_container .post_hover` 内首个 `class=zoom` 锚点（href/title/img src），备注取块内 `rel="category tag"`。Java 固定 `pagecount=999`；这里仅在确有卡片时沿用，空页返回当前页以停止无限翻页。
- 搜索：`POST /e/search/11index.php`，表单字段与原 Java 完全一致（`show=title`、`tempid=1`、`tbname=article`、`mid=1`、`dopost=search`、`submit=`、`keyboard=<query>`），带 UA/Origin/Referer。原接口无翻页，第 N(N>1) 页返回空。
- 详情：`#post_content` 内 `a[href^=magnet]` 锚点构成磁力分集；分集 `Id` = `magnet` + `\u0001` + magnet URL，单线路 `LineId=magnet`。标题取 `.article_container > h1`，封面取 `#post_content img`，年份/地区/类别/上映日期/主演/导演/简介按原 Java 的 `◎…` 正则做 best-effort 提取。
- 解析：`ResolvePlaybackAsync` 以详情页为权威来源校验 magnet 归属，再交给后端注册，返回本机回环播放地址。请求头/方法/防盗链只在抓取阶段使用。

## BitTorrent 串流后端设计（TorrentStreamServer）

- 引擎：单例 `ClientEngine`，懒启动。默认 `EngineSettings` 启用 DHT（含 bootstrap 路由）与 UPnP/NAT-PMP 端口映射（`Mono.Nat`）。
- 会话：`RegisterAsync(magnet)` → `AddStreamingAsync(magnet, cacheDir)` → `StartAsync` → `WaitForMetadataAsync` → 选择文件 → `StreamProvider.CreateStreamAsync(file, prebuffer:false)`。同一 magnet 复用已有会话；最多 4 个会话、闲置 30 分钟回收，容量满时淘汰最久未使用会话（磁力分集每个是独立 swarm）。
- 文件选择：优先体积最大的视频扩展名（mp4/mkv/avi/…），否则退回体积最大的文件。
- 本地服务：ASP.NET Core `WebApplication.CreateSlimBuilder` + Kestrel 仅绑定 `127.0.0.1:0`，路由 `/torrent/{id}.{ext}`，仅接受回环连接且 Host 必须与监听地址一致（对照 `AiDjMediaProxy`）。
- 生命周期：`StopAllAsync` / `IAsyncDisposable.DisposeAsync` 会依次 dispose 各 `LocalStream`、`StopAsync` 各 `TorrentManager`、`RemoveAsync`、停止 Kestrel、`StopAllAsync` 并 dispose 引擎。共享实例 `TorrentStreamServer.Shared` 由 `SixVSource` 默认注入。

## Range / seek 语义

- 支持 `Range: bytes=start-end`、`bytes=start-`、`bytes=-suffix`；非法或越界返回 `416` 并带 `Content-Range: bytes */total`。
- Range 请求返回 `206` + `Content-Range: bytes start-end/total`；非 Range 返回 `200` 全量。两者都设置 `Content-Length`、`Accept-Ranges: bytes`、`Cache-Control: no-store`。`HEAD` 只返回头。
- 读数据通过 MonoTorrent 可 seek 的 `LocalStream`：每 64 KiB 分块内串行 `seek + read`，块间释放 `SemaphoreSlim`。这样既保证 LocalStream「同一时刻一个读位置」的约束，又允许 MP4 moov 尾读与正片首读等并发 Range 交错（与 MonoTorrent `HttpStream` 的做法一致）。
- seek 到尚未下载的位置时，`LocalStream.ReadAsync` 会阻塞到对应分片就绪，实现边下边播。

## AOT 注意事项

- MonoTorrent 3.0.2 + Mono.Nat 3.0.4 已用一个临时探针（`PublishAot=true`、`TreatWarningsAsErrors=true`）验证引擎可加载（`ENGINE_OK`）；探针用完已清理，本切块沿用同一版本。
- 不使用 MonoTorrent 自带的 `HttpListener` 串流（`CreateHttpStreamAsync`），改用 Kestrel + `CreateStreamAsync` 的 `LocalStream`，避免额外 `HttpListener`/反射依赖路径，并与项目现有 loopback 代理风格一致。
- 未引入任何反射式 JSON/序列化；HTML 解析用 `[GeneratedRegex]` 与固定 `Regex`，无动态代码生成。
- 仍待主 Agent 在 NativeAOT 发布会话中验证整条 magnet 流路径（元数据、DHT 连通、Range 读）无 trim/AOT 告警。

## 需要主 Agent 落地的注册行（未自行修改 NativeSpiders.cs）

`src/VodBox.Infrastructure/NativeSpiders.cs`：

- `Factories` 增加：`["csp_SixVGuard"] = info => new SixVSource(info),`
- `IsSupported(TvBoxSite site)` 增加：`"csp_SixVGuard" => SixVSource.Supports(Ext(site)),`
- `ExtGated` 增加：`"csp_SixVGuard",`

`SixVSource.csproj` 依赖（`MonoTorrent`/`Mono.Nat`）已加在 `VodBox.Infrastructure.csproj`。若还有其它注册表（如 `SourceRegistry` 的 api→工厂映射或文档计数）需同步，由主 Agent 决定。

## 未完成 / 风险点

- 未 build/test/typecheck；`SixVSource` 与 `TorrentStreamServer` 的编译、NativeAOT 发布、真实站点抓取（`https://www.xb6v.com/`）与真实磁力 DHT/peer 连通、libmpv 实播/seek 均由主 Agent 统一验证。
- 未执行原 Java 逐字段差分；抓取选择器（`.post_hover`/`[class=zoom]`/`rel="category tag"`）按 Java 语义实现，但真实站点 HTML 结构若与 payload 时期不同，解析需按在线页面回归。
- `Supports` 只接受站点首页形态；若真实配置的 ext 含子路径或 query，需按证据调整门控。
- 详情内字段（年份/地区/类别/上映日期/主演/导演/简介）为 best-effort 提取，未逐字节对照 Java；不影响磁力分集身份与播放链路。
- 端口映射（UPnP/NAT-PMP）依赖网络环境；不可用时仅影响可达性，不影响本机串流。DHT/bootstrap 路由可用性、tracker 连通、无公网 IP 场景下的 peer 获取速度未验证。
- `LocalStream` 并发 Range 交错时会频繁 seek；极端并发下可能增加分片重排开销，未做压测。
- 未实现在线验证，不把「引擎可加载」当作「已可播验收」。
