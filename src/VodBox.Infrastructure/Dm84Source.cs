using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using VodBox.Core;

namespace VodBox.Infrastructure;

/// <summary>
/// 动漫巴士（dm84）原生爬虫：MacCMS 模板站。列表 <c>/show-{分类}--{排序}-{类型}--{年份}-{页}.html</c>，
/// 搜索 <c>/s-{词}---------{页}.html</c>；播放页内嵌 hhplayer 解析器，向其 <c>/api/parse</c>
/// 提交页面引导参数（<c>client_fallback</c> 让服务端直接给出可播地址，而不是要求浏览器端优酷解析）。
/// </summary>
public sealed partial class Dm84Source : IResolvingContentSource
{
    private const string DefaultHost = "https://dm84.net";
    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36";
    private readonly SourceInfo _site;
    private readonly string _host;
    private readonly Func<string, string?, string?, CancellationToken, Task<string>> _send;
    private readonly Dictionary<string, IReadOnlyList<FilterGroup>> _filters = new(StringComparer.Ordinal);
    public string Key => _site.Key;
    public string Name => _site.Name;

    public Dm84Source(SourceInfo site) : this(site, null) { }
    /// <param name="send">(url, referer, jsonBody 为 null 时 GET) → 响应文本。</param>
    internal Dm84Source(SourceInfo site, Func<string, string?, string?, CancellationToken, Task<string>>? send)
    {
        _site = site;
        _host = Uri.TryCreate(site.Ext, UriKind.Absolute, out var ext) && ext.Scheme is "http" or "https" ? ext.GetLeftPart(UriPartial.Authority) : DefaultHost;
        _send = send ?? Send;
    }

    private static async Task<string> Send(string url, string? referer, string? json, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(json is null ? HttpMethod.Get : HttpMethod.Post, url);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        if (referer is not null) request.Headers.TryAddWithoutValidation("Referer", referer);
        if (json is not null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await DefaultHttp.PlainClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > 4 * 1024 * 1024) throw new InvalidDataException("动漫巴士响应过大。");
        return await response.Content.ReadAsStringAsync(ct);
    }

    private Task<string> Page(string path, CancellationToken ct) => _send(_host + path, _host + "/", null, ct);

    public Task<IReadOnlyList<Category>> GetCategoriesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Category>>([new("1", "国产动漫"), new("2", "日本动漫"), new("3", "欧美动漫"), new("4", "动漫电影")]);

    public async Task<IReadOnlyList<FilterGroup>> GetFiltersAsync(string categoryId, CancellationToken ct = default)
    {
        RequireCategory(categoryId);
        if (_filters.TryGetValue(categoryId, out var cached)) return cached;
        var html = await Page($"/list-{categoryId}.html", ct);
        var links = ShowLink().Matches(html).Select(match => (Parts: WebUtility.UrlDecode(match.Groups[1].Value).Split('-'), Label: Text(match.Groups[2].Value)))
            .Where(link => link.Parts.Length >= 6 && link.Parts[0] == categoryId).ToArray();
        FilterGroup? Group(string key, string name, int position) =>
            links.Where(link => link.Parts[position].Length > 0 && link.Label.Length > 0)
                .Select(link => new FilterValue(link.Label, link.Parts[position])).DistinctBy(value => value.Value).ToList() is { Count: > 0 } values
                ? new FilterGroup(key, name, key == "by" ? values : [new FilterValue("全部", ""), .. values], key == "by" ? values[0].Value : "") : null;
        IReadOnlyList<FilterGroup> groups = new[] { Group("class", "类型", 3), Group("year", "年份", 5), Group("by", "排序", 2) }.OfType<FilterGroup>().ToArray();
        return _filters[categoryId] = groups;
    }

    public async Task<MediaPage> GetHomeAsync(CancellationToken ct = default)
    {
        var items = ParseCards(await Page("/", ct));
        if (items.Count == 0) throw new InvalidDataException("动漫巴士首页没有影片。");
        return new MediaPage(items, 1, 1);
    }

    public async Task<MediaPage> GetItemsAsync(string categoryId, int page, IReadOnlyDictionary<string, string>? filters, CancellationToken ct = default)
    {
        RequireCategory(categoryId);
        if (page < 1) throw new InvalidDataException("动漫巴士页码无效。");
        string Value(string key)
        {
            var value = filters is not null && filters.TryGetValue(key, out var found) ? found : "";
            if (value.Any(c => c is '-' or '/' or '?' or '#' or '.' || char.IsControl(c))) throw new InvalidDataException("动漫巴士筛选值无效。");
            return Uri.EscapeDataString(value);
        }
        var html = await Page($"/show-{categoryId}--{Value("by")}-{Value("class")}--{Value("year")}-{(page > 1 ? page : "")}.html", ct);
        return Paged(html, page, "-" + (page + 1) + ".html\"");
    }

    public async Task<MediaPage> SearchAsync(string query, int page, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query) || page < 1) throw new InvalidDataException("动漫巴士搜索词或页码无效。");
        var html = await Page($"/s-{Uri.EscapeDataString(query.Trim())}---------{(page > 1 ? page : "")}.html", ct);
        return Paged(html, page, "---------" + (page + 1) + ".html\"");
    }

    private static MediaPage Paged(string html, int page, string nextMarker)
    {
        var items = ParseCards(html);
        return new MediaPage(items, page, html.Contains(nextMarker, StringComparison.Ordinal) ? page + 1 : page);
    }

    internal static IReadOnlyList<MediaItem> ParseCards(string html) =>
        Card().Matches(html).Select(match => new MediaItem
        {
            Id = match.Groups[1].Value,
            Poster = WebUtility.HtmlDecode(match.Groups[2].Value),
            Title = Text(match.Groups[3].Value),
            Remarks = Text(match.Groups[4].Value) is { Length: > 0 } remarks ? remarks : null,
        }).Where(item => item.Title.Length > 0).DistinctBy(item => item.Id).ToArray();

    public async Task<MediaDetail> GetDetailAsync(string mediaId, CancellationToken ct = default)
    {
        if (!IsNumber(mediaId)) throw new InvalidDataException("动漫巴士影片编号无效。");
        return ParseDetail(mediaId, await Page($"/v/{mediaId}.html", ct));
    }

    internal static MediaDetail ParseDetail(string mediaId, string html)
    {
        var title = Text(Heading().Match(html).Groups[1].Value);
        if (title.Length == 0) throw new InvalidDataException("动漫巴士详情页缺少片名。");
        var names = LineName().Matches(Section(html, "play_from")).Select(match => Text(match.Groups[1].Value)).ToArray();
        var lines = PlayList().Matches(html).Select((list, index) =>
        {
            var episodes = EpisodeLink().Matches(list.Groups[1].Value)
                .Where(match => match.Groups[1].Value == mediaId)
                .Select(match => (Number: int.Parse(match.Groups[3].Value), Episode: new Episode(
                    $"{match.Groups[1].Value}-{match.Groups[2].Value}-{match.Groups[3].Value}", EpisodeTitle(Text(match.Groups[4].Value)))))
                .DistinctBy(item => item.Episode.Id).OrderBy(item => item.Number).Select(item => item.Episode).ToArray();
            return new PlaybackLine("line" + (index + 1), index < names.Length && names[index].Length > 0 ? names[index] : "线路" + (index + 1), episodes);
        }).Where(line => line.Episodes.Count > 0).ToArray();
        if (lines.Length == 0) throw new InvalidDataException("动漫巴士详情页没有可播放剧集。");
        var intro = Paragraph().Matches(Intro().Match(html).Groups[1].Value).Select(match => Text(match.Groups[1].Value)).Where(text => text.Length > 0).ToArray();
        string? Labeled(string label) => intro.FirstOrDefault(text => text.StartsWith(label + "：", StringComparison.Ordinal)) is { } found ? found[(label.Length + 1)..].Trim() : null;
        var plain = intro.Where(text => !LabeledParagraph().IsMatch(text)).ToArray();
        var summary = Summary().Match(html);
        var facts = summary.Success ? summary.Groups[1].Value.Split("<em class=\"hr\">|</em>").Select(Text).ToArray() : [];
        return new MediaDetail
        {
            Item = new MediaItem
            {
                Id = mediaId, Title = title,
                Poster = Cover().Match(html) is { Success: true } cover ? WebUtility.HtmlDecode(cover.Groups[1].Value) : null,
                Remarks = facts.ElementAtOrDefault(0), Year = facts.ElementAtOrDefault(1), Area = facts.ElementAtOrDefault(2), TypeName = facts.ElementAtOrDefault(3),
            },
            Description = Labeled("剧情") ?? string.Join("\n", plain),
            Director = Labeled("导演"), Actor = Labeled("主演"),
            Lines = lines,
        };
    }

    public async Task<PlaybackRequest> ResolvePlaybackAsync(string mediaId, string episodeId, CancellationToken ct = default)
    {
        var parts = episodeId.Split('-');
        if (!IsNumber(mediaId) || parts.Length != 3 || parts[0] != mediaId || !parts.All(IsNumber)) throw new InvalidDataException("动漫巴士剧集编号无效。");
        var page = await Page($"/p/{episodeId}.html", ct);
        var frame = Frame().Match(page).Groups[1].Value;
        if (!Uri.TryCreate(WebUtility.HtmlDecode(frame), UriKind.Absolute, out var player) || player.Scheme is not ("http" or "https"))
            throw new InvalidDataException("动漫巴士播放页没有解析器地址。");
        var uri = await ResolvePlayerAsync(player, ct);
        var title = Text(Heading().Match(page).Groups[1].Value);
        return new PlaybackRequest
        {
            Uri = uri, Title = title.Length > 0 ? title : Name, SourceKey = Key, SourceName = Name,
            MediaId = mediaId, LineId = "line" + parts[1], EpisodeId = episodeId,
        };
    }

    private async Task<string> ResolvePlayerAsync(Uri player, CancellationToken ct)
    {
        // 解析器以 url= 直接给出媒体地址时无需再请求。
        var direct = System.Web.HttpUtility.ParseQueryString(player.Query)["url"];
        if (Uri.TryCreate(direct, UriKind.Absolute, out var media) && media.Scheme is "http" or "https") return media.AbsoluteUri;
        var html = await _send(player.AbsoluteUri, _host + "/", null, ct);
        var bootstrap = Bootstrap().Match(html);
        if (!bootstrap.Success) throw new InvalidDataException("动漫巴士解析器页面格式无法识别。");
        using var boot = JsonDocument.Parse(bootstrap.Groups[1].Value);
        var root = boot.RootElement;
        var body = JsonSerializer.Serialize(new ParseRequest(Str(root, "url"), root.GetProperty("t").GetInt64(), Str(root, "key"), true), Dm84Json.Default.ParseRequest);
        var origin = player.GetLeftPart(UriPartial.Authority);
        using var result = JsonDocument.Parse(await _send(origin + "/api/parse", player.AbsoluteUri, body, ct));
        var data = result.RootElement;
        var url = Str(data, "url");
        if (!data.TryGetProperty("code", out var code) || code.GetInt32() != 200 || url.Length == 0) throw new InvalidDataException("动漫巴士解析失败。");
        if (Str(data, "ext") == "hls_rewrite")
            url = $"{origin}/getts?url={Uri.EscapeDataString(url)}&t={root.GetProperty("t").GetInt64()}&key={Uri.EscapeDataString(Str(root, "ts_key"))}";
        else if (Str(data, "ext").Length > 0) throw new InvalidDataException("动漫巴士该线路需要浏览器端解析，暂不支持。");
        return Uri.TryCreate(url, UriKind.Absolute, out var final) && final.Scheme is "http" or "https" ? final.AbsoluteUri : throw new InvalidDataException("动漫巴士解析地址无效。");
    }

    internal sealed record ParseRequest(
        [property: System.Text.Json.Serialization.JsonPropertyName("url")] string Url,
        [property: System.Text.Json.Serialization.JsonPropertyName("t")] long T,
        [property: System.Text.Json.Serialization.JsonPropertyName("key")] string Key,
        [property: System.Text.Json.Serialization.JsonPropertyName("client_fallback")] bool ClientFallback);

    private static string Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static void RequireCategory(string id) { if (id is not ("1" or "2" or "3" or "4")) throw new InvalidDataException("动漫巴士分类无效。"); }
    private static bool IsNumber(string value) => value.Length is > 0 and <= 10 && value.All(char.IsAsciiDigit);
    private static string EpisodeTitle(string label) => label.Length > 0 && label.All(char.IsAsciiDigit) ? "第" + label + "集" : label;
    private static string Section(string html, string className)
    {
        var start = html.IndexOf(className, StringComparison.Ordinal);
        if (start < 0) return "";
        var end = html.IndexOf("</ul>", start, StringComparison.Ordinal);
        return end < 0 ? "" : html[start..end];
    }
    private static string Text(string html) => WebUtility.HtmlDecode(Tag().Replace(html, "")).Trim();

    [GeneratedRegex("<a[^>]*href=\"/show-([^\"]+)\\.html\"[^>]*>(.*?)</a>")] private static partial Regex ShowLink();
    [GeneratedRegex("<a href=\"/v/(\\d+)\\.html\" class=\"cover[^\"]*\" data-bg=\"([^\"]*)\"[^>]*>.*?</a>\\s*<a class=\"title\"[^>]*title=\"([^\"]*)\"[^>]*>.*?</a>\\s*(?:<span class=\"desc\">([^<]*)</span>)?", RegexOptions.Singleline)] private static partial Regex Card();
    [GeneratedRegex("<h1[^>]*>(.*?)</h1>", RegexOptions.Singleline)] private static partial Regex Heading();
    [GeneratedRegex("<li[^>]*>(.*?)</li>", RegexOptions.Singleline)] private static partial Regex LineName();
    [GeneratedRegex("<ul class=\"play_list[^\"]*\">(.*?)</ul>", RegexOptions.Singleline)] private static partial Regex PlayList();
    [GeneratedRegex("<a href=\"/p/(\\d+)-(\\d+)-(\\d+)\\.html\"[^>]*>(.*?)</a>", RegexOptions.Singleline)] private static partial Regex EpisodeLink();
    [GeneratedRegex("<p class=\"v_desc\">(.*?)</p>", RegexOptions.Singleline)] private static partial Regex Summary();
    [GeneratedRegex("<div class=\"cover\"><img src=\"([^\"]+)\"")] private static partial Regex Cover();
    [GeneratedRegex("<div id=\"intro\">(.*?)<div class=\"show_more\"", RegexOptions.Singleline)] private static partial Regex Intro();
    [GeneratedRegex("<p[^>]*>(.*?)</p>", RegexOptions.Singleline)] private static partial Regex Paragraph();
    [GeneratedRegex("^(导演|主演|又名|编剧|别名)：")] private static partial Regex LabeledParagraph();
    [GeneratedRegex("<iframe src=\"([^\"]+)\"")] private static partial Regex Frame();
    [GeneratedRegex("__HHJX_BOOTSTRAP__=(\\{.*?\\});", RegexOptions.Singleline)] private static partial Regex Bootstrap();
    [GeneratedRegex("<[^>]+>")] private static partial Regex Tag();
}

[System.Text.Json.Serialization.JsonSerializable(typeof(Dm84Source.ParseRequest))]
internal sealed partial class Dm84Json : System.Text.Json.Serialization.JsonSerializerContext;
