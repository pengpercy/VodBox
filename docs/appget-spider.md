# C# AppGet Spider

`AppGetProvider` 通过 `ProviderFactory` 显式注册 `appget` / `csp_AppGet`，使用原生 .NET 异步 HTTP、JSON DOM 和 AES API，不加载 JAR、不运行 Python、没有反射注册。参考公开协议约定独立实现；协议线索固定在 [AppGet.py 的提交版本](https://github.com/Hululu007/drpy-node/blob/295f2b7047e14122d542a7736cb931e81abcf85c/spider/py/AppGet.py)。

## 配置与范围

示例 [appget.json](../examples/appget.json) 的域名和 key 都是占位值，必须填写站点参数。不会导入旧 ext 字符串。

- `entry`：服务根 URL；发现文件地址需要同时配置 `discovery=true`。
- `protocol`：显式 `v119`（签名表单）或 `qiji-v122`（JSON）；不自动试遍接口版本。Qiji 分支不是所有标有 V122 的站点的通用保证。
- `key`：16 个 UTF-8 字节，用于 AES-128-CBC/PKCS7；可显式配置 16 字节 `iv`，缺省使用 key。
- `apiPath`：V119 默认 `/api.php/getappapi`，Qiji V122 默认 `/api.php/qijiappapi`；请求在其后添加 `.index/{action}`。
- `version`：V119 默认 `210`，发送在签名请求头；Qiji V122 默认 `305`，使用源码生成 JSON 的 version 字段，和接口版本不是同一个字段。
- `userAgent`：默认 `okhttp/3.14.9`；`deviceId` 可选，默认实例内随机标识。

入口发现只发送 User-Agent，不携带签名、设备标识或密钥；最多读取 512 字节，返回值必须是 HTTP/HTTPS 根地址。默认传输禁用重定向和 Cookie 容器。

已实现 `initV119` 分类/推荐、`typeFilterVodList` 分类分页及 area/year/lang/class/sort 筛选、`searchList` 搜索分页、`vodDetail` 多线路/分集、直接媒体和 `vodParse` 解析。未提供总页数时以空页结束分页；最多 1000 页。不会把首页 HTTP 200、空加密数组、验证码/登录响应当作可用内容。

详情最多缓存 128 项/十分钟；每次打开分集重新读取详情，刷新可能过期的媒体 URL 和解析令牌，解析结果不缓存。只有媒体 User-Agent 传入播放请求，API 签名和设备标识不会传往 CDN。直接媒体根据 URI 路径扩展名或显式 parse_type=0 识别；不把网页解析页面当作视频。player_parse_type=2 的声明式外部 JSON 解析已接入，支持顶层 url/data.url、准备好的 parse_api_url、1 MiB 上限和共享四路并发，仅发送 User-Agent。网页解析器、图片伪装媒体、player headers 字典、账号登录与验证码交互尚未接入。

最多四路 HTTP；每个请求 20 秒，包含排队、发现、响应读取；整个播放解析另设 20 秒期限。加密 HTTP 响应最多 8 MiB，分类最多 256、推荐缓存最多 1000、线路最多 64、详情总分集最多 10000。请求取消时释放并发槽位。

## 本地检查

使用本机私人配置，包含真实站点参数的文件不要提交：

```sh
VodBox --diagnostics --appget-smoke /absolute/path/appget-local.json
VodBox --diagnostics --appget-smoke /absolute/path/appget-local.json --appget-line 1 --native
```

`--appget-line` 从 0 开始，默认 0；可传 `--appget-media 5092` 固定视频编号，避免首页内容变动导致测试目标漂移。内容读取、播放解析、原生解码分别输出结果；一阶段成功不代表下一阶段成功。Native 解码必须有视频轨、已解码视频块和至少三秒位置。该网络检查是手动诊断，不自动触发 CI。

当前共 17 项 AppGet fixture 测试覆盖独立 OpenSSL 密文向量、时间戳签名、UTF-8 表单、加密响应、首页复用、分页、多线路、新鲜解析、发现入口隔离、重定向、无效响应、四路取消与不支持的配置。真实站点与 AOT 结果见 [播放测试](playback-testing.md)。


## 解析协议修正与 Qiji V122

较新的 [公开协议参考](https://github.com/wliqi495-create/jaychouqq/blob/bb36475f2457182a7988386b244593e1f3fcd5f5/yingshi/js/%E3%80%90APP%E3%80%91%E9%87%91%E7%89%8CAPP.js) 显式传递 player_parse_type，直接提交 AES Base64，并按播放器声明选择外部 JSON。旧 Python 参考对密文预先 URL 编码且缺少该字段，不能用于所有当前站点。实现已改为只进行一次表单编码，按需解码不透明输入，再加密；不改写已经直接可播放的签名 URL。修正后咕咕固定视频 5092 实际 JIT 和 Native AOT 解码通过。

Qiji V122 配置见 [appget-qiji-v122.json](../examples/appget-qiji-v122.json)，参数均为占位值。该分支以 initV122、searchList4、vodDetail2 和 JSON version 字段工作，支持独立 IV；若初始化 config.system_search_verify_status 启用，搜索明确返回尚不支持交互验证码。未知验证状态格式也明确报错。JSON 请求使用 VodBoxJson 源码生成器。默认传输、响应上限、取消与媒体头隔离规则保持一致。

Qiji 协议目前仅通过 fixture；参考站点发现文件 TLS 错误，未取得可验证的真实初始化数据。宝盒首发的入口失败也不能用 Qiji fixture 替代。不会把任意 csp_AppGet/V122/ext 自动映射到此分支。
