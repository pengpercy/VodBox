using System.Text.Json;
using VodBox.Core;

namespace VodBox.Infrastructure;

/// <summary>TVBox type1/type2 JSON解析；输出只允许HTTP(S)媒体，不把网页或任意协议传给引擎。</summary>
public sealed class JsonPlayResolver : IPlayResolver
{
    private readonly TvBoxParse _parse;
    private readonly Func<string, IReadOnlyDictionary<string, string>?, CancellationToken, Task<string>> _fetch;
    public string Name { get; set; }
    public JsonPlayResolver(TvBoxParse parse, DefaultHttp http) : this(parse, (url, headers, ct) => FetchBoundedAsync(http, url, headers, ct)) { }
    public JsonPlayResolver(TvBoxParse parse, Func<string, IReadOnlyDictionary<string, string>?, CancellationToken, Task<string>> fetch)
    {
        _parse = parse; _fetch = fetch; Name = parse.Name;
    }

    private static async Task<string> FetchBoundedAsync(DefaultHttp http, string url, IReadOnlyDictionary<string, string>? headers, CancellationToken ct)
    {
        var bytes = await http.GetBoundedAsync(url, 2 * 1024 * 1024, ct, headers);
        return DefaultHttp.Decode(bytes);
    }

    public async Task<PlaybackRequest?> ResolveAsync(PlaybackRequest request, CancellationToken ct = default)
    {
        if (_parse.Type is not (1 or 2)) throw new NotSupportedException("该解析器不是 JSON 类型。");
        var address = _parse.Url.Contains("{url}", StringComparison.Ordinal)
            ? _parse.Url.Replace("{url}", Uri.EscapeDataString(request.Uri), StringComparison.Ordinal) : _parse.Url + request.Uri;
        if (!Uri.TryCreate(address, UriKind.Absolute, out var endpoint) || endpoint.Scheme is not ("http" or "https"))
            throw new InvalidDataException("解析器地址无效。");
        var requestHeaders = new Dictionary<string, string>(_parse.Header ?? [], StringComparer.OrdinalIgnoreCase);
        if (_parse.Ext is { ValueKind: JsonValueKind.Object } ext && ext.TryGetProperty("header", out var configured) && configured.ValueKind == JsonValueKind.Object)
            foreach (var configuredHeader in configured.EnumerateObject())
                if (configuredHeader.Value.ValueKind == JsonValueKind.String) requestHeaders[configuredHeader.Name] = configuredHeader.Value.GetString()!;
        foreach (var (key, headerValue) in requestHeaders)
            if (key.IndexOfAny(['\r', '\n', '\0']) >= 0 || headerValue.IndexOfAny(['\r', '\n', '\0']) >= 0) throw new InvalidDataException("解析器请求头非法。");
        var text = await _fetch(address, requestHeaders, ct);
        if (text.Length > 2 * 1024 * 1024) throw new InvalidDataException("解析响应过大。");
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object) root = data;
        // url 允许字符串（单地址）或字符串数组（多地址）；只接受 HTTP(S)，其余（网页/javascript 等）丢弃。
        var mediaUrls = ParseMediaUrls(root);
        if (mediaUrls.Count == 0) return null;
        var media = new Uri(mediaUrls[0], UriKind.Absolute);
        var choices = new List<PlaybackChoice>();
        for (var index = 0; index < mediaUrls.Count; index++) choices.Add(new PlaybackChoice("地址" + (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), mediaUrls[index]));
        var headers = new Dictionary<string, string>(request.Headers, StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("header", out var header) && header.ValueKind == JsonValueKind.Object)
            foreach (var item in header.EnumerateObject())
            {
                if (item.Value.ValueKind != JsonValueKind.String) continue;
                var content = item.Value.GetString()!;
                // 完整保留 Cookie/Authorization 等播放所需头（引擎直传不了的由受控媒体代理转发），
                // UA/Referer 归一化大小写，其余按原名；非法头名/控制字符明确拒绝而非静默丢弃。
                if (!IsHeaderName(item.Name) || content.IndexOfAny(['\r', '\n', '\0']) >= 0) throw new InvalidDataException("解析响应请求头非法。");
                headers[CanonicalHeaderName(item.Name)] = content;
            }
        return request with { Uri = media.AbsoluteUri, Choices = choices, Headers = headers, Resolution = ResolutionKind.Direct };
    }

    /// <summary>解析响应里的媒体地址：字符串单地址或字符串数组多地址，仅保留合法的 HTTP(S) 绝对地址。</summary>
    private static IReadOnlyList<string> ParseMediaUrls(JsonElement root)
    {
        var urls = new List<string>();
        if (!root.TryGetProperty("url", out var value)) return urls;
        if (value.ValueKind == JsonValueKind.String)
        {
            if (IsHttpUrl(value.GetString())) urls.Add(value.GetString()!);
            return urls;
        }
        if (value.ValueKind != JsonValueKind.Array) return urls;
        foreach (var item in value.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String && IsHttpUrl(item.GetString())) urls.Add(item.GetString()!);
        return urls;
    }

    private static bool IsHttpUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    private static string CanonicalHeaderName(string name) => name.ToLowerInvariant() switch
    {
        "user-agent" => "User-Agent",
        "referer" or "referrer" => "Referer",
        _ => name,
    };

    /// <summary>RFC 7230 token 字符：非 token 的头名（含空格、冒号等）拒绝，不原样透传。</summary>
    private static bool IsHeaderName(string name)
    {
        if (name.Length == 0) return false;
        foreach (var c in name)
            if (!char.IsAsciiLetterOrDigit(c) && !"!#$%&'*+-.^_`|~".Contains(c)) return false;
        return true;
    }
}
