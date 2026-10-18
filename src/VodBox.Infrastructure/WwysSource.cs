using System.Net;
using System.Text.RegularExpressions;
using VodBox.Core;

namespace VodBox.Infrastructure;

/// <summary>
/// 旺旺影视（宝盒 <c>csp_Wwys</c>）HTML 原生实现，按随宝盒下发的 <c>bh0822.jar</c> 反编译源码
/// （<c>com.github.catvod.spider.Wwys</c>）逐选择器重写。ext 为该站的 HTTP(S) 基地址。
/// </summary>
public sealed class WwysSource : IResolvingContentSource
{
    private const string Agent = "Mozilla/5.0 (Linux; Android 10; SM-G975F) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.120 Mobile Safari/537.36";

    /// <summary>导航解析失败时的兜底分类，取抓取时实际导航栏内容；不是凭空扩展。</summary>
    private static readonly (string Id, string Name)[] FallbackCategories =
        [("1", "电影"), ("2", "连续剧"), ("3", "综艺"), ("4", "动漫"), ("26", "短剧"), ("20", "小姐姐"), ("31", "音乐")];

    private static readonly (string Value, string Name)[] MovieTypes =
        [("1", "全部"), ("5", "动作片"), ("6", "喜剧片"), ("7", "爱情片"), ("8", "科幻片"), ("9", "恐怖片"), ("10", "剧情片"), ("11", "战争片"), ("16", "惊悚片"), ("17", "奇幻片")];

    private static readonly (string Value, string Name)[] SeriesTypes =
        [("2", "全部"), ("12", "国产剧"), ("13", "港台剧"), ("14", "日韩剧"), ("15", "欧美剧")];

    private readonly SourceInfo _site;
    private readonly string _base;
    private readonly Func<string, CancellationToken, Task<string>> _fetch;

    public string Key => _site.Key;
    public string Name => _site.Name;

    public WwysSource(SourceInfo site) : this(site, null) { }

    internal WwysSource(SourceInfo site, Func<string, CancellationToken, Task<string>>? fetch)
    {
        _site = site;
        if (!Supports(site.Ext)) throw new NotSupportedException("Wwys ext 不是有效的 HTTP(S) 基地址。");
        _base = site.Ext!.Trim().TrimEnd('/');
        _fetch = fetch ?? Fetch;
    }

    /// <summary>ext 必须是纯 HTTP(S) 基地址（无查询/片段/用户信息），否则不接管该定义。</summary>
    internal static bool Supports(string? ext)
    {
        if (!Uri.TryCreate(ext?.Trim(), UriKind.Absolute, out var uri)) return false;
        return uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0 && uri.Query.Length == 0 &&
            uri.Fragment.Length == 0 && uri.AbsolutePath.Trim('/').Length == 0;
    }

    private static async Task<string> Fetch(string url, CancellationToken ct) =>
        DefaultHttp.Decode(await DefaultHttp.Shared.GetBoundedAsync(url, 4 * 1024 * 1024, ct,
            new Dictionary<string, string> { ["User-Agent"] = Agent }));

    private static Regex Pattern(string pattern) => HtmlRegex.Get(pattern);

    /// <summary>去标签后解码实体，保留空白原样（对应插件的元素文本）。</summary>
    private static string RawText(string html) => WebUtility.HtmlDecode(Pattern("<[^>]+>").Replace(html, ""));

    /// <summary>去标签、解码实体并把连续空白折叠为单空格（对应 .j() 的 normaliseWhitespace）。</summary>
    private static string Text(string html) => Pattern("\\s+").Replace(RawText(html), " ").Trim();

    private static string Attribute(string html, string name) =>
        WebUtility.HtmlDecode(Pattern("\\b" + Regex.Escape(name) + "\\s*=\\s*([\"'])(.*?)\\1").Match(html).Groups[2].Value);

    private static string Group(string html, string pattern) => Pattern(pattern).Match(html).Groups[1].Value;

    /// <summary>指定片段内所有 <c>&lt;a&gt;</c> 的文本拼成一行（对应 <c>d.j()</c> 的多元素连接）。</summary>
    private static string Links(string html) =>
        string.Join(" ", Pattern("<a\\b[^>]*>(.*?)</a>").Matches(html).Select(link => Text(link.Groups[1].Value)).Where(text => text.Length > 0));

    /// <summary>抓取页内的媒体卡片：只认 <c>/vod-detail-id-N.html</c> 且带 <c>div.pic</c> 的条目，排除导航与轮播。</summary>
    private static List<MediaItem> Cards(string html)
    {
        var items = new List<MediaItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match block in Pattern("<li\\b[^>]*>(.*?)</li>").Matches(html))
        {
            var body = block.Groups[1].Value;
            if (!body.Contains("class=\"pic\"", StringComparison.OrdinalIgnoreCase)) continue;
            var href = Pattern("<a\\b[^>]*href=\"([^\"]+)\"").Match(body).Groups[1].Value;
            var id = Group(href, "/vod-detail-id-(\\d+)\\.html");
            if (id.Length == 0 || !seen.Add(id)) continue;
            var img = Pattern("<img\\b[^>]*>").Match(body).Value;
            var poster = Attribute(img, "data-src");
            if (poster.Length == 0) poster = Attribute(img, "src");
            var remarks = Group(body, "<span class=\"sBottom\">(.*?)</span>");
            items.Add(new MediaItem { Id = id, Title = Attribute(body, "title"), Poster = poster, Remarks = Text(remarks) });
        }
        return items;
    }

    public async Task<IReadOnlyList<Category>> GetCategoriesAsync(CancellationToken ct = default)
    {
        var html = await _fetch(_base + "/", ct).ConfigureAwait(false);
        var nav = Group(html, "<div[^>]*id=\"topnav\"[^>]*>(.*?)</ul>");
        var found = new List<Category>();
        foreach (Match link in Pattern("<li\\b[^>]*>\\s*<a\\b[^>]*href=\"([^\"]+)\"[^>]*>(.*?)</a>").Matches(nav))
        {
            var parts = link.Groups[1].Value.Split('-');
            if (parts.Length < 4 || parts[3].Length == 0) continue;
            var name = Text(link.Groups[2].Value);
            if (name.Length == 0 || found.Any(c => c.Id == parts[3])) continue;
            found.Add(new Category(parts[3], name));
        }
        return found.Count > 0 ? found : FallbackCategories.Select(c => new Category(c.Id, c.Name)).ToArray();
    }

    public Task<IReadOnlyList<FilterGroup>> GetFiltersAsync(string categoryId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        // 原插件把类型筛选挂在 cateId 上，选中值直接替换列表接口的 id；这里保持同一语义。
        var values = categoryId switch
        {
            "1" => MovieTypes,
            "2" => SeriesTypes,
            _ => [],
        };
        IReadOnlyList<FilterGroup> groups = values.Length == 0
            ? []
            : [new FilterGroup("cateId", "类型", values.Select(v => new FilterValue(v.Name, v.Value)).ToList(), "")];
        return Task.FromResult(groups);
    }

    public async Task<MediaPage> GetHomeAsync(CancellationToken ct = default)
    {
        var html = await _fetch(_base + "/", ct).ConfigureAwait(false);
        return new MediaPage(Cards(html), 1, 1);
    }

    public async Task<MediaPage> GetItemsAsync(string categoryId, int page, IReadOnlyDictionary<string, string>? filters, CancellationToken ct = default)
    {
        if (page < 1) throw new InvalidDataException("页码无效。");
        var id = filters is not null && filters.TryGetValue("cateId", out var overrideId) && overrideId.Length > 0 ? overrideId : categoryId;
        if (id.Length == 0 || !id.All(char.IsAsciiDigit)) throw new InvalidDataException("Wwys 分类无效。");
        var html = await _fetch($"{_base}/vod-list-id-{id}-pg-{page}-order--by-time-class-0-year-0-letter--area--lang-.html", ct).ConfigureAwait(false);
        var items = Cards(html);
        var pages = Group(html, "pagego\\('[^']*',\\s*(\\d+)\\s*\\)");
        var count = int.TryParse(pages, out var total) && total >= page ? total : page;
        return new MediaPage(items, page, count);
    }

    public async Task<MediaPage> SearchAsync(string query, int page, CancellationToken ct = default)
    {
        if (page < 1) throw new InvalidDataException("页码无效。");
        if (page > 1) return new MediaPage([], page, page);
        var html = await _fetch(_base + "/index.php?m=vod-search&wd=" + Uri.EscapeDataString(query), ct).ConfigureAwait(false);
        var items = new List<MediaItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match block in Pattern("<li\\b[^>]*>(.*?)</li>").Matches(html))
        {
            var body = block.Groups[1].Value;
            var href = Pattern("<a\\b[^>]*href=\"([^\"]+)\"").Match(body).Groups[1].Value;
            var id = Group(href, "/vod-detail-id-(\\d+)\\.html");
            if (id.Length == 0 || !seen.Add(id)) continue;
            var img = Pattern("<img\\b[^>]*>").Match(body).Value;
            var poster = Attribute(img, "data-src");
            if (poster.Length == 0) poster = Attribute(img, "src");
            items.Add(new MediaItem
            {
                Id = id,
                Title = Text(Group(body, "<span[^>]*>(.*?)</span>")),
                Poster = poster,
                Remarks = Text(LastSpan(body)),
            });
        }
        return new MediaPage(items, 1, 1);
    }

    /// <summary>搜索结果第三段文本（原插件 <c>div:nth-child(2) &gt; span:nth-child(3)</c>）取该 div 里的第 3 个 span。</summary>
    private static string LastSpan(string body)
    {
        var spans = Pattern("<span\\b[^>]*>(.*?)</span>").Matches(body);
        return spans.Count >= 3 ? spans[2].Groups[1].Value : "";
    }

    public async Task<MediaDetail> GetDetailAsync(string mediaId, CancellationToken ct = default)
    {
        if (mediaId.Length == 0 || !mediaId.All(char.IsAsciiDigit)) throw new InvalidDataException("Wwys 影片编号无效。");
        var html = await _fetch($"{_base}/vod-detail-id-{mediaId}.html", ct).ConfigureAwait(false);
        var titleTag = Pattern("<h1\\b[^>]*class=\"title\"[^>]*>\\s*<a\\b[^>]*>").Match(html).Value;
        var title = Attribute(titleTag, "title");
        if (title.Length == 0) title = Text(Group(html, "<h1\\b[^>]*class=\"title\"[^>]*>(.*?)</h1>"));
        var descItems = Pattern("<div\\b[^>]*class=\"desc_item\"[^>]*>(.*?)</div>").Matches(html);
        var remarks = descItems.Count >= 1 ? Text(Group(descItems[0].Groups[1].Value, "<font\\b[^>]*>(.*?)</font>")) : "";
        var actor = descItems.Count >= 2 ? Links(descItems[1].Groups[1].Value) : "";
        var director = descItems.Count >= 3 ? Links(descItems[2].Groups[1].Value) : "";
        var poster = Attribute(Pattern("<section\\b[^>]*class=\"page-hd\"[^>]*>.*?<img\\b[^>]*>").Match(html).Value, "src");
        var description = Text(Group(html, "<article\\b[^>]*class=\"detail-con\"[^>]*>.*?<p\\b[^>]*>(.*?)</p>")).Replace("简介：", "");

        var lines = ParseLines(html, mediaId);
        var item = new MediaItem { Id = mediaId, Title = title, Poster = poster, Remarks = remarks };
        return new MediaDetail { Item = item, Description = description, Actor = actor, Director = director, Lines = lines };
    }

    /// <summary>详情页 <c>div.numList</c> 分线路；id 含 m3u8 记为「量子有广」，否则「高清无广」，与插件同名。</summary>
    private static List<PlaybackLine> ParseLines(string html, string mediaId)
    {
        var tags = Pattern("<div\\b[^>]*class=\"numList\"[^>]*>").Matches(html);
        var lines = new List<PlaybackLine>();
        var byName = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < tags.Count; i++)
        {
            var start = tags[i].Index + tags[i].Length;
            var end = i + 1 < tags.Count ? tags[i + 1].Index : html.Length;
            var name = Attribute(tags[i].Value, "id").Contains("m3u8", StringComparison.OrdinalIgnoreCase) ? "量子有广" : "高清无广";
            var episodes = new List<Episode>();
            foreach (Match link in Pattern("<li\\b[^>]*>\\s*<a\\b[^>]*href=\"([^\"]+)\"[^>]*>(.*?)</a>").Matches(html[start..end]))
            {
                var href = link.Groups[1].Value;
                if (href.Length == 0) continue;
                episodes.Add(new Episode(href, Text(link.Groups[2].Value)));
            }
            if (episodes.Count == 0) continue;
            episodes.Reverse();
            // 同名线路按插件 LinkedHashMap 语义覆盖前一条。
            if (byName.TryGetValue(name, out var index)) lines[index] = new PlaybackLine(name, name, episodes);
            else { byName[name] = lines.Count; lines.Add(new PlaybackLine(name, name, episodes)); }
        }
        return lines;
    }

    public async Task<PlaybackRequest> ResolvePlaybackAsync(string mediaId, string episodeId, CancellationToken ct = default)
    {
        if (episodeId.Length == 0 || !episodeId.StartsWith('/')) throw new InvalidDataException("Wwys 播放标识无效。");
        var page = await _fetch(_base + episodeId, ct).ConfigureAwait(false);
        var from = Group(page, "mac_from='(.*?)'");
        var urls = Group(page, "mac_url='(.*?)';");
        var src = ParseIndex(episodeId, "vod-play-id-\\d+-src-(\\d+)-num-\\d+");
        var num = ParseIndex(episodeId, "vod-play-id-\\d+-src-\\d+-num-(\\d+)");
        if (from.Length == 0 || urls.Length == 0 || src <= 0 || num <= 0) throw new InvalidDataException("Wwys 播放页缺少 mac_from/mac_url。");
        var lines = from.Split("$$$");
        var lineUrls = urls.Split("$$$");
        if (src > lines.Length || src > lineUrls.Length) throw new InvalidDataException("Wwys 播放线路越界。");
        var episodes = lineUrls[src - 1].Split('#');
        if (num > episodes.Length) throw new InvalidDataException("Wwys 剧集越界。");
        var parts = episodes[num - 1].Split('$');
        if (parts.Length < 2 || parts[1].Length == 0) throw new InvalidDataException("Wwys 剧集地址无效。");

        var target = parts[1];
        var resolved = await ResolveThroughPlayerAsync(lines[src - 1], target, ct).ConfigureAwait(false);
        return new PlaybackRequest
        {
            Uri = resolved ?? target,
            SourceKey = Key,
            SourceName = Name,
            MediaId = mediaId,
            EpisodeId = episodeId,
            Title = Name,
            Headers = new() { ["User-Agent"] = Agent },
        };
    }

    /// <summary>
    /// 原插件播放链路：取 <c>/player/{line}.js</c> 里的 <c>src="…'</c> 前缀拼上直链，再请求该播放器页取
    /// <c>"url":"…"</c>。链路上任何一步失败都回退到 mac_url 里的直链，避免播放器脚本变动导致整站不可播。
    /// </summary>
    private async Task<string?> ResolveThroughPlayerAsync(string line, string target, CancellationToken ct)
    {
        if (line.Length == 0) return null;
        try
        {
            var script = await _fetch($"{_base}/player/{line}.js", ct).ConfigureAwait(false);
            var prefix = Group(script, "src=\"(.*?)'");
            if (prefix.Length == 0) return null;
            var request = prefix + target;
            if (!Uri.TryCreate(request, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return null;
            var player = await _fetch(request, ct).ConfigureAwait(false);
            var value = Group(player, "\"url\":\\s*\"([^\"]+)\"");
            if (value.Length == 0) value = Group(player, "url\\s*=\\s*'([^']*)'");
            if (value.Length == 0) value = Group(player, "\\$]\\(\"#myVideo\"\\)\\.attr\\(\"src\",\"(.*?)\"\\)");
            return value.Length == 0 ? null : value;
        }
        catch (Exception error) when (error is HttpRequestException or InvalidDataException or IOException or TaskCanceledException or UriFormatException or RegexMatchTimeoutException)
        {
            return null;
        }
    }

    private static int ParseIndex(string value, string pattern)
    {
        var match = Pattern(pattern).Match(value);
        return match.Success && int.TryParse(match.Groups[1].Value, out var index) ? index : 0;
    }
}
