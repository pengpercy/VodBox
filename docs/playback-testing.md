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

### 原生固定片单验证（2026-10-05）

`options.categories[].videos` 接入 BV/av 固定片单，每页 20 项、四路 worker、整页 20 秒期限，保持配置顺序并复用详情缓存。新增九个测试用例，完整本地回归为 87 项通过；覆盖跨页、缓存、取消、失效内容及配置限制。

显式 `-p:RuntimeIdentifier=osx-x64` 选择已提交的 RID 锁文件后，严格发布（`TreatWarningsAsErrors=true`、`IlcTreatWarningsAsErrors=true`）通过。产物 `artifacts/bilibili-playlist-aot/VodBox` 真实网络诊断返回分类 5、热门 20、搜索 20、片单 1、分集 1、弹幕 1200；LibVLC VideoToolbox H.264 解码至 3.40 秒，视频 217 块、音频 421 块。日志 `/private/tmp/vodbox-bilibili-playlist-native.log`。

首次 JIT 网络诊断遇到搜索响应缺失 `result` 数组，明确报错退出；未把不完整响应当成功或新增无限重试。对应日志 `/private/tmp/vodbox-bilibili-playlist-jit.log`。单次有限复核通过同一网络、片单、弹幕和实际解码链路，日志 `/private/tmp/vodbox-bilibili-playlist-jit-repeat.log`。公开 API 有即时响应差异，网络诊断仍为手动检查，不作为每次 CI 的必跑项。本轮没有触发 GitHub Actions。

## C# AppGet V119 验证（2026-10-05）

新增 `AppGetProvider` 的八项 fixture 测试，完整本地回归 95 项通过。使用独立 OpenSSL 密文向量验证 AES 协议，测试加密时间戳头、UTF-8 表单、HTTP 发现隔离、分页、详情/分集、每次刷新解析结果、重定向拒绝和四路取消。日志 `/private/tmp/vodbox-appget-final-tests.log`。

严格 `osx-x64` Native AOT 发布通过，所有编译/ILC 警告作为错误；日志 `/private/tmp/vodbox-appget-desktop-aot.log`。原生产物 `artifacts/appget-desktop-aot/VodBox` 实际取得一碗分类 5、列表 30、搜索 14、线路 12、分集 12，并完成播放地址解析；日志 `/private/tmp/vodbox-appget-yiwan-native-aot.log`。站点内容动态变化，之前检查为 9 条线路，因此不会固定远端列表数作为单元测试断言。

最终 JIT 内容链路：咕咕分类 3/列表 30/搜索 1/线路 1/分集 4；蔬菜分类 7/列表 30/搜索 1/线路 2/分集 2。两项均在 `vodParse` 收到 code=0 与空 data 后明确失败，不将空结果当成功，未尝试绕过验证。日志 `/private/tmp/vodbox-appget-gugu-final-jit.log`、`/private/tmp/vodbox-appget-shucai-final-jit.log`。

一碗首条线路媒体 HTTPS/HLS 连接失败，未产生可验证的视频解码；第二条线路只读检查 TLS 握手超时，未继续重复解码。失败日志 `/private/tmp/vodbox-appget-yiwan-jit.log`；首发初始化只读检查 HTTP 503。AppGet 的真实视频解码尚未通过，不能用初始化、内容接口或地址解析成功代替播放成功。V120/V122、外部解析、账号/验证码仍未实现。真实 key 不提交，示例仅含占位值；本轮不触发 GitHub Actions。


### AppGet 解析参数修正与实际播放

首版 vodParse 失败并非已确认需要登录：新协议参考传递 player_parse_type 且直接提交 AES Base64。取消密文预先 URL 编码、补齐该字段，并按协议解码不透明输入后，咕咕视频 5092 成功返回 MP4。首轮修正验证曾在分类接口遇到超时；固定视频复核通过，不新增无限自动重试。

最终 104 项本地测试通过（AppGet 17 项），日志 `/private/tmp/vodbox-appget-final-parser-tests.log`。外部 JSON 解析新增顶层/嵌套 URL、准备好的 parse_api_url、协议/URI 校验、1 MiB 上限和共享四路取消测试；Qiji V122 的 JSON 请求、独立 IV、端点和验证码声明通过 fixture。

macOS JIT 实际 VideoToolbox H.264 解码至 3.40 秒，视频 216 块、音频 387 块，日志 `/private/tmp/vodbox-appget-gugu-fixed-media-jit.log`。最终严格 Native AOT 发布通过，日志 `/private/tmp/vodbox-appget-parser-final-aot.log`；`artifacts/appget-parser-aot/VodBox` 同一视频解码至 3.16 秒，视频 200 块、音频 367 块，日志 `/private/tmp/vodbox-appget-gugu-parser-native-aot.log`。

蔬菜以新参数复核仍返回 code=0 / 空 data；一碗媒体线路和首发初始化未通过项仍保留。显式 Qiji V122 参考发现文件遇到 TLS 错误，未进行证书绕过；只有 fixture 支持，不能计为四个原 AppGet 条目全部可用。CI 未触发。


## App99 BN v2 真实验收（2026-10-05）

双星匿名分类、列表、搜索、详情、解析通过（8 分类、21 列表、5 搜索、9 线路 / 414 集）。JIT LibVLC 使用 VideoToolbox 实际 H.264 解码到 3.38 秒，187 视频块 / 470 音频块。剧圈匿名内容和解析通过（18 分类、21 列表、2 搜索、7 线路），首次媒体返回 403。

112 项本地测试通过，包含 8 项 App99 协议测试；严格 macOS x64 Native AOT 编译及实际 App99 内容/解析通过。动态首条媒体 403；固定视频 1026210 首线路 404，未记录为 AOT 解码通过。固定视频 1052399 JIT 首线路也 404。真实媒体可用性与原生协议通过分别记录，不能由一次成功推断全部线路可用。新版示例只保留占位参数，真实配置、解密内容与签名数据不入库，未触发 CI。更多范围见 [App99 Spider](app99-spider.md)。
