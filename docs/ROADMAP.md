# VodBox 开发路线图

> 基线：commit `9a67c2d`（clean-slate 重写）+ `ea938e4`。构建 0 错误，21 测试通过。
> 目标：把 FongMi/TV 的能力搬到 Avalonia 12 + .NET 10 NativeAOT + libmpv 桌面端。
> 本文档由能力差距分析生成，按阶段推进，每阶段有验收标准。

## 当前进度快照

**代码量**：约 2500 行托管代码，4 个项目（Core / Infrastructure / Playback.Mpv / Desktop）+ 21 项测试。

**已跑通（A 类）**
- TVBox 配置加载：远程拉取、JS 变量包裹、`//` 注释剥离、JPEG 尾部 base64 隐写（饭太硬真实样本回归通过）
- MacCMS v10 JSON 采集：分类 / 分页 / 搜索 / 详情 / `vod_play_from$$$vod_play_url` 线路拆分
- M3U + TXT 直播解析（group-title / tvg-logo / `#genre#` / 多线路 `$`）
- SQLite 存储：历史 / 收藏 / 配置订阅 / 偏好（4 表）
- 多站点并发聚合搜索
- libmpv 传输层：loadfile / play / pause / seek / 倍速 / 音量 / UA-Referer / 断点续播位置
- 8 页面 Avalonia 外壳 + 详情选集 + 全视图设计预览数据

**关键缺口（实测验证）**

用饭太硬真实配置（`http://www.饭太硬.net/tv`，48 站点）测试：
- **94% 站点是 `csp_` Java jar 爬虫**（需 JVM，桌面端无解，长期只能靠原生 C# 适配高频站点）
- **6% 是 `.js` drpy 脚本站点**（虎牙 / 斗鱼 / 儿童启蒙）
- **0 个 MacCMS 站点** —— 当前唯一支持的运行时
- 6 个 lives 直播源、wallpaper 字段、0 parses

结论：**当前实现对真实 TVBox 配置的可用站点数 = 0**。这是最大的功能性缺口。

**视频渲染是空壳**：`MpvNative.cs` 无 `mpv_render_context_*` 绑定，`MpvVideoSurface` 是占位，`PlayerOverlay.axaml` 只画黑矩形 + "libmpv 渲染窗口"文字。**有传输层但看不到画面**。
好消息：git 历史（`9a67c2d^`）有可用的 `MpvRenderContext.cs` + `MpvNative.Render.cs` + `OpenGlControlBase` 版 `MpvVideoSurface.cs`，旧版曾通过 macOS JIT/AOT 真实解码验证，可捞回适配。

**其他半成品（B 类）**
- 本地文件页能浏览不能播（`PlayRequested` 事件零订阅者）
- 历史续播丢集数上下文（不存 `LineId`/`EpisodeId`，`SourceName` 硬编码"本地"）
- 直播收藏 / 历史 Tab 是空 stub，`lives[]` 解析后被丢弃
- 海报全是 emoji 占位，无图片加载 / 缓存
- 音轨 / 字幕轨引擎支持但 UI 未接
- README 声称的 catchup 解析实际没做

## 阶段计划

### S0 · 快速修复与地基（P0，0.5–1 天）
1. 本地文件播放接线：`FilesViewModel.PlayRequested` → `PlayerViewModel.Play`
2. 历史持久化补全：存 `LineId` / `EpisodeId` / `Remarks` / 真实 `SourceName`；修 mpv 事件线程阻塞
3. 中文域名配置 URL 验证（`IdnMapping` 测试；实测 .NET HttpClient 自动转 punycode，但加回归锁定）
4. README 与实现对齐：删掉未实现的 catchup / 轨道 UI 声称
5. 接入测试源：`.cache/test-sources.md` 里的两个源加进冒烟脚本

### S1 · 视频渲染落地（P0，2–4 天）★ 最关键
- 捞回 `9a67c2d^` 的 `MpvRenderContext` / `MpvNative.Render.cs`，适配 Avalonia 12 `OpenGlControlBase` API
- `MpvNative` 补 `mpv_render_context_create/update/render/free/set_parameter/proc_address` 绑定
- `MpvEngine.OpenAsync` 装 render context（当前设了 `vo=libmpv` 但没回调 → 必然无画面）
- `PlayerOverlay.axaml` 黑矩形换成 `MpvVideoSurface`；DPI / 尺寸变化 / GL 丢失恢复
- 三平台验证（macOS 本机先过，Windows/Linux 靠 CI）
- 验收：本地 mp4 + 一个 HLS 直播源能看到画面、暂停、seek、全屏往返

### S2 · 播放体验（P0–P1，3–5 天）
- 音轨 / 字幕轨 / 视频轨选择 UI（引擎已就绪）
- 快捷键：空格 / ←→ seek / ↑↓ 音量 / F 全屏 / Esc / 数字选集
- 画面比例、片头片尾跳过（字段已存在未用）、自动下一集、控制条自动隐藏
- 播放偏好持久化（音量 / 倍速 / 比例 / 内核设置）
- 验收：键盘完整操作一遍点播 + 直播

### S3 · 内容源运行时扩展（P1，最重，2–4 周）
- **QuickJS 宿主**（drpy `.js` 站点）：QuickJS.NET P/Invoke + TVBox Spider API 桥（init/home/category/detail/search/player）+ `fetchText`/`sha256`/资源限制；AOT 安全
- **原生 C# 高频站点适配**：按真实配置频率推进（旧代码有 Bilibili/AppGet/App99/音频站参考实现可捞）
- **`csp_` 策略**：明确不支持并 UI 提示"该源需 Android 端"，长期挑高频的用 C# 重写
- 验收：饭太硬配置的 3 个 js 站点能浏览 + 播放

### S4 · 直播完整化（P1，1 周）
- TVBox `lives[]` 消费（多直播源、per-source UA/timeout）
- EPG / XMLTV：解析 + 缓存 + tvg-id 匹配 + 频道信息条 + 节目单时间轴
- catchup 回看（M3U `catchup-source` 模板替换）
- 直播收藏 / 历史 Tab 落地、密码分组锁
- 验收：加载饭太硬 6 个直播源，能看节目单、回看

### S5 · UI 打磨与海报（P1，3–5 天）
- 海报图片加载 + 磁盘缓存 + 占位（替换所有 emoji）
- 筛选面板 UI（`FilterGroup` 模型已存在，MacCMS 参数已映射）
- 搜索联想 / 热搜词 / 搜索历史持久化
- 详情页：倒序选集、分段（>20集）、已看标记、快搜换站
- 主题（深/浅/跟随）、海报密度、无痕模式、壁纸
- 验收：视觉稿 `design/index.html` 8 画板逐一对照

### S6 · 设置中心与数据（P1，3–5 天）
- 配置订阅管理 UI（`IConfigStore` 已实现未接）：多配置历史、切换、设为首页源
- 站点管理：星标 / 隐藏 / 排序 / searchable-changeable 开关
- 播放 / 弹幕 / 字幕 / 界面 / 数据 / 关于 分组表单
- 备份 / 恢复（SQLite + prefs 打包）、跨设备同步（依赖 S7）
- 验收：设置 8 分组全可操作且持久化

### S7 · 本地 HTTP 服务与推送（P2，1 周）
- Kestrel 自宿主 minimal API：`/proxy` `/cache` `/action` `/media` `/upload`（AOT source-gen JSON）
- 媒体代理：请求头回环、HLS 清单/密钥转发、DASH 模板
- 推送页：二维码 + Web 遥控器 + 剪贴板推送
- 验收：手机扫码能遥控播放

### S8 · 弹幕 / 解析 / 字幕（P2，1–2 周）
- 弹幕：拉取 + 解析（XML/JSON/gzip）+ SkiaSharp 自绘层（滚动/顶部/底部）+ 时间轴同步
- VIP 解析器：`IPlayResolver` type 1/2（JSON 接口）实现 + 解析线路选择 UI
- 在线字幕（assrt）搜索 + 本地字幕加载 + 样式设置
- 验收：B 站源弹幕能看、一个需解析的源能播

### S9 · 平台与发布硬化（P2，持续）
- 跨平台媒体键、休眠抑制、多屏 DPI
- Windows 签名、Apple Developer ID / 公证
- 自动更新检查 + 签名校验
- 六 RID CI 全绿 + 安装包冒烟
- DLNA 投屏（可选，二期）

## 明确不做
- JVM / Dex 加载 `csp_` jar 爬虫（架构不可行）
- Android Auto、PiP 系统级（桌面用迷你窗口替代）
- DRM（Widevine/PlayReady，mpv 不支持）
- 旧 TVBox 配置导入 / Java 插件兼容层（项目决策）

## 测试源（用户提供）
- `https://宝盒接口.top` —— 当前网络 TLS 被重置，待复测
- `http://www.饭太硬.net/tv` —— 可用，JPEG 隐写形态，48 站点（94% csp_）
