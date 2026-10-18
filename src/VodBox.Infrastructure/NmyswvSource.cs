using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using VodBox.Core;

namespace VodBox.Infrastructure;

/// <summary>Nmyswv's static MacCMS data and aF codec. Browser-only routes fail explicitly.</summary>
public sealed partial class NmyswvSource : IResolvingContentSource
{
    private const string UserAgent = "Mozilla/5.0 (Linux; Android 12) AppleWebKit/537.36 (KHTML, like Gecko) Version/4.0 Chrome/97.0.4692.98 Mobile Safari/537.36";
    private static readonly HttpClient DefaultHttp = new(new SocketsHttpHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All, UseCookies = false });
    private static readonly IReadOnlyList<Category> Categories = [new("1", "电影"), new("2", "电视剧"), new("3", "综艺"), new("4", "动漫"), new("26", "短剧")];
    private readonly SourceInfo _source;
    private readonly HttpClient _http;
    private readonly Uri _origin;
    public string Key => _source.Key;
    public string Name => _source.Name;
    public NmyswvSource(SourceInfo source) : this(source, DefaultHttp) { }
    public NmyswvSource(SourceInfo source, HttpClient http)
    {
        _source = source; _http = http;
        _origin = Endpoint(source.Ext);
    }
    private static Uri Endpoint(string? ext)
    {
        var address = string.IsNullOrWhiteSpace(ext) ? "https://vip.wwgz.cn:5200/" : ext.Trim();
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/")
            throw new InvalidDataException("Nmyswv ext 必须是 HTTP(S) 站点首页。");
        return uri;
    }
    internal static bool Supports(string? ext)
    { try { Endpoint(ext); return true; } catch (InvalidDataException) { return false; } }
    public Task<IReadOnlyList<Category>> GetCategoriesAsync(CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); return Task.FromResult(Categories); }
    public Task<IReadOnlyList<FilterGroup>> GetFiltersAsync(string categoryId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); RequireCategory(categoryId);
        List<FilterValue> types = categoryId switch
        {
            "1" => [new("全部", "1"), new("动作片", "5"), new("喜剧片", "6"), new("爱情片", "7"), new("科幻片", "8"), new("恐怖片", "9"), new("剧情片", "10"), new("战争片", "11"), new("惊悚片", "16"), new("奇幻片", "17")],
            "2" => [new("全部", "2"), new("国产剧", "12"), new("港台剧", "13"), new("日韩剧", "14"), new("欧美剧", "15")],
            "4" => [new("全部", "4"), new("动漫剧", "18"), new("动漫片", "19")],
            _ => [new("全部", categoryId)]
        };
        return Task.FromResult<IReadOnlyList<FilterGroup>>([
            new("tid", "类型", types, categoryId),
            new("year", "年份", [new("全部", ""), .. Enumerable.Range(2010, 17).Reverse().Select(y => new FilterValue(y.ToString(CultureInfo.InvariantCulture), y.ToString(CultureInfo.InvariantCulture)))], ""),
            new("area", "地区", [new("全部", ""), .. new[] { "大陆", "香港", "台湾", "米国", "韩国", "日本", "泰国", "新加坡", "马来西亚", "印度", "英国", "法国", "加拿大" }.Select(a => new FilterValue(a, a))], ""),
            new("by", "排序", [new("全部", ""), new("时间", "time"), new("人气", "hits"), new("评分", "score")], "")]);
    }
    public async Task<MediaPage> GetHomeAsync(CancellationToken ct = default)
    {
        var html = await ReadAsync(_origin, ct).ConfigureAwait(false);
        return new(ParseCards(html), 1, 1);
    }
    public async Task<MediaPage> GetItemsAsync(string categoryId, int page, IReadOnlyDictionary<string, string>? filters, CancellationToken ct = default)
    {
        RequireCategory(categoryId); RequirePage(page);
        string Value(string key)
        {
            var value = filters is not null && filters.TryGetValue(key, out var found) ? found : "";
            if (value.Length > 64 || value.Any(c => !char.IsLetterOrDigit(c))) throw new InvalidDataException("Nmyswv 筛选值无效。");
            return value;
        }
        var tid = Value("tid"); if (tid.Length == 0) tid = categoryId;
        var allowed = (await GetFiltersAsync(categoryId, ct).ConfigureAwait(false))[0].Values;
        if (!allowed.Any(v => v.Value == tid)) throw new InvalidDataException("Nmyswv 子分类无效。");
        string path = $"/index.php?m=vod-list-id-{tid}-pg-{page}-order--by-{Value("by")}-class--year-{Value("year")}-letter--area-{Uri.EscapeDataString(Value("area"))}-lang-.html";
        var html = await ReadAsync(new(_origin, path), ct).ConfigureAwait(false);
        return new(ParseCards(html), page, HasNext(html, page) ? page + 1 : page);
    }
    public async Task<MediaPage> SearchAsync(string query, int page, CancellationToken ct = default)
    {
        RequirePage(page);
        if (string.IsNullOrWhiteSpace(query) || query.Length > 256) throw new InvalidDataException("Nmyswv 搜索词无效。");
        // Java exposes no paged search endpoint. Do not replay page 1 under later page numbers.
        if (page != 1) return new([], page, page);
        var html = await ReadAsync(new(_origin, "/index.php?m=vod-search&wd=" + Uri.EscapeDataString(query.Trim())), ct).ConfigureAwait(false);
        return new(ParseCards(html), page, page);
    }
    private readonly DetailCache _details = new(TimeSpan.FromMinutes(5));
    public Task<MediaDetail> GetDetailAsync(string mediaId, CancellationToken ct = default) => _details.GetAsync(mediaId, LoadDetailAsync, ct);
    private async Task<MediaDetail> LoadDetailAsync(string mediaId, CancellationToken ct)
    {
        var uri = SitePage(mediaId);
        var html = await ReadAsync(uri, ct).ConfigureAwait(false);
        var title = Text(Title().Match(html).Groups[1].Value);
        if (title.Length == 0) throw new InvalidDataException("Nmyswv 详情缺少标题。");
        var lines = new List<PlaybackLine>();
        int sourceIndex = 0;
        foreach (Match block in NumList().Matches(html))
        {
            sourceIndex++;
            // Original Java uses attr("div.id"), not a CSS selector or attr("id").
            var flag = Attr(block.Groups["attrs"].Value, "div.id");
            if (!flag.Contains("m3u8", StringComparison.Ordinal)) flag = "线路" + sourceIndex;
            if (flag.Contains('\u0001')) throw new InvalidDataException("Nmyswv 线路标识无效。");
            var episodes = Anchors().Matches(block.Groups["body"].Value).Select(a => (Path: Attr(a.Groups["attrs"].Value, "href"), Title: Text(a.Groups["body"].Value)))
                .Where(a => PlayPath().IsMatch(a.Path)).Select(a => new Episode(flag + '\u0001' + a.Path, a.Title)).DistinctBy(e => e.Id).Reverse().ToArray();
            if (episodes.Length == 0) continue;
            // Validate source and show id rather than accepting arbitrary page links.
            foreach (var item in episodes) SitePage(item.Id[(item.Id.IndexOf('\u0001') + 1)..]);
            lines.Add(new(flag, flag, episodes));
        }
        if (lines.Count == 0) throw new InvalidDataException("Nmyswv 详情没有静态分集列表。");
        var cover = Cover().Match(html);
        // 原插件：section.page-bd > div.desc_item:nth-child(2) > font 为备注，nth-child(5) > a 里取四位年份。
        var facts = PageBodyChildren(html);
        string Fact(int child, string tag) => facts.Count >= child && facts[child - 1].Class.Contains("desc_item", StringComparison.Ordinal)
            ? Text(Rx("<" + tag + "\\b[^>]*>(.*?)</" + tag + "\\s*>").Match(facts[child - 1].Body).Groups[1].Value) : "";
        var year = Rx("(\\d{4})").Match(Fact(5, "a"));
        return new() { Item = new() { Id = mediaId, Title = title, Poster = cover.Success ? Media(Attr(cover.Groups[1].Value, "src"), uri)?.AbsoluteUri : null,
            Remarks = Fact(2, "font"), Year = year.Success ? year.Groups[1].Value : null },
            Description = Text(Description().Match(html).Groups[1].Value), Lines = lines };
    }
    public async Task<PlaybackRequest> ResolvePlaybackAsync(string mediaId, string episodeId, CancellationToken ct = default)
    {
        SitePage(mediaId);
        var separator = episodeId.IndexOf('\u0001');
        var path = separator < 0 ? episodeId : episodeId[(separator + 1)..];
        var episode = PlayPath().Match(path);
        if (!episode.Success) throw new InvalidDataException("Nmyswv 分集编号无效。");
        // The detail page is authoritative: callers cannot resolve another show's arbitrary episode.
        var detail = await GetDetailAsync(mediaId, ct).ConfigureAwait(false);
        var line = detail.Lines.FirstOrDefault(l => l.Episodes.Any(e => e.Id == episodeId || separator < 0 && e.Id.EndsWith('\u0001' + path, StringComparison.Ordinal))) ?? throw new InvalidDataException("Nmyswv 分集不属于当前影片。");
        var page = SitePage(path);
        var html = await ReadAsync(page, ct).ConfigureAwait(false);
        var url = DecodePlayback(html, path);
        return new() { Uri = url, Title = detail.Item.Title, SourceKey = Key, SourceName = Name, MediaId = mediaId, EpisodeId = episodeId, LineId = line.Id,
            Headers = new() { ["User-Agent"] = UserAgent } };
    }
    /// <summary>section.page-bd 的直接子元素（按出现顺序，供 nth-child 定位）。</summary>
    private static List<(string Class, string Body)> PageBodyChildren(string html)
    {
        var result = new List<(string, string)>();
        var open = Rx("<section\\b[^>]*class=[\"'][^\"']*\\bpage-bd\\b[^\"']*[\"'][^>]*>").Match(html);
        if (!open.Success) return result;
        int depth = 0, start = 0; string cls = "";
        foreach (Match token in Rx("<(/?)([a-zA-Z][a-zA-Z0-9]*)\\b([^>]*)>").Matches(html, open.Index + open.Length))
        {
            string tag = token.Groups[2].Value.ToLowerInvariant();
            if (tag is "br" or "img" or "hr" or "input" or "meta" or "link" || token.Value.EndsWith("/>", StringComparison.Ordinal)) continue;
            if (token.Groups[1].Value.Length == 0)
            {
                if (depth == 0) { start = token.Index + token.Length; cls = Attr(token.Groups[3].Value, "class"); }
                depth++;
            }
            else
            {
                if (depth == 0) break; // </section>
                if (--depth == 0) result.Add((cls, html[start..token.Index]));
            }
        }
        return result;
    }
    private static Regex Rx(string pattern) => HtmlRegex.Get(pattern);
    internal static string DecodePlayback(string html, string episodeId)
    {
        var separator = episodeId.IndexOf('\u0001');
        var match = PlayPath().Match(separator < 0 ? episodeId : episodeId[(separator + 1)..]);
        if (!match.Success || !int.TryParse(match.Groups["source"].Value, out var source) || !int.TryParse(match.Groups["number"].Value, out var number) || source < 1 || number < 1)
            throw new InvalidDataException("Nmyswv 分集坐标无效。");
        var script = MacUrl().Match(html);
        if (!script.Success) throw new InvalidDataException("Nmyswv 播放页缺少 mac_url 静态数据。");
        var sources = script.Groups[1].Value.Split("$$$", StringSplitOptions.None);
        if (source > sources.Length) throw new InvalidDataException("Nmyswv 播放线路越界。");
        var episodes = sources[source - 1].Split('#');
        if (number > episodes.Length) throw new InvalidDataException("Nmyswv 播放分集越界。");
        var entry = episodes[number - 1]; var addressSeparator = entry.IndexOf('$');
        if (addressSeparator < 0) throw new InvalidDataException("Nmyswv 分集数据无地址。");
        var value = WebUtility.HtmlDecode(entry[(addressSeparator + 1)..]);
        if (value.StartsWith("aF", StringComparison.Ordinal))
        {
            if (value.Length < 68) throw new InvalidDataException("Nmyswv aF 编码数据过短。");
            var prefix = new string(Enumerable.Range(0, 34).Select(i => value[i * 2]).ToArray());
            var encoded = prefix + value[67..].Replace("oo00o", "/", StringComparison.Ordinal).Replace("o000o", "+", StringComparison.Ordinal).Replace("O0O0O", "=", StringComparison.Ordinal);
            try { value = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(encoded)); }
            catch (Exception ex) when (ex is FormatException or DecoderFallbackException) { throw new InvalidDataException("Nmyswv aF 编码数据无效。", ex); }
        }
        // Java passes unknown routes to parse=1 (browser sniffing); never pretend that page is media.
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0 ||
            !(uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) || uri.AbsolutePath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)))
            throw new NotSupportedException("Nmyswv 当前线路需要浏览器嗅探；仅支持已还原的 HLS/MP4 媒体地址。");
        return uri.AbsoluteUri;
    }
    private IReadOnlyList<MediaItem> ParseCards(string html)
    {
        var items = new List<MediaItem>();
        foreach (Match li in ListItem().Matches(html))
        {
            var anchor = Anchors().Matches(li.Groups[1].Value).FirstOrDefault(a => DetailPath().IsMatch(Attr(a.Groups["attrs"].Value, "href")));
            if (anchor is null) continue;
            var path = Attr(anchor.Groups["attrs"].Value, "href"); SitePage(path);
            var title = Attr(anchor.Groups["attrs"].Value, "title");
            if (title.Length == 0) title = Text(SearchTitle().Match(li.Groups[1].Value).Groups[1].Value);
            if (title.Length == 0) continue;
            var image = Image().Match(li.Groups[1].Value); var attrs = image.Groups[1].Value;
            var picture = Attr(attrs, "data-src"); if (picture.Length == 0) picture = Attr(attrs, "src");
            items.Add(new() { Id = path, Title = title, Poster = Media(picture, _origin)?.AbsoluteUri, Remarks = Text(Remarks().Match(li.Groups[1].Value).Groups[1].Value) });
        }
        if (items.Count == 0 && !html.Contains("data_list", StringComparison.Ordinal) && !html.Contains("resize_list", StringComparison.Ordinal) && !html.Contains("globalPicList", StringComparison.Ordinal))
            throw new InvalidDataException("Nmyswv 页面缺少影片列表，可能是验证页。");
        return items.DistinctBy(i => i.Id).ToArray();
    }
    private async Task<string> ReadAsync(Uri uri, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(25));
        using var request = new HttpRequestMessage(HttpMethod.Get, uri); request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
        if ((int)response.StatusCode is >= 300 and < 400 || response.RequestMessage?.RequestUri is { } final && final != uri)
            throw new InvalidDataException("Nmyswv 页面发生未授权重定向。");
        response.EnsureSuccessStatusCode(); await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
        return new UTF8Encoding(false, true).GetString(await BoundedContent.ReadAsync(stream, 4 * 1024 * 1024, deadline.Token).ConfigureAwait(false));
    }
    private Uri SitePage(string path)
    {
        if (path.Length > 512 || !(DetailPath().IsMatch(path) || PlayPath().IsMatch(path))) throw new InvalidDataException("Nmyswv 页面编号无效。");
        return new(_origin, path);
    }
    private static Uri? Media(string value, Uri page) => value.Length > 0 && Uri.TryCreate(page, value, out var uri) && uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0 ? uri : null;
    private static bool HasNext(string html, int page) => html.Contains("-pg-" + (page + 1) + "-", StringComparison.Ordinal);
    private static void RequireCategory(string id) { if (!Categories.Any(c => c.Id == id)) throw new InvalidDataException("Nmyswv 分类无效。"); }
    private static void RequirePage(int page) { if (page is < 1 or > 1000) throw new InvalidDataException("Nmyswv 页码无效。"); }
    private static string Text(string value) => WebUtility.HtmlDecode(Tags().Replace(value, "")).Trim();
    private static string Attr(string text, string key) => Attributes().Matches(text).FirstOrDefault(a => a.Groups["key"].Value.Equals(key, StringComparison.OrdinalIgnoreCase)) is { } found ? WebUtility.HtmlDecode(found.Groups["value"].Value) : "";
    [GeneratedRegex(@"\A/index\.php\?m=vod-detail-id-\d+\.html\z", RegexOptions.CultureInvariant, 1000)] private static partial Regex DetailPath();
    [GeneratedRegex(@"\A/index\.php\?m=vod-play-id-\d+-src-(?<source>\d+)-num-(?<number>\d+)\.html\z", RegexOptions.CultureInvariant, 1000)] private static partial Regex PlayPath();
    [GeneratedRegex("<li\\b[^>]*>(.*?)</li\\s*>", RegexOptions.Singleline | RegexOptions.IgnoreCase, 1000)] private static partial Regex ListItem();
    [GeneratedRegex("<a\\b(?<attrs>[^>]*)>(?<body>.*?)</a\\s*>", RegexOptions.Singleline | RegexOptions.IgnoreCase, 1000)] private static partial Regex Anchors();
    [GeneratedRegex("<img\\b([^>]*)>", RegexOptions.IgnoreCase, 1000)] private static partial Regex Image();
    [GeneratedRegex("(?<key>[a-zA-Z.-]+)\\s*=\\s*(?:\"(?<value>[^\"]*)\"|'(?<value>[^']*)')", RegexOptions.CultureInvariant, 1000)] private static partial Regex Attributes();
    [GeneratedRegex("<span\\b[^>]*class=[\"']sTit[\"'][^>]*>(.*?)</span>", RegexOptions.Singleline | RegexOptions.IgnoreCase, 1000)] private static partial Regex SearchTitle();
    [GeneratedRegex("<span\\b[^>]*class=[\"'](?:vod_remarks|sBottom)[\"'][^>]*>(.*?)</span>", RegexOptions.Singleline | RegexOptions.IgnoreCase, 1000)] private static partial Regex Remarks();
    [GeneratedRegex("<h1\\b[^>]*class=[\"']title[\"'][^>]*>(.*?)</h1>", RegexOptions.Singleline | RegexOptions.IgnoreCase, 1000)] private static partial Regex Title();
    [GeneratedRegex("<section\\b[^>]*class=[\"']page-hd[\"'][^>]*>.*?<img\\b([^>]*)>", RegexOptions.Singleline | RegexOptions.IgnoreCase, 1000)] private static partial Regex Cover();
    [GeneratedRegex("<article\\b[^>]*class=[\"']detail-con[\"'][^>]*>(.*?)</article>", RegexOptions.Singleline | RegexOptions.IgnoreCase, 1000)] private static partial Regex Description();
    [GeneratedRegex("<div\\b(?<attrs>[^>]*class=[\"']numList[\"'][^>]*)>(?<body>.*?)</ul>", RegexOptions.Singleline | RegexOptions.IgnoreCase, 1000)] private static partial Regex NumList();
    [GeneratedRegex("\\bmac_url\\s*=\\s*'([^'\\r\\n]*)'\\s*;", RegexOptions.CultureInvariant, 1000)] private static partial Regex MacUrl();
    [GeneratedRegex("<[^>]+>", RegexOptions.CultureInvariant, 1000)] private static partial Regex Tags();
}
