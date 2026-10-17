using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using VodBox.Core;

namespace VodBox.Infrastructure;

/// <summary>
/// The FTY T4 spider's JSON/type=1 branch. Despite its name, this is not a TVBox type4 endpoint.
/// Keeps encrypted configuration, category restrictions and optional MacCMS web-search discovery.
/// </summary>
public sealed partial class FtyT4Source : IResolvingContentSource
{
    private static readonly DefaultHttp SharedHttp = new();
    private readonly SourceInfo _site;
    private readonly Configuration _config;
    private readonly Func<string, IReadOnlyDictionary<string, string>, CancellationToken, Task<string>> _fetch;
    private readonly SemaphoreSlim _requests = new(4, 4);

    public string Key => _site.Key;
    public string Name => _site.Name;
    public string? ParseEndpoint => _config.PlayUrl.Length > 0 ? _config.PlayUrl : _site.PlayUrl;

    internal sealed record Configuration(Uri Endpoint, IReadOnlyList<string> Categories,
        IReadOnlyList<string> RemovedCategories, Uri? SearchHome, string PlayUrl);

    public FtyT4Source(SourceInfo site) : this(site, (url, headers, ct) => Fetch(SharedHttp, url, headers, ct)) { }

    internal FtyT4Source(SourceInfo site, Func<string, IReadOnlyDictionary<string, string>, CancellationToken, Task<string>> fetch)
    {
        _site = site;
        _config = ParseConfig(site.Ext);
        _fetch = fetch;
    }

    private static async Task<string> Fetch(DefaultHttp http, string url, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
        => DefaultHttp.Decode(await http.GetBoundedAsync(url, 8 * 1024 * 1024, ct, headers).ConfigureAwait(false));

    internal static bool Supports(string? ext)
    {
        try { ParseConfig(ext); return true; }
        catch (Exception error) when (error is InvalidDataException or JsonException or NotSupportedException) { return false; }
    }

    internal static Configuration ParseConfig(string? ext)
    {
        using var document = JsonDocument.Parse(FtyExtCodec.JsonConfig(ext));
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type) ||
            type.ValueKind != JsonValueKind.Number || !type.TryGetInt32(out var number) || number != 1)
            throw new NotSupportedException("FTY T4 当前只支持已验证的 JSON type=1 配置。");
        var endpoint = HttpUri(Text(root, "siteUrl"));
        var categories = Strings(root, "categories");
        var removed = Strings(root, "rmCategories");
        string mode = Text(root, "searchMode");
        Uri? search = null;
        if (mode.Length > 0)
        {
            if (!mode.StartsWith("web&", StringComparison.Ordinal))
                throw new NotSupportedException("FTY T4 searchMode 尚未支持。");
            search = HttpUri(mode[4..]);
            if (search.Query.Length != 0) throw new InvalidDataException("FTY T4 搜索主页不能包含查询参数。");
            search = new Uri(search.AbsoluteUri.TrimEnd('/') + "/");
        }
        string playUrl = Text(root, "playUrl");
        if (playUrl.Length > 0) HttpUri(playUrl);
        return new(endpoint, categories, removed, search, playUrl);
    }

    public async Task<IReadOnlyList<Category>> GetCategoriesAsync(CancellationToken ct = default)
    {
        var response = await RequestApi([], ct).ConfigureAwait(false);
        return response.Classes.Where(category => category.TypeId.Length > 0 && Allowed(category.TypeName))
            .DistinctBy(category => category.TypeId).Select(category => new Category(category.TypeId, category.TypeName)).ToArray();
    }

    public async Task<MediaPage> GetHomeAsync(CancellationToken ct = default)
        => Page(await RequestApi([], ct).ConfigureAwait(false), 1);

    public async Task<MediaPage> GetItemsAsync(string categoryId, int page, IReadOnlyDictionary<string, string>? filters, CancellationToken ct = default)
    {
        ValidatePage(page);
        if (filters is { Count: > 0 }) throw new NotSupportedException("FTY T4 type=1 不支持筛选参数。");
        return Page(await RequestApi(new() { ["ac"] = "detail", ["t"] = Id(categoryId), ["pg"] = Number(page) }, ct).ConfigureAwait(false), page);
    }

    public async Task<MediaDetail> GetDetailAsync(string mediaId, CancellationToken ct = default)
        => MacCmsSource.ToDetail(await Vod(mediaId, ct).ConfigureAwait(false));

    private async Task<MacCmsVod> Vod(string mediaId, CancellationToken ct)
    {
        var response = await RequestApi(new() { ["ac"] = "detail", ["ids"] = Id(mediaId) }, ct).ConfigureAwait(false);
        return response.List.FirstOrDefault(vod => vod.VodId == mediaId)
            ?? throw new InvalidDataException("FTY T4 详情中没有所请求的视频。");
    }

    public async Task<MediaPage> SearchAsync(string query, int page, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ValidatePage(page);
        if (query.Length > 256) throw new InvalidDataException("搜索关键词过长。");
        if (string.IsNullOrWhiteSpace(query)) return new([], page, page);
        if (_config.SearchHome is not { } home)
        {
            var result = await RequestApi(new() { ["ac"] = "detail", ["wd"] = query, ["pg"] = Number(page) }, ct).ConfigureAwait(false);
            return Page(result, page, query);
        }

        // The native spider discovers ids from the web page, then hydrates them using the API.
        // Keep this second request: a title/link on the HTML page is not a playable detail.
        string path = "index.php/vod/search/page/" + Number(page) + "/wd/" + Uri.EscapeDataString(query) + ".html";
        string html = await Request(new Uri(home, path), ct).ConfigureAwait(false);
        if (html.Contains("系统安全验证", StringComparison.Ordinal) || html.Contains("验证码", StringComparison.Ordinal))
            throw new NotSupportedException("FTY T4 搜索站要求验证码，当前原生适配无法完成此挑战。");
        var ids = new List<string>();
        foreach (Match anchor in Anchor().Matches(html))
        {
            string href = WebUtility.HtmlDecode(anchor.Groups["href"].Value);
            string title = WebUtility.HtmlDecode(Tags().Replace(anchor.Groups["body"].Value, ""));
            if (!title.Contains(query, StringComparison.Ordinal) || !Uri.TryCreate(home, href, out var link) || !SameOrigin(home, link)) continue;
            var match = DetailId().Match(link.AbsolutePath);
            if (match.Success && match.Groups[1].Length <= 128 && match.Groups[1].Value.All(char.IsAsciiDigit) &&
                !ids.Contains(match.Groups[1].Value, StringComparer.Ordinal)) ids.Add(match.Groups[1].Value);
            if (ids.Count >= 100) break;
        }
        if (ids.Count == 0) return new([], page, page);
        return Page(await RequestApi(new() { ["ac"] = "detail", ["ids"] = string.Join(',', ids) }, ct).ConfigureAwait(false), page, query);
    }

    public async Task<PlaybackRequest> ResolvePlaybackAsync(string mediaId, string episodeId, CancellationToken ct = default)
    {
        // As with existing MacCMS sources, episode identity is its URL, preserving saved history.
        var detail = MacCmsSource.ToDetail(await Vod(mediaId, ct).ConfigureAwait(false));
        var line = detail.Lines.FirstOrDefault(candidate => candidate.Episodes.Any(episode => episode.Id == episodeId));
        var episode = line?.Episodes.FirstOrDefault(candidate => candidate.Id == episodeId)
            ?? throw new InvalidDataException("FTY T4 所选分集已失效，请刷新详情。");
        var target = HttpUri(episode.Uri ?? episode.Id);
        string extension = Path.GetExtension(target.AbsolutePath);
        bool direct = extension.Equals(".m3u8", StringComparison.OrdinalIgnoreCase) || extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".flv", StringComparison.OrdinalIgnoreCase) || extension.Equals(".mkv", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".ts", StringComparison.OrdinalIgnoreCase);
        return new PlaybackRequest
        {
            Uri = target.AbsoluteUri, Resolution = direct && _config.PlayUrl.Length == 0 ? ResolutionKind.Direct : ResolutionKind.Json,
            ParseEndpoint = ParseEndpoint, Title = detail.Item.Title + " · " + episode.Title,
            SourceKey = Key, SourceName = Name, MediaId = mediaId, LineId = line!.Id, EpisodeId = episodeId,
            Headers = new() { ["User-Agent"] = UserAgent }
        };
    }

    private bool Allowed(string name) =>
        (_config.Categories.Count == 0 || _config.Categories.Contains(name, StringComparer.Ordinal)) &&
        !_config.RemovedCategories.Contains(name, StringComparer.Ordinal);

    private async Task<MacCmsListResponse> RequestApi(Dictionary<string, string> parameters, CancellationToken ct)
    {
        var uri = _config.Endpoint;
        if (parameters.Count > 0)
        {
            var controlled = new HashSet<string>(["ac", "t", "pg", "ids", "wd"], StringComparer.OrdinalIgnoreCase);
            var retained = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Where(pair => !controlled.Contains(Uri.UnescapeDataString(pair.Split('=', 2)[0].Replace("+", " "))));
            uri = new UriBuilder(uri)
            {
                Query = string.Join('&', retained.Concat(parameters.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value))))
            }.Uri;
        }
        string json = await Request(uri, ct).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("list", out var list) || list.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("FTY T4 响应缺少视频列表。");
        if (list.GetArrayLength() > 1000) throw new InvalidDataException("FTY T4 视频列表过大。");
        return JsonSerializer.Deserialize(json, Json.TypeInfo<MacCmsListResponse>())
            ?? throw new InvalidDataException("FTY T4 响应无效。");
    }

    private string UserAgent => _site.Header?.FirstOrDefault(pair => pair.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase)).Value
        ?? DefaultHttp.BrowserUserAgent;

    private async Task<string> Request(Uri address, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        await _requests.WaitAsync(timeout.Token).ConfigureAwait(false);
        try
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["User-Agent"] = UserAgent };
            // A configured search host can differ from the API. Do not forward API cookies there.
            if (SameOrigin(_config.Endpoint, address) && _site.Header is not null)
                foreach (var pair in _site.Header) headers[pair.Key] = pair.Value;
            string result = await _fetch(address.AbsoluteUri, headers, timeout.Token).ConfigureAwait(false);
            timeout.Token.ThrowIfCancellationRequested();
            if (result.Length > 8 * 1024 * 1024) throw new InvalidDataException("FTY T4 响应过大。");
            return result;
        }
        finally { _requests.Release(); }
    }

    private static MediaPage Page(MacCmsListResponse response, int page, string? query = null)
    {
        var items = response.List.Where(vod => vod.VodId.Length > 0 && (query is null || vod.VodName.Contains(query, StringComparison.Ordinal)))
            .DistinctBy(vod => vod.VodId).Select(MacCmsSource.ToItem).ToArray();
        // Native search responses may omit pagecount. Offer one next page until an empty result.
        int count = items.Length == 0 ? page : response.PageCount > 1 ? Math.Max(page, response.PageCount) : Math.Min(10000, page + 1);
        return new(items, page, count);
    }

    private static IReadOnlyList<string> Strings(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var value)) return [];
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 500)
            throw new InvalidDataException("FTY T4 分类配置无效。");
        var result = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || item.GetString() is not { Length: > 0 and <= 256 } text)
                throw new InvalidDataException("FTY T4 分类名称无效。");
            if (!result.Contains(text, StringComparer.Ordinal)) result.Add(text);
        }
        return result;
    }

    private static string Text(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value)) return "";
        if (value.ValueKind != JsonValueKind.String) throw new InvalidDataException("FTY T4 配置字段 " + name + " 必须是字符串。");
        return value.GetString() ?? "";
    }

    private static Uri HttpUri(string value)
    {
        if (value.Length is 0 or > 16384 || value.IndexOfAny(['\r', '\n', '\0']) >= 0 ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0)
            throw new InvalidDataException("FTY T4 地址必须是有效的 HTTP(S) 地址。");
        return uri;
    }

    private static string Id(string value) => value.Length is > 0 and <= 128 && value.All(char.IsAsciiDigit)
        ? value : throw new InvalidDataException("FTY T4 视频/分类标识必须是数字。");
    private static bool SameOrigin(Uri left, Uri right) => left.Scheme == right.Scheme && left.IdnHost == right.IdnHost && left.Port == right.Port;
    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
    private static void ValidatePage(int page) { if (page is < 1 or > 10000) throw new ArgumentOutOfRangeException(nameof(page)); }

    [GeneratedRegex("<a\\b[^>]*\\bhref\\s*=\\s*[\"'](?<href>[^\"']+)[\"'][^>]*>(?<body>.*?)</a\\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline, 2000)]
    private static partial Regex Anchor();
    [GeneratedRegex(@"^/index\.php/vod/detail/id/(\d+)\.html$", RegexOptions.IgnoreCase)]
    private static partial Regex DetailId();
    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tags();
}
