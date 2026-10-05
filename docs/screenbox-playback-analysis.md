# Screenbox 对 VodBox 播放器设计的借鉴

源码检查日期：2026-10-04；方案更新日期：2026-10-05；Screenbox main 快照：`46aadf6b20ef5d8348a6049c16c882c22ec0f84e`。本文基于实际源码阅读与 LibVLCSharp 官方文档，未运行 Screenbox 或 Avalonia 播放实验。

## 1. 核心判断

Screenbox 是非常合适的 C# 播放器应用层参考，可以借鉴接口封装、会话管理、队列、轨道、交互和生命周期处理。它本身是 UWP 应用；底层解码由原生 LibVLC 完成，C# 的 LibVLCSharp 是调用层，并不是纯托管 C# 解码器。

最初采用 **Avalonia + LibVLC + LibVLCSharp**，参考 Screenbox。根据用户新增要求，现改为保留 LibVLC 与 libmpv 双内核，提供自动/手动选择；最新实施状态见 [重写方案](avalonia-rewrite-plan.md) 与 [mpv/弹幕进展](danmaku.md)。内容层保留 C# Provider、QuickJS、Python、Node.js，Java Spider 按需重写为 C#，不做 Java 兼容层，首版不兼容旧配置。

参考：[项目说明](https://github.com/huynhsontung/Screenbox)、[LibVLCSharp 官方仓库](https://github.com/videolan/libvlcsharp)。

## 2. 已从代码确认的结构

### 2.1 Core 不是可直接引用的跨平台类库

`Directory.Build.props` 指定 `net10.0-windows10.0.26100.0`。`Screenbox.Core.csproj` 使用 `UseUwp=true`，仅声明 win-x86/win-x64/win-arm64，并引用 `LibVLCSharp.UWP`。

`IMediaPlayer` 中直接出现 `TypedEventHandler、DeviceInformation、ChapterCue、MediaPlaybackState、Rect、IStorageFile`。这些属于 Windows/WinRT API；迁移需要替换类型，并不只是改 TargetFramework。

参考：[构建属性](https://github.com/huynhsontung/Screenbox/blob/46aadf6b20ef5d8348a6049c16c882c22ec0f84e/Directory.Build.props)、[Core 项目](https://github.com/huynhsontung/Screenbox/blob/46aadf6b20ef5d8348a6049c16c882c22ec0f84e/Screenbox.Core/Screenbox.Core.csproj)、[IMediaPlayer](https://github.com/huynhsontung/Screenbox/blob/46aadf6b20ef5d8348a6049c16c882c22ec0f84e/Screenbox.Core/Playback/IMediaPlayer.cs)。

### 2.2 最值得参考：VlcMediaPlayer 事件与属性桥接

该类将 LibVLCSharp.MediaPlayer 包装为应用接口，处理状态、缓冲、时长、进度、音量、速度、字幕/音频延迟、章节、轨道与错误。

值得迁移的细节：

- 时长小幅变化有容差，避免属性通知抖动。
- seek 夹在合法范围内；到结尾再跳转需要处理重播状态。
- 暂停时 seek 可能不会触发通常的进度事件，需要显式更新显示状态。
- 音量将内核的百分比标准化；内部时间使用 TimeSpan。
- 音频/字幕延迟在 API 边界转换毫秒和微秒。
- 内核自动选择字幕后同步轨道选择状态，避免 UI 与实际播放不一致。
- 将 Buffering/Opening/Playing/Ended 等内核事件转换为业务状态。

迁移时同时补充：所有事件带会话身份；UI 更新经 IUiDispatcher；失败包含类型、内核消息和上下文；解除事件订阅和资源释放有明确所有权。

不要照搬示例中的“只发 MediaFailed 空事件”，VodBox 需要区分源解析失败、HTTP 失败、解码失败和不支持 DRM。

参考：[VlcMediaPlayer](https://github.com/huynhsontung/Screenbox/blob/46aadf6b20ef5d8348a6049c16c882c22ec0f84e/Screenbox.Core/Playback/VlcMediaPlayer.cs)。

### 2.3 PlayerService 的创建与销毁职责

PlayerService 集中创建 LibVLC、MediaPlayer、Media 和 PlaybackItem，连接日志及 VLC 对话框，统一释放原生资源。这种资源工厂模式很适合借鉴。

但其中 `winrt://`、FutureAccessList、SharedStorageAccessManager、`--aout=winstore`、swapChainOptions 都是 UWP 专用路径。桌面版改为普通 URI/本地路径或受控 Stream 输入，并按平台初始化音频输出。

VodBox 不让 ViewModel 直接 new LibVLC/Media；应用层只持有 PlaybackRequest，LibVlcEngine 负责创建并释放底层 Media。

参考：[PlayerService](https://github.com/huynhsontung/Screenbox/blob/46aadf6b20ef5d8348a6049c16c882c22ec0f84e/Screenbox.Core/Services/PlayerService.cs)。

### 2.4 播放器重建的竞态防护

`PlayerElementViewModel.Initialize` 会先解绑旧播放器事件、将共享上下文置空，然后后台执行耗时的释放/初始化，并丢弃被更新初始化请求替代的播放器实例。

可以据此设计 EngineLifetimeManager：每次初始化递增 generation ID；创建完成先检查 generation；过期实例立即释放；窗口退出后禁止新实例发布到 UI。

原生操作只在指定内核线程串行执行；后台释放和 UI 重建并不是可以任意并行。线程规则依 LibVLC 的 API 和视频 surface 生命周期确定。

参考：[PlayerElementViewModel](https://github.com/huynhsontung/Screenbox/blob/46aadf6b20ef5d8348a6049c16c882c22ec0f84e/Screenbox.Core/ViewModels/PlayerElementViewModel.cs)。

### 2.5 细分 ViewModel 与进度条交互

Screenbox 单独组织 PlaybackSessionViewModel、SeekBarViewModel、VolumeViewModel、PlayerControlsViewModel 和 PlayerElementViewModel。

- 会话设置：速度与延迟随播放器更换重新应用，重置会话时清理延迟。
- 进度条：区分内核进度通知和用户拖拽，避免数据绑定反复触发 seek；连续滚轮/键盘操作去抖。
- 缓冲展示：处理开始/结束，而非把任意进度变化当作缓冲。
- 交互：单击/双击、滚轮、快进/快退、长按临时倍速各自恢复状态。

Avalonia 中替换 DispatcherQueue 与 UWP RangeBase 参数。建议明确维护 `IsScrubbing、PreviewPosition、ActualPosition`，用户结束拖拽后提交 seek，不用“两个值差多少”作为唯一交互判据。

速度和延迟通过跨平台接口暴露，避免 ViewModel 出现 `if (player is VlcMediaPlayer)` 的内核类型判断。

参考：[PlaybackSessionViewModel](https://github.com/huynhsontung/Screenbox/blob/46aadf6b20ef5d8348a6049c16c882c22ec0f84e/Screenbox.Core/ViewModels/PlaybackSessionViewModel.cs)、[SeekBarViewModel](https://github.com/huynhsontung/Screenbox/blob/46aadf6b20ef5d8348a6049c16c882c22ec0f84e/Screenbox.Core/ViewModels/SeekBarViewModel.cs)。

### 2.6 队列、轨道和进度保存

- PlaybackControlService：下一项、上一项、列表循环和单项循环。迁移为 QueueNavigator，并增加 TV 的同集换线/下一集策略。
- PlaybackItem：将 Media、轨道和章节归属于一次播放。借鉴归属关系，但不要让 Core 的 DTO 引用 LibVLCSharp.Media。
- SingleSelectTrackList：单选轨道和选择通知。改为自有 MediaTrackInfo，使用内核稳定 track ID；不能混用列表下标与 LibVLC 轨道 ID。
- PlaybackProgressTracker：内存更新与 SQLite 保存分离，读取失败不阻止启动。VodBox 应周期性增量 upsert，主键使用 config/site/vod/episode，而非仅 URL；临时播放 URL 经常变化。

参考：[PlaybackControlService](https://github.com/huynhsontung/Screenbox/blob/46aadf6b20ef5d8348a6049c16c882c22ec0f84e/Screenbox.Core/Services/PlaybackControlService.cs)、[PlaybackItem](https://github.com/huynhsontung/Screenbox/blob/46aadf6b20ef5d8348a6049c16c882c22ec0f84e/Screenbox.Core/Playback/PlaybackItem.cs)、[PlaybackProgressTracker](https://github.com/huynhsontung/Screenbox/blob/46aadf6b20ef5d8348a6049c16c882c22ec0f84e/Screenbox.Core/Services/PlaybackProgressTracker.cs)。

## 3. 不能直接迁移的渲染实现

Screenbox.VideoView 使用 UWP SwapChainPanel，通过 Vortice 创建 D3D11 Device 和 DXGI swap chain，把指针传给 LibVLC 的 `--winrt-d3dcontext` 与 `--winrt-swapchain`。它还处理缩放、物理像素尺寸与销毁。

这证明 Screenbox 在 Windows 上有专门的视频合成实现，不证明 Avalonia 的标准控件能在三平台获得同样效果。

可借鉴“视频 surface 与控制层分离、尺寸/DPI 同步、显式销毁”的设计；不能直接复用 SwapChainPanel、COM 扩展、UWP XAML、MediaDevice 或 SystemMediaTransportControls。

参考：[VideoView](https://github.com/huynhsontung/Screenbox/blob/46aadf6b20ef5d8348a6049c16c882c22ec0f84e/Screenbox.VideoView/VideoView.cs)、[SwapChainPanelExtensions](https://github.com/huynhsontung/Screenbox/blob/46aadf6b20ef5d8348a6049c16c882c22ec0f84e/Screenbox.VideoView/SwapChainPanelExtensions.cs)。

LibVLCSharp 官方提供 Avalonia 控件并列出 Windows/macOS/Linux，但其文档明确说明叠层受到 airspace 限制，叠层内容应放入 VideoView.Content，内部使用独立窗口方案。这需要实测焦点、拖动、裁剪、透明菜单、跨屏 DPI、全屏和 Linux 窗口管理器。它不是“三平台叠层已无条件解决”的保证。

参考：[官方 Avalonia VideoView 说明](https://github.com/videolan/libvlcsharp/blob/3.x/src/LibVLCSharp.Avalonia/README.md)。

## 4. 已确定的内核与实施边界

LibVLC + LibVLCSharp 提供现成 C# API 与 Screenbox 的应用层参考。现在并行实现 libmpv 后端，保留 LibVLC 网络浏览/投屏等能力，并验证用户可选择的双内核播放。

保留 IPlaybackEngine 抽象用于隔离应用逻辑与原生对象。验证控制层/弹幕叠层、HTTP Header、字幕、硬解回退、原生制品和六 RID 打包；确有无法解决的渲染问题时再修订设计。

内容源使用新的版本化契约：C# Provider 直接实现业务，QuickJS 使用 C# 绑定宿主，Python/Node 使用独立 worker。三种脚本运行时明确保留；旧 Java/Dex 插件和旧配置暂不支持。

## 5. 建议的跨平台播放器接口

下面是设计草案，类型在实现阶段补齐并测试，不作为已编译 API：

```csharp
public interface IPlaybackEngine : IAsyncDisposable
{
    PlaybackCapabilities Capabilities { get; }
    PlaybackSnapshot Snapshot { get; }
    event EventHandler<PlaybackEvent>? StateChanged;

    Task OpenAsync(PlaybackRequest request, CancellationToken cancellationToken);
    Task PlayAsync(CancellationToken cancellationToken);
    Task PauseAsync(CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
    Task SeekAsync(TimeSpan position, CancellationToken cancellationToken);
    Task SetRateAsync(double rate, CancellationToken cancellationToken);
    Task SetVolumeAsync(double volume, CancellationToken cancellationToken);
    Task SelectTrackAsync(TrackKind kind, string? trackId,
        CancellationToken cancellationToken);
    Task AddSubtitleAsync(SubtitleSource subtitle, CancellationToken cancellationToken);
    Task SetTimingAsync(TimeSpan audioDelay, TimeSpan subtitleDelay,
        CancellationToken cancellationToken);
}
```

Snapshot 包含 position、duration、seekability、buffering、尺寸与轨道。PlaybackEvent 包含 session ID、事件类型与结构化错误。公开接口不出现 Windows.*、Avalonia 控件、LibVLCSharp.Media 或原生指针。

UI 的 VideoSurfaceView 单独绑定内核所需的渲染适配器，Core 不暴露 native window handle。多个界面共用一个活动播放会话。

## 6. 可执行的原型步骤与验收

1. 建立 Core + Playback.LibVlc + Desktop 三工程，使用普通桌面 TFM，不引用 UWP 包。
2. 锁定 LibVLCSharp、Avalonia 和实际原生 LibVLC 版本；先确认兼容的包版本再写依赖文件。
3. 为 win-x64 / osx-arm64 / linux-x64 配置原生路径；先在开发机以目录发布验证加载。其余三 RID 作为后续发布 gate。
4. 建立 LibVlcEngine 与事件桥，仅做文件和 URL 播放；实现状态/时间/字幕/音轨/速度。
5. 建立 VideoView.Content 的控制层，验证进度拖拽、弹出菜单、弹幕、全屏、多屏。
6. 接入 PlaybackCoordinator、QueueNavigator、ProgressStore；按 Screenbox 的细分方式拆 ViewModel；所有四类内容源通过同一 PlaybackRequest 进入播放管线。
7. 对项目实际源地址单测 UA、Referer、Cookie；若内核不能表达完整 Header 策略，使用本地流代理，并正确处理 HLS 分片/密钥/Range/相对 URI。Header 不以任意字符串拼成内核参数。
8. 三平台分别记录 MP4、MKV+ASS、HLS、DASH、硬解/软解、暂停 seek、快速切源和退出的结果。
9. 记录 LibVLC 首帧延迟、CPU、内存、丢帧、叠层问题与安装包体积，明确平台及媒体样本；不填写未测性能数字。
10. 更新 LibVLC native manifest 和 Downio 风格打包脚本，携带 plugins 与实际依赖；脚本完整版本同时验证 QuickJS、Python、Node 运行时/worker/SDK。

验收门槛：最终安装包在没有开发工具的三平台机器可启动；播放/字幕/叠层/全屏正常；关闭无泄漏；每个平台至少一种硬解路径有日志证据或明确软解回退。

## 7. 对原计划的具体影响

- 已完成的阶段 1 使用 LibVLC 三平台播放；新增阶段实现 libmpv 和自动/手动双内核路由。源码阅读仍不等于运行验证。
- 应用层参考 Screenbox，TV 作为功能参考；内容与配置使用新契约，通过 PlaybackRequest 进入播放器。
- C# Provider、QuickJS、Python、Node.js 均保留；无 Java/JVM/Dex 兼容层。
- 对象拆分补上 QueueNavigator、ProgressStore、EngineLifetimeManager、IUiDispatcher 和轨道模型。
- 发布工作流结构仍参照 Downio；使用 LibVLC 原生依赖清单，并携带 plugins/许可证。
- Screenbox 同样标示 GPL-3.0；直接复制/改编源码须保留相应许可要求，不能因为改用 Avalonia 就忽略它。

完整实施顺序见 [总方案](avalonia-rewrite-plan.md)。
