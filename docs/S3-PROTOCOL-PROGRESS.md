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

## 验证边界
- 本轮音频适配使用离线 HTTP fixture；未声称网络站点实播或全部十项完成。
- 待全量测试、云端与真实站点验证后逐项更新。
