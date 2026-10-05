# 播放测试

用户授权将 `http://www.饭太硬.net/tv` 作为后续播放测试来源。该地址返回 JPEG 尾部封装的 Base64 配置，解码后是含注释的旧 TV JSON。2026-10-05 读取结果包含 48 个站点与 6 个直播列表；大部分点播站点依赖 `csp_*` Java Spider。

该来源不是直接视频地址，也不是 VodBox 新配置。暂不兼容旧配置、不要实现 JVM/Dex 兼容层的既定范围保持不变。测试时可从其中选取直接媒体 URL，或使用 `examples/user-live-test.json` 中转换好的公开直播列表，验证现有 M3U 解析与 LibVLC 播放。该示例不导入其点播插件。

2026-10-05 本地已确认网易列表返回有效 M3U，其首个 IPv6 频道探测超时。GitHub 代理镜像返回 403，改为读取 Kimentanm/aptv 的公开 raw 列表后得到可用 IPv4 测试地址。

使用项目实际 `LibVlcEngine(headless: true)` 验证 `http://101.35.240.114:88/live.php?id=CCTV1`，请求头 `User-Agent: AptvPlayer-UA`。HTTP 200 返回 HLS 清单；原生日志识别 TS、H.264 与 AAC（双声道 44100 Hz），显示 VideoToolbox 解码，状态进入 `Playing`，播放进度达到 6102 ms。测试随后主动停止。M3U 的 `http-user-agent`、`http-referrer` 和 `#EXTVLCOPT` 对应字段已传递到播放器；没有显式请求头时该频道探测返回 404。该记录确认当前网络下媒体接收和原生解码，不代表所有站点、频道或桌面画面已验证；流地址可随时失效。

临时诊断使用已打包 VLC 库和插件目录，不加载 Java 插件。测试记录区分网络可达性、列表解析、实际解码与界面显示。

CI 使用应用生成的四秒 WAV 验证 AOT 原生解码，用示例插件验证随包脚本运行时，不把外部直播源作为每次提交的阻断条件。测试外部源时记录测试时间、选用的频道 URL、HTTP 状态、VLC 状态、收到的数据和播放进度；避免把配置里的 `csp_*` 字符串直接当作播放地址。


## 音频延迟增强验证

在相同实际 CCTV1 HLS 来源再次解码，150ms 延迟设置后 LibVLC 返回 AudioDelay=150000 微秒，恢复零值成功；播放器状态 Playing，位置 5929ms。使用 dummy 音视频输出完成 native API 校验，不代表主观声画同步、字幕延迟或截图界面已经验收。
