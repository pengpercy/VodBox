package dev.vodbox.jarhost.dex;

/**
 * DEX 转换失败分类码。取值与 C# 侧 {@code JarPluginErrors} 的常量一一对应，
 * 双方共享同一套字符串契约——这样宿主返回的分类码能被上层原样翻译成用户可读信息。
 *
 * 这些码只描述**转换过程**的失败；转换成功后插件若仍依赖 {@code android.*}，
 * 会在加载/链接阶段以 {@code unsupported_android} 明确失败（不由这里伪装成成功）。
 */
public final class DexCodes {
    private DexCodes() {}

    /** 转换器已运行但转换失败（不支持的指令/校验失败等确定性失败）。 */
    public static final String CONVERSION_FAILED = "dex_conversion_failed";
    /** 找不到可用的 DEX→JVM 转换器（缺 dex2jar 或运行时），无法转换。 */
    public static final String CONVERTER_UNAVAILABLE = "dex_converter_unavailable";
    /** 归档里的 DEX 结构非法/损坏。 */
    public static final String CORRUPT = "dex_corrupt";
    /** DEX 版本不受支持。 */
    public static final String UNSUPPORTED_VERSION = "dex_unsupported_version";
    /** 无法在超时内取得转换锁（并发转换进行中）。可重试。 */
    public static final String LOCKED = "dex_conversion_locked";
}
