using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using VodBox.Core;

namespace VodBox.Infrastructure;

/// <summary>
/// SixV（饭太硬「新6V」）原生实现：ext 是站点首页，12 个固定磁力目录分类。
/// 列表/搜索/详情都是普通 HTML；分集身份就是 magnet 链接，播放时交给 <see cref="TorrentStreamServer"/> 转成本机可 seek 的回环地址。
/// </summary>
public sealed partial class SixVSource : IResolvingContentSource
{
    /// <summary>详情磁力线路的固定标识（对应 Java <c>magnetN</c> 的单线路形态）。</summary>
    internal const string MagnetLine = "magnet";

    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; WOW64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/80.0.3987.163 Safari/537.36";
    private static readonly HttpClient DefaultHttp = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.All });
    private static readonly IReadOnlyList<Category> Categories =
    [
        new("dianshiju/guoju", "国剧"), new("dianshiju/rihanju", "日韩剧"), new("dianshiju/oumeiju", "欧美剧"),
        new("xijupian", "喜剧片"), new("dongzuopian", "动作片"), new("aiqingpian", "爱情片"), new("kehuanpian", "科幻片"),
        new("kongbupian", "恐怖片"), new("juqingpian", "剧情片"), new("zhanzhengpian", "战争片"), new("jilupian", "纪录片"),
        new("donghuapian", "动画片"),
    ];
    private readonly SourceInfo _source;
    private readonly HttpClient _http;
    private readonly Func<string, CancellationToken, Task<string>> _register;
    private readonly Uri _origin;
    private readonly string _base;
    public string Key => _source.Key;
    public string Name => _source.Name;
    public SixVSource(SourceInfo source) : this(source, DefaultHttp, TorrentStreamServer.Shared.RegisterAsync) { }
    internal SixVSource(SourceInfo source, HttpClient http, Func<string, CancellationToken, Task<string>>? register = null)
    {
        if (!Supports(source.Ext)) throw new NotSupportedException("SixV ext 必须是 HTTP(S) 站点首页。");
        _source = source; _http = http; _register = register ?? TorrentStreamServer.Shared.RegisterAsync;
        _origin = Endpoint(source.Ext);
        _base = _origin.AbsoluteUri.TrimEnd('/');
    }

    /// <summary>ext 是站点首页：既接受明文 URL，也接受 FTY v8 加密后的同一明文。</summary>
    internal static bool Supports(string? ext)
    {
        try { Endpoint(ext); return true; }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException or FormatException or ArgumentException) { return false; }
    }

    private static Uri Endpoint(string? ext)
    {
        if (string.IsNullOrWhiteSpace(ext)) throw new InvalidDataException("SixV ext 缺失，无法确定站点首页。");
        string text = ext.Trim();
        string value = Uri.TryCreate(text, UriKind.Absolute, out var direct) && direct.Scheme is "http" or "https" ? text : FtyExtCodec.JsonConfig(text);
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/")
            throw new InvalidDataException("SixV ext 必须是 HTTP(S) 站点首页。");
        return uri;
    }

    public Task<IReadOnlyList<Category>> GetCategoriesAsync(CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); return Task.FromResult(Categories); }

    /// <summary>Java 没有为磁力目录提供筛选器，不暴露空控件。</summary>
    public Task<IReadOnlyList<FilterGroup>> GetFiltersAsync(string categoryId, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); RequireCategory(categoryId); return Task.FromResult<IReadOnlyList<FilterGroup>>([]); }

    /// <summary>Java <c>homeContent</c> 只返回分类，不返回影片卡片。</summary>
    public Task<MediaPage> GetHomeAsync(CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); return Task.FromResult(new MediaPage([], 1, 1)); }

    public async Task<MediaPage> GetItemsAsync(string categoryId, int page, IReadOnlyDictionary<string, string>? filters, CancellationToken ct = default)
    {
        RequireCategory(categoryId); RequirePage(page);
        // 目录页必须带结尾斜杠：站点对 /dianshiju/guoju 返回 301，未授权重定向会被 ReadAsync 拒绝。
        string path = "/" + categoryId + "/" + (page > 1 ? "index_" + page.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".html" : "");
        var html = await ReadAsync(new(_origin, path), ct).ConfigureAwait(false);
        var items = ParseCards(html);
        // 原 Java 固定 pagecount=999；这里仅在确有条目时沿用，空页不再无限翻页。
        return new(items, page, items.Count > 0 ? 999 : page);
    }

    public async Task<MediaPage> SearchAsync(string query, int page, CancellationToken ct = default)
    {
        RequirePage(page);
        if (string.IsNullOrWhiteSpace(query) || query.Length > 256) throw new InvalidDataException("SixV 搜索词无效。");
        // 原 Java 搜索接口无翻页参数，不能把第 1 页结果重复成第 N 页。
        if (page != 1) return new([], page, page);
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["show"] = "title", ["tempid"] = "1", ["tbname"] = "article", ["mid"] = "1",
            ["dopost"] = "search", ["submit"] = "", ["keyboard"] = query.Trim(),
        });
        // 站点已把搜索入口从 /e/search/11index.php 迁到 /e/search/so.php：POST 后 302 到 result/?searchid=，
        // 结果是普通 post_hover 列表。searchid 在地址里，无需 Cookie 会话。
        var html = await PostSearchAsync(new(_origin, "/e/search/so.php"), form, ct).ConfigureAwait(false);
        return new(ParseSearchCards(html), 1, 1);
    }

    private readonly DetailCache _details = new(TimeSpan.FromMinutes(5));
    public Task<MediaDetail> GetDetailAsync(string mediaId, CancellationToken ct = default) => _details.GetAsync(mediaId, LoadDetailAsync, ct);
    private async Task<MediaDetail> LoadDetailAsync(string mediaId, CancellationToken ct)
    {
        var page = await ReadAsync(SitePage(mediaId), ct).ConfigureAwait(false);
        var heading = ArticleTitle().Match(page);
        string title = heading.Success ? Text(heading.Groups[1].Value) : Text(FirstTitle().Match(page).Groups[1].Value);
        if (title.Length == 0) throw new InvalidDataException("SixV 详情缺少标题。");
        var content = ElementBody(page, PostContentOpen());
        var episodes = new List<Episode>();
        foreach (Match anchor in Anchors().Matches(content))
        {
            var magnet = Attr(anchor.Groups["attrs"].Value, "href");
            if (!magnet.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)) continue;
            var name = Text(anchor.Groups["body"].Value);
            episodes.Add(new(MagnetLine + '\u0001' + magnet, name.Length > 0 ? name : "磁力"));
        }
        episodes = episodes.DistinctBy(e => e.Id).ToList();
        if (episodes.Count == 0) throw new InvalidDataException("SixV 详情没有磁力链接。");
        var image = Image().Match(content);
        string actor = Field(page, ActorField());
        if (actor.Length == 0) actor = Field(page, ActorAltField());
        return new()
        {
            Item = new()
            {
                Id = mediaId, Title = title,
                Poster = image.Success ? Media(Attr(image.Groups[1].Value, "src"))?.AbsoluteUri : null,
                Remarks = Field(page, RemarksField()), TypeName = Field(page, TypeField()),
                Year = Field(page, YearField()), Area = Field(page, AreaField()),
            },
            Description = Field(page, DescriptionField()),
            Actor = actor,
            Director = Field(page, DirectorField()),
            Lines = [new(MagnetLine, "磁力（边下边播）", episodes)],
        };
    }

    public async Task<PlaybackRequest> ResolvePlaybackAsync(string mediaId, string episodeId, CancellationToken ct = default)
    {
        var separator = episodeId.IndexOf('\u0001');
        var magnet = separator < 0 ? episodeId : episodeId[(separator + 1)..];
        if (!magnet.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("SixV 分集不是磁力链接。");
        // 详情页是权威来源：不能凭调用方传入的任意 magnet 解析别的影片。
        var detail = await GetDetailAsync(mediaId, ct).ConfigureAwait(false);
        var line = detail.Lines.FirstOrDefault(l => l.Episodes.Any(e => e.Id == episodeId || separator < 0 && e.Id.EndsWith('\u0001' + magnet, StringComparison.Ordinal)))
            ?? throw new InvalidDataException("SixV 磁力分集不属于当前影片。");
        var local = await _register(magnet, ct).ConfigureAwait(false);
        return new() { Uri = local, Title = detail.Item.Title, MediaId = mediaId, EpisodeId = episodeId, LineId = line.Id, SourceKey = Key, SourceName = Name };
    }

    private IReadOnlyList<MediaItem> ParseCards(string html)
    {
        var items = new List<MediaItem>();
        foreach (var block in Classes(html, "post_hover"))
        {
            var anchor = ZoomAnchor().Match(block);
            if (!anchor.Success) continue;
            var item = Card(anchor);
            if (item is null) continue;
            var remarks = RelTag().Match(block);
            items.Add(item with { Remarks = remarks.Success ? Text(remarks.Groups["body"].Value) : null });
        }
        if (items.Count == 0 && !html.Contains("post_container", StringComparison.Ordinal))
            throw new InvalidDataException("SixV 页面缺少影片列表，可能是验证页。");
        return items.DistinctBy(i => i.Id).ToArray();
    }

    private IReadOnlyList<MediaItem> ParseSearchCards(string html)
    {
        var items = new List<MediaItem>();
        foreach (Match anchor in ZoomAnchor().Matches(html))
        {
            var item = Card(anchor);
            if (item is not null) items.Add(item);
        }
        return items.DistinctBy(i => i.Id).ToArray();
    }

    private MediaItem? Card(Match anchor)
    {
        var attrs = anchor.Groups["attrs"].Value;
        string href = Attr(attrs, "href"), title = Attr(attrs, "title");
        if (title.Length == 0) title = Text(anchor.Groups["body"].Value);
        if (title.Length == 0 || !DetailPath().IsMatch(href)) return null;
        var image = Image().Match(anchor.Groups["body"].Value);
        return new() { Id = href, Title = title, Poster = image.Success ? Media(Attr(image.Groups[1].Value, "src"))?.AbsoluteUri : null };
    }

    private Uri SitePage(string mediaId)
    {
        if (mediaId.Length > 512 || !DetailPath().IsMatch(mediaId)) throw new InvalidDataException("SixV 影片标识无效。");
        return new(_origin, mediaId);
    }

    private static string Field(string html, Regex pattern)
    {
        var match = pattern.Match(html);
        return match.Success ? Text(match.Groups[1].Value) : "";
    }

    private Uri? Media(string value) =>
        value.Length > 0 && Uri.TryCreate(_origin, value, out var uri) && uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0 ? uri : null;

    private async Task<string> ReadAsync(Uri uri, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(25));
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Headers.TryAddWithoutValidation("Referer", _base + "/");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
        return await BodyAsync(response, uri, deadline.Token).ConfigureAwait(false);
    }

    private async Task<string> PostSearchAsync(Uri uri, HttpContent content, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(25));
        using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = content };
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Headers.TryAddWithoutValidation("Origin", _base);
        request.Headers.TryAddWithoutValidation("Referer", _base + "/");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
        // 关闭了自动重定向：按 302 的 Location 取结果页，而不是把重定向当成未授权跳转拒绝。
        if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            return await ReadAsync(new Uri(uri, location), deadline.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
        return Encoding.UTF8.GetString(await BoundedContent.ReadAsync(stream, 4 * 1024 * 1024, deadline.Token).ConfigureAwait(false));
    }

    private static async Task<string> BodyAsync(HttpResponseMessage response, Uri uri, CancellationToken ct)
    {
        if ((int)response.StatusCode is >= 300 and < 400 || response.RequestMessage?.RequestUri is { } final && final != uri)
            throw new InvalidDataException("SixV 页面发生未授权重定向。");
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return Encoding.UTF8.GetString(await BoundedContent.ReadAsync(stream, 4 * 1024 * 1024, ct).ConfigureAwait(false));
    }

    private static void RequireCategory(string id) { if (!Categories.Any(c => c.Id == id)) throw new InvalidDataException("SixV 分类无效。"); }
    private static void RequirePage(int page) { if (page is < 1 or > 1000) throw new InvalidDataException("SixV 页码无效。"); }

    private static Regex Rx(string expression) => HtmlRegex.Get(expression);

    private static string Text(string value) => WebUtility.HtmlDecode(Tags().Replace(value, " ")).Trim();
    private static string Attr(string text, string key) =>
        Attributes().Matches(text).FirstOrDefault(a => a.Groups["key"].Value.Equals(key, StringComparison.OrdinalIgnoreCase)) is { } found
            ? WebUtility.HtmlDecode(found.Groups["value"].Value) : "";

    /// <summary>取出 class 含指定 CSS 类的全部元素体（不依赖标签名，post_hover 可能是 li 或 div）。</summary>
    private static List<string> Classes(string html, string css)
    {
        var result = new List<string>();
        var open = Rx("<(?<tag>[a-zA-Z][a-zA-Z0-9]*)\\b[^>]*\\bclass=[\"'][^\"']*\\b" + Regex.Escape(css) + "\\b[^\"']*[\"'][^>]*>");
        var close = Rx("</?(?<tag>[a-zA-Z][a-zA-Z0-9]*)\\b[^>]*>");
        foreach (Match match in open.Matches(html))
        {
            string tag = match.Groups["tag"].Value; int start = match.Index + match.Length, depth = 1;
            foreach (Match token in close.Matches(html, start))
            {
                if (!token.Groups["tag"].Value.Equals(tag, StringComparison.OrdinalIgnoreCase)) continue;
                if (token.Value.StartsWith("</", StringComparison.Ordinal)) depth--;
                else if (token.Value.EndsWith("/>", StringComparison.Ordinal)) continue;
                else depth++;
                if (depth == 0) { result.Add(html[start..token.Index]); break; }
            }
        }
        return result;
    }

    /// <summary>按深度配对取出匹配元素的内部文本；缺失时返回空串。</summary>
    private static string ElementBody(string html, Regex open)
    {
        var match = open.Match(html); if (!match.Success) return "";
        string tag = match.Groups["tag"].Value; int start = match.Index + match.Length, depth = 1;
        foreach (Match token in CloseTags().Matches(html, start))
        {
            if (!token.Groups["tag"].Value.Equals(tag, StringComparison.OrdinalIgnoreCase)) continue;
            if (token.Value.StartsWith("</", StringComparison.Ordinal)) depth--;
            else if (token.Value.EndsWith("/>", StringComparison.Ordinal)) continue;
            else depth++;
            if (depth == 0) return html[start..token.Index];
        }
        return html[start..];
    }

    [GeneratedRegex(@"\A/[A-Za-z0-9_\-./]+\.html\z", RegexOptions.CultureInvariant, 1000)] private static partial Regex DetailPath();
    [GeneratedRegex(@"<a\b(?<attrs>[^>]*\bclass=[""'][^""']*\bzoom\b[^""']*[""'][^>]*)>(?<body>.*?)</a\s*>", RegexOptions.Singleline)] private static partial Regex ZoomAnchor();
    [GeneratedRegex(@"<a\b(?<attrs>[^>]*)>(?<body>.*?)</a\s*>", RegexOptions.Singleline)] private static partial Regex Anchors();
    [GeneratedRegex(@"<img\b([^>]*)>")] private static partial Regex Image();
    [GeneratedRegex(@"<[a-zA-Z][a-zA-Z0-9]*\b(?<attrs>[^>]*\brel=[""']category tag[""'][^>]*)>(?<body>.*?)</", RegexOptions.Singleline)] private static partial Regex RelTag();
    [GeneratedRegex(@"(?<key>[a-zA-Z.:-]+)\s*=\s*(?:""(?<value>[^""]*)""|'(?<value>[^']*)')", RegexOptions.CultureInvariant)] private static partial Regex Attributes();
    [GeneratedRegex(@"<[^>]+>", RegexOptions.CultureInvariant)] private static partial Regex Tags();
    [GeneratedRegex(@"</?(?<tag>[a-zA-Z][a-zA-Z0-9]*)\b[^>]*>", RegexOptions.CultureInvariant)] private static partial Regex CloseTags();
    [GeneratedRegex(@"<(?<tag>[a-zA-Z][a-zA-Z0-9]*)\b[^>]*\bid=[""']post_content[""'][^>]*>")] private static partial Regex PostContentOpen();
    [GeneratedRegex(@"<[a-zA-Z][a-zA-Z0-9]*\b[^>]*class=[""'][^""']*\barticle_container\b[^""']*[""'][^>]*>.*?<h1\b[^>]*>(.*?)</h1\s*>", RegexOptions.Singleline)] private static partial Regex ArticleTitle();
    [GeneratedRegex(@"<h1\b[^>]*>(.*?)</h1\s*>", RegexOptions.Singleline)] private static partial Regex FirstTitle();
    [GeneratedRegex(@"◎类\s*别\s*(.*?)<br", RegexOptions.Singleline)] private static partial Regex TypeField();
    [GeneratedRegex(@"◎年\s*代\s*(.*?)<br", RegexOptions.Singleline)] private static partial Regex YearField();
    [GeneratedRegex(@"◎产\s*地\s*(.*?)<br", RegexOptions.Singleline)] private static partial Regex AreaField();
    [GeneratedRegex(@"◎上映日期\s*(.*?)<br", RegexOptions.Singleline)] private static partial Regex RemarksField();
    [GeneratedRegex(@"◎演\s*员\s*(.*?)</p>", RegexOptions.Singleline)] private static partial Regex ActorField();
    [GeneratedRegex(@"◎主\s*演\s*(.*?)</p>", RegexOptions.Singleline)] private static partial Regex ActorAltField();
    [GeneratedRegex(@"◎导\s*演\s*(.*?)<br", RegexOptions.Singleline)] private static partial Regex DirectorField();
    [GeneratedRegex(@"◎简\s*介\s*(.*?)<hr", RegexOptions.Singleline)]
    private static partial Regex DescriptionField();
}
