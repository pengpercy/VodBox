# 播放解析与媒体代理

配置的 `resolvers` 声明解析器，内容源使用 `resolverId` 关联。打开地址栏也能选择解析器；`_direct` 为直接播放，`_browser` 为网页嗅探。配置快照包含解析器，历史保存原始地址及解析器标识，重新打开时再次解析。

```json
{
  "schemaVersion": 1,
  "id": "my-library",
  "sources": [],
  "liveSources": [],
  "resolvers": [
    {
      "id": "parser",
      "name": "我的 JSON 解析器",
      "kind": "json",
      "entry": "https://your-parser.example/api?url={url}",
      "urlPath": "data.url",
      "headersPath": "data.headers",
      "timeoutSeconds": 20
    }
  ]
}
```

`entry` 的 `{url}` 替换为完整输入地址的 URL 编码值；无占位符时追加在 entry 末尾，适合 `?url=` 接口。URL 字段使用点路径，支持数组索引，例如 `data.0.url`。媒体请求头来自 `headersPath`，解析器自身的 `headers` 仅用于访问解析 API。相对媒体地址以解析响应地址为基准。`nextResolverId` 可串联，运行时拒绝循环和超过六级的链；每一级独立超时，JSON 响应最多 8 MiB。

任意非 User-Agent / Referer 请求头会触发回环媒体代理。它支持直接流、GET / HEAD、Range / If-Range、206 Content-Range、HLS 子清单、片段、AES 密钥与 URI 属性。仅监听 127.0.0.1，随机会话路由，最多三会话、16 并发连接，单清单最多 8 MiB。跳转改写为代理路由；Cookie、Authorization 以及其他自定义头只传到原媒体地址的同源资源。跨域资源如另需认证，应由解析器提供适合该媒体主机的入口。DASH 模板代理尚未实现，代理收到 DASH 返回 501；无特殊请求头的 DASH 仍由 LibVLC 直接播放。

网页嗅探使用安装在系统中的 Chrome / Edge / Chromium，无浏览器二进制打包。可设置 `VODBOX_BROWSER` 或解析器的 `browserExecutable` 为可执行文件绝对路径。每次创建临时 profile、随机回环调试端口，通过 CDP 捕获成功的 m3u8 / mpd / mp4 / webm / mp3 请求。默认无界面，可设置 `visibleBrowser: true`；默认调用页面 video/audio 的 play。处理结束或取消时终止本次启动的进程树并清理临时 profile。协议事件最大 4 MiB，缓冲最多 1024 个请求头条目。

当前浏览器限制：不连接既有浏览器会话；入口仅支持 User-Agent / Referer；不能处理登录、验证码、DRM、需要定制点击的页面以及独立调试目标中的跨域 iframe。媒体候选选择尚未提供多候选 UI。捕获后浏览器关闭，依赖浏览器持续运行的媒体需进一步实现。

验证：18 项自动测试通过，新增 JSON 编码/链循环、HLS 密钥与 Range 回环 HTTP、SQLite v1→v2 保留历史测试。macOS 安装版 Chrome 的临时 profile / CDP 受控网页捕获成功；这不代表任意网站均可解析。

协议依据：[Chrome DevTools Network](https://chromedevtools.github.io/devtools-protocol/tot/Network/)、[Chrome Headless](https://developer.chrome.com/docs/automation-and-testing/headless)。协议消息使用 Utf8JsonWriter / JsonDocument，业务无运行时反射。
