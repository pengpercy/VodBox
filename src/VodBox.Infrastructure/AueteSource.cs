using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using VodBox.Core;

namespace VodBox.Infrastructure;

/// <summary>Auete HTML protocol, ported from the fixed FTY Auete plugin.</summary>
public sealed class AueteSource : IResolvingContentSource
{
    private const string Agent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/118.0.0.0 Safari/537.36";
    private static readonly HttpClient Http = new(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(2);
    private readonly SourceInfo _site;
    private readonly Uri _host;
    private readonly Func<string, CancellationToken, Task<string>> _fetch;
    internal static bool Supports(string? ext)
    {
        if (string.IsNullOrWhiteSpace(ext)) return true;
        try
        {
            string value = Uri.TryCreate(ext, UriKind.Absolute, out var rawUri) && rawUri.Scheme is "http" or "https" ? ext : FtyExtCodec.JsonConfig(ext);
            if (Uri.TryCreate(value, UriKind.Absolute, out var direct) && direct.Scheme is "http" or "https") return false;
            using var json = System.Text.Json.JsonDocument.Parse(value);
            if (json.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return false;
            foreach (var property in json.RootElement.EnumerateObject())
            {
                // siteUrl 必须是 HTTP(S) 基地址；filterSpeedUp 在真实插件里是布尔开关，只接受标量形态。
                if (property.Name == "siteUrl")
                {
                    if (property.Value.ValueKind != System.Text.Json.JsonValueKind.String) return false;
                    if (property.Value.GetString() is { Length: > 0 } host && (!Uri.TryCreate(host, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0)) return false;
                }
                else if (property.Name == "filterSpeedUp")
                {
                    if (property.Value.ValueKind is not (System.Text.Json.JsonValueKind.String or System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False or System.Text.Json.JsonValueKind.Number)) return false;
                }
                else return false;
            }
            return true;
        }
        catch (Exception error) when (error is System.Text.Json.JsonException or InvalidDataException or FormatException or ArgumentException) { return false; }
    }
    public string Key => _site.Key;
    public string Name => _site.Name;
    private static readonly (string Id, string Label)[] Categories = [("Movie", "电影"), ("Tv", "电视剧"), ("Zy", "综艺"), ("Dm", "动漫"), ("qita", "其他")];
    private static readonly Dictionary<string, string> LineNames = new()
    {
        ["云播B线"]="byun", ["云播Y线"]="yyun", ["云播C线"]="cyun", ["云播D线"]="dbm3u8", ["云播E线"]="i8i",
        ["云播F线"]="m3u8hd", ["云播O线"]="oyun", ["云播G线"]="languang", ["云播H线"]="hyun", ["云播K线"]="kyun",
        ["云播X线"]="xun", ["云播粤语"]="bpyueyu", ["云播国语"]="bpguoyu", ["云播L线"]="lyun", ["云播M线"]="myun", ["Dplayer"]="dp"
    };
    public AueteSource(SourceInfo site) : this(site, null) { }
    internal AueteSource(SourceInfo site, Func<string, CancellationToken, Task<string>>? fetch)
    {
        _site = site;
        if (!Supports(site.Ext)) throw new NotSupportedException("Auete ext 配置不受支持。");
        string host = "https://auete.cc/";
        if (!string.IsNullOrWhiteSpace(site.Ext))
        {
            using var config = JsonDocument.Parse(FtyExtCodec.JsonConfig(site.Ext));
            if (config.RootElement.TryGetProperty("siteUrl", out var value) && value.GetString() is { Length: > 0 } configured) host = configured;
        }
        _host = HttpUri(host.TrimEnd('/') + "/");
        _fetch = fetch ?? Fetch;
    }
    private static Regex Pattern(string pattern) => new(pattern, RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout);
    private static string Text(string html) => WebUtility.HtmlDecode(Pattern("<[^>]+>").Replace(html, "")).Trim();
    private static string Attribute(string html, string name) => WebUtility.HtmlDecode(Pattern("\\b" + Regex.Escape(name) + "\\s*=\\s*([\"'])(.*?)\\1").Match(html).Groups[2].Value);
    private static string ClassBody(string html, string tag, string css)
    {
        var opening = Pattern("<" + tag + "\\b[^>]*class=[\"'][^\"']*\\b" + Regex.Escape(css) + "\\b[^\"']*[\"'][^>]*>").Match(html);
        if (!opening.Success) return "";
        int start = opening.Index + opening.Length, depth = 1;
        foreach (Match token in Pattern("</?" + tag + "\\b[^>]*>").Matches(html, start))
        {
            if (token.Value.StartsWith("</", StringComparison.Ordinal)) depth--;
            else if (!token.Value.EndsWith("/>", StringComparison.Ordinal)) depth++;
            if (depth == 0) return html[start..token.Index];
        }
        return "";
    }
    private string Absolute(string path) => new Uri(_host, path).AbsoluteUri;
    private async Task<string> Fetch(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, HttpUri(url));
        request.Headers.TryAddWithoutValidation("User-Agent", Agent);
        request.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.8,en-US;q=0.3");
        request.Headers.TryAddWithoutValidation("DNT", "1");
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return Encoding.UTF8.GetString(await BoundedContent.ReadAsync(stream, 4 * 1024 * 1024, ct).ConfigureAwait(false));
    }
    private Task<string> Page(string path, CancellationToken ct) => _fetch(Absolute(path), ct);
    public async Task<IReadOnlyList<Category>> GetCategoriesAsync(CancellationToken ct = default)
    {
        var html = await Page("", ct).ConfigureAwait(false);
        var found = Pattern("<a\\b[^>]*href=[\"'][^\"']*/(\\w+)/index\\.html[\"'][^>]*>(.*?)</a>").Matches(html)
            .Where(m => Categories.Any(c => c.Label == Text(m.Groups[2].Value)))
            .Select(m => new Category(m.Groups[1].Value, Text(m.Groups[2].Value))).DistinctBy(c => c.Id).ToArray();
        return found;
    }
    public Task<IReadOnlyList<FilterGroup>> GetFiltersAsync(string categoryId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var options = categoryId switch
        {
            "Movie" => "喜剧片:xjp,动作片:dzp,爱情片:aqp,科幻片:khp,恐怖片:kbp,惊悚片:jsp,战争片:zzp,剧情片:jqp",
            "Tv" => "美剧:oumei,韩剧:hanju,日剧:riju,泰剧:yataiju,网剧:wangju,台剧:taiju,国产:neidi,港剧:tvbgj",
            "Zy" => "国综:guozong,韩综:hanzong,美综:meizong",
            "Dm" => "动画:donghua,日漫:riman,国漫:guoman,美漫:meiman",
            "qita" => "纪录片:Jlp,经典片:Jdp,经典剧:Jdj,网大电影:wlp,国产老电影:laodianying",
            _ => throw new InvalidDataException("Auete 分类无效。")
        };
        return Task.FromResult<IReadOnlyList<FilterGroup>>([new("0", "分类", [new("全部", ""), .. options.Split(',').Select(s => s.Split(':')).Select(s => new FilterValue(s[0], s[1]))], "")]);
    }
    public async Task<MediaPage> GetHomeAsync(CancellationToken ct = default) => Paged(await Page("", ct).ConfigureAwait(false), 1);
    public async Task<MediaPage> GetItemsAsync(string categoryId, int page, IReadOnlyDictionary<string, string>? filters, CancellationToken ct = default)
    {
        ValidatePage(page);
        if (!Categories.Any(c => c.Id == categoryId)) throw new InvalidDataException("Auete 分类无效。");
        string subtype = filters?.TryGetValue("0", out var value) == true ? value : "";
        if (filters?.Keys.Any(k => k != "0") == true || (subtype.Length > 0 && !(await GetFiltersAsync(categoryId, ct)).SelectMany(g => g.Values).Any(v => v.Value == subtype)))
            throw new InvalidDataException("Auete 筛选无效。");
        var path = categoryId + "/" + (subtype.Length > 0 ? subtype + "/" : "") + (page == 1 ? "index.html" : "index" + page.ToString(CultureInfo.InvariantCulture) + ".html");
        return Paged(await Page(path, ct).ConfigureAwait(false), page);
    }
    public async Task<MediaPage> SearchAsync(string query, int page, CancellationToken ct = default)
    {
        ValidatePage(page);
        if (string.IsNullOrWhiteSpace(query)) return new([], page, page);
        if (query.Length > 256) throw new ArgumentException("搜索词过长。", nameof(query));
        return Paged(await Page("auete4so.php?searchword=" + Uri.EscapeDataString(query) + (page == 1 ? "" : "&page=" + page.ToString(CultureInfo.InvariantCulture)), ct).ConfigureAwait(false), page);
    }
    private MediaPage Paged(string html, int page)
    {
        var items = new List<MediaItem>();
        foreach (Match list in Pattern("<ul\\b[^>]*class=[\"'][^\"']*\\bthreadlist\\b[^\"']*[\"'][^>]*>(.*?)</ul>").Matches(html))
        foreach (Match card in Pattern("<li\\b[^>]*>(.*?)</li>").Matches(list.Groups[1].Value))
        {
            string body = card.Groups[1].Value;
            string heading = Pattern("<h2\\b[^>]*>(.*?)</h2>").Match(body).Groups[1].Value;
            string anchor = Pattern("<a\\b[^>]*>.*?</a>").Match(heading).Value;
            var id = Pattern("/(\\w+/\\w+/\\w+)/").Match(Attribute(body, "href"));
            string title = Attribute(anchor, "title");
            if (title.Length == 0) title = Text(anchor);
            if (!id.Success || title.Length == 0 || id.Groups[1].Value.Contains("/jqp/", StringComparison.Ordinal)) continue;
            var image = Pattern("<img\\b[^>]*>").Match(body).Value;
            string pic = Attribute(image, "src");
            items.Add(new() { Id=id.Groups[1].Value, Title=title, Poster=pic.Length > 0 ? Absolute(pic) : null, Remarks=Text(ClassBody(body, "span", "hdtag")) });
        }
        int count = page;
        foreach (Match link in Pattern("<a\\b[^>]*href=[\"']([^\"']+)[\"'][^>]*>(.*?)</a>").Matches(ClassBody(html, "ul", "pagination")))
        {
            var number = Pattern("(?:index|[?&]page=)(\\d+)").Match(link.Groups[1].Value);
            if (number.Success && int.TryParse(number.Groups[1].Value, out int n)) count = Math.Max(count, n);
        }
        return new(items.DistinctBy(i => i.Id).ToArray(), page, count);
    }
    public async Task<MediaDetail> GetDetailAsync(string mediaId, CancellationToken ct = default)
    {
        if (!Pattern("^\\w+/\\w+/\\w+$").IsMatch(mediaId)) throw new InvalidDataException("Auete 影片标识无效。");
        string html = await Page(mediaId + "/", ct).ConfigureAwait(false);
        string title = Text(ClassBody(html, "h1", "detail-title")).Replace("《", "", StringComparison.Ordinal).Replace("》", "", StringComparison.Ordinal);
        string main = Pattern("<div\\b[^>]*class=[\"'][^\"']*detail-main-card[^\"']*[\"'][^>]*>").Match(html).Value;
        if (title.Length == 0) title = Attribute(main, "data-pname");
        string poster = Attribute(Pattern("<div\\b[^>]*class=[\"'][^\"']*detail-poster[^\"']*[\"'][^>]*>\\s*(<img\\b[^>]*>)").Match(html).Groups[1].Value, "src");
        if (poster.Length == 0) poster = Attribute(main, "data-ppic");
        string Fact(string label)
        {
            foreach (Match p in Pattern("<p\\b[^>]*>(.*?)</p>").Matches(html))
            {
                string body=p.Groups[1].Value;
                if (!Text(ClassBody(body,"span","detail-label")).Contains(label, StringComparison.Ordinal)) continue;
                string bold=Text(Pattern("<b\\b[^>]*>(.*?)</b>").Match(body).Groups[1].Value);
                return bold.Length > 0 ? bold : Text(body).Split('：',2).Last().Trim();
            }
            return "";
        }
        var lines = new List<PlaybackLine>();
        var cards = Pattern("<div\\b[^>]*(?:class=[\"'][^\"']*play-card[^\"']*[\"'][^>]*id=[\"']player_list[\"']|id=[\"']player_list[\"'][^>]*class=[\"'][^\"']*play-card[^\"']*[\"'])[^>]*>").Matches(html);
        for (int index=0; index<cards.Count; index++)
        {
            int start=cards[index].Index + cards[index].Length;
            string body=html[start..(index+1 < cards.Count ? cards[index+1].Index : html.Length)];
            string label=Text(Pattern("<b\\b[^>]*>(.*?)</b>").Match(ClassBody(body,"div","card-header")).Groups[1].Value);
            label=label[(label.LastIndexOf('』')+1)..];
            if (!LineNames.TryGetValue(label,out var lineId)) continue;
            var episodes=Pattern("<a\\b[^>]*href=[\"']([^\"']+)[\"'][^>]*>(.*?)</a>").Matches(ClassBody(body,"ul","episode-list"))
                .Where(m=>Pattern("^/"+Regex.Escape(mediaId)+"/play-\\d+-\\d+\\.html$").IsMatch(m.Groups[1].Value))
                .Select(m=>new Episode(lineId+'\u0001'+m.Groups[1].Value,Text(m.Groups[2].Value))).ToArray();
            if (episodes.Length>0) lines.Add(new(lineId,label,episodes));
        }
        if (title.Length==0) throw new InvalidDataException("Auete 详情页缺少片名。");
        return new() { Item=new() { Id=mediaId,Title=title,Poster=poster.Length>0 ? Absolute(poster):null, TypeName=Fact("分类"),Year=Fact("年份"),Area=Fact("地区"),Remarks=Fact("备注") }, Actor=Fact("主演"),Director=Fact("导演"),Description=Text(ClassBody(html,"p","detail-des")),Lines=lines };
    }
    public async Task<PlaybackRequest> ResolvePlaybackAsync(string mediaId, string episodeId, CancellationToken ct = default)
    {
        var detail=await GetDetailAsync(mediaId,ct).ConfigureAwait(false);
        var line=detail.Lines.FirstOrDefault(l=>l.Episodes.Any(e=>e.Id==episodeId)) ?? throw new InvalidDataException("Auete 选集已失效，请刷新详情。");
        string html=await Page(episodeId[(episodeId.IndexOf('\u0001')+1)..],ct).ConfigureAwait(false);
        var now=Pattern("\\bvar\\s+now\\s*=\\s*(?:base64\\s*\\(\\s*([\"'])(.*?)\\1\\s*\\)|([\"'])(.*?)\\3)").Match(html);
        if (!now.Success) throw new InvalidDataException("Auete 播放页缺少 now 地址。");
        string url=now.Groups[3].Success ? now.Groups[4].Value : DecodeBase64(now.Groups[2].Value);
        if (!url.StartsWith("http",StringComparison.OrdinalIgnoreCase) && url.Contains("m3u8",StringComparison.OrdinalIgnoreCase)) url="https://datas-s8pwfqdu9yystn90fb----------------cache.haozhansou.com/"+url;
        url=HttpUri(url).AbsoluteUri;
        return new() { Uri=url,ProxyMedia=url.Contains("m3u8",StringComparison.OrdinalIgnoreCase),SourceKey=Key,SourceName=Name,MediaId=mediaId,EpisodeId=episodeId,LineId=line.Id,Title=detail.Item.Title+" · "+line.Episodes.First(e=>e.Id==episodeId).Title };
    }
    private static string DecodeBase64(string value) => Encoding.UTF8.GetString(Convert.FromBase64String(value.PadRight(value.Length+(4-value.Length%4)%4,'=')));
    private static Uri HttpUri(string value) => Uri.TryCreate(value,UriKind.Absolute,out var uri) && uri.Scheme is "http" or "https" && uri.UserInfo.Length==0 ? uri : throw new InvalidDataException("Auete HTTP(S)地址无效。");
    private static void ValidatePage(int page) { if(page<1 || page==int.MaxValue) throw new ArgumentOutOfRangeException(nameof(page)); }
}
