# Spider 移植优先级

2026-10-07：承接饭太硬导入时未覆盖的 42 项，新增兔小贝、虎牙、斗鱼、急救教学、荐影预告片五个原生 Provider，并修复明星 MV 129 项分类导入。余下 36 项仍待适配，具体范围、验收和限制见 [本批记录](public-site-spiders.md)。下文早期“Guard 未接入”记录不替代该批次的限定入口映射，也不意味着通用 Guard 已兼容。

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

基础适配包含热门、关键词分类、原生固定 BV/av 片单分类、搜索分页、详情、分集、实时播放地址和 cid 对应的 XML 弹幕。请求全程异步、最多四路，支持取消和超时。WBI 参数和正常访客会话只在内存缓存六小时；详情最多缓存 128 项/十分钟，播放地址每次刷新。可选 Cookie 仅发往固定官方 API，不传给媒体 CDN；默认传输不跟随重定向。

首版使用 `qn=16` 的单段音画合一媒体，不误把多段第一段当完整影片。DASH/多段拼接、番剧、账号登录、原有 Bili 片单 JSON/筛选参数和 `csp_BiliGuard` 行为仍需实现。不能声称宝盒九项或饭太硬七项已经全部可用。

本地 87 项测试通过；macOS x64 JIT 和 Native AOT 均实际取得热门 20 项、搜索 20 项、一集视频及 1200 条弹幕，以 VideoToolbox 解码 H.264：位置 3.40 秒、视频 217 个块、音频 421 个块。测试视频 `BV1WSHL66EdZ`，仅代表这条链路，不代表全部内容、GUI 或全部平台。日志 `/private/tmp/vodbox-bilibili-native-aot-network.log`，更多记录见 [播放测试](playback-testing.md)。

后续按此频率继续源适配，双内核切换排在这批 Spider 之后。保留 libmpv 底层工作，不增加 Java 兼容层，不触发 GitHub Actions。

## 原生片单分类

新版配置的 `options.categories` 每项只能包含 `query` 或 `videos` 之一。`videos` 为 1–2000 个不重复的 BV/av 号字符串，例如：

```json
{ "id": "lessons", "name": "课程片单", "videos": ["BV1WSHL66EdZ", "av170001"] }
```

片单按配置顺序展示，每页 20 项，通过详情 API 补齐标题、封面和分集。最多四个 worker，整页 20 秒期限；缓存复用现有十分钟详情缓存。不可访问的视频令该页失败，不静默跳过或改变页内顺序；调用方取消会终止并等待所有 worker 退出。片单不是平台收藏夹/合集 API，也不读取旧插件 ext JSON；固定片单的搜索仍使用站点全局公开投稿搜索。

本地 fixture 验证 25 项跨页顺序、尾页、详情复用、四路上限、取消后槽位回收、失效条目和配置拒绝。`examples/bilibili.json` 含单视频片单，网络诊断会同时读取并校验身份。

## AppGet 入口核查

2026-10-05 只读核查宝盒四个配置入口。两个 `.txt` 发现地址返回 HTTP 200 和短文本 HTTP 服务入口；另外两项分别返回咕咕番 HTML 与苹果 CMS 介绍页。首页可访问不能证明 AppGet API 可用。配置中已声明 V119、V122 以及未声明版本的变体，后续需要核对真实接口路径、签名和响应加解密，不能用一个猜测协议注册所有 `csp_AppGet`。

入口核查阶段未下载/执行 JAR，未发送签名，也未新增 AppGet 支持标记。随后协议核对仅将配置密钥用于本机生成请求签名，密钥明文不作为请求字段；原始响应与真实测试配置仅保留在本机临时目录。

找到可核对的公开 [AppGet Python 协议参考](https://github.com/Hululu007/drpy-node/blob/295f2b7047e14122d542a7736cb931e81abcf85c/spider/py/AppGet.py)，固定提交 `295f2b7047e14122d542a7736cb931e81abcf85c`。其中采用表单 POST、时间戳 AES-CBC/PKCS7 签名、Base64 加密 data 响应，以及 `getappapi.index/initV119` 和 `qijiappapi.index/initV120` 两组路径。仅作为协议核对线索，未运行或复制该脚本，不视为已证实用户源中 V122 或另一个 JAR 变体的协议。

已独立实现并显式注册 `appget` / `csp_AppGet` 的 V119 分支；配置明确指定协议、路径、key 和版本码。支持受限异步传输、加解密、分类/推荐、分页筛选、搜索、详情多线路、直接媒体/站内解析，以及每次播放刷新详情/令牌。新增八项协议 fixture，完整本地 95 项回归通过；严格 macOS Native AOT 发布及一碗内容/地址解析通过。详见 [AppGet Spider](appget-spider.md)。

真实验收：咕咕（分类 3）、一碗（分类 5）、蔬菜（分类 7）均通过分类、列表、搜索、详情；咕咕和蔬菜 `vodParse` 返回 code=0 / 空 data，明确失败。一碗返回直接媒体，但首条 HLS 连接失败，第二条只读检查 TLS 握手超时；首发初始化 HTTP 503。上述为首版验证记录，未将四项算作完整可播放。随后补齐 player_parse_type 并取消密文预先 URL 编码，咕咕固定视频 5092 的 JIT 与 Native AOT 实际解码均通过。蔬菜同样修正后仍返回 code=0；一碗和首发的问题继续保留。

## 复现统计

```sh
python3 build/analyze-spiders.py \
  --source '饭太硬=/path/to/fantai-response.bin' \
  --source '宝盒=/path/to/baohe-response.json' \
  --output docs/spider-inventory.json
```

脚本仅解析本地数据，支持 JSON、注释/尾逗号、gzip、JPEG 尾部 Base64。原始响应不入库；统计保留响应 SHA-256，不保存 Cookie、密钥或 ext 参数。


## 最新解析进展

完整本地回归更新为 104 项，包含 17 项 AppGet 测试。咕咕在 macOS JIT 解码至 3.40 秒（216/387 视频/音频块），最终严格 Native AOT 产物解码至 3.16 秒（200/367 块）。声明式外部 JSON 解析已接入；显式 qiji-v122 分支以源码生成 JSON 请求、独立 IV、对应端点与验证码状态检查实现，仅 fixture 验证，参考发现入口 TLS 错误。它不能代替首发 V122 的真实验收。App99 BN v2 已随后接入，详见下节；AppGet 未通过项继续保留。


## App99 BN v2 落地

显式注册 `app99` / `csp_App99`，保留入口 `/app/bn` 前缀，以原生 C# 实现 AES-256-CBC、匿名签名、zlib 加密响应、分类/筛选/分页/搜索、多线路和外部 JSON 解析。公开参考只用于核对协议，未运行或复制 Python 文件；不依赖 Java 或 Python 来实现本适配。8 项专项测试、112 项完整回归通过。

宝盒双星实际返回 8 类、21 项列表、5 项搜索、9 条线路 / 414 集，LibVLC JIT 解码至 3.38 秒（187 视频 / 470 音频块）。剧圈返回 18 类、21 项列表、2 项搜索、7 条线路，内容及地址解析通过。`csp_App99Guard` 尚未核实，不能把三个候选条目全计为已覆盖。详见 [协议和验收范围](app99-spider.md)。


## AppDrama / AppYd 入口核查

2026-10-05：宝盒 AppDrama 天堂、橘汁均未直接填写 host，而由 site JSON 的 domain 发现地址。两个发现入口 HTTP 200，按声明 commonKey 生成匿名 publicParams 后，真实分类接口 `/api/v3/drama/getCategory?orderBy=type_id` 分别返回 code=200、9 类 / 7 类。尚未验证分页、详情或播放。

[固定提交的 AppDrama 参考](https://github.com/5q68fs6b86-netizen/CatVodSpider-maintainable/blob/3ac7e6ea46339e6cfdf94dec4ff8e2e573638802/app/src/main/java/com/github/catvod/spider/AppDrama.java) 表明内容阶段还需 RSA 公钥协商、AES 和 protobuf。但是同提交 ApiResult/Drama/SecureRequest 等 protobuf 类均为返回空值的占位实现，没有 wire 字段定义，不能作为真实协议实现或验收依据。精确 SecureRequestProto 搜索未找到完整定义；唯一相关 Python 搜索结果只是配置文本。当前不注册 `csp_AppDrama`，也不增加功能完成项。真实发现响应、匿名分类与参考仅保留本机临时目录。

AppYd 按名称搜索得到无关结果，精确 AppYd.java 文件搜索为空，尚无充分接口依据。继续核查同频 XBPQ 的两个音频内容站点；它们已有公开 HTML 规则，先验证可达性，不能将整个 XBPQ 规则引擎算作已经兼容。


## 当前批次收尾与播放内核优先级

宝盒两个 XBPQ 音频条目对应 xsmp3/psmp3 的公开页面。独立 C# `audio-site` 显式实现 `audio-zblog-v1` 分类分页、专辑分集和直接音频，不读取旧规则，不代表整个 XBPQ 引擎兼容。两个站点的分类与专辑均 HTTP 200，媒体 audio/mpeg；相声 JIT 解码 3.03 秒 / 317 音频块，评书 JIT 解码 3.21 秒 / 332 音频块。公开搜索 POST 跳至 search.php 后 301 回首页，明确不支持搜索，不把首页内容当搜索结果。详见 [音频 Spider](audio-site-spider.md)。

用户最新调整：当前高频批次验证收尾后先继续双播放内核，随后再扩大 Spider 适配。不能据此宣称两个配置大多数条目已经覆盖；AppDrama 字段定义、AppYd、Guard 和其余入口仍留在待核查清单。
