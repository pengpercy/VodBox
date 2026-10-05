# C# App99 Spider

显式注册 `app99` / `csp_App99`，新版配置必须指定 `protocol=bn-v2`。不加载 JAR，不运行参考 Python，不自动认为 `csp_App99Guard` 使用同一协议。示例 [app99.json](../examples/app99.json) 全部使用占位参数。

## 配置和协议

`entry` 保留服务器路径前缀，例如 `https://api.example.com/app/bn`。必填 `appkey`、`name`、`versionName`、`buildSignature`；可选 `apiVersion`、`userAgent`、`uuid`。默认每个 provider 生成随机 UUID，仅在内存中复用；配置指定 UUID 时须为 32/36 字符 GUID。不要把真实站点密钥提交进仓库。

匿名请求使用 AES-256-CBC：密钥是去掉连字符的 UUID 的 32 个 ASCII 字节，每次生成随机 16 字节 IV，IV 前置到密文后 Base64 编码，作为 application/json 的原始字符串正文。毫秒时间戳、随机 nonce、正文、空 token 与 appkey 参与 SHA-256 签名。JSON 请求由 System.Text.Json 源码生成，支持 Native AOT，不引入业务反射。

支持 `/app/systemInit`、`/vod/search`、`/vod/detail`；响应解密后处理 PKCS7 JSON 或 zlib JSON。只接受明确的 code=0；无效密文、填充、API 错误不会转成空列表成功。匿名公开内容已实测可读，当前没有账号登录、访客注册或获取用户 token 的调用。

分类、筛选、搜索分页、海报、多线路分集均接入应用的 IContentProvider。分类筛选支持 class/area/lang/year；分页最多 1000 页。初始化复用，详情最多缓存 128 项/10 分钟，每次播放重新读取详情以刷新地址。

已知直接线路返回 HTTP 媒体；解析线路只使用初始化中声明的 JSON 解析器，最多尝试三个并共享 20 秒期限。支持 root/data/result 中的 url/playUrl/play_url，不执行网页脚本。未识别线路只有明确媒体扩展名才接受。API 签名、appkey、UUID 不传给解析服务器和媒体 CDN；它们只得到 User-Agent。

传输最多四路，每个 API 请求包括排队在内 20 秒，播放解析整体 20 秒；加密与解压响应各限制 8 MiB，外部 JSON 1 MiB。不跟随重定向，不维护 Cookie 容器；取消会释放并发槽位。

公开协议核对参考：[固定提交的 App99 Python 文件](https://github.com/maoystv/6/blob/1c3b1dc7458b06d618938333d8a2f8e65c8d4493/PY/app99.py)。仅核对协议；源码实现独立编写。

## 本地验收

2026-10-05：8 项专项测试，完整 112 项回归通过。包括独立 OpenSSL 加密向量、原始/压缩响应、路径前缀和签名、分页/筛选/多线路、地址刷新、解析请求头隔离、四路并发/取消、重定向和解压上限。

宝盒双星：8 分类、21 列表、5 搜索结果、9 线路 / 414 分集；macOS x64 JIT 实际 LibVLC 解码 3.38 秒，187 视频块、470 音频块，VideoToolbox H.264。剧圈：18 分类、21 列表、2 搜索结果、7 线路，内容与地址解析通过，暂未作为实际解码通过记录；随后首条实际媒体返回 HTTP 403。

诊断命令：`VodBox --diagnostics --app99-smoke /absolute/private-config.json --native`。可用 `--app99-media` 指定视频编号、`--app99-line` 指定从 0 开始的线路，`--spider-category` 固定分类编号。真实配置和日志保留在本机临时目录，不随包发布。

以上验证不等于全部内容、Guard、桌面显示、所有平台或长时播放均已验收。账号、网页解析、特殊媒体请求头、插件专用弹幕仍需独立核对与实现。

严格 macOS x64 Native AOT 编译通过（编译/AOT 警告视为错误）。AOT 实际匿名分类、搜索、详情和地址解析通过；初次首条媒体 HTTP 403，固定视频 1026210 首线路 HTTP 404，因此不记录为 App99 AOT 实际解码通过。JIT 补测固定视频 1052399 首线路也返回 404。这些媒体失败不自动切换未声明协议、忽略 TLS 或伪造成功。

本机日志：`/private/tmp/vodbox-app99-all-tests.log`、`/private/tmp/vodbox-app99-real-jit.log`、`/private/tmp/vodbox-app99-juq-jit.log`、`/private/tmp/vodbox-app99-final-aot.log`、`/private/tmp/vodbox-app99-real-aot.log`、`/private/tmp/vodbox-app99-fixed-media-aot.log`。本轮没有触发 GitHub Actions。
