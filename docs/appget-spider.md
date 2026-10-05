# C# AppGet V119 Spider

`AppGetProvider` 通过 `ProviderFactory` 显式注册 `appget` / `csp_AppGet`，使用原生 .NET 异步 HTTP、JSON DOM 和 AES API，不加载 JAR、不运行 Python、没有反射注册。参考公开协议约定独立实现；协议线索固定在 [AppGet.py 的提交版本](https://github.com/Hululu007/drpy-node/blob/295f2b7047e14122d542a7736cb931e81abcf85c/spider/py/AppGet.py)。

## 配置与范围

示例 [appget.json](../examples/appget.json) 的域名和 key 都是占位值，必须填写站点参数。不会导入旧 ext 字符串。

- `entry`：服务根 URL；发现文件地址需要同时配置 `discovery=true`。
- `protocol`：目前必须显式为 `v119`；不推断 V120/V122，不自动试遍接口版本。
- `key`：16 个 UTF-8 字节，用于该协议的 AES-128-CBC/PKCS7，IV 按协议使用同一密钥。
- `apiPath`：默认 `/api.php/getappapi`；请求在其后添加 `.index/{action}`。
- `version`：默认 `210`，是请求头中的客户端版本码，和 `protocol` 的接口版本不是同一个字段。
- `userAgent`：默认 `okhttp/3.14.9`；`deviceId` 可选，默认实例内随机标识。

入口发现只发送 User-Agent，不携带签名、设备标识或密钥；最多读取 512 字节，返回值必须是 HTTP/HTTPS 根地址。默认传输禁用重定向和 Cookie 容器。

已实现 `initV119` 分类/推荐、`typeFilterVodList` 分类分页及 area/year/lang/class/sort 筛选、`searchList` 搜索分页、`vodDetail` 多线路/分集、直接媒体和 `vodParse` 解析。未提供总页数时以空页结束分页；最多 1000 页。不会把首页 HTTP 200、空加密数组、验证码/登录响应当作可用内容。

详情最多缓存 128 项/十分钟；每次打开分集重新读取详情，刷新可能过期的媒体 URL 和解析令牌，解析结果不缓存。只有媒体 User-Agent 传入播放请求，API 签名和设备标识不会传往 CDN。直接媒体识别基于 URI 路径扩展名；不把任意网页地址当作视频。外部 JSON/网页解析器、图片伪装媒体、player headers 字典、账号登录与验证码流程尚未接入。

最多四路 HTTP；每个请求 20 秒，包含排队、发现、响应读取；整个播放解析另设 20 秒期限。加密 HTTP 响应最多 8 MiB，分类最多 256、推荐缓存最多 1000、线路最多 64、详情总分集最多 10000。请求取消时释放并发槽位。

## 本地检查

使用本机私人配置，包含真实站点参数的文件不要提交：

```sh
VodBox --diagnostics --appget-smoke /absolute/path/appget-local.json
VodBox --diagnostics --appget-smoke /absolute/path/appget-local.json --appget-line 1 --native
```

`--appget-line` 从 0 开始，默认 0。内容读取、播放解析、原生解码分别输出结果；一阶段成功不代表下一阶段成功。Native 解码必须有视频轨、已解码视频块和至少三秒位置。该网络检查是手动诊断，不自动触发 CI。

新增 8 项 fixture 测试覆盖独立 OpenSSL 密文向量、时间戳签名、UTF-8 表单、加密响应、首页复用、分页、多线路、新鲜解析、发现入口隔离、重定向、无效响应、四路取消与不支持的配置。真实站点与 AOT 结果见 [播放测试](playback-testing.md)。
