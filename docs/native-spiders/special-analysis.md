# 特殊源 Java 逻辑迁移审计

2026-10-18；证据为固定 payload 的 clean/src/sources Java，以及静态展开的字符串。没有执行原插件、Java Oracle、Python 插件，也没有执行 build/test/typecheck。此文不把源码已写等同于真实可播验收。

## Nmyswv

Java 全量方法和展开字符串显示：

- 首页/分类/搜索/详情全部是普通 HTTP + Jsoup。WebView 辅助类存在，但 `playerContent` 的 HLS 和 `aF` 分支不依赖它。
- 分类 `/index.php?m=vod-list-id-{tid}-pg-{pg}-order--by-{by}-class--year-{year}-letter--area-{area}-lang-.html`，列表 `ul.resize_list > li`；搜索 `/index.php?m=vod-search&wd=`，列表 `ul#data_list > li`，Java 没有搜索翻页参数。
- 详情 `div.numList` 的 `ul > li > a` 构成分集，Java 将每组反转；播放页从 `mac_url='…';` 中依次用 `$$$`、`#`、`$` 取指定线路和分集。页面路径中的 `src`、`num` 都是从 1 开始，必须按原数组索引而非展示反转后的序号。
- `aF` 编码：先取索引 0,2,…,66 共 34 个字符，再拼原字符串索引 67 开始的后缀；后缀替换 `oo00o`→`/`、`o000o`→`+`、`O0O0O`→`=`；Base64 UTF-8 解码。原 Java 不包含加密运算，也不需要 JS。
- `.m3u8` 原 Java 通过 `merge.DC.B` 注册 `do=m3u8` 代理；DC 后续还含广告段识别/过滤逻辑。VodBox 原生媒体代理已有 HLS 子清单、片段/密钥和 Range 支持，但本次未移植 DC 广告过滤算法。
- 非上述可识别媒体的分支返回 `parse=1` 原播放页，这确实需要浏览器嗅探，不能作为最终媒体地址返回。

新增 `NmyswvSource.cs`：分类/筛选/首页/搜索/详情、站内路径约束、播放页数据提取、aF 解码、分集归属验证、HTTP 时间和响应体上限、静态 Supports ext 门控；浏览器分支抛出明确 NotSupported。分集输出保持 JarPlugin `flag+U+0001+path` 身份，LineId 保持 flag，解析兼容早期裸 path。核对 `iK.N` 后确认原 Java `N("div.id")` 是字面属性读取而非 CSS selector；无该属性或不含 m3u8 时使用原始 numList 序号生成“线路N”。主 Agent 负责注册映射及离线/真实媒体验收。实现当前仅接受 HLS/MP4，不声称支持全部网页嗅探线路。

## AiDj

`AiDj.v3()` 实际生成 3433 行 Python，Java 继承 `merge.YU` 桥接 Python。解混淆阅读稿（含生成的脚本）保存在分析中间产物 `/private/tmp/vodbox-protocol-read/AiDj.py`（仅审阅用，不提交外部完整源码）。Java `playerContent` 另调用 `uA.dT(true)` 和 `sI.SN()` 环境判断，不是媒体算法本身。

真实闭环：官网 SSR / App API 取得 series/vid → `_video_model` 签名请求 → `_select_quality` → `_decrypt_spade_url`（如 URL 加密）→ `derive_content_key(spade_a)` → MP4 CENC 样本解密 → 可 seek 的本地 Range 串流。

没有发现必须账号密码的前置条件：脚本生成 18 位随机 device/install id，cookie/token 字段均可空。签名与媒体运算可移植 C#，当前工作量不能解释为不可替代 Python 运行时。

### 请求签名边界

- `_core_sixgod` (2796–2823)：统一小写请求头，生成 x-ladon / x-khronos / x-argus / x-gorgon / x-helios / x-medusa / x-ss-stub；签名同时绑定编码 query 和准确 UTF-8 请求体。x-ladon、x-argus 是时间戳大/小端 Base64，不应误当其他通用算法。
- `_get_params_encrypturl` (2614–2624)：设备配置覆盖已有参数，加入 ts、_rticket；保留字典插入顺序。`_url_encode` 用 urlencode，再将 +→%20、%2A→*。
- Gorgon (2625–2658)：MD5(query/body) 头四字节 + 固定值/时间戳 + RC4 变体（只覆盖 S[i]，没有标准 RC4 的交换），再半字节/位翻转。
- Helios (2659–2693)：随机数+8662 的 MD5 ASCII hex 衍生 64 位轮密钥，34 轮运算与 PKCS#7。需 unchecked ulong 和显式字节序。
- Medusa (2694–2795)：protobuf 环境采样、SM3、`hash_f13` 分支、自定义 AES_V3、key hash、xmxor、种子混合。不可用标准 AES 直接替代 AES_V3。
- SM3/rotate/key hash (1591–1676)；AES_V3 (1797–1998) 依赖 S-box 和自定义轮运算；hash_f13/branch_0/1/2 及支撑函数 (2186–2490) 依赖大常量表。
- `_BRANCH_ONE_B85` (765–1590) 是压缩常量，不是运行时脚本：构建迁移时静态 Base85 + LZMA 解压为二进制资源即可，应用无需依赖 Python/LZMA。需记录长度/哈希并读取显式 little-endian 值。
- protobuf 编码 (2491–2597)：自有 varint、sint、定长字段和 nested message，不能靠 JSON 序列化代替；浮点字段需保持 IEEE 位模式。

新增 `AiDjCrypto.cs` 移植 Gorgon、Helios、spade 内容密钥和 AES-CTR；`AiDjSigning.cs` 移植 SM3、KeyHash、自定义 AES_V3、HashF13 三分支；`AiDjSigning.Tables.cs` 保存静态提取并转 gzip 的分支表；`AiDjSigning.Medusa.cs` 移植 protobuf 采样环境、Medusa 封装与完整 `Sign` 入口。所有时间戳/随机量可通过 `Entropy` 明确注入，方便主 Agent 逐层固定向量回归。未执行构建或向量测试，尚未接任何生产源映射，也没有声称可播。

分支表静态解压结果为 81936 字节，SHA256 `239ddf955d14a008311b8c3adcf05713b858954068fff02ab19c2ba81e31d1d3`。应用只用 BCL GZipStream 解压，不需要 LZMA/Python。Branch2 保留原 Python `iv & 15 - 2` 的实际优先级语义 `iv & 13`。

### 媒体边界

- `_find_box`/`_parse_track`/`decrypt_mp4_cenc` (189–386)：moov/trak/stbl/stsz/stco/co64/stsc/saiz/saio，按 sample IV 运行 AES-CTR，改写 encv→avc1 / enca→mp4a、sinf→free。
- Range 路径 (388–750)：查头尾 moov，构建 sample 索引，按范围取完整加密样本后解密并切片；重写 moov 后保持偏移。只移植全文件版本会损失边下边播/seek。
- spade URL (3045–3085)：根据模型 seed 衍生 key/IV 的 AES-CBC + 去 padding；与 MP4 内容 AES-CTR 是不同步骤。
- 当前 VodBox `MediaProxy` 是 HTTP/HLS 透明代理，没有 CENC 解密和 moov/sample Range 重映射，因此不能直接把加密 URL 填给播放器当作完成。

剩余交付为：①主 Agent 对所有签名分支及完整 Medusa 固定向量验收；② CENC sample 索引/解密和 Range 本地服务；③请求模型/官网和 App 数据 provider。由主 Agent 组合验证后再注册 AiDj。

## SixV

完整 Java 是普通 HTML 磁力目录：ext 是站点首页，12 个固定分类，分类 `#post_container .post_hover`、详情 `#post_content table a[href^=magnet]`，搜索 POST `/e/search/11index.php`。

`playerContent` 只原样返回 magnet，实际下载和流式播放由宿主媒体后端承担。VodBox 现有 HTTP/HLS MediaProxy 不支持 BitTorrent metadata/DHT/peer 下载/文件选择/seek；返回 magnet 或伪造 HTTP 地址均不构成可播完成。主 Agent 正在评估独立下载串流后端；应在后端有明确生命周期与回归后连接 provider。
