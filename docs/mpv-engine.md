# libmpv 统一播放接口

`MpvEngine` 已实现 IPlaybackEngine，新增 `PlaybackEngineRouter` 统一管理两个惰性创建的内核。原生客户端和渲染表面保留；主窗口目前仍使用 LibVLC，设置选择与双视频表面尚未接入。

引擎首次打开媒体才创建客户端，音量、倍速和延迟可以提前设置而不加载原生库。命令在后台串行执行；通过 mpv_observe_property 获取位置、时长、暂停、可跳转、缓存暂停与轨道变化，而非周期查询所有媒体属性。每 100ms 非阻塞批量处理最多 256 个事件，并合并状态通知。事件中的 native 指针只在 PollEvent 内复制，字符串使用 mpv_free 释放；绑定使用 LibraryImport 源码生成。

支持播放/暂停/停止、精确跳转、音量、倍速、音轨/字幕选择、外部字幕、截图命令、音频与字幕延迟、续播起点。轨道元数据在后台缓存，UI 读取不触发 native 查询。播放失败与结束分别处理，过时的 unavailable 属性不会把失败改成成功。

每次打开重置 User-Agent 和 Referer，避免上一源的请求头泄漏。其他头要求经过现有媒体代理，不能静默丢弃。默认禁止读取用户 mpv 配置、自动加载脚本和 ytdl；正常视频模式使用 libmpv Render API / hwdec=auto-safe，无窗口测试使用 vo/ao=null、hwdec=no。文件/HTTP/HTTPS/RTSP/RTMP/UDP/RTP 使用明确 URI，不拼接命令字符串。

## 已验证

2026-10-05：3 项专项测试，完整 126 项回归通过。验证惰性初始化、会话、源切换时请求头清理、缓存轨道、状态与终止原因、控制、取消与幂等释放。

macOS x64 真实 libmpv JIT 与严格 Native AOT 产物均通过 15 秒受控视频/AAC 样本：位置、时长、暂停、seekable 事件、1.5 秒续播、暂停下精确跳至 2.5 秒、恢复、音量/倍速、轨道字符串 ABI、音频 120ms / 字幕 -340ms 延迟、停止和释放。测试样本在本机合成，不提交媒体。严格发布将编译与 AOT 警告视为错误。

本机日志：`/private/tmp/vodbox-mpv-engine-all-tests.log`、`/private/tmp/vodbox-mpv-engine-native-smoke.log`、`/private/tmp/vodbox-mpv-engine-aot.log`、`/private/tmp/vodbox-mpv-engine-native-aot.log`。没有启动 Actions。

## 双内核路由

自动模式按 PlaybackEnginePolicy 为普通点播、直播和本地媒体选择 mpv，为网络文件系统选择 LibVLC。启动异常或异步播放失败最多回退一次；手动固定模式与网络文件不自动回退。网络浏览/投屏目前只是选择策略预留，尚无完整业务界面。

切换保留播放位置（直播重新接入）、暂停、倍速、音量和延迟，深拷贝请求头与字幕请求；同一播放会话继续使用原 session。过期事件和排队回退不能覆盖新媒体。取消切换会停止替代内核并清理 Loading 状态，释放时即使某个内核失败也会尝试释放另一个。LibVLC 无音轨或字幕时只保存延迟偏好，避免无轨道媒体播放失败。外部追加字幕与用户轨道选择尚未迁移到另一个内核。

新增 13 项路由回归，完整 139 项本地测试通过。真实 JIT 的 mpv → LibVLC → mpv 验证保留 2.5 秒位置、暂停、倍速与音频延迟；LibVLC 暂停后的 seek 缓冲回调不再覆盖暂停状态。mpv 原生音量验证通过；LibVLC dummy 音频输出返回音量 0，设备输出音量仍须桌面验收。AOT 验收记录见 [播放测试](playback-testing.md)。

最终严格 Native AOT 产物的真实往返切换与缺失 mpv 库的 LibVLC 回退均通过（macOS x64）。无音轨媒体的延迟恢复也通过；没有运行 Actions。

## 接下来的接入

接入主窗口的视频表面、弹幕、偏好与设计预览，补上轨道与追加字幕的切换迁移，再做有画面的跨平台验收、native 打包和网络浏览/投屏。

现有路由验证不等于主窗口已双内核播放或六个平台均已完成 GUI 验收。
