# VodBox

使用 Avalonia 的 macOS / Windows / Linux 媒体应用。采用 .NET 10 Native AOT、LibVLC 播放内核，以及 C#、QuickJS、Python、Node.js 内容源。Java Spider 的业务逻辑可通过 C# Provider 实现，不包含 JVM/Dex 兼容层；暂不兼容旧 TV 配置。

当前工程是首版实现，不是 FongMi/TV 全功能移植。方案与分阶段范围见 [实现方案](docs/avalonia-rewrite-plan.md)，逐项进度与未完成任务见 [功能清单](docs/implementation-progress.md)。

## 已实现

- 本地文件/URL 播放、暂停、停止、进度、音量、倍速、字幕与音轨选择、全屏。
- 新版 JSON 配置，C# 目录源及 MacCMS HTTP JSON/XML 采集源的分类、分页、搜索、详情、多线路和选集。
- 配置快照仓库、偏好设置持久化、全源并发搜索、收藏打开、历史重新解析、连续播放与片头片尾设置。
- 直播分组/搜索/备用地址、UTF-8/UTF-16/GB18030 编码、gzip XMLTV 缓存与节目表界面。
- QuickJS-NG 独立 AOT 宿主，LibraryImport 源码生成绑定；Python/Node 独立进程 NDJSON 协议。
- SQLite 历史、续播、收藏和无痕播放；M3U/TXT/JSON 直播列表与 XMLTV 解析。
- MVVM 与 JSON 源码生成、XAML 编译绑定、`Design.DataContext` 预览数据。
- Mica 优先，AcrylicBlur/Blur/实色回退。Mica 原生效果限 Windows 11；其他平台使用可用的材质效果。
- Downio 风格的 Build / Package / CI / Draft Release 工作流，覆盖六个 RID 的本机 AOT 构建。

## 版本与开发

固定 .NET SDK **10.0.401** / 运行时 **10.0.12**、Avalonia **12.1.3**、LibVLCSharp **3.10.1**。需要对应平台的 C/C++ 工具链；macOS 使用 Xcode Command Line Tools，Windows 使用 Visual Studio C++ 工具链，Linux 使用 clang/zlib 开发包。

```sh
dotnet restore VodBox.slnx
dotnet build VodBox.slnx -c Release
dotnet test tests/VodBox.Tests/VodBox.Tests.csproj -c Release
dotnet run --project src/VodBox.Desktop
```

本机若已由 Codex 将 SDK 放入项目 `.cache/dotnet`，可用 `.cache/dotnet/dotnet` 替代上述 `dotnet`。项目级 NuGet.Config 继承系统包源/代理，使用华为镜像和 nuget.org 映射；CI 使用 `build/NuGet.ci.config`，避免依赖本机镜像或代理。

设计预览打开 `src/VodBox.Desktop/MainWindow.axaml`。设计数据不访问 SQLite、网络、脚本或原生播放器。设计器使用深色实色底板；系统级材质需运行应用查看。

开发运行播放前需安装 VLC 3.x，或配置 `VODBOX_VLC_PATH` 为原生库目录。打包产物携带播放器与三种脚本运行时；开发目录中的 QuickJS 宿主需按下列步骤构建并复制到桌面输出的 `plugin-host` 子目录。Python/Node 开发模式可使用系统运行时或 `VODBOX_PYTHON` / `VODBOX_NODE` 指定路径。

## Native AOT 与打包

下面以 macOS Intel 为例。每个 RID 在对应的本机 runner 上构建，避免混用架构。

```sh
cmake -S build/quickjs -B artifacts/quickjs -DCMAKE_BUILD_TYPE=Release
cmake --build artifacts/quickjs --config Release --target vodbox_quickjs --parallel 2
dotnet publish src/VodBox.Desktop -c Release -r osx-x64 --self-contained -o artifacts/publish/osx-x64
dotnet publish src/VodBox.PluginHost -c Release -r osx-x64 --self-contained -o artifacts/publish/osx-x64/plugin-host
python3 build/bundle.py osx-x64 artifacts/publish/osx-x64
python3 build/smoke.py osx-x64 artifacts/publish/osx-x64
python3 build/package.py osx-x64 artifacts/publish/osx-x64 artifacts/packages
```

输出格式：Windows ZIP；macOS `.app`、ZIP、DMG；Linux deb、rpm、AppImage。Linux 构建机需安装 libvlc-dev、vlc-plugin-base、vlc-plugin-video-output、libicu-dev；打包机额外安装 dpkg-deb、rpm、Ruby/fpm 1.16.0。原生依赖闭包从目标 runner 收集，保留系统 glibc、显示服务器、字体与 GPU 驱动依赖。Linux 产物基线为 Ubuntu 22.04 / glibc 2.35，不承诺兼容所有发行版。

默认 macOS 使用本地 ad-hoc 签名供验证，未做 Apple notarization。`CODESIGN_IDENTITY` 可以选择已安装的 Developer ID；正式分发前仍需配置签名与公证流程。CI 默认仅上传构建和安装包 artifacts；推送与 VERSION 匹配的 `v*` tag 才创建草稿 Release。

Python、Node、VLC 的下载地址与 SHA-256 固定在 `build/native-assets.json`，QuickJS 固定提交与压缩包 SHA-256。`native-manifest.json` 记录实际打包文件的校验值。调试符号与运行包分离。Node 仅携带运行时和许可证，排除 npm、开发头文件与文档。macOS 使用标准 app bundle 资源目录，封包前验证签名、原生解码和三种脚本宿主。AOT 主要减小托管运行时开销；完整 VLC 编解码插件和外部脚本运行时仍占据大部分包体积。

## 内容源与插件

`examples/vodbox.json` 演示四种源类型，`examples/catalog.json` 是 C# 目录格式。示例不附带影片；可以把自己的媒体放到 `examples/sample.mp4`，也可在三个脚本源的 `options.mediaUri` 中填写媒体地址。

插件实现 `init`、`categories`、`items`、`search`、`detail`、`resolvePlayback`。QuickJS 使用全局 `VodBoxProvider` 对象；Node 使用 default export；Python 使用同名函数。返回模型以 `VodBox.Core` 为准，协议版本为 1，stdout 仅用于 NDJSON，日志发到 stderr。Python 支持同步与 async 函数，Node 支持 Promise，QuickJS 支持立即可完成的 Promise jobs。

QuickJS 宿主提供 `vodbox.fetchText(url)` 与 `vodbox.sha256(text)`；无 CLR 反射对象暴露、Node 模块兼容或浏览器 DOM。插件入口必须是本地文件 URI；相对路径由配置加载器解析。

## 验证与剩余范围

六个 RID 的 Native AOT 构建、27 项功能测试、真实 LibVLC WAV 解码、随包 QuickJS/Python/Node 协议测试与全部安装包生成已通过 [CI 验证](https://github.com/pengpercy/VodBox/actions/runs/37245948796)（ed2cc55）。macOS 安装包另外执行严格签名与依赖哈希校验。用户提供来源中的一个 HLS 直播流在 macOS x64 实际进入 Playing，解码 H.264/AAC 并观察到 VideoToolbox 日志；详见 [播放测试记录](docs/playback-testing.md)。有画面的交互、长时稳定性和各系统完整安装流程仍需真机验证。

本轮继续实现海报缓存 / 缩略图、历史管理、直播收藏 / 恢复 / 有限重试 / 节目表刷新、音频字幕延迟 / 截图、快捷键 / 拖放 / 主题，以及 DASH 请求头代理。42 项自动测试通过，新增数据备份 / 合并导入、单源搜索分页、MacCMS 年份 / 完结筛选、可见海报加载和无窗口 XAML 预览测试。上一轮六 RID 构建与打包全部通过；当前改动的本机 Native AOT 与实际 HLS 的 150ms 音频延迟验证通过；本轮新增功能的跨平台 CI 与桌面验收独立进行。通用筛选元数据、聚合分页、弹幕、大屏布局、媒体键 / 休眠、DLNA / 局域网、SMB / WebDAV、自动更新及完整桌面验收仍需继续。旧配置与 Java 兼容层不在当前范围内。

参考项目：[FongMi/TV](https://github.com/FongMi/TV)、[Screenbox](https://github.com/huynhsontung/Screenbox)、[Downio](https://github.com/pengpercy/Downio)。当前实现没有复制其应用源码。分发原生依赖前应保留各依赖的许可证与 notice；相关文件随运行时打包。

解析器配置、HLS 请求头代理与独立浏览器嗅探使用说明见 [播放解析](docs/playback-resolution.md)。完整重写的逐项覆盖及尚未实现内容见 [实现进度](docs/implementation-progress.md)。

海报缓存、直播恢复、快捷键、延迟、截图与主题操作见 [桌面操作](docs/desktop-controls.md)。

设置页支持导出与合并导入数据备份，范围和恢复规则见 [数据备份](docs/data-backup.md)。
