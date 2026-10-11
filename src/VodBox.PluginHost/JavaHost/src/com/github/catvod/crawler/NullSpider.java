package com.github.catvod.crawler;

/**
 * 「无源」spider——VodBox 的独立实现，非上游源码复制。
 *
 * <p>用途：{@link Spider} 是抽象的，需要一个**可实例化的具体类**作为「该站点没有可用 spider」的占位。
 * 它**不覆写任何方法**，因此全部继承 {@link Spider} 的中性默认：内容型方法返回空串（宿主归一为「无结果」或
 * 显式 {@code source_empty} 失败）、布尔型返回 {@code false}、{@link Spider#proxy(java.util.Map)} 返回 {@code null}。
 *
 * <p>刻意保持零覆写：这本身就是对「默认值即中性、不虚构媒体」的断言——{@code NullSpider} 不会编造分类、
 * 详情或播放地址，也不会伪装成可播放。
 *
 * <p>与上游的差异（已核对）：上游把同名类放在 {@code com.github.catvod.spider}；本仓库的宿主只把
 * {@code com.github.catvod.crawler.} 判定为宿主提供（见 {@code DexArchive.PLATFORM_PREFIXES}），
 * 故它放在与基类同包，避免侵入由插件负责的 {@code com.github.catvod.spider}。
 */
public final class NullSpider extends Spider {
}
