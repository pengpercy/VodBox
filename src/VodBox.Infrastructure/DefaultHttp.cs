using System.Globalization;
using System.Net;
using System.Text;

namespace VodBox.Infrastructure;

/// <summary>统一 HTTP 抓取：默认 UA（可按请求覆盖）、超时、重定向、gzip。</summary>
public sealed class DefaultHttp : IDisposable
{
    /// <summary>默认浏览器 UA；部分 TVBox 配置站点会 WAF 拦截它，调用方可按请求覆盖（见 <see cref="TvBoxUserAgent"/>）。</summary>
    public const string BrowserUserAgent =
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36";

    /// <summary>TVBox/okhttp 客户端 UA：饭太硬、宝盒接口等站点对桌面浏览器 UA 返回 WAF 挑战页，换此 UA 可拿到真内容。</summary>
    public const string TvBoxUserAgent = "okhttp/4.12.0";

    private readonly HttpClient _client;

    public DefaultHttp()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
        };
        _client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
    }

    public async Task<string> GetStringAsync(string url, Dictionary<string, string>? headers = null, CancellationToken ct = default)
    {
        var bytes = await GetByteArrayAsync(url, headers, ct);
        return Decode(bytes);
    }

    /// <summary>
    /// 中文/IDN 域名归一化为 Punycode（ASCII），两种写法行为一致。
    /// .NET 的 <see cref="Uri.Host"/> 保留 Unicode 原文，仅 <see cref="Uri.IdnHost"/> 是 Punycode，
    /// HttpClient 内部虽用 IdnHost 解析 DNS，但请求 URI、日志、站点匹配若混用两种形态会踩坑，故统一。
    /// 纯 ASCII / 非法 URL 原样返回（幂等）。
    /// </summary>
    public static string NormalizeIdn(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url;
        var host = uri.Host;
        if (host.Length == 0 || IsAscii(host)) return url;
        string ascii;
        try
        {
            ascii = new IdnMapping().GetAscii(host);
        }
        catch (ArgumentException)
        {
            return url; // 非法域名交给 HttpClient 原样报错
        }
        return new UriBuilder(uri) { Host = ascii }.Uri.AbsoluteUri;
    }

    private static bool IsAscii(string value)
    {
        foreach (var c in value)
            if (c > 0x7F) return false;
        return true;
    }

    public async Task<byte[]> GetByteArrayAsync(string url, Dictionary<string, string>? headers = null, CancellationToken ct = default)
    {
        url = NormalizeIdn(url);
        // 外层硬超时：覆盖 DNS/TLS/TFO 阶段挂起（远端黑洞时 20s HttpClient.Timeout 不可靠）
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        // 每次请求只发一个 UA：默认浏览器 UA，headers 里的 user-agent 覆盖它（不能两个都加，否则会合并成一行）
        var userAgent = BrowserUserAgent;
        if (headers is not null)
            foreach (var (key, value) in headers)
                if (key.Equals("user-agent", StringComparison.OrdinalIgnoreCase)) userAgent = value;
                else request.Headers.TryAddWithoutValidation(key, value);
        request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        using var response = await _client.SendAsync(request, timeout.Token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(timeout.Token);
    }

    /// <summary>
    /// 处理 UTF-8 / UTF-16 / GB18030 等编码的文本解码。
    /// 委托给 <see cref="TextEncoding.Decode"/>：旧实现在非 Windows 平台不回退 GB18030
    /// （仅靠 U+FFFD 替换字符判断，中文站点在 macOS/Linux 上会乱码），现已全平台严格回退。
    /// 保留此静态入口以免破坏现有调用点（ConfigLoader 等）。
    /// </summary>
    public static string Decode(byte[] bytes) => TextEncoding.Decode(bytes);

    public void Dispose() => _client.Dispose();
}
