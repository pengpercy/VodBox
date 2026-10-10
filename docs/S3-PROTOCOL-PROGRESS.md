# S3 协议扩展进度（2026-10-10）

## 用户要求
- 高频点播：AppDrama、AppSxGuard、AppYd、S_zpsGuard、T4Guard、XBPQ。
- 非点播：XPath（真实配置也出现 XPathGuard，不能视为同一实现）。
- 另外三个独立 JAR 协议用 C# 重写；不计重复别名，不执行未知 JAR。

## 当前已落地
- XBPQ：只覆盖宝盒 xsmp3/psmp3 的 Z-Blog/APlayer 音频配置；不是通用规则引擎。
- 原站抓取、播放时刷新地址、解析限制、4并发、有限缓存、取消和受限导入门控。
- 不支持的 XBPQ 定义继续明确拒绝；公开搜索未实现。

## 事实与阻塞
- 宝盒公开配置 XPath 两项 ext 为 null，内容是导航/广告提醒；通用 XPath HTML 规则不能冒充这些项。
- 公开 AppDrama 实现包含加密 protobuf，依赖消息 schema/签名，不能用 AppGet 端点代替。
- 查到的公开 XBPQ.java 是空类；旧 AudioSiteProvider 明确不是通用 XBPQ。
- AppSxGuard、AppYd、S_zpsGuard、T4Guard 尚未取得足够可验证的协议实现；继续静态核验配置指向的插件。
- 额外三项 C# 适配已接入：
  - csp_Push：HTTP(S) 单地址/标题地址列表、直连与 JSON 解析；不支持网页嗅探、文件、迅雷或 YouTube 提取。
  - csp_AppRJ：v3 timestamp/MD5 签名 multipart、分类/筛选/分页搜索/详情、播放时刷新地址、最多3个解析器顺序尝试及 UA；未移植 Android 弹幕代理。
  - csp_AppYs：api.php/app 和 xgapp JSON 分支，分类/首页/列表/搜索/详情、直连与 JSON 解析；.vod、特殊站点和网页嗅探分支明确不支持。
- XPath：已接入无 ext 规则的非点播导航/提醒类型，并在首页/分类显示说明；通用抓取规则及 XPathGuard 未支持。
- 三个额外入口的“代码及离线协议适配”完成，不代表所有 JAR 版本完整兼容或真实网络验收完成。

## 源码依据
- 公开参考 https://github.com/5q68fs6b86-netizen/CatVodSpider-maintainable 中 Push.java、AppRJ.java、AppYs.java；仅按接口行为独立实现，不复制 Android 运行时依赖。
- AppRJ 验证 multipart 字段、签名、筛选、分页、动态详情及解析器失败转备用；AppYs 验证两种 JSON 分支与拒绝未支持变体。

## 最新本地验证
- 主测试 376/376、预览 159/159，TreatWarningsAsErrors=true，通过；git diff --check 通过。
- 额外适配专用测试 10/10；Push 原有测试另计。
- 真实在线站点实播、全部插件版本兼容未验收；六平台 CI 待本次提交运行结果。

## 追加五个独立 C# 协议（2026-10-10）

- `csp_QiutongTY`：球通体育 room/page 与 room/info，分类、分页列表、详情、FLV/HLS 两线路与直播请求。
- `csp_QingtingFM`：蜻蜓 FM GraphQL 分类/搜索及 radio 详情、直播音频地址，GraphQL 字符串转义。
- `csp_KafeiTY`：咖啡体育 schedule 与 room-info，比赛列表、直播信号与播放地址。
- `csp_GuaziTY`：瓜子体育 AES-CBC 加密 sports/detail 请求响应、赛程和直播线路；独立 OpenSSL 密文向量验证。
- `csp_919TY`：919体育赛程和详情，复合比赛身份、主播 FLV/HLS 多线路。
- 除蜻蜓 FM 外，另外四项公开协议无搜索入口，明确报不支持；不存在自动伪造搜索。
- 五项已独立注册，与既有三项及 Guard 别名不重复。未执行 Java/JAR。
- 源码契约依据：`5q68fs6b86-netizen/CatVodSpider-maintainable` 的对应 Java 入口，核对提交 `3ac7e6ea46339e6cfdf94dec4ff8e2e573638802`。未声称上游站点永久可用。
- Release 主测试 386/386、预览 159/159，TreatWarningsAsErrors=true；本机 osx-x64 NativeAOT 编译发布通过，无 IL2026/IL3050 输出。此次发布编译跳过 QuickJS 重建，不等于完整六平台安装包运行验收。
- 真实网络站点播放未验收；五项的完成口径是实现/离线协议契约/本机AOT，六平台结果单独报告。

## 验证边界
- 本轮音频适配使用离线 HTTP fixture；未声称网络站点实播或全部十项完成。
- 待全量测试、云端与真实站点验证后逐项更新。
