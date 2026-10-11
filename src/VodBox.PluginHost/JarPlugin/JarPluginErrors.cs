namespace VodBox.PluginHost.JarPlugin;

/// <summary>插件失败归类：决定上层是「重试」还是「明确禁用」。</summary>
public enum JarPluginFailureKind
{
    /// <summary>可重试（网络抖动、超时、宿主进程意外退出）。</summary>
    Temporary,
    /// <summary>该源在当前宿主下不可用（缺 Android/DEX 运行时、缺类、需要未实现能力）。</summary>
    Unsupported,
    /// <summary>协议/宿主实现问题（响应缺失、JSON 非法、宿主不可用）。</summary>
    Protocol,
}

/// <summary>一次插件失败的分类结果。</summary>
public sealed record JarPluginFailure(string Code, JarPluginFailureKind Kind, string Message)
{
    public bool Retryable => Kind == JarPluginFailureKind.Temporary;
}

/// <summary>
/// 宿主错误码与用户可读信息的映射。错误码取值对齐可观测的宿主响应信封（<c>errorCode</c>），
/// 另加本层自有的宿主/协议码。**不臆造**：未知码按下层原文透出，不伪装成成功。
/// </summary>
public static class JarPluginErrors
{
    // ---- 宿主返回（errorCode）----
    public const string SourceHttp = "source_http";
    public const string SourceEmpty = "source_empty";
    public const string SourceNetwork = "source_network";
    public const string UnsupportedNativeLibrary = "unsupported_native_library";
    public const string UnsupportedAndroid = "unsupported_android";
    public const string ClassConversion = "class_conversion";
    public const string PluginCrash = "plugin_crash";
    public const string UnsupportedDex = "unsupported_dex";
    public const string PluginClassMissing = "plugin_class_missing";
    // ---- DEX→JVM 转换（与 Java 宿主 DexCodes 一一对应）----
    /// <summary>转换器已运行但转换失败（不支持的构造/校验失败）。</summary>
    public const string DexConversionFailed = "dex_conversion_failed";
    /// <summary>未发现可用的 DEX→JVM 转换器（缺 dex2jar 或运行时）。</summary>
    public const string DexConverterUnavailable = "dex_converter_unavailable";
    /// <summary>归档里的 DEX 结构损坏。</summary>
    public const string DexCorrupt = "dex_corrupt";
    /// <summary>DEX 版本不受支持。</summary>
    public const string DexUnsupportedVersion = "dex_unsupported_version";
    /// <summary>无法在超时内取得转换锁（并发转换进行中），可重试。</summary>
    public const string DexConversionLocked = "dex_conversion_locked";
    // ---- 本层自有 ----
    public const string HostUnavailable = "host_unavailable";
    public const string HostLaunchFailed = "host_launch_failed";
    public const string HostTimeout = "host_timeout";
    public const string HostExited = "host_exited";
    public const string ProtocolError = "protocol_error";
    public const string DownloadFailed = "download_failed";

    /// <summary>把宿主信封翻译成分类失败；成功信封返回 null。</summary>
    public static JarPluginFailure? Classify(JarPluginEnvelope? envelope)
    {
        if (envelope is null) return new(ProtocolError, JarPluginFailureKind.Protocol, "插件宿主未返回可读响应。");
        if (!envelope.Failed) return null;
        var code = string.IsNullOrWhiteSpace(envelope.ErrorCode) ? ProtocolError : envelope.ErrorCode!;
        return Classify(code, envelope.Error, envelope.Host, envelope.Status, envelope.Library, envelope.MissingClass);
    }

    /// <summary>按错误码与上下文生成分类结果。</summary>
    public static JarPluginFailure Classify(string code, string? detail = null, string? host = null, int? status = null,
        string? library = null, string? missingClass = null)
    {
        var trimmed = string.IsNullOrWhiteSpace(detail) ? null : detail!.Trim();
        return code switch
        {
            SourceHttp => new(code, JarPluginFailureKind.Temporary,
                $"源站返回 HTTP {status?.ToString() ?? "错误"}{HostPart(host)}：{trimmed ?? "请求被拒绝"}" ),
            SourceNetwork => new(code, JarPluginFailureKind.Temporary, $"网络错误{HostPart(host)}：{trimmed ?? "无法连接源站"}"),
            SourceEmpty => new(code, JarPluginFailureKind.Temporary, "该源没有返回内容，请换源或稍后重试。"),
            HostTimeout => new(code, JarPluginFailureKind.Temporary, "插件执行超时，已终止该源会话，可重试。"),
            HostExited => new(code, JarPluginFailureKind.Temporary, "插件进程意外退出，可重试。"),
            DownloadFailed => new(code, JarPluginFailureKind.Temporary, $"插件 JAR 下载失败：{trimmed ?? "未知原因"}"),
            UnsupportedNativeLibrary => new(code, JarPluginFailureKind.Unsupported,
                $"插件依赖未实现的原生库{LibPart(library)}，当前宿主不能运行{(trimmed is null ? "。" : $"：{trimmed}")}"),
            UnsupportedAndroid => new(code, JarPluginFailureKind.Unsupported,
                $"该插件需要 Android 运行时{ClassPart(missingClass)}，桌面宿主未实现。"),
            UnsupportedDex => new(code, JarPluginFailureKind.Unsupported, "该插件是 Android DEX 包（classes.dex），无法在普通 Java 字节码宿主下加载。"),
            DexConversionFailed => new(code, JarPluginFailureKind.Unsupported, $"插件 DEX 无法转换为 JVM 字节码：{trimmed ?? "转换器报告失败"}"),
            DexConverterUnavailable => new(code, JarPluginFailureKind.Unsupported, "未发现 DEX→JVM 转换器（dex2jar），无法转换该 Android 插件。"),
            DexCorrupt => new(code, JarPluginFailureKind.Unsupported, $"插件的 DEX 结构损坏，无法转换：{trimmed ?? "非法 DEX"}"),
            DexUnsupportedVersion => new(code, JarPluginFailureKind.Unsupported, $"插件的 DEX 版本不受支持：{trimmed ?? "未知版本"}"),
            DexConversionLocked => new(code, JarPluginFailureKind.Temporary, "同一插件的 DEX 转换正在进行，请稍后重试。"),
            ClassConversion => new(code, JarPluginFailureKind.Unsupported, $"插件类无法加载{ClassPart(missingClass)}，可能缺少依赖或需要转换。"),
            PluginClassMissing => new(code, JarPluginFailureKind.Unsupported, $"在 JAR 中找不到 spider 类（{trimmed ?? "com.github.catvod.spider.*"}）。"),
            PluginCrash => new(code, JarPluginFailureKind.Unsupported, $"插件自身抛出异常：{trimmed ?? "未知错误"}"),
            HostUnavailable => new(code, JarPluginFailureKind.Protocol, "未找到可用的 JAR 插件宿主（需要 Java 运行时与 host.jar）。"),
            HostLaunchFailed => new(code, JarPluginFailureKind.Protocol, $"插件宿主启动失败：{trimmed ?? "未知原因"}"),
            ProtocolError => new(code, JarPluginFailureKind.Protocol, $"插件宿主协议异常：{trimmed ?? "响应不合规"}"),
            _ => new(code, JarPluginFailureKind.Unsupported, trimmed ?? $"插件失败（{code}）。"),
        };
    }

    private static string HostPart(string? host) => string.IsNullOrWhiteSpace(host) ? "" : $"（{host}）";
    private static string LibPart(string? library) => string.IsNullOrWhiteSpace(library) ? "" : $"：{library}";
    private static string ClassPart(string? missingClass) => string.IsNullOrWhiteSpace(missingClass) ? "" : $"（缺少 {missingClass}）";
}

/// <summary>插件宿主相关的可分类异常：调用方按 <see cref="Failure"/> 决定提示或禁用。</summary>
public sealed class JarPluginException : Exception
{
    public JarPluginFailure Failure { get; }

    public JarPluginException(JarPluginFailure failure, Exception? inner = null)
        : base(failure.Message, inner) => Failure = failure;

    public JarPluginException(string code, JarPluginFailureKind kind, string message, Exception? inner = null)
        : this(new JarPluginFailure(code, kind, message), inner) { }
}
