# 弹幕

设置页提供“显示弹幕”、弹幕地址、本地文件选择、字号、不透明度、覆盖比例与延迟。正延迟会推迟弹幕，在时间尚未到达时不显示时间 0 的弹幕。加载或关闭弹幕不会替换 LibVLC 字幕轨道；切换媒体会取消旧加载并清空旧弹幕。

目录源的每集可加入 `danmakuUri`，相对地址以目录文件位置为基准。QuickJS / Python / Node 的 `resolvePlayback` 返回值也可包含绝对 `danmakuUri`；播放解析及流代理保留此字段。示例：

```json
{"uri":"https://media.example/movie.mp4","danmakuUri":"https://media.example/comments.json"}
```

`examples/catalog.json` 关联 `examples/danmaku.json`，示例没有附带影片，需自行提供 `examples/sample.mp4`。手动加载的弹幕地址不会写入历史，重新选集时使用该集解析返回的地址。

新版 JSON 格式：

```json
{"version":1,"comments":[{"timeMs":1000,"text":"你好","mode":"scroll","color":16777215}]}
```

`timeMs` 为毫秒，`mode` 支持 `scroll`、`top`、`bottom`，`color` 为十进制 RGB（默认白色）。常见 `<i><d p="秒,模式,字号,颜色,...">文本</d></i>` XML 支持模式 1–3 滚动、4 底部、5 顶部；高级 / 脚本模式不执行。字号统一由当前显示设置控制。UTF-8、带 BOM 的 UTF-16 和 GB18030 使用已有字符编码处理。解析时按时间稳定排序，保持同时间条目的原始顺序，忽略无效时间 / 颜色 / 空文本等条目。

本地文件、HTTP / HTTPS 与 gzip 均支持，下载和解压后的数据各不超过 8 MiB；整体加载截止时间 20 秒，最多 100000 条、每条 512 字符，时间范围最长七天。XML 禁用 DTD 与外部解析器。当前尚不支持需要自定义认证头的弹幕端点、DPlayer 数组、protobuf 分段弹幕、自动按片名匹配或发送弹幕。

调度使用媒体时钟，播放时在相邻原生时间事件之间做最多 500ms 的插值；暂停 / 缓冲冻结，倒退或大幅跳转按最近八秒窗口重建。用二分查找定位到期条目，每帧最多处理 256 个候选，超过视口宽度四倍的弹幕略过、同屏最多 128 条；固定轨道占用和滚动追尾检测限制碰撞，密集时丢弃条目，不补发已过期的弹幕。seek 重建只考虑最近窗口，轨道和被丢弃条目不保证与连续播放完全一致。字体布局缓存最多 256 个，30Hz 只重绘单个控件，未复制或逐帧处理视频像素。

原生视频使用独立透明附属窗口显示弹幕，窗口仅跟随视频宿主区域，不设置全局置顶。使用 Avalonia 12 的固定 API，无版本 / 属性反射。视频区域的鼠标按下将焦点交回主窗口，按键转发主窗口；目前不是系统级鼠标穿透窗口。窗口系统未提供透明支持时隐藏弹幕层并提示，避免遮住视频。全屏、弹出菜单遮挡、macOS Spaces、Linux 非合成窗口管理器及多屏 DPI 仍待验收。

设计器提供两条示例弹幕，使用暂停的设计时间，不访问网络、不启动 LibVLC 或计时器。55 项本地测试通过；macOS x64 临时隔离 GUI 驱动以合成视频验证 `VoutCount=1`、实际透明窗口、跟随窗口移动、暂停保持、开关隐藏 / 恢复，并确认三种模式均产生绘制条目。该驱动使用 JIT 和真实 LibVLC，不代表 Windows / Linux 或最终 AOT GUI 全部通过。控件渲染图：[弹幕层](/Users/percy/RiderProjects/VodBox/artifacts/danmaku-overlay-preview.png)，仅包含弹幕层，不是影片截图。日志：`/private/tmp/vodbox-danmaku-native-ui-final.log`。

渲染使用 Avalonia [TextLayout 自绘 API](https://docs.avaloniaui.net/docs/graphics-animation/custom-rendering)，窗口生命周期使用 [Window API](https://docs.avaloniaui.net/controls/primitives/window)。实现独立编写，没有复制参考播放器控件中的反射或应用代码。

本轮 macOS x64 Native AOT 发布成功，实际运行诊断通过 JSON/XML 弹幕解析、时间轴、配置、SQLite、备份导入与 LibVLC 解码；日志 `/private/tmp/vodbox-danmaku-native-aot.log`。本轮未运行 GitHub Actions。

### Bili.Copilot 与 mpv 参考更新

核对的 [Bili.Copilot 源码版本](https://github.com/Richasy/Bili.Copilot/tree/896a06290ba440cb17776d292dff119bbd3383d6) 将媒体核心、UI 与弹幕渲染分开：播放位置驱动弹幕时间，暂停/恢复与倍率变化同步弹幕；视频弹幕按六分钟分段、最多六路并行下载，单段失败不阻断其他分段。VodBox 的媒体时钟、暂停冻结、倍率插值和渲染生命周期已采用相同职责划分；分段下载、重复文本合并以及分别隐藏滚动/顶部/底部弹幕列入后续扩展，尚未实现。其 `docs/danmaku.md` 当前只有 TBD，具体机制以上述源码为依据。WinUI、Win2D/DirectX 渲染组件需要独立用 Avalonia 实现。

[mpv-kernel 原生绑定](https://github.com/Richasy/mpv-kernel/tree/9c0b352efc2932f9d6a23e29603a8bc47d5d752d/src/MpvKernel.Interop) 使用 `LibraryImport` 源码生成并声明 `IsAotCompatible`，值得借鉴。默认库名为 Windows 的 `libmpv-2.dll`，WinUI 层仍依赖 Windows；跨平台接入需要维护各 RID 的 dll/dylib/so 与传递依赖，并独立验证实际 AOT 构建，不能仅凭项目属性判定已兼容。

libmpv 本身可以用于 Windows、macOS 和 Linux。按 [mpv 嵌入文档](https://mpv.io/manual/stable/#options-wid)，`wid` 明确支持 Windows HWND 与 X11 Window；macOS 与 Wayland 的嵌入不能直接照搬该路径。VodBox 的候选实现应先验证 libmpv render API 与 Avalonia OpenGL 控件的上下文、帧缓冲和更新回调，目标是在 GPU 内完成视频合成并在同一 UI 树绘制弹幕，减少透明附属窗口的限制。不能预先承诺该路径在所有渲染后端都零拷贝。

接入顺序：先做独立 `VodBox.Playback.Mpv` 的源码生成 C ABI、原生库定位和事件循环；再实现现有 `IPlaybackEngine` 的播放、seek、音轨/字幕、延迟与快照能力；随后实现 Avalonia 视频表面及弹幕合成；最后对三平台六 RID 测试硬件解码、全屏、DPI、资源释放与包体大小，再决定默认后端。当前实际播放仍使用 LibVLC，mpv 尚未接入，也没有因为参考项目而同时打包两套媒体依赖。
