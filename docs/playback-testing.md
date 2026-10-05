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


## 音频专辑 Spider 真实验收（2026-10-05）

xsmp3/psmp3 公开分类与专辑均可读，两个首集媒体小范围读取 HTTP 200 / audio/mpeg。相声分类 12 专辑，首专辑 50 集，JIT LibVLC 解码 3.03 秒 / 317 音频块；单田芳评书 50 集，JIT 及严格 Native AOT 都解码 3.21 秒 / 332 音频块。相声 AOT 内容及解析通过，但媒体 TLS 握手超时，未记为 AOT 解码通过。没有忽略 TLS 校验或回退明文媒体。

完整 123 项本地回归、11 项音频专项测试、最终严格 Native AOT 发布通过。搜索实际跳回首页，明确不支持，诊断显示 unsupported；音频验收使用 DecodedAudio，未伪装成视频解码。配置 `examples/audio-sites.json` 可直接使用，完整范围见 [音频 Spider](audio-site-spider.md)。本轮没有运行 Actions。


## mpv IPlaybackEngine 验收（2026-10-05）

完整 126 项回归、3 项 MpvEngine 专项通过。真实 libmpv JIT 及严格 Native AOT 均通过含 AAC 音轨的 15 秒合成视频：观察位置/时长/暂停/seekable、续播 1.5 秒、暂停精确跳转 2.5 秒、恢复、音轨缓存、音频 120ms / 字幕 -340ms 延迟以及停止/释放。日志 `/private/tmp/vodbox-mpv-engine-native-aot.log`。旧合成样本只有视频轨道，音轨检查失败后使用自生成音视频样本修正，未放宽音轨断言。它是接口/native ABI 验证，不能代替主窗口路由或 GUI 验收；没有运行 Actions。

## 双内核路由验收（2026-10-05）

新增 13 项专项，完整 139 项本地测试通过，编译警告作为错误处理。覆盖一次启动/异步失败回退、固定模式不回退、网络文件限制、旧事件隔离、位置/暂停/音量/倍速/延迟迁移、排队请求取消、切换取消后的停止清理、缺失轨道和异常释放。

`--diagnostics --engine-router-smoke <本地音视频文件>` 使用真实 mpv 与 LibVLC 无窗口内核，验证 mpv → LibVLC → mpv，保留暂停下的 2.5 秒位置、倍速与延迟，恢复播放后停止。mpv 音量读取验证为 35；LibVLC dummy 输出读取为 0，不记为设备音量通过。该测试定位并修复 LibVLC 暂停后 seek 缓冲事件覆盖暂停的问题。

`--diagnostics --engine-router-fallback-smoke <本地媒体文件>` 在独立进程将 `VODBOX_MPV_PATH` 指向不存在的库，验证真实 LibVLC 接管同一媒体与会话。JIT 已验证，无音轨的视频也不会因为恢复延迟偏好而失败。正常切换日志 `/private/tmp/vodbox-router-native-switch-jit.log`，回退日志 `/private/tmp/vodbox-router-native-fallback-jit.log`。

主窗口仍使用 LibVLC。这些诊断不包含主窗口设置、GPU 表面切换、弹幕叠层、字幕轨道迁移、网络浏览/投屏或六 RID 原生包验收，没有运行 Actions。

最终 macOS x64 Native AOT 发布（全裁剪、编译与 ILC 警告作为错误）以及上述两个真实诊断均通过。AOT dummy LibVLC 音量可返回 -1，同样不视为设备音量验证。日志 `/private/tmp/vodbox-router-native-aot-build.log`、`/private/tmp/vodbox-router-native-switch-aot.log`、`/private/tmp/vodbox-router-native-fallback-aot.log`。

## 主窗口双内核与 PC 布局（2026-10-05）

主窗口设置、偏好、原生视频表面、两种弹幕与布局已接入。新增测试后完整 148 项回归通过，其中 4 项覆盖 900／1280／1600 DIP、浏览／播放页切换及视频容器不重建。普通测试仍不加载原生播放器。

`tests/VodBox.Mpv.UiSmoke` 新增 `--desktop <音视频文件>`，使用实际 MainWindow 与临时内存数据服务，不加载或保存用户配置。JIT / Native AOT 已验证 mpv → LibVLC → mpv、GPU 帧数、2.5 秒暂停位置、LibVLC 输出/解码与设备静音、设置绑定、上下文复用、mpv 内嵌弹幕及 LibVLC 透明窗口弹幕。最终 AOT 探针还在暂停状态切到 900 DIP、返回浏览、恢复播放页并回到 1280 DIP，确认原渲染表面保留且恢复播放。

`--desktop-fallback <音视频文件>` 在独立进程将 `VODBOX_MPV_PATH` 指向不存在的库，JIT/AOT 均观察到真实 LibVLC 接管、可用原生视频表面、解码和弹幕，模式仍为自动。这不是模拟内核测试。

实际测试修复了三个初始化竞争：mpv render context 创建前 loadfile 导致只播放音频；LibVLC drawable 创建前开始播放；Playing 回调后的过早暂停让原生输出尚未创建。现在等待表面就绪，桌面暂停时有界等待输出，音频设备创建后恢复缓存音量，并回到原暂停位置。关闭先保存进度和停止解码，再解绑 drawable 和释放表面。

严格发布使用 `-warnaserror -p:TreatWarningsAsErrors=true -p:IlcTreatWarningsAsErrors=true`、全裁剪和源码生成 JSON，编译无警告。还原复用用户 NuGet 配置，但通过 `--source https://repo.huaweicloud.com/repository/nuget/v3/index.json` 明确单镜像，消除双源中央包管理 NU1507；没有修改系统配置。RID 还原/发布统一用 `-p:RuntimeIdentifier=osx-x64`，不使用会导致锁文件选择时序不同的 `-r`。

日志：`/private/tmp/vodbox-desktop-dual-engine-tests.log`、`/private/tmp/vodbox-desktop-dual-engine-ui-jit.log`、`/private/tmp/vodbox-desktop-dual-engine-ui-aot-build.log`、`/private/tmp/vodbox-desktop-dual-engine-ui-aot.log`、`/private/tmp/vodbox-desktop-dual-engine-ui-fallback-aot.log`。其他平台、长期运行、硬解/HDR、真实多字幕切换及六 RID 双内核原生包未验收，没有运行 Actions。

最终正式桌面产物也以相同严格参数发布，通过偏好/内核模式备份、SQLite、弹幕与真实无窗口 mpv/LibVLC 往返控制诊断：`/private/tmp/vodbox-desktop-dual-engine-aot-build.log`、`/private/tmp/vodbox-desktop-dual-engine-aot-diagnostics.log`。GUI 复验曾在显示非活动时于窗口创建前遇到 Avalonia 原生 RenderTimer `-6661`；临时唤醒显示后最终两个 AOT 窗口探针均以 0 退出，往返探针绘制 44 帧，回退探针解码 100 帧。未修改系统睡眠设置。

受控 MPEG4 样本停止时 LibVLC 仍可能记录 `get_buffer()` / `avcodec_send_packet` 解码器日志；退出码为 0，关闭先停止解码再解绑 drawable，不将日志描述为“无运行时警告”。这些合成样本验收不能替代其他编码与长期运行测试。

## 自适应海报网格本地验收

149 项回归全部通过，新增 1,000 条内容的实际控件虚拟化、分页追加复用、列数重排顺序与选中状态测试。设计器预览保持通过。日志：`/private/tmp/vodbox-poster-grid-tests.log`。

macOS x64 严格 NativeAOT（完整裁剪，编译／ILC 警告作为错误）编译通过。真实主窗口在 1,000 条海报数据下验证 mpv → LibVLC → mpv、暂停位置、静音、弹幕、900／1280 DIP 切换与渲染表面复用；实际海报 Image 控件数介于 1–100，mpv 渲染 44 帧，进程退出 0。日志：`/private/tmp/vodbox-poster-grid-aot-build.log`、`/private/tmp/vodbox-poster-grid-aot-ui.log`。本轮没有运行 GitHub Actions，也未验证 Windows／Linux 真机。

## 详情与分集浏览本地验收

150 项回归全部通过，新增 2,000 集搜索、倒序、无匹配结果、线路替换／清空和实际按钮虚拟化测试。筛选与排序保持原始线路顺序，未创建播放内核。日志：`/private/tmp/vodbox-detail-browser-tests.log`。

macOS x64 严格 NativeAOT 完整裁剪编译通过，未报告编译或 ILC 警告。真实 AOT 主窗口加载 1,000 条海报和 2,000 集，确认搜索第 2000 集、恢复倒序列表、分集按钮虚拟化和原始线路顺序，同时验证 mpv → LibVLC → mpv、暂停位置、静音、弹幕、900／1280 DIP 布局与表面复用，mpv 渲染 44 帧，退出 0。日志：`/private/tmp/vodbox-detail-browser-aot-build.log`、`/private/tmp/vodbox-detail-browser-aot-ui.log`。独立详情页、完整播放视图及其他平台验收仍待完成；本轮未运行 Actions。

## 页面职责拆分与独立播放页验收

按 FongMi/TV 的 Home／Vod／Type／Video 和设置子页职责重新整理，而非仅移动原有 XAML。主窗口保留外壳与原生生命周期，13 个视图分别承载页面／组件，均有 Design.DataContext 与编译绑定；通用、播放、弹幕设置独立。浏览和播放页面分离，播放页内部宽屏视频／详情并列、窄屏上下排列，直播播放状态与当前浏览导航分离。

164 项回归通过，包括 13 项独立视图预览、海报点击与自适应列数、设置导航、视频容器复用、播放页内部宽窄布局，以及既有海报／分集虚拟化测试。日志：`/private/tmp/vodbox-view-split-tests.log`。

共享主题使用强类型 DesktopTheme 编译 XAML，未使用 C# 动态 StyleInclude 或反射 ViewLocator。macOS x64 严格 NativeAOT 完整裁剪编译通过，最终日志没有编译／ILC 警告。真实 JIT／AOT 主窗口验证 mpv → LibVLC → mpv、暂停位置、静音、两种弹幕、900／1280 DIP、所有导航页切换、VLC 叠层随页面隐藏／恢复和原生表面复用；1,000 条海报与 2,000 集的首次布局和虚拟化验证通过。JIT 渲染 40 帧、AOT 渲染 43 帧，均退出 0。

AOT 缺失 mpv 自动回退也通过，LibVLC 解码 98 帧，原生视频／弹幕和自动模式正常，退出 0；关闭 MPEG4 测试片段仍有前述 avcodec 停止日志，未宣称此日志已消除。日志：`/private/tmp/vodbox-view-split-ui-jit.log`、`/private/tmp/vodbox-view-split-aot-build.log`、`/private/tmp/vodbox-view-split-aot-ui.log`、`/private/tmp/vodbox-view-split-aot-fallback.log`。

本轮未运行 GitHub Actions；Windows／Linux 真机和完整全屏控制仍待验收。页面当前共享 MainViewModel 与应用服务，进一步拆分页面状态模型仍待继续。

## 全屏悬浮控制与平台图标（2026-10-05）

166 项本地回归通过。全屏测试从浏览/播放两种起点进入，检查原视频容器复用、导航/详情隐藏、底栏透明、底栏不再占独立行、所有操作控件中心线一致；实际点击 2× 预设、打开精细滑块调到 1.5×、检查播放/暂停图标状态，再通过拥有焦点的图标按钮发出 Esc，恢复原页面并保留倍速。每个页面仍有 Design.DataContext，预览不启动原生内核。

macOS x64 JIT 真实窗口验证通过，mpv 渲染 78 帧、进程退出 0。LibVLC 的全屏控制使用独立透明窗口，倍速弹层调节到 1.25× 后原生 Player.Rate 一致；mpv 全屏确认客户区/视频都是 1920×1200 DIP、缩放率 2，透明底栏与视频底部重叠、单行控件中心一致，原生 speed 到 1.5×，退出保留倍速与渲染表面。

探针曾把这里的屏幕逻辑尺寸再次除以 RenderScaling，误判全屏大小；已改为检查视频填满实际客户区，并记录屏幕与客户区。macOS 切换动画完成前连续切换也曾产生 `not in fullscreen state`，探针增加动画等待，切换入口恢复主窗口焦点。最终 JIT 记录未出现该日志，但不据此宣称其他平台/多屏动画均已完成验收。日志：`/private/tmp/vodbox-fullscreen-tests.log`、`/private/tmp/vodbox-fullscreen-jit-ui.log`。

MSBuild 实际求值确认：Debug（含 RID）PublishAot=false、PublishTrimmed=false、DebuggerSupport=true、JsonSerializerIsReflectionEnabledByDefault=true；Release RID 为 AOT/full trim、JSON 反射默认关闭；DisableAOT=true 时关闭 AOT 与裁剪。条件集中在 PropertyGroup，避免 Rider 对属性级 Condition 的提示。原生 UI 探针仅在 Release RID 时向桌面引用传递 AOT 属性，避免 SDK 的自包含可执行项目引用不匹配，也不强制 Debug 设计器进入 AOT。

图标已验证 ICO 内置七种尺寸、ICNS 包含 1024 画布、Linux 九种 PNG 尺寸、macOS alpha 边界为 (100,100)-(924,924)。模拟打包调用验证图标进入 Contents/Resources、CFBundleIconFile 正确且进入依赖清单；签名/DMG/Windows/Linux 完整包生成没有在本轮重复执行。所有图标资源位于 desktop/Assets/Icons，仅 256px PNG 嵌入 Avalonia 窗口资源。未运行 GitHub Actions。

最终 macOS x64 NativeAOT（完整裁剪，编译/ILC 警告作为错误）编译成功；真实 AOT 主窗口通过以上全屏叠放、透明度、单行中心线、原生倍速及表面复用检查，渲染 80 帧，退出 0。日志：`/private/tmp/vodbox-fullscreen-aot-build.log`、`/private/tmp/vodbox-fullscreen-aot-ui.log`。尝试按该进程的 CGWindow ID 截取窗口，系统返回 `could not create image from window`，未得到截图；本轮验收为窗口几何/属性与原生状态检查，不能替代截图级视觉和三平台人工验收。


## 首页和配置流程修正验证（2026-10-05）

171 项本地回归通过，包含 15 个独立视图预览，以及首次启动不加载示例、设置配置成功后返回首页/失败留在设置、推荐与分类搜索隔离、首页全屏返回。旧播放页用例显式进入播放/点播再验证，没有削弱全屏、弹幕、海报或分集断言。

macOS x64 严格 NativeAOT/full trim 发布通过（编译和 ILC 警告作为错误）。真实 AOT 主窗口导航包含新增首页，检查推荐快照、首页可见与侧栏隐藏；随后验证原有设置/直播/历史/收藏/点播与播放页往返、LibVLC/mpv 切换、透明原生叠层、全屏控制/倍速和视频表面复用，渲染 77 帧，进程退出 0。日志：`/private/tmp/vodbox-home-tests.log`、`/private/tmp/vodbox-home-aot-build.log`、`/private/tmp/vodbox-home-aot-ui.log`。本轮没有截图级视觉验收或 Windows/Linux 真机验收，没有运行 GitHub Actions。

### 2026-10-05：独立设置窗口与视频内悬浮控制区

本地完整回归 178 项通过；严格 NativeAOT 发布 osx-x64 UI probe 成功。真实 macOS 窗口验证退出码 0，mpv → VLC → mpv、73 帧视频、窗口及全屏控制区、设置窗口往返、播放倍速保留均通过。日志位于本地 `/private/tmp/vodbox-floating-controls-aot-build.log` 和 `/private/tmp/vodbox-floating-controls-aot-ui.log`。Debug 依赖已重新恢复以保留设计器预览。未运行 GitHub Actions；Windows/Linux 真机验证仍待完成。

真实 Avalonia 控件的离屏渲染输出位于 `artifacts/previews/`，设置窗口包含收紧菜单及贴边滚动区域。播放预览背景是示意渐变，不是原生视频截图；原生视频另由上述集成验证覆盖。应用样式改为显式 StyleInclude 引用，便于 IDE 追踪共享样式类；编辑器诊断列表未通过自动化直接读取。
