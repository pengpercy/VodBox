# C# 音频专辑 Spider

新版配置使用 `provider=audio-site`、`protocol=audio-zblog-v1`，入口为 `https://www.xsmp3.com` 或 `https://www.psmp3.com`。也注册 `csp_XBPQ` 别名，但必须显式声明同一协议；不读取旧 XBPQ 规则，不代表通用 XBPQ 引擎兼容。可直接加载 [audio-sites.json](../examples/audio-sites.json)。

支持相声六个分类、评书七个分类，分类分页、专辑海报/标题/简介、分集顺序、直接音频和每次播放刷新专辑中的地址。分集使用稳定的零基索引；音频请求携带 User-Agent 与所属专辑 Referer。现有播放协调器可记录历史、收藏、恢复位置和连续选集。

HTML 抽取使用源码生成的正则，静态 APlayer 数组由受限 C# 读取器解析。只接受对象的字符串字段，不执行 JS；拒绝函数表达式、重复字段、无效或带凭据媒体地址。解析字符串使用源码生成 JSON，支持转义、Unicode 和字符串内的引号、逗号。音频不要求 VideoTrack/DecodedVideo，实际诊断单独验证音频解码计数。

页面请求只发往原 HTTPS 站点，不跟随重定向、不维护 Cookie；HTTP/HTTPS 媒体可按页面声明跨 CDN 播放。最多四路请求、排队与传输共 20 秒，HTML 限制 2 MiB；专辑缓存 128 项/10 分钟，播放时刷新。最多 1000 页、1000 项页内专辑、10000 个分集；页面结构缺失不会冒充空结果。

公开搜索当前 POST 跳往 search.php，随后 301 回首页，因此 SearchAsync 明确抛出不支持。搜索、其他页面协议、特殊账户或通用配置规则仍待实现。

## 本地验收（2026-10-05）

11 项专项测试，完整 123 项回归通过，编译警告视为错误。覆盖两个站点协议、分页、标题元数据/H1 差异、实体/转义、缓存与刷新、执行表达式拒绝、路径/源验证、HTML 上限、四路并发与取消、重定向及搜索不支持。

相声：郭德纲分类 12 专辑，固定专辑 `gdg-yq/gdg-yq-1.html` 50 集；JIT LibVLC 解码到 3.03 秒，317 音频块。评书：单田芳分类及专辑 `stf-styy/styy-1.html` 50 集；JIT 解码到 3.21 秒，332 音频块。两个站点的小范围媒体读取均 HTTP 200 / audio/mpeg。此记录不代表全部内容或各平台桌面音画均已验收。

诊断：`VodBox --diagnostics --audio-site-smoke /absolute/audio-config.json --native`；用 `--spider-category` 选择分类、`--spider-media` 选择固定专辑路径。相声和评书分类编号不同，双源诊断不同时传入仅一站存在的分类编号。

日志保存在本机 `/private/tmp/vodbox-audio-site-final-tests.log`、`/private/tmp/vodbox-audio-xsmp3-jit.log`、`/private/tmp/vodbox-audio-psmp3-final-jit.log`，本轮没有触发 Actions。

最终 macOS x64 全裁剪 Native AOT 发布零警告。评书 AOT 实际分类、专辑、50 分集与 LibVLC 音频解码通过：3.21 秒 / 332 音频块。相声 AOT 内容与解析通过，媒体 TLS 握手超时，不能记录为 AOT 解码通过；JIT 已实际通过。日志 `/private/tmp/vodbox-audio-site-final-aot.log`、`/private/tmp/vodbox-audio-psmp3-aot.log`、`/private/tmp/vodbox-audio-xsmp3-aot.log`。
