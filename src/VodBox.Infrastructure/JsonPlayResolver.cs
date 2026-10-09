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
        if (!root.TryGetProperty("url", out var value) || value.ValueKind != JsonValueKind.String) return null;
        var url = value.GetString();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var media) || media.Scheme is not ("http" or "https")) return null;
        var headers = new Dictionary<string, string>(request.Headers, StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("header", out var header) && header.ValueKind == JsonValueKind.Object)
            foreach (var item in header.EnumerateObject())
            {
                var name = item.Name.ToLowerInvariant() switch { "user-agent" => "User-Agent", "referer" or "referrer" => "Referer", _ => null };
                if (item.Value.ValueKind != JsonValueKind.String || name is null) continue;
                var content = item.Value.GetString()!;
                if (content.IndexOfAny(['\r', '\n', '\0']) >= 0) throw new InvalidDataException("解析响应请求头非法。");
                headers[name] = content;
            }
        return request with { Uri = media.AbsoluteUri, Headers = headers, Resolution = ResolutionKind.Direct };
    }
}
