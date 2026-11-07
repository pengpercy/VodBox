# VodBox

跨平台桌面影音应用，点播源解析方式参照 [LunaTV](https://github.com/MoonTechLab/LunaTV)（苹果CMS V10 采集源 + 豆瓣数据）。**Avalonia 12 + .NET 10（NativeAOT）+ libmpv**，覆盖 **macOS / Windows / Linux**（x64 + arm64，共 6 个 RID）。

订阅源怎么写、格式说明与示例见 **[订阅源格式说明](docs/SOURCE-CONFIG.md)**。

设计与交互稿见 [design/DESIGN.md](design/DESIGN.md)：Mica/Fluent 深色主题、左侧导航 Shell、影院式详情页、全局播放浮层。

## 已实现（0.2.4）

- **配置订阅（LunaTV config.json）**：`api_site` / `custom_category` / `lives` / `cache_time`，JSON 或 base58 编码；重新拉取订阅时保留用户对源的启用状态与排序（refineConfig 合并语义），默认每小时最多自动检查一次。
- **采集源维护**：启用/禁用、排序、添加/删除自定义源、批量启停、关键词有效性检测与一键禁用无效源。
- **聚合搜索**：全部启用源并发流式搜索（多页、10 分钟缓存、黄色分类过滤），按「标题 + 年份 + 电影/剧集」合并；详情页列出同名影片在各源的播放源，可换源（保持集数），按分辨率/速度/延迟测速自动优选。
- **豆瓣**：首页与点播页的热门/推荐/Top250/自定义分类均来自豆瓣；豆瓣数据代理与图片代理支持 LunaTV 的各模式；采集源缺海报时按片名+年份从豆瓣刮削。动漫「每日放送」来自 Bangumi。
- **去广告**：本机媒体代理过滤 HLS 播放列表中插播的广告片段（可在设置中关闭）。
- **直播**：M3U（group-title/tvg-logo/tvg-id）与 TXT（`分组,#genre#`）双格式解析、多线路、频道面板（分组/筛选）。`catchup` / `catchup-source` 回看属性尚未解析（计划 S4）。
- **播放器**：libmpv 唯一内核，SafeHandle 生命周期 + 锁串行化 + 手工 NULL 结尾 UTF-8 argv；`LibraryImport` 源码生成 P/Invoke + `DllImportResolver` 按平台候选路径加载（`VODBOX_MPV_LIB` 可覆盖）；播放/暂停/Seek/倍速/音量、UA-Referer（引擎层可设，站点配置暂未透传）、断点续播、历史进度续播（含线路/选集/海报上下文）。
- **视频渲染（S1 已落地）**：`mpv_render_context_*` OpenGL 渲染经 `OpenGlControlBase` 子类 `MpvVideoSurface` 上屏（16ms 脏标记驱动、`flip=1, block=0`、DPI 缩放、GL 丢失上报）；引擎等待渲染面握手（`waitForVideoSurface`）后才 loadfile，消除静默丢画面竞态。macOS 本机 JIT + NativeAOT 均验证真帧渲染（见 `tests/VodBox.Mpv.UiSmoke`）。
- **数据**：SQLite（Microsoft.Data.Sqlite）历史/收藏/配置订阅/偏好；多站点并发聚合搜索。
- **UI**：8 页面（首页/点播/详情/直播/搜索/收藏/历史/本地文件）+ 设置中心 + 播放浮层；本地文件页可直接播放；全视图 `Design.DataContext` 设计预览数据（Rider 打开 axaml 即可预览，不触网不触库）。
- **AOT 约束**：全 JSON 源码生成（`JsonSerializerIsReflectionEnabledByDefault=false`）、`PublishAot` 六 RID 发布、无反射容器（手写组合根）。

## 已知限制（0.2.4）

- **非 UA/Referer 请求头暂拒绝**：mpv 只能全局设置 UA/Referer，其余请求头（Cookie 等）需媒体代理转发——引擎目前明确报错而非静默丢弃（媒体代理计划 S7）。
- **字幕外挂接口未移植**：旧栈的 `AddSubtitleAsync`/`SubtitleSource` 在新契约无位置，S1 未移植（在线/本地字幕计划 S8）。
- **轨道切换**：播放器设置菜单已支持音轨/字幕轨与关闭字幕；多轨真实媒体的完整原生交互仍待验收。
- **不记录播放历史**：播放器设置菜单可开关并持久化；开启后不写新的切集/结束/关闭历史，不删除已有历史、不隐藏网络请求。
- **历史进度**：只在关闭播放浮层或播放结束/失败时落库（无周期性保存）；且播完（`Ended`）时快照位置为 0，该集不会保留续播点。
- 只支持苹果CMS V10 采集接口；不再支持 TVBox / FongMi 配置中的 jar/js 爬虫、type4 与解析接口。
- 网页播放页（`.html` 分享页、云播放器页面）线路会被丢弃，因为 mpv 无法播放网页。

## 开发

要求 .NET SDK 10.0.400（`global.json` 固定）。

**播放前先在开发机上准备原生库**：发布包由 `build/bundle.py` 把 libmpv 与媒体依赖打进产物，但 `bin/<Config>/net10.0` 默认没有它们，直接运行会在播放时报 `DllNotFoundException: vodbox-mpv`。

```sh
bash build/dev-natives.sh          # 自动识别 RID，也接受 osx-x64 / linux-arm64 / win-x64 等
```

脚本把 libmpv 与其依赖闭包放入 `.cache/dev-natives/<rid>/`，并同步到已有 `bin` 目录；Desktop 项目在构建时也会自动复制，因此 IDE 里改完直接跑即可。首次准备可指定来源：

```sh
bash build/dev-natives.sh osx-x64 --from artifacts/publish/osx-x64
```

也可以继续用环境变量覆盖：`VODBOX_MPV_LIB` 指向具体库文件。

```sh
dotnet restore VodBox.slnx
dotnet build VodBox.slnx -c Debug
dotnet test tests/VodBox.Tests/VodBox.Tests.csproj
dotnet run --project src/VodBox.Desktop
```

**排查播放问题**：设置 → 诊断 可开关日志、打开日志目录并查看最后若干行；日志位于 `<数据目录>/logs/vodbox-YYYYMMDD.log`（macOS 为 `~/Library/Application Support/VodBox/logs`）。日志记录选台、线路、mpv 打开/载入/结束原因与失败异常，地址中的 `key`/`authid`/`token` 会自动脱敏。需要 mpv 原始日志时设 `VODBOX_LOG_VERBOSE=1`。

首次启动 → 设置 → 播放源 → 填入 LunaTV 配置订阅地址（如 `https://example.com/config.json`），或直接粘贴配置内容；直播默认使用配置中的 `lives`，也可手动填 m3u 地址。配置文件格式、base58 订阅链接制作和采集接口要求见 [订阅源格式说明](docs/SOURCE-CONFIG.md)。

## 打包与发布

推送 `vX.Y.Z` tag（与 `VERSION` 一致）触发 Release：每 RID 在对应本机 runner 上 NativeAOT 构建 → 所有平台捆绑钉定 SHA256 的 libmpv 与媒体依赖（Windows 额外包含 Vulkan 加载器，Linux 递归收集完整依赖闭包） → 产物为 Windows ZIP、macOS `.app`/ZIP/DMG（ad-hoc 签名）、Linux deb/rpm（fpm）。CI 全量构建 + 测试在每次 push 运行。

```sh
# 本机 AOT 发布（macOS x64 示例）
bash build/publish-aot.sh osx-x64 artifacts/publish/osx-x64
# 脚本先按 Release + RID restore，再以相同参数 --no-restore publish。
# 普通 solution/Debug restore 不产生 net10.0/osx-x64 的 AOT 资产；不能直接复用。
python3 build/bundle.py osx-x64 artifacts/publish/osx-x64
python3 build/package.py osx-x64 artifacts/publish/osx-x64 artifacts/packages --version 0.2.4
```

## 结构

```
src/VodBox.Core           领域模型 + LunaTV 配置/MacCMS 契约（零依赖，JSON 源码生成）
src/VodBox.Infrastructure 配置订阅、MacCMS 采集、聚合搜索、豆瓣、测速、HLS 去广告、直播解析、SQLite 存储
src/VodBox.Playback.Mpv   libmpv P/Invoke（LibraryImport）+ DllImportResolver + SafeHandle 客户端 + OpenGL 渲染面
src/VodBox.Desktop        Avalonia Shell + 8 页面 + 播放浮层（CommunityToolkit.Mvvm）
tests/VodBox.Tests        领域回归测试：配置/MacCMS/聚合/豆瓣/去广告/测速/直播/存储/续播上下文/MpvEngine
tests/VodBox.Mpv.Smoke    真 libmpv headless 传输层 + 引擎冒烟（需 VODBOX_MPV_LIB + ≥3s 视频 fixture）
tests/VodBox.Mpv.UiSmoke  真 libmpv OpenGL 渲染冒烟：断言 RenderedFrames>0、位置推进、UI 树内叠加控件
build/                    mpv 资产捆绑、打包、冒烟、归档脚本
```


## 隔离的原生桌面验收

在带图形桌面的目标机器上，先完成发布及 mpv 捆绑，再运行：

```bash
bash build/test-desktop-smoke.sh artifacts/publish/osx-x64/VodBox.Desktop
```

需要 ffmpeg。脚本在项目 `.alma` 下创建临时数据与合成媒体，验收真实窗口、八页面/八设置分组、
主题切换和主播放器渲染帧/暂停/seek/全屏往返/小窗恢复，正常退出后清理自己的测试文件。
不会加载现有订阅或改写用户库存储。Windows/Linux/arm64 必须在对应图形环境运行，
macOS x64 的通过结果不能当作其他平台通过。


完整媒体内核捆绑的开发变更与最低系统要求见 [媒体发行说明](docs/MEDIA-BUNDLING.md)。0.2.3 起，Windows/Linux 发行包也包含完整媒体内核，无需额外安装系统 libmpv 或 FFmpeg。
