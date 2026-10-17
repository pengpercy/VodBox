using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using VodBox.Core;

namespace VodBox.Infrastructure;

/// <summary>FTY AppTT v3: signed multipart requests and per-detail parser selection.</summary>
public sealed partial class AppTtSource : IResolvingContentSource
{
    internal const string UserAgent = "okhttp-okgo/jeasonlzy";
    // InitOrigin changes the first returned flag in the frozen payload. Keep its persisted
    // identity while displaying the actual API flag; users may have history from JavaHost.
    internal const string LegacyFirstFlag = "免费分享 仅供测试";
    private static readonly HttpClient SharedHttp = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        AllowAutoRedirect = false
    }) { Timeout = TimeSpan.FromSeconds(20) };
    private readonly SourceInfo _site;
    private readonly Uri _base;
    private readonly HttpClient _http;
    private readonly Func<long> _clock;
    public string Key => _site.Key;
    public string Name => _site.Name;

    public AppTtSource(SourceInfo site) : this(site, SharedHttp, () => DateTimeOffset.UtcNow.ToUnixTimeSeconds()) { }
    internal AppTtSource(SourceInfo site, HttpClient http, Func<long> clock)
    {
        _site = site;
        _base = Endpoint(site.Ext);
        _http = http;
        _clock = clock;
    }

    internal static Uri Endpoint(string? ext)
    {
        string value = ext?.Trim() ?? "";
        if (!Uri.TryCreate(value, UriKind.Absolute, out var address) || address.Scheme is not ("http" or "https"))
            value = FtyExtCodec.Decode(value);
        var endpoint = HttpUri(value);
        if (endpoint.Query.Length > 0 || endpoint.AbsolutePath.Trim('/') != "")
            throw new NotSupportedException("AppTT ext 必须是接口根地址。");
        return endpoint;
    }

    internal static bool Supports(string? ext)
    {
        try { Endpoint(ext); return true; }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException) { return false; }
    }

    private static Uri HttpUri(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0
        ? uri : throw new InvalidDataException("AppTT 返回无效 HTTP(S) 地址。");

    private async Task<JsonDocument> Request(string path, Dictionary<string, string> fields, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        long timestamp = _clock();
        fields["sign"] = AppRjSource.Sign(timestamp); // independently verified against AppTT.yq
        fields["timestamp"] = timestamp.ToString(CultureInfo.InvariantCulture);
        using var form = new MultipartFormDataContent();
        // The original includes an empty part on these two routes.
        if (path is "/v3/type/top_type" or "/v3/type/tj_vod")
        {
            var empty = new StringContent("");
            empty.Headers.ContentDisposition = new("form-data") { Name = "\"\"" };
            form.Add(empty);
        }
        foreach (var field in fields) form.Add(new StringContent(field.Value), field.Key);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_base, path)) { Content = form };
        return await Send(request, ct).ConfigureAwait(false);
    }

    private async Task<JsonDocument> Send(HttpRequestMessage request, CancellationToken ct)
    {
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        using var scope = CancellationTokenSource.CreateLinkedTokenSource(ct);
        scope.CancelAfter(TimeSpan.FromSeconds(20));
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, scope.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(scope.Token).ConfigureAwait(false);
        var body = await BoundedContent.ReadAsync(stream, 8 * 1024 * 1024, scope.Token).ConfigureAwait(false);
        scope.Token.ThrowIfCancellationRequested();
        return JsonDocument.Parse(body);
    }

    private static JsonElement Data(JsonDocument document) => document.RootElement.ValueKind == JsonValueKind.Object &&
        document.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
        ? data : throw new InvalidDataException("AppTT 响应缺少 data 对象。");
    private static JsonElement Array(JsonElement item, string key) => item.ValueKind == JsonValueKind.Object &&
        item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Array
        ? value : throw new InvalidDataException("AppTT 响应缺少数组：" + key);
    private static string Text(JsonElement item, string key) => AppRjSource.Text(item, key);
    private static MediaItem Item(JsonElement item) => AppRjSource.Item(item);

    public async Task<IReadOnlyList<Category>> GetCategoriesAsync(CancellationToken ct = default)
    {
        using var document = await Request("/v3/type/top_type", [], ct).ConfigureAwait(false);
        return Array(Data(document), "list").EnumerateArray()
            .Select(item => new Category(Text(item, "type_id"), Text(item, "type_name")))
            .Where(item => item.Id.Length > 0).ToArray();
    }

    public async Task<MediaPage> GetHomeAsync(CancellationToken ct = default)
    {
        using var document = await Request("/v3/type/tj_vod", [], ct).ConfigureAwait(false);
        var items = Array(Data(document), "type_vod").EnumerateArray()
            .SelectMany(group => Array(group, "vod").EnumerateArray()).Select(Item).Where(item => item.Id.Length > 0).ToArray();
        return new(items, 1, 1);
    }

    public async Task<MediaPage> GetItemsAsync(string categoryId, int page, IReadOnlyDictionary<string, string>? filters, CancellationToken ct = default)
    {
        ValidatePage(page);
        if (filters is { Count: > 0 }) throw new NotSupportedException("此版本 AppTT 不支持筛选参数。");
        using var document = await Request("/v3/home/type_search", new()
        {
            ["limit"] = "12", ["type_id"] = categoryId, ["page"] = page.ToString(CultureInfo.InvariantCulture)
        }, ct).ConfigureAwait(false);
        var items = Array(Data(document), "list").EnumerateArray().Select(Item).Where(item => item.Id.Length > 0).ToArray();
        return new(items, page, items.Length > 0 ? checked(page + 1) : page);
    }

    public async Task<MediaPage> SearchAsync(string query, int page, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ValidatePage(page);
        if (query.Length > 256) throw new ArgumentException("搜索关键词过长。", nameof(query));
        // This plugin exposes one search page and does not send page/limit fields.
        if (page > 1 || string.IsNullOrWhiteSpace(query)) return new([], page, page);
        using var document = await Request("/v3/home/search", new() { ["keyword"] = query }, ct).ConfigureAwait(false);
        var items = Array(Data(document), "list").EnumerateArray().Select(Item)
            .Where(item => item.Id.Length > 0 && item.Title.Contains(query, StringComparison.Ordinal)).ToArray();
        return new(items, 1, 1);
    }

    private Task<JsonDocument> Detail(string id, CancellationToken ct) => Request("/v3/home/vod_details", new() { ["vod_id"] = id }, ct);

    private static IReadOnlyList<PlaybackLine> Lines(JsonElement data)
    {
        var lines = new List<PlaybackLine>();
        foreach (var line in Array(data, "vod_play_list").EnumerateArray())
        {
            string flag = Text(line, "flag");
            if (flag.Length == 0 || flag.Contains('\u0001')) throw new InvalidDataException("AppTT 线路 flag 无效。");
            string identity = lines.Count == 0 ? LegacyFirstFlag : flag;
            var episodes = Array(line, "urls").EnumerateArray().Where(ep => Text(ep, "url").Length > 0)
                .Select(ep => new Episode(identity + '\u0001' + Text(ep, "url"), Text(ep, "name"))).ToArray();
            string id = lines.Any(existing => existing.Id == identity) ? identity + ":" + lines.Count : identity;
            lines.Add(new(id, flag, episodes));
        }
        return lines;
    }

    public async Task<MediaDetail> GetDetailAsync(string mediaId, CancellationToken ct = default)
    {
        using var document = await Detail(mediaId, ct).ConfigureAwait(false);
        var data = Data(document);
        return new()
        {
            Item = Item(data) with { Id = mediaId },
            Description = Tags().Replace(WebUtility.HtmlDecode(Text(data, "vod_content")), "").Trim(),
            Actor = Text(data, "vod_actor"), Director = Text(data, "vod_director"), Lines = Lines(data)
        };
    }

    public async Task<PlaybackRequest> ResolvePlaybackAsync(string mediaId, string episodeId, CancellationToken ct = default)
    {
        using var document = await Detail(mediaId, ct).ConfigureAwait(false);
        var data = Data(document);
        var lines = Lines(data);
        var selected = lines.SelectMany((line, index) => line.Episodes.Select(ep => (Line: line, Episode: ep, Index: index)))
            .FirstOrDefault(pair => pair.Episode.Id == episodeId);
        if (selected.Episode is null) throw new InvalidDataException("AppTT 选集已失效，请刷新详情。");
        string url = episodeId[(episodeId.IndexOf('\u0001') + 1)..];
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!IsMedia(url))
        {
            var remoteLines = Array(data, "vod_play_list");
            string? parser = Parser(remoteLines[selected.Index]);
            // The original falls back to the first line's parser if this flag has none.
            parser ??= Parser(remoteLines[0]);
            if (parser is null) throw new NotSupportedException("AppTT 此线路缺少可用解析器。");
            using var request = new HttpRequestMessage(HttpMethod.Get, HttpUri(parser + url));
            using var result = await Send(request, ct).ConfigureAwait(false);
            var parsed = result.RootElement;
            if (parsed.ValueKind == JsonValueKind.Object && parsed.TryGetProperty("data", out var nested) && nested.ValueKind == JsonValueKind.Object) parsed = nested;
            url = Text(parsed, "url");
            if (parsed.TryGetProperty("header", out var header))
            {
                if (header.ValueKind == JsonValueKind.String)
                {
                    using var obj = JsonDocument.Parse(header.GetString() ?? "{}");
                    ReadHeaders(obj.RootElement, headers);
                }
                else ReadHeaders(header, headers);
            }
            string agent = Text(parsed, "UA");
            if (agent.Length == 0) agent = Text(parsed, "ua");
            if (agent.Length > 0) headers["User-Agent"] = Header(agent);
        }
        HttpUri(url);
        return new()
        {
            Uri = url, SourceKey = Key, SourceName = Name, MediaId = mediaId,
            LineId = selected.Line.Id, EpisodeId = episodeId,
            Title = Text(data, "vod_name") + " · " + selected.Episode.Title, Headers = headers
        };
    }

    private static string? Parser(JsonElement line)
    {
        if (!line.TryGetProperty("parse_urls", out var parsers) || parsers.ValueKind != JsonValueKind.Array) return null;
        foreach (var value in parsers.EnumerateArray())
            if (value.ValueKind == JsonValueKind.String && value.GetString()?.Trim() is { Length: > 0 } parser && parser != "null")
                return parser; // AppTT stores the first nonempty parser per flag; no RJ signing suffix.
        return null;
    }
    private static void ReadHeaders(JsonElement value, Dictionary<string, string> target)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("AppTT 播放请求头不是对象。");
        foreach (var pair in value.EnumerateObject())
        {
            if (pair.Value.ValueKind != JsonValueKind.String || pair.Name.Length == 0 ||
                pair.Name.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')) throw new InvalidDataException("AppTT 播放请求头无效。");
            target[pair.Name] = Header(pair.Value.GetString()!);
        }
    }
    private static string Header(string value) => value.Length <= 8192 && value.IndexOfAny(['\r', '\n', '\0']) < 0
        ? value : throw new InvalidDataException("AppTT 播放请求头无效。");
    private static bool IsMedia(string url) => !url.Contains("url=http", StringComparison.Ordinal) &&
        !url.Contains(".html", StringComparison.Ordinal) && !url.Contains(".js", StringComparison.Ordinal) &&
        !url.Contains(".css", StringComparison.Ordinal) && Media().IsMatch(url);
    private static void ValidatePage(int page) { if (page < 1 || page == int.MaxValue) throw new ArgumentOutOfRangeException(nameof(page)); }
    [GeneratedRegex(@"\.(?:m3u8|mp4|flv|mp3|m4a|aac|mkv|ts)(?:[?&#]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex Media();
    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tags();
}
