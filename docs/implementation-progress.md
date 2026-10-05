# 实现进度与继续工作清单

本清单以原重写方案为目标。CI 通过不等于功能完成；一个功能写入代码、单元测试通过、跨平台编译通过、桌面交互验收通过是不同层次。旧配置与 Java/JVM/Dex 兼容层按用户决定不实施。

本清单包含 41 项能力：17 项有基础验证，16 项已有实现但仍需完整验收，8 项仍需实现。按能力项计数，不代表工时比例或完整移植百分比。

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

## 仍需实现的原方案能力

- [ ] 分类筛选与搜索分页。
- [ ] 弹幕下载、时间轴、渲染与开关。
- [ ] 数据备份 / 导入；单条 / 全部历史删除及 v1→v2 数据库迁移已实现并测试。
- [ ] 跨平台媒体键、休眠抑制、睡眠恢复与多屏 DPI 验证。
- [ ] DLNA / 局域网互通、SMB / WebDAV。
- [ ] 自动更新、签名校验与回滚。
- [ ] Windows 正式签名、Apple Developer ID / 公证与安装后真机测试。
- [ ] 有画面的字幕 / 叠层 / 全屏验收、硬解回退与长时稳定性压测。

上一批增强功能 CI：[e6628b7 的六 RID 构建与打包均通过](https://github.com/pengpercy/VodBox/actions/runs/37227048670)。解析链、代理与浏览器改动需独立验证，详见 [播放解析](playback-resolution.md)。

解析链 / HLS / 浏览器阶段：[539b711 的六 RID CI](https://github.com/pengpercy/VodBox/actions/runs/37244505010)，六 RID 构建及六 RID 打包均通过。后续缓存 / 控制 / 主题改动不继承该轮验证结论。当前本地缓存 / 直播增强的测试继续增加，见 [桌面操作](desktop-controls.md)，编译零警告。

当前首版基线 CI：[7105ef7 的六 RID 验证](https://github.com/pengpercy/VodBox/actions/runs/37223401606)。本轮新增功能不继承这轮 CI 的结论，验证完成后另行记录。

MacCMS 适配依据公开接口字段独立实现，没有复制上游应用代码；接口参考：[官方 Provide.php](https://github.com/magicblack/maccms10/blob/master/application/api/controller/Provide.php)。不导入旧 TV 配置或 Java 插件。
