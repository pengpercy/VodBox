# VodBox

基于 [FongMi/TV](https://github.com/FongMi/TV) 重新设计的跨平台桌面影音应用。**Avalonia 12 + .NET 10（NativeAOT）+ libmpv**，覆盖 **macOS / Windows / Linux**（x64 + arm64，共 6 个 RID）。

设计与交互稿见 [design/DESIGN.md](design/DESIGN.md)：Mica/Fluent 深色主题、左侧导航 Shell、影院式详情页、全局播放浮层。

## 已实现（0.2.0）

- **TVBox 配置体系**：远程订阅加载、JS 变量包裹、`//` 行注释剥离、JPEG 尾部 base64 隐写（饭太硬 `in.bmp` 形态）、宽容字段解析（`type_id` 数字/字符串、`ext`/`style` 对象、`rules` 数组）——与 FongMi Gson 容错行为对齐。
- **点播**：MacCMS（苹果CMS V10 JSON 采集）站点分类/分页/筛选/详情/搜索全链路；`vod_play_from$$$vod_play_url` 线路/选集拆分。
- **直播**：M3U（group-title/tvg-logo/tvg-id）与 TXT（`分组,#genre#`）双格式解析、多线路、频道面板（分组/筛选）。`catchup` / `catchup-source` 回看属性尚未解析（计划 S4）。
- **播放器**：libmpv 唯一内核，SafeHandle 生命周期 + 锁串行化 + 手工 NULL 结尾 UTF-8 argv；`LibraryImport` 源码生成 P/Invoke + `DllImportResolver` 按平台候选路径加载（`VODBOX_MPV_LIB` 可覆盖）；播放/暂停/Seek/倍速/音量、UA-Referer（引擎层可设，站点配置暂未透传）、断点续播、历史进度续播（含线路/选集/海报上下文）。
- **视频渲染（S1 已落地）**：`mpv_render_context_*` OpenGL 渲染经 `OpenGlControlBase` 子类 `MpvVideoSurface` 上屏（16ms 脏标记驱动、`flip=1, block=0`、DPI 缩放、GL 丢失上报）；引擎等待渲染面握手（`waitForVideoSurface`）后才 loadfile，消除静默丢画面竞态。macOS 本机 JIT + NativeAOT 均验证真帧渲染（见 `tests/VodBox.Mpv.UiSmoke`）。
- **数据**：SQLite（Microsoft.Data.Sqlite）历史/收藏/配置订阅/偏好；多站点并发聚合搜索。
- **UI**：8 页面（首页/点播/详情/直播/搜索/收藏/历史/本地文件）+ 设置中心 + 播放浮层；本地文件页可直接播放；全视图 `Design.DataContext` 设计预览数据（Rider 打开 axaml 即可预览，不触网不触库）。
- **AOT 约束**：全 JSON 源码生成（`JsonSerializerIsReflectionEnabledByDefault=false`）、`PublishAot` 六 RID 发布、无反射容器（手写组合根）。

## 已知限制（0.2.0）

- **非 UA/Referer 请求头暂拒绝**：mpv 只能全局设置 UA/Referer，其余请求头（Cookie 等）需媒体代理转发——引擎目前明确报错而非静默丢弃（媒体代理计划 S7）。
- **字幕外挂接口未移植**：旧栈的 `AddSubtitleAsync`/`SubtitleSource` 在新契约无位置，S1 未移植（在线/本地字幕计划 S8）。
- **轨道切换无 UI**：`MpvEngine.GetTracks` / `SelectTrackAsync` 已实现却未被 Desktop 调用，音轨/字幕轨选择只在引擎层可用（计划 S2）。
- **历史进度**：只在关闭播放浮层或播放结束/失败时落库（无周期性保存）；且播完（`Ended`）时快照位置为 0，该集不会保留续播点。
- `csp_`（Java jar 爬虫）站点已过滤——桌面端无 JVM，等待后续适配层。

## 开发

要求 .NET SDK 10.0.400（`global.json` 固定）。播放需 libmpv：macOS 开发机可下载 mpv 官方 app 并把主二进制重链接（或 `export VODBOX_MPV_LIB=/path/to/libmpv.dylib`）；Windows 放 `mpv-2.dll`；Linux `apt install libmpv2`。

```sh
dotnet restore VodBox.slnx
dotnet build VodBox.slnx -c Debug
dotnet test tests/VodBox.Tests/VodBox.Tests.csproj
dotnet run --project src/VodBox.Desktop
```

首次启动 → 设置 → 填入 TVBox 配置地址（如 `https://example.com/tvbox.json`）加载；直播页填 m3u 地址。

## 打包与发布

推送 `vX.Y.Z` tag（与 `VERSION` 一致）触发 Release：每 RID 在对应本机 runner 上 NativeAOT 构建 → 捆绑钉定 sha256 的 libmpv → 产物为 Windows ZIP、macOS `.app`/ZIP/DMG（ad-hoc 签名）、Linux deb/rpm（fpm）。CI 全量构建 + 测试在每次 push 运行。

```sh
# 本机 AOT 发布（macOS x64 示例）
dotnet publish src/VodBox.Desktop -c Release -r osx-x64 --self-contained -o artifacts/publish/osx-x64
python3 build/bundle.py osx-x64 artifacts/publish/osx-x64
python3 build/package.py osx-x64 artifacts/publish/osx-x64 artifacts/packages --version 0.2.0
```

## 结构

```
src/VodBox.Core           领域模型 + TVBox/MacCMS 契约（零依赖，JSON 源码生成）
src/VodBox.Infrastructure 配置加载/隐写解码、MacCMS 适配、直播解析、SQLite 存储、聚合搜索
src/VodBox.Playback.Mpv   libmpv P/Invoke（LibraryImport）+ DllImportResolver + SafeHandle 客户端 + OpenGL 渲染面
src/VodBox.Desktop        Avalonia Shell + 8 页面 + 播放浮层（CommunityToolkit.Mvvm）
tests/VodBox.Tests        71 项测试：配置/隐写/IDN/MacCMS/直播/存储/续播上下文/MpvEngine（含饭太硬真实样本全链路锚点测试）
tests/VodBox.Mpv.Smoke    真 libmpv headless 传输层 + 引擎冒烟（需 VODBOX_MPV_LIB + ≥3s 视频 fixture）
tests/VodBox.Mpv.UiSmoke  真 libmpv OpenGL 渲染冒烟：断言 RenderedFrames>0、位置推进、UI 树内叠加控件
build/                    mpv 资产捆绑、打包、冒烟、归档脚本
```
