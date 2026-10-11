using VodBox.Core;

namespace VodBox.PluginHost.JarPlugin;

/// <summary>
/// 一个 JAR 引用：<c>地址</c> 加可选的 <c>;md5;&lt;hash&gt;</c> 声明段。
///
/// 解析相对地址时以订阅最终 origin 为基准；<see cref="DeclaredMd5"/> 只做**原样保留**，
/// 本层不下载、不比对、不宣称已校验——配置里的 md5 是否可信取决于源站，宿主无权假装验证。
/// </summary>
public sealed record JarReference(string Raw, string Url, string? DeclaredMd5)
{
    /// <summary>声明的 md5 段是否存在。<b>不等于</b>内容已校验。</summary>
    public bool HasDeclaredMd5 => !string.IsNullOrEmpty(DeclaredMd5);

    /// <summary>把配置里的 jar 声明解析为绝对地址；空/无效返回 null。</summary>
    public static JarReference? Parse(string? specification, string? origin)
    {
        if (string.IsNullOrWhiteSpace(specification)) return null;
        var raw = specification.Trim();
        var separator = raw.IndexOf(";md5;", StringComparison.OrdinalIgnoreCase);
        var address = separator >= 0 ? raw[..separator].Trim() : raw;
        var md5 = separator >= 0 ? raw[(separator + 5)..].Trim() : null;
        if (string.IsNullOrEmpty(md5)) md5 = null;
        var resolved = ResolveRelative(address, origin);
        return string.IsNullOrEmpty(resolved) ? null : new JarReference(raw, resolved, md5);
    }

    /// <summary>把相对地址按 origin 归一化为绝对地址；已是绝对地址则原样返回（trim 后）。</summary>
    public static string ResolveRelative(string? address, string? origin)
    {
        if (string.IsNullOrWhiteSpace(address)) return "";
        var value = address.Trim();
        if (Uri.TryCreate(value, UriKind.Absolute, out var absolute) && absolute.Scheme.Length > 1) return value;
        if (string.IsNullOrWhiteSpace(origin) || !Uri.TryCreate(origin, UriKind.Absolute, out var basis)) return value;
        return Uri.TryCreate(basis, value, out var combined) ? combined.AbsoluteUri : value;
    }
}

/// <summary>
/// 一个 JAR 源所需的全部上下文：站点身份、spider 类名、全局/站点 JAR 引用、ext、订阅最终 origin、
/// 以及站点原始 JSON。构造时不做网络、不校验 md5。
/// </summary>
public sealed record JarPluginSourceLayout
{
    public required string SourceKey { get; init; }
    public required string Api { get; init; }
    /// <summary>spider 全类名，如 <c>com.github.catvod.spider.AppRJ</c>。</summary>
    public required string SpiderClass { get; init; }
    public JarReference? GlobalSpider { get; init; }
    public JarReference? SiteJar { get; init; }
    /// <summary>站点扩展参数（相对路径已按 origin 解析）。</summary>
    public string Ext { get; init; } = "";
    public string? Origin { get; init; }
    public string SiteJson { get; init; } = "";

    /// <summary>实际使用的 JAR：站点自带优先，否则回退配置级全局 spider。</summary>
    public JarReference? EffectiveJar => SiteJar ?? GlobalSpider;

    /// <summary>会话隔离键：同一站点同一 JAR 复用宿主进程，不同站点各自独立进程。</summary>
    public string SessionKey => string.Join('\u0000', SourceKey, EffectiveJar?.Url ?? "", Ext);

    public static JarPluginSourceLayout From(SourceInfo info)
    {
        var api = info.Api ?? "";
        return new JarPluginSourceLayout
        {
            SourceKey = info.Key,
            Api = api,
            SpiderClass = ToSpiderClass(api),
            GlobalSpider = JarReference.Parse(info.Spider, info.Origin),
            SiteJar = JarReference.Parse(info.Jar, info.Origin),
            Ext = ResolveExt(info.Ext, info.Origin),
            Origin = info.Origin,
            SiteJson = info.SiteJson ?? "",
        };
    }

    /// <summary>csp_AppRJ → com.github.catvod.spider.AppRJ；非 csp_ 前缀原样返回。</summary>
    public static string ToSpiderClass(string api)
    {
        const string prefix = "csp_";
        var name = api.StartsWith(prefix, StringComparison.Ordinal) ? api[prefix.Length..] : api;
        return string.IsNullOrEmpty(name) ? "" : "com.github.catvod.spider." + name;
    }

    /// <summary>
    /// ext 里以 <c>./</c>、<c>../</c> 或 <c>//</c> 开头的相对地址按订阅 origin 解析；
    /// 其它内容原样保留（可能是 JSON 规则或内联值，不能擅自改写）。
    /// </summary>
    public static string ResolveExt(string? ext, string? origin)
    {
        if (string.IsNullOrWhiteSpace(ext)) return "";
        var value = ext.Trim();
        var relative = value.StartsWith("./", StringComparison.Ordinal) ||
                       value.StartsWith("../", StringComparison.Ordinal) ||
                       value.StartsWith("//", StringComparison.Ordinal);
        if (!relative) return value;
        if (value.StartsWith("//", StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(origin) || !Uri.TryCreate(origin, UriKind.Absolute, out var basis)) return value;
            return basis.Scheme + ":" + value;
        }
        return JarReference.ResolveRelative(value, origin);
    }
}
