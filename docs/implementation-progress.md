# 实现进度与继续工作清单

本清单以原重写方案为目标。CI 通过不等于功能完成；一个功能写入代码、单元测试通过、跨平台编译通过、桌面交互验收通过是不同层次。旧配置与 Java/JVM/Dex 兼容层按用户决定不实施。

本清单包含 43 项能力：17 项有基础验证，21 项已有实现但仍需完整验收，5 项仍需实现。按能力项计数，不代表工时比例或完整移植百分比。

## 已实现并有基础验证

- [x] .NET 10 / Avalonia 最新稳定版本锁定、可移植 NuGet 锁文件与系统镜像复用。
- [x] Core / Application / Infrastructure / Playback / Desktop / PluginHost 分层。
- [x] MVVM 与 JSON 源码生成、编译绑定、业务代码无反射注册。
- [x] Design.DataContext 与不访问运行时服务的设计数据。
- [x] Mica / Acrylic / Blur / 实色回退请求。
- [x] 本地媒体、URL、暂停、停止、跳转、音量、倍速、全屏。
- [x] 字幕载入与字幕、音轨选择。
- [x] SQLite 配置隔离的历史、收藏和无痕播放。
- [x] 播放请求取消、会话编号与原生命令串行调度。
- [x] C# JSON 目录源：分类、分页、搜索、详情、选集。
- [x] QuickJS-NG 的独立 AOT 宿主、fetchText / sha256 与资源限制。
- [x] Python / Node NDJSON worker 与随包运行时。
- [x] M3U / TXT / JSON 直播解析、备用地址合并、User-Agent / Referer 传递。
- [x] XMLTV 显式时区解析与 DTD 禁用。
- [x] 六 RID 本机 Native AOT、原生解码、脚本协议和安装包 CI。
- [x] 固定原生资产版本 / SHA-256、许可证保留、符号分离与打包依赖哈希校验。
- [x] 用户提供测试来源中的实际 HLS H.264 / AAC 接收与 VideoToolbox 解码记录。

## 本轮已写入功能，继续做集成与跨平台验收

- [~] C# AppGet V119 Spider：加密异步 HTTP、分类/推荐、分页筛选、搜索、多线路分集、直接媒体/站内解析、播放令牌刷新；95 项本地回归通过。三个真实站点初始化已验证，一碗内容与地址解析通过，真实媒体解码未通过；V120/V122、外部解析、登录/验证码仍待实现。

- [~] C# 哔哩哔哩公开投稿 Spider：热门、关键词分类、原生 BV/av 片单分页、搜索分页、分集、实时播放解析与自动 XML 弹幕；macOS JIT/AOT 基础实际解码通过，旧片单配置/Guard/DASH/账号功能未实现。
- [~] C# MacCMS JSON / XML HTTP 采集适配：分类、分页、搜索、详情、多线路与选集；合成 HTTP fixture 测试通过。
- [~] 原配置位置与解析后快照的配置仓库；原子写入、离线切换与配置移除。
- [~] 音量、倍速、自动下一集、续播、片头片尾和主题偏好持久化。
- [~] 四路并发聚合搜索、逐源结果、超时 / 失败隔离、取消和打开结果。
- [~] 收藏返回所属配置与内容源的详情；历史重新解析播放地址。
- [~] 独立于浏览页的播放列表、上一集 / 下一集、自动连播、片头片尾与已完播进度处理。
- [~] 直播分组与搜索、备用地址按钮。
- [~] gzip XMLTV 下载限制、磁盘 / 内存缓存、tvg-id / 名称 / 映射匹配与节目表界面。
- [~] UTF-8 / UTF-16 / GB18030 直播字符编码处理。

- [~] JSON 解析链、地址栏解析器选择、原始地址保留；编码与循环测试通过。
- [~] 请求头回环代理、Range / HEAD、HLS 清单 / 密钥和 DASH 嵌套 / 继承模板转发；真实 TCP fixture 测试通过，长时与真实 DASH 播放验收待做。
- [~] 独立 Chrome / Edge / Chromium CDP 嗅探；本机受控网页已验证，iframe、认证与多候选 UI 待补齐。

- [~] 海报磁盘缓存、后台缩略图解码、四路下载与容量淘汰；缓存复用 / 淘汰测试通过。
- [~] 音频 / 字幕延迟与截图；本机实际 HLS 的音频 150ms 设置及还原验证通过，字幕 / 截图有画面验收待做。
- [~] 播放快捷键、媒体 / 字幕 / 配置 / URL 拖放与明暗主题资源；大屏布局和桌面交互验收待做。

- [~] 直播收藏、上次频道恢复、有限次数的备用线路重试与分钟级节目表刷新；直播历史按频道身份重开，不保存时间轴位置。

- [~] 单源搜索分页、MacCMS 年份 / 完结筛选、脚本筛选参数透传；通用筛选元数据和聚合分页仍待扩展。

- [~] 可迁移 JSON 数据备份 / 合并导入、导入前自动备份、全部历史事务快照、配置与偏好迁移；跨平台文件选择和异常 I/O 恢复仍需验收。

- [~] XML / JSON / gzip 弹幕、本地 / HTTP 下载、滚动 / 顶部 / 底部调度、延迟和显示偏好、每集自动地址以及透明原生视频叠层；macOS 原生窗口行为验证通过，其余平台及全屏 / 多屏叠层验收待做。

## 仍需实现的原方案能力

- [ ] 跨平台媒体键、休眠抑制、睡眠恢复与多屏 DPI 验证。
- [ ] DLNA / 局域网互通、SMB / WebDAV。
- [ ] 自动更新、签名校验与回滚。
- [ ] Windows 正式签名、Apple Developer ID / 公证与安装后真机测试。
- [ ] 有画面的字幕 / 叠层 / 全屏验收、硬解回退与长时稳定性压测。

上一批增强功能 CI：[e6628b7 的六 RID 构建与打包均通过](https://github.com/pengpercy/VodBox/actions/runs/37227048670)。解析链、代理与浏览器改动需独立验证，详见 [播放解析](playback-resolution.md)。

解析链 / HLS / 浏览器阶段：[539b711 的六 RID CI](https://github.com/pengpercy/VodBox/actions/runs/37244505010)，六 RID 构建及六 RID 打包均通过。后续缓存 / 控制 / 主题改动不继承该轮验证结论。当前本地缓存 / 直播增强的测试继续增加，见 [桌面操作](desktop-controls.md)，编译零警告。

当前首版基线 CI：[7105ef7 的六 RID 验证](https://github.com/pengpercy/VodBox/actions/runs/37223401606)。本轮新增功能不继承这轮 CI 的结论，验证完成后另行记录。

MacCMS 适配依据公开接口字段独立实现，没有复制上游应用代码；接口参考：[官方 Provide.php](https://github.com/magicblack/maccms10/blob/master/application/api/controller/Provide.php)。不导入旧 TV 配置或 Java 插件。

`csp_*` 通常对应 Java Spider 入口。当前没有 JAR/JVM 加载器；已增加 `csp_Bili` 的 C# 公开投稿基础适配并做真实点播/弹幕验证，`csp_BiliGuard` 与其余 Spider 未移植。QuickJS/Python/Node 的 VodBox 接口不代表旧 drpy 可直接运行。两个源共 98 个站点条目、69 个精确 Java 入口，按频率推进，详见 [Spider 优先级](spider-migration-priority.md) 和统计快照。

缓存 / 直播 / 播放控制阶段：[ed2cc55 六 RID 构建与打包全部通过](https://github.com/pengpercy/VodBox/actions/runs/37245948796)，包含 27 项测试。本轮继续增加分页、筛选、可见海报加载及无反射视频句柄控件，37 项本地测试通过，包含真实 XAML 的无窗口预览与主题资源验证；这些测试不验证原生视频画面或 Mica。

备份阶段增加五项集成测试，当前共 42 项测试通过。验证超过 200 条历史完整导出、较新历史保留、已有收藏合并、配置 / 偏好恢复、错误版本及配置身份拒绝、取消数据库事务回滚。

备份导出 / 导入已加入实际 AOT 诊断，本地 macOS x64 执行通过；无反射视频宿主 / 分页阶段的六 RID 构建均通过，[流水线](https://github.com/pengpercy/VodBox/actions/runs/37249201548)打包结果另行记录。

备份阶段提交 edd68d0：[本轮 CI](https://github.com/pengpercy/VodBox/actions/runs/37250007256)六 RID 的 42 项测试、Native AOT、实际备份导入、LibVLC 解码及三个脚本协议全部通过。六个安装包任务未启动，GitHub 注释原文为“recent account payments have failed or your spending limit needs to be increased”；整轮因而标记失败，不能称安装包验证通过。按用户要求暂停逐批 CI，CI 改为仅 workflow_dispatch 手动触发，继续本地功能开发和验证，后续集中验证。

弹幕阶段：55 项本地测试通过，新增格式 / DTD / 解压限制 / 编码 / 取消 / 自动地址 / 稠密调度 / seek / 延迟 / 设计预览验证。macOS x64 的真实 LibVLC 视频输出、透明附属窗口、随视频移动、暂停和开关验证通过；这是 JIT 隔离测试驱动，不等同于六平台 AOT GUI 验收。CI 保持手动触发，本轮未运行。详见 [弹幕](danmaku.md)。

双内核阶段：根据用户要求保留 LibVLC 与 libmpv，核心选择策略已通过 10 项测试，完整本地测试共 65 项。libmpv C ABI、SafeHandle、事件轮询、OpenGL Render API 和 Avalonia 实验表面已完成，macOS x64 真实 Native AOT 控制与 GPU 渲染隔离测试通过。主窗口仍使用 LibVLC；实际路由/设置切换、失败回退、状态迁移、网络浏览/投屏和六 RID 双内核打包尚未完成。本轮未触发 CI。

窗口外观：主窗口启用 `ExtendClientAreaToDecorationsHint` 与 40 DIP 标题栏高度，使用 Avalonia 12 的 `WindowDecorationProperties.ElementRole=TitleBar` 标记拖动区域，并保留系统窗口按钮。透明标题栏与现有 Mica/Acrylic/Blur 回退共用背景。65 项本地测试与真实 XAML 设计预览通过；跨平台原生按钮/拖动行为仍待真机验收。

Spider 阶段：本地 78 项测试通过。真实 WBI 搜索/正常访客会话、分集 cid、每次刷新媒体地址、Cookie 不传 CDN、取消与四路并发已验证；HTTP 弹幕补齐 gzip/deflate（raw/zlib）/Brotli 解压及逐层大小限制。macOS x64 最新 Native AOT 发布零警告，实际热门/搜索各 20 项、1200 条弹幕、H.264 VideoToolbox 解码通过。后续先完善哔哩哔哩相关适配，再 AppGet、App99 候选家族及重复内容入口；双内核切换后置，本轮未触发 Actions。
