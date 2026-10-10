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
- 额外三项：首项选择 csp_Push，HTTP(S) 单地址/标题地址列表、直连与 JSON 解析；不支持网页嗅探、文件、迅雷或 YouTube 提取。其余两项尚未选择，不把重复 Guard 别名计为完成。

## 验证边界
- 本轮音频适配使用离线 HTTP fixture；未声称网络站点实播或全部十项完成。
- 待全量测试、云端与真实站点验证后逐项更新。
