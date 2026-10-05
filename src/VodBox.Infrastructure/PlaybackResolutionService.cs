using System.Text.Json;
using VodBox.Core;

namespace VodBox.Infrastructure;

public sealed class PlaybackResolutionService(HttpClient http) : IPlaybackResolver, IAsyncDisposable
{
    private readonly MediaProxy _proxy = new();
    private IReadOnlyDictionary<string, ResolverDefinition> _resolvers = new Dictionary<string, ResolverDefinition>();

    public void Configure(VodBoxConfig config) => _resolvers = (config.Resolvers ?? []).ToDictionary(x => x.Id, StringComparer.Ordinal);

    public async Task<PlaybackRequest> ResolveAsync(PlaybackRequest request, CancellationToken token)
    {
        var definitions = _resolvers;
        string? id = request.ResolverId;
        if (id == "_direct") return Finish(request);
        if (id == "_browser" || id is null && request.ResolutionKind == ResolutionKind.Browser)
            return Finish(await new BrowserMediaResolver(http).ResolveAsync(request with { OriginalUri = request.OriginalUri ?? request.Uri }, new() { Id = "_browser", Name = "网页嗅探", Kind = ResolutionKind.Browser, TimeoutSeconds = 30 }, token));
        if (id is null && request.ResolutionKind == ResolutionKind.Json)
            throw new InvalidDataException("JSON 解析请求需要 resolverId。");
        var current = request with { OriginalUri = request.OriginalUri ?? request.Uri };
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (id is not null)
        {
            if (!visited.Add(id) || visited.Count > 6) throw new InvalidDataException("解析器链循环或超过六级。");
            if (!definitions.TryGetValue(id, out var definition)) throw new InvalidDataException($"找不到解析器：{id}");
            if (definition.Kind == ResolutionKind.Browser) current = await new BrowserMediaResolver(http).ResolveAsync(current, definition, token);
            if (definition.Kind == ResolutionKind.Json) current = await ParseJsonAsync(current, definition, token);
            id = definition.NextResolverId;
        }
        return Finish(current);
    }

    private PlaybackRequest Finish(PlaybackRequest request)
    {
        if (!Uri.TryCreate(request.Uri, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "file" or "rtsp" or "rtsps" or "rtmp" or "rtmps" or "udp" or "rtp" or "mms" or "mmsh" or "smb" or "srt"))
            throw new InvalidDataException("解析结果不是受支持的媒体传输地址。");
        return uri.Scheme is "http" or "https" && request.Headers.Keys.Any(key => !key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase) && !key.Equals("Referer", StringComparison.OrdinalIgnoreCase))
            ? _proxy.Register(request) : request;
    }

    private async Task<PlaybackRequest> ParseJsonAsync(PlaybackRequest request, ResolverDefinition definition, CancellationToken token)
    {
        string entry = definition.Entry ?? throw new InvalidDataException("JSON 解析器缺少 entry。");
        string encoded = Uri.EscapeDataString(request.Uri);
        bool template = entry.Contains("{url}", StringComparison.Ordinal) || entry.Contains("%7Burl%7D", StringComparison.OrdinalIgnoreCase);
        entry = entry.Replace("{url}", encoded, StringComparison.Ordinal).Replace("%7Burl%7D", encoded, StringComparison.OrdinalIgnoreCase);
        if (!template) entry += encoded;
        if (!Uri.TryCreate(entry, UriKind.Absolute, out var endpoint) || endpoint.Scheme is not ("http" or "https")) throw new InvalidDataException("解析器地址必须是 HTTP/HTTPS。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(definition.TimeoutSeconds));
        using var message = new HttpRequestMessage(HttpMethod.Get, endpoint);
        foreach (var header in definition.Headers)
        {
            if (header.Key.Contains('\r') || header.Key.Contains('\n') || header.Value.Contains('\r') || header.Value.Contains('\n')) throw new InvalidDataException("解析器请求头含换行。");
            message.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token); response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var document = JsonDocument.Parse(await BoundedContent.ReadAsync(stream, 8 * 1024 * 1024, timeout.Token));
        var value = AtPath(document.RootElement, definition.UrlPath);
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString())) throw new InvalidDataException($"解析结果缺少字符串字段 {definition.UrlPath}。");
        string result = value.GetString()!;
        if (!Uri.TryCreate(result, UriKind.Absolute, out var resolved)) resolved = new Uri(response.RequestMessage?.RequestUri ?? endpoint, result);
        var headers = new Dictionary<string, string>(request.Headers, StringComparer.OrdinalIgnoreCase);
        if (definition.HeadersPath is not null && AtPath(document.RootElement, definition.HeadersPath) is { ValueKind: JsonValueKind.Object } supplied)
            foreach (var header in supplied.EnumerateObject())
                if (header.Value.ValueKind == JsonValueKind.String) headers[header.Name] = header.Value.GetString()!;
        return request with { Uri = resolved.AbsoluteUri, Headers = headers };
    }

    private static JsonElement AtPath(JsonElement root, string path)
    {
        foreach (var part in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (root.ValueKind == JsonValueKind.Array && int.TryParse(part, out int index) && index >= 0 && index < root.GetArrayLength()) root = root[index];
            else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(part, out var child)) root = child;
            else return default;
        }
        return root;
    }
    public ValueTask DisposeAsync() => _proxy.DisposeAsync();
}
