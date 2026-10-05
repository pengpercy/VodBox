# Spider 移植优先级

2026-10-05 读取 [饭太硬](http://www.饭太硬.net/tv) 与 [宝盒](https://宝盒接口.top) 配置，未下载或执行 JAR。完整逐站点快照见 [spider-inventory.json](spider-inventory.json)，含条目序号、名称、入口和 JAR 声明指纹。

## 计数口径

- 饭太硬：48 个站点条目，45 个 Java Spider 条目、3 个 JavaScript 条目。
- 宝盒：50 个站点条目，49 个 Java Spider 条目、1 个 JavaScript 条目。
- 合计 98 个条目，其中 Java Spider 94 个，按精确 `api` 名去重为 69 个 Java 入口。
- 四个 JavaScript 条目使用 `drpy2.min.js`；按引擎文件名归类，不代表各 URL 版本相同。
- 重复站点 key 不去重；这是配置使用次数，不是独立站点数、用户使用率或可播放比例。
- 两个源没有完全同名的 Java 入口；`Guard` 后缀不自动合并，相似名称只是候选复用家族。
- JAR 变体只按配置声明的位置和哈希区分，未核实二进制内容，同名入口不保证行为一致。

## 按频率移植

1. **哔哩哔哩相关适配**：宝盒 `csp_Bili` 9 次，饭太硬 `csp_BiliGuard` 7 次，候选家族共 16 个条目，占总数 16.3%。先完善公开投稿基础，再核对片单、分类 JSON、Guard 参数和不同 JAR 行为；不把这 16 个条目全部算成已覆盖。
2. **`csp_AppGet`**：宝盒 4 次、两个声明的 JAR 变体。核对入口发现、接口版本、签名/加解密、分页、详情和播放解析，按版本建立 fixture。
3. **App99 候选家族**：宝盒 `csp_App99` 2 次、饭太硬 `csp_App99Guard` 1 次。先验证是否能共用协议，再实现站点参数；未证实 Guard 等价。
4. **其他双次内容入口**：`csp_AppDrama`、`csp_AppSxGuard`、`csp_AppYd`、`csp_S_zpsGuard`、`csp_T4Guard`、`csp_XBPQ` 各 2 次。同频时先做有接口约定、稳定公开访问的站点，再处理账号或云盘会话。
5. **辅助及低频入口**：`csp_XPath` 的两个宝盒条目是导航和提醒，不先于实际点播站点。推送、网盘管理和其余单次入口分别排期；完整清单见快照。

`drpy2.min.js` 的 4 次使用进入 QuickJS 兼容任务，不计作 Java Spider 移植。当前 QuickJS 宿主实现的是 VodBox 协议，旧 drpy 和规则文件尚不能直接运行。

## 当前落地与限制

`BilibiliProvider` 已接入 `ProviderFactory`，显式注册 `bilibili` / `csp_Bili`。示例 [bilibili.json](../examples/bilibili.json) 使用新版配置，不自动导入两个旧配置的完整站点定义，不加载 JAR。

基础适配包含热门、关键词分类、搜索分页、详情、分集、实时播放地址和 cid 对应的 XML 弹幕。请求全程异步、最多四路，支持取消和超时。WBI 参数和正常访客会话只在内存缓存六小时；详情最多缓存 128 项/十分钟，播放地址每次刷新。可选 Cookie 仅发往固定官方 API，不传给媒体 CDN；默认传输不跟随重定向。

首版使用 `qn=16` 的单段音画合一媒体，不误把多段第一段当完整影片。DASH/多段拼接、番剧、账号登录、原有 Bili 片单 JSON/筛选参数和 `csp_BiliGuard` 行为仍需实现。不能声称宝盒九项或饭太硬七项已经全部可用。

本地 78 项测试通过；macOS x64 JIT 和 Native AOT 均实际取得热门 20 项、搜索 20 项、一集视频及 1200 条弹幕，以 VideoToolbox 解码 H.264：位置 3.40 秒、视频 217 个块、音频 421 个块。测试视频 `BV1WSHL66EdZ`，仅代表这条链路，不代表全部内容、GUI 或全部平台。日志 `/private/tmp/vodbox-bilibili-native-aot-network.log`，更多记录见 [播放测试](playback-testing.md)。

后续按此频率继续源适配，双内核切换排在这批 Spider 之后。保留 libmpv 底层工作，不增加 Java 兼容层，不触发 GitHub Actions。

## 复现统计

```sh
python3 build/analyze-spiders.py \
  --source '饭太硬=/path/to/fantai-response.bin' \
  --source '宝盒=/path/to/baohe-response.json' \
  --output docs/spider-inventory.json
```

脚本仅解析本地数据，支持 JSON、注释/尾逗号、gzip、JPEG 尾部 Base64。原始响应不入库；统计保留响应 SHA-256，不保存 Cookie、密钥或 ext 参数。
