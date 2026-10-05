# libmpv 统一播放接口

`MpvEngine` 已实现 IPlaybackEngine，可接入现有 PlaybackCoordinator。原生客户端和渲染表面保留；主窗口目前仍使用 LibVLC，双内核路由/设置切换尚未接入。

引擎首次打开媒体才创建客户端，音量、倍速和延迟可以提前设置而不加载原生库。命令在后台串行执行；通过 mpv_observe_property 获取位置、时长、暂停、可跳转、缓存暂停与轨道变化，而非周期查询所有媒体属性。每 100ms 非阻塞批量处理最多 256 个事件，并合并状态通知。事件中的 native 指针只在 PollEvent 内复制，字符串使用 mpv_free 释放；绑定使用 LibraryImport 源码生成。

支持播放/暂停/停止、精确跳转、音量、倍速、音轨/字幕选择、外部字幕、截图命令、音频与字幕延迟、续播起点。轨道元数据在后台缓存，UI 读取不触发 native 查询。播放失败与结束分别处理，过时的 unavailable 属性不会把失败改成成功。

每次打开重置 User-Agent 和 Referer，避免上一源的请求头泄漏。其他头要求经过现有媒体代理，不能静默丢弃。默认禁止读取用户 mpv 配置、自动加载脚本和 ytdl；正常视频模式使用 libmpv Render API / hwdec=auto-safe，无窗口测试使用 vo/ao=null、hwdec=no。文件/HTTP/HTTPS/RTSP/RTMP/UDP/RTP 使用明确 URI，不拼接命令字符串。

## 已验证

2026-10-05：3 项专项测试，完整 126 项回归通过。验证惰性初始化、会话、源切换时请求头清理、缓存轨道、状态与终止原因、控制、取消与幂等释放。

macOS x64 真实 libmpv JIT 与严格 Native AOT 产物均通过 15 秒受控视频/AAC 样本：位置、时长、暂停、seekable 事件、1.5 秒续播、暂停下精确跳至 2.5 秒、恢复、音量/倍速、轨道字符串 ABI、音频 120ms / 字幕 -340ms 延迟、停止和释放。测试样本在本机合成，不提交媒体。严格发布将编译与 AOT 警告视为错误。

本机日志：`/private/tmp/vodbox-mpv-engine-all-tests.log`、`/private/tmp/vodbox-mpv-engine-native-smoke.log`、`/private/tmp/vodbox-mpv-engine-aot.log`、`/private/tmp/vodbox-mpv-engine-native-aot.log`。没有启动 Actions。

## 接下来的接入

统一路由复用 PlaybackEnginePolicy：普通点播/直播默认 mpv，网络浏览和投屏使用 LibVLC；手动模式固定内核，自动模式每个播放会话只允许一次回退。切换应保存位置、暂停/速度/音量/延迟和轨道状态，并确保旧内核事件不污染新会话。随后接入主窗口的视频表面、弹幕、偏好与设计预览，再做有画面的跨平台验收、native 打包和网络浏览/投屏。

现有验证不等于主窗口已双内核播放、自动回退已实现，或六个平台均已完成 GUI 验收。
