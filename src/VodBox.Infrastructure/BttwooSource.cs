using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using VodBox.Core;

namespace VodBox.Infrastructure;

/// <summary>
/// 两个BT（bttwoo）原生爬虫：列表/筛选/搜索解析页面 HTML，播放地址按站点前端的签名规则请求 <c>/video/play</c>。
/// 签名：<c>s = HMAC-SHA256(key=v, "{dataid}:{t}:{v}")</c> 十六进制前 32 位；<c>k</c> 为页面 userlink 与固定密钥循环异或后的 Base64。
/// 片段伪装成 PNG，需经本机媒体代理剥离前缀（<see cref="PlaybackRequest.ProxyMedia"/>）。
/// </summary>
public sealed partial class BttwooSource : IResolvingContentSource
{
    private const string DefaultHost = "https://www.bttwoo.com";
    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36";
    private static readonly byte[] LinkKey = "nbmovie2024secretkey"u8.ToArray();
    private static readonly string[] FilterKeys = ["areas", "types", "years", "tags"];
    private static readonly Dictionary<string, string> FilterNames = new() { ["areas"] = "地区", ["types"] = "类型", ["years"] = "年份", ["tags"] = "标签" };
    private readonly SourceInfo _site;
    private readonly string _host;
    private readonly Func<string, string?, CancellationToken, Task<string>> _get;
    private readonly Func<long> _now;
    private IReadOnlyList<FilterGroup>? _filters;
    public string Key => _site.Key;
    public string Name => _site.Name;

    public BttwooSource(SourceInfo site) : this(site, null, null) { }
    internal BttwooSource(SourceInfo site, Func<string, string?, CancellationToken, Task<string>>? get, Func<long>? now)
    {
        _site = site;
        _host = Uri.TryCreate(site.Ext, UriKind.Absolute, out var ext) && ext.Scheme is "http" or "https" ? ext.GetLeftPart(UriPartial.Authority) : DefaultHost;
        _get = get ?? Get;
        _now = now ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    private static async Task<string> Get(string url, string? referer, CancellationToken ct)
    {
        var headers = new Dictionary<string, string> { ["User-Agent"] = UserAgent };
        if (referer is not null) headers["Referer"] = referer;
        return DefaultHttp.Decode(await DefaultHttp.Shared.GetBoundedAsync(url, 4 * 1024 * 1024, ct, headers));
    }

    private async Task<string> Request(string pathAndQuery, CancellationToken ct, string? referer = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        return await _get(_host + pathAndQuery, referer, timeout.Token);
    }

    public Task<IReadOnlyList<Category>> GetCategoriesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Category>>([new("1", "电影"), new("2", "电视剧"), new("3", "动漫"), new("4", "综艺")]);

    public async Task<IReadOnlyList<FilterGroup>> GetFiltersAsync(string categoryId, CancellationToken ct = default)
    {
        if (_filters is { } cached) return cached;
        var html = await Request("/filter", ct);
        var groups = new List<FilterGroup>();
        foreach (var key in FilterKeys)
        {
            var values = FilterLink().Matches(html).Where(match => match.Groups[1].Value == key)
                .Select(match => new FilterValue(Text(match.Groups[3].Value), match.Groups[2].Value))
                .Where(value => value.Name.Length > 0).DistinctBy(value => value.Value).ToList();
            if (values.Count > 0) groups.Add(new FilterGroup(key, FilterNames[key], [new FilterValue("全部", ""), .. values], ""));
        }
        return _filters = groups;
    }

    public async Task<MediaPage> GetHomeAsync(CancellationToken ct = default)
    {
        var items = ParseCards(await Request("/", ct));
        if (items.Count == 0) throw new InvalidDataException("两个BT首页没有影片。");
        return new MediaPage(items, 1, 1);
    }

    public async Task<MediaPage> GetItemsAsync(string categoryId, int page, IReadOnlyDictionary<string, string>? filters, CancellationToken ct = default)
    {
        if (categoryId is not ("1" or "2" or "3" or "4") || page < 1) throw new InvalidDataException("两个BT分类或页码无效。");
        var query = new StringBuilder("/filter?classify=").Append(categoryId);
        foreach (var key in FilterKeys)
            if (filters is not null && filters.TryGetValue(key, out var value) && value.Length > 0)
            {
                if (!value.All(char.IsAsciiDigit)) throw new InvalidDataException("两个BT筛选值无效。");
                query.Append('&').Append(key).Append('=').Append(value);
            }
        if (page > 1) query.Append("&page=").Append(page);
        var html = await Request(query.ToString(), ct);
        return Page(html, page);
    }

    public async Task<MediaPage> SearchAsync(string query, int page, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query) || page < 1) throw new InvalidDataException("两个BT搜索词或页码无效。");
        var html = await Request("/search?q=" + Uri.EscapeDataString(query.Trim()) + (page > 1 ? "&page=" + page : ""), ct);
        return Page(html, page);
    }

    private static MediaPage Page(string html, int page)
    {
        var items = ParseCards(html);
        var hasNext = html.Contains($"page={page + 1}\"", StringComparison.Ordinal);
        return new MediaPage(items, page, hasNext ? page + 1 : page);
    }

    internal static IReadOnlyList<MediaItem> ParseCards(string html) =>
        Card().Matches(html).Select(match => new MediaItem
        {
            Id = match.Groups[1].Value,
            Poster = WebUtility.HtmlDecode(match.Groups[2].Value),
            Title = Text(match.Groups[3].Value),
        }).Where(item => item.Title.Length > 0).DistinctBy(item => item.Id).ToArray();

    public async Task<MediaDetail> GetDetailAsync(string mediaId, CancellationToken ct = default)
    {
        if (!IsVideoId(mediaId)) throw new InvalidDataException("两个BT影片编号无效。");
        return ParseDetail(mediaId, await Request("/play/" + mediaId, ct));
    }

    internal static MediaDetail ParseDetail(string mediaId, string html)
    {
        var title = Text(Heading().Match(html).Groups[1].Value);
        if (title.Length == 0) throw new InvalidDataException("两个BT详情页缺少片名。");
        var lines = EpisodeLink().Matches(html)
            .Select(match => (Line: match.Groups[2].Value, Number: int.Parse(match.Groups[3].Value), Episode: new Episode(
                match.Groups[4].Value + "|" + match.Groups[1].Value,
                EpisodeLabel(match.Groups[5].Value, match.Groups[3].Value))))
            .DistinctBy(item => item.Episode.Id)
            .GroupBy(item => item.Line)
            .Select(group => new PlaybackLine("line" + group.Key, "线路" + group.Key, group.OrderBy(item => item.Number).Select(item => item.Episode).ToArray()))
            .ToArray();
        if (lines.Length == 0) throw new InvalidDataException("两个BT详情页没有可播放剧集。");
        string? Field(string label) => InfoField().Matches(html).FirstOrDefault(match => Text(match.Groups[1].Value) == label) is { } match ? Text(match.Groups[2].Value) : null;
        return new MediaDetail
        {
            Item = new MediaItem
            {
                Id = mediaId, Title = title,
                Poster = Meta(html, "og:image"), Area = Field("地区"), TypeName = Field("类型"),
                Year = Keywords().Match(html) is { Success: true } year ? year.Groups[1].Value : null,
            },
            Description = Meta(html, "og:description") ?? "",
            Director = Field("导演"), Actor = Field("主演"),
            Lines = lines,
        };
    }

    public async Task<PlaybackRequest> ResolvePlaybackAsync(string mediaId, string episodeId, CancellationToken ct = default)
    {
        var parts = episodeId.Split('|');
        if (parts.Length != 2 || parts[0].Length == 0 || !parts[0].All(char.IsAsciiDigit) || !IsVideoId(parts[1]) || !IsVideoId(mediaId))
            throw new InvalidDataException("两个BT剧集编号无效。");
        var (dataId, videoId) = (parts[0], parts[1]);
        var page = await Request("/play/" + videoId, ct);
        var userLink = UserLink().Match(page).Groups[1].Value;
        var path = BuildPlayPath(dataId, videoId, "1080", userLink, _now());
        using var document = JsonDocument.Parse(await Request(path, ct, _host + "/play/" + videoId));
        var root = document.RootElement;
        if (!root.TryGetProperty("code", out var code) || code.ValueKind != JsonValueKind.Number || code.GetInt32() != 200 ||
            !root.TryGetProperty("data", out var data) || !data.TryGetProperty("quality_urls", out var qualities) || qualities.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("两个BT播放接口失败：" + (root.TryGetProperty("message", out var message) ? message.GetString() : "格式无效"));
        var choices = qualities.EnumerateArray()
            .Where(item => !(item.TryGetProperty("locked", out var locked) && locked.ValueKind == JsonValueKind.True))
            .Select(item => new PlaybackChoice(
                (AppRjSource.Text(item, "title") + " " + AppRjSource.Text(item, "description")).Trim(), AppRjSource.Text(item, "url")))
            .Where(choice => Uri.TryCreate(choice.Uri, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            .ToArray();
        if (choices.Length == 0) throw new InvalidDataException("两个BT没有可用清晰度（可能仅会员可看）。");
        var detail = ParseDetail(mediaId, page);
        var line = detail.Lines.FirstOrDefault(item => item.Episodes.Any(episode => episode.Id == episodeId));
        return new PlaybackRequest
        {
            Uri = choices[0].Uri, Choices = choices.Length > 1 ? choices : [],
            Title = detail.Item.Title, Poster = detail.Item.Poster, SourceKey = Key, SourceName = Name,
            MediaId = mediaId, LineId = line?.Id ?? "", EpisodeId = episodeId, ProxyMedia = true,
        };
    }

    /// <summary>与站点前端 build_play_url 相同的签名路径（q 为 1080/720/480 或 "1" 表示 4K）。</summary>
    internal static string BuildPlayPath(string dataId, string videoId, string quality, string userLink, long timestamp)
    {
        var t = timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var signature = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(videoId), Encoding.UTF8.GetBytes($"{dataId}:{t}:{videoId}")))[..32];
        string key;
        if (userLink is "" or "0") key = "0";
        else
        {
            var bytes = Encoding.UTF8.GetBytes(userLink);
            for (var index = 0; index < bytes.Length; index++) bytes[index] ^= LinkKey[index % LinkKey.Length];
            key = Uri.EscapeDataString(Convert.ToBase64String(bytes));
        }
        return $"/video/play?p={dataId}&v={videoId}&q={quality}&s={signature}&t={t}&k={key}";
    }

    private static bool IsVideoId(string value) => value.Length is > 0 and <= 32 && value.All(char.IsAsciiLetterOrDigit);
    private static string EpisodeLabel(string inner, string number)
    {
        var label = Text(EpisodeSpan().Match(inner).Groups[1].Value);
        return label.Length == 0 || label.All(char.IsAsciiDigit) ? "第" + (label.Length == 0 ? number : label) + "集" : label;
    }
    private static string? Meta(string html, string property) =>
        Regex.Match(html, "<meta property=\"" + Regex.Escape(property) + "\" content=\"([^\"]*)\"") is { Success: true } match && match.Groups[1].Value.Length > 0
            ? WebUtility.HtmlDecode(match.Groups[1].Value) : null;
    private static string Text(string html) => WebUtility.HtmlDecode(Tag().Replace(html, "")).Trim();

    [GeneratedRegex("<a[^>]*href=\"\\?(areas|types|years|tags)=(\\d+)\"[^>]*>\\s*([^<\\s][^<]{0,29}?)\\s*<")] private static partial Regex FilterLink();
    [GeneratedRegex("<a href=\"/play/([a-z0-9]+)\"[^>]*>\\s*<div[^>]*>\\s*<img data-src=\"([^\"]+)\"[^>]*alt=\"([^\"]*)\"")] private static partial Regex Card();
    [GeneratedRegex("<h1[^>]*>(.*?)</h1>", RegexOptions.Singleline)] private static partial Regex Heading();
    [GeneratedRegex("<a href=\"/play/([a-z0-9]+)\"[^>]*data-line=\"(\\d+)\" data-episode=\"(\\d+)\" dataid=\"(\\d+)\"[^>]*>(.*?)</a>", RegexOptions.Singleline)] private static partial Regex EpisodeLink();
    [GeneratedRegex("<span x-show=\"!isEpisodeActive[^\"]*\">(.*?)</span>", RegexOptions.Singleline)] private static partial Regex EpisodeSpan();
    [GeneratedRegex("<div class=\"col-span-1 text-gray-500\">([^<]*)</div>\\s*<div class=\"col-span-2 text-gray-300\">(.*?)</div>", RegexOptions.Singleline)] private static partial Regex InfoField();
    [GeneratedRegex("<meta name=\"keywords\" content=\"[^\"]*?,((?:19|20)\\d{2}),")] private static partial Regex Keywords();
    [GeneratedRegex("userlink:'([A-Za-z0-9+/=]*)'")] private static partial Regex UserLink();
    [GeneratedRegex("<[^>]+>")] private static partial Regex Tag();
}
