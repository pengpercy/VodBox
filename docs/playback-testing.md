# 播放测试

用户授权将 `http://www.饭太硬.net/tv` 作为后续播放测试来源。该地址返回 JPEG 尾部封装的 Base64 配置，解码后是含注释的旧 TV JSON。2026-10-05 读取结果包含 48 个站点与 6 个直播列表；大部分点播站点依赖 `csp_*` Java Spider。

该来源不是直接视频地址，也不是 VodBox 新配置。暂不兼容旧配置、不要实现 JVM/Dex 兼容层的既定范围保持不变。测试时可从其中选取直接媒体 URL，或使用 `examples/user-live-test.json` 中转换好的公开直播列表，验证现有 M3U 解析与 LibVLC 播放。该示例不导入其点播插件。

2026-10-05 本地已确认网易列表返回有效 M3U，其首个 IPv6 频道探测超时。GitHub 代理镜像返回 403，改为读取 Kimentanm/aptv 的公开 raw 列表后得到可用 IPv4 测试地址。

使用项目实际 `LibVlcEngine(headless: true)` 验证 `http://101.35.240.114:88/live.php?id=CCTV1`，请求头 `User-Agent: AptvPlayer-UA`。HTTP 200 返回 HLS 清单；原生日志识别 TS、H.264 与 AAC（双声道 44100 Hz），显示 VideoToolbox 解码，状态进入 `Playing`，播放进度达到 6102 ms。测试随后主动停止。M3U 的 `http-user-agent`、`http-referrer` 和 `#EXTVLCOPT` 对应字段已传递到播放器；没有显式请求头时该频道探测返回 404。该记录确认当前网络下媒体接收和原生解码，不代表所有站点、频道或桌面画面已验证；流地址可随时失效。

临时诊断使用已打包 VLC 库和插件目录，不加载 Java 插件。测试记录区分网络可达性、列表解析、实际解码与界面显示。

CI 使用应用生成的四秒 WAV 验证 AOT 原生解码，用示例插件验证随包脚本运行时，不把外部直播源作为每次提交的阻断条件。测试外部源时记录测试时间、选用的频道 URL、HTTP 状态、VLC 状态、收到的数据和播放进度；避免把配置里的 `csp_*` 字符串直接当作播放地址。


## 音频延迟增强验证

在相同实际 CCTV1 HLS 来源再次解码，150ms 延迟设置后 LibVLC 返回 AudioDelay=150000 微秒，恢复零值成功；播放器状态 Playing，位置 5929ms。使用 dummy 音视频输出完成 native API 校验，不代表主观声画同步、字幕延迟或截图界面已经验收。

## 请求头代理实际 HLS 验证

通过给同一测试流添加 X-VodBox-Test 请求头触发回环媒体代理，macOS x64 在约 6060ms 时进入 Playing，日志确认 H.264 / AAC 和 VideoToolbox 解码。记录：`/private/tmp/vodbox-proxy-live-probe.log`。该测试使用实际 LibVLC 与 JIT 测试驱动，验证代理到解码链路，不等同于 AOT 桌面窗口或长时验收。

## 宝盒来源与直播验证

2026-10-05 读取 `https://宝盒接口.top`，返回 gzip JSON，解压后 50 个点播条目（49 个 csp_*、1 个 drpy）和 6 个直播列表。完整插件频率见 [Spider 优先级](spider-migration-priority.md)，未下载或执行 JAR。

新版直播测试配置为 `examples/user-baohe-live-test.json`。aptv 使用原始 GitHub raw 地址替代旧代理，其余五个列表保留来源 URL。实际 LiveParser 解析虎牙 1078 个频道、斗鱼 949 个频道；不是全部频道可播放的证明。

虎牙“周星星”地址 `https://goodiptv.club/huya/11342412` 由实际 LibVlcEngine 接收 FLV，日志确认 H.264 VideoToolbox 解码，状态 Playing、位置 8.89 秒后主动停止，记录 `/private/tmp/vodbox-baohe-vlc-live.log`。相同地址的实验 libmpv 返回 END_FILE reason=4/error=-13（加载失败），原因尚未定位，不标记为 libmpv 验证通过。源码配置中的 4K SDR HLS 清单可读取，但未做解码测试。

## C# 哔哩哔哩 Spider 验证

公开投稿 `BV1WSHL66EdZ` 通过真正的 ProviderFactory / BilibiliProvider 与新版 `examples/bilibili.json` 接入。最初旧搜索端点返回 HTTP 412；改为公开 nav 的 WBI 参数、正常访客 buvid 会话和现行 WBI 搜索端点后，热门/搜索各返回 20 个条目。弹幕 HTTP 响应为 raw deflate，原加载器解码失败；补齐 Content-Encoding 后解析 1200 条评论。

macOS x64 JIT 与最新 Native AOT 均通过分类、热门、搜索、详情、分集、实时播放地址和弹幕加载。LibVLC 的 H.264 VideoToolbox 解码实际观察到位置 3.40 秒、时长 272.50 秒、视频轨 0，视频解码块 217、音频解码块 421。结束由诊断主动 Stop，随后 Interrupted system call 是中断网络读取的日志。测试使用 dummy 输出，不代表 GUI 画面验收。

JIT 日志 `/private/tmp/vodbox-bilibili-danmaku-jit-network.log`；Native AOT 日志 `/private/tmp/vodbox-bilibili-native-aot-network.log`；严格 AOT 发布日志 `/private/tmp/vodbox-bilibili-wbi-desktop-aot.log`。78 项本地测试通过，当前轮没有 GitHub Actions。

可手动复现（源站/媒体 URL 可变化，不加入每次 CI 的阻断检查）：

```sh
VodBox --diagnostics --bilibili-smoke BV1WSHL66EdZ --native
```

删除 `--native` 可只检查内容接口与弹幕。开发运行需要 `VODBOX_VLC_PATH`，随包产物使用已有原生依赖。当前只支持单段音画合一公开投稿；DASH、多段、番剧、账号、原片单配置和 `csp_BiliGuard` 尚未实现，不能将上述成功等同于两个旧源的全部 Bili 条目可用。
