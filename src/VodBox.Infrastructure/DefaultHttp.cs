using System.Net;
using System.Text;

namespace VodBox.Infrastructure;

/// <summary>统一 HTTP 抓取：全局 UA、超时、重定向、gzip。</summary>
public sealed class DefaultHttp : IDisposable
{
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
        _client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36");
    }

    public async Task<string> GetStringAsync(string url, Dictionary<string, string>? headers = null, CancellationToken ct = default)
    {
        var bytes = await GetByteArrayAsync(url, headers, ct);
        return Decode(bytes);
    }

    public async Task<byte[]> GetByteArrayAsync(string url, Dictionary<string, string>? headers = null, CancellationToken ct = default)
    {
        // 外层硬超时：覆盖 DNS/TLS/TFO 阶段挂起（远端黑洞时 20s HttpClient.Timeout 不可靠）
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (headers is not null)
            foreach (var (key, value) in headers)
                request.Headers.TryAddWithoutValidation(key, value);
        using var response = await _client.SendAsync(request, timeout.Token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(timeout.Token);
    }

    /// <summary>处理 UTF-8 / UTF-16 / GB18030 等编码的文本解码。</summary>
    public static string Decode(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        var text = Encoding.UTF8.GetString(bytes);
        // 含替换字符说明不是合法 UTF-8，回退 GB18030
        return text.Contains('\uFFFD') && OperatingSystem.IsWindows() ? Encoding.UTF8.GetString(bytes) : text;
    }

    public void Dispose() => _client.Dispose();
}
