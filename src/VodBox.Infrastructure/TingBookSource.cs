using System.Text.Json;
using System.Text.RegularExpressions;
using VodBox.Core;

namespace VodBox.Infrastructure;

/// <summary>
/// 六月听书（宝盒 <c>csp_TingBook</c>）HTML 原生实现，按随宝盒下发的 <c>bh0822.jar</c> 反编译源码
/// （<c>com.github.catvod.spider.TingBook</c>）重写。ext 可为基地址或含 <c>site/url</c> 的 JSON。
/// </summary>
public sealed class TingBookSource : IResolvingContentSource
{
    private const string DefaultBase = "https://app.365ting.com";
    private const string Agent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

    private static readonly (string Id, string Name)[] Categories =
    [
        ("6", "玄幻奇幻"), ("7", "都市言情"), ("8", "宫斗女频"), ("9", "官场商战"), ("10", "武侠仙侠"), ("11", "刑侦推理"),
        ("12", "探险科幻"), ("13", "重生穿越"), ("14", "恐怖惊悚"), ("15", "文学历史"), ("31", "评书相声"), ("49", "两性情感"),
        ("51", "儿童文学"), ("52", "国学启蒙"), ("53", "家教育儿"), ("54", "卡通动画"),
    ];

    private static readonly Regex BookPath = HtmlRegex.Get("/book/\\d+\\.html");
    private static readonly Regex PageQuery = HtmlRegex.Get("page=(\\d+)");
    private static readonly Regex CharCodes = HtmlRegex.Get("\\d+(\\*\\d+)+");

    private readonly SourceInfo _site;
    private readonly string _base;
    private readonly Func<string, CancellationToken, Task<string>> _fetch;

    public string Key => _site.Key;
    public string Name => _site.Name;

    public TingBookSource(SourceInfo site) : this(site, null) { }

    internal TingBookSource(SourceInfo site, Func<string, CancellationToken, Task<string>>? fetch)
    {
        _site = site;
        if (!Supports(site.Ext)) throw new NotSupportedException("TingBook ext 配置不受支持。");
        _base = (ResolveBase(site.Ext) ?? DefaultBase).TrimEnd('/');
        _fetch = fetch ?? Fetch;
    }

    /// <summary>ext 为空、绝对 URL，或含 <c>site/url</c> 字符串字段的 JSON 对象时才接管。</summary>
    internal static bool Supports(string? ext)
    {
        if (string.IsNullOrWhiteSpace(ext)) return true;
        var value = ext.Trim();
        if (IsHttp(value)) return true;
        if (!value.StartsWith('{')) return false;
        try
        {
            using var document = JsonDocument.Parse(value);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Name is not ("site" or "url")) return false;
                if (property.Value.ValueKind != JsonValueKind.String) return false;
                var host = property.Value.GetString();
                if (!string.IsNullOrEmpty(host) && !IsHttp(host)) return false;
            }
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static bool IsHttp(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    private static string? ResolveBase(string? ext)
    {
        if (string.IsNullOrWhiteSpace(ext)) return null;
        var value = ext.Trim();
        if (value.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return value;
        if (!value.StartsWith('{')) return null;
        using var document = JsonDocument.Parse(value);
        foreach (var key in new[] { "site", "url" })
            if (document.RootElement.TryGetProperty(key, out var node) && node.ValueKind == JsonValueKind.String && node.GetString() is { Length: > 0 } host)
                return host;
        return null;
    }

    private CancellationTokenSource Timeout(CancellationToken ct)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(ct);
        source.CancelAfter(TimeSpan.FromSeconds(20));
        return source;
    }

    private async Task<string> Fetch(string url, CancellationToken ct)
    {
        using var timeout = Timeout(ct);
        return DefaultHttp.Decode(await DefaultHttp.Shared.GetBoundedAsync(url, 4 * 1024 * 1024, timeout.Token,
            new Dictionary<string, string>
            {
                ["User-Agent"] = Agent,
                ["Referer"] = _base + "/",
                ["Accept"] = "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8",
            }));
    }

    private static Regex Pattern(string pattern) => HtmlRegex.Get(pattern);
    private static string Text(string html) => System.Net.WebUtility.HtmlDecode(Pattern("<[^>]+>").Replace(html, "")).Trim();
    private static string Attribute(string html, string name) =>
        System.Net.WebUtility.HtmlDecode(Pattern("\\b" + Regex.Escape(name) + "\\s*=\\s*([\"'])(.*?)\\1").Match(html).Groups[2].Value);

    /// <summary>图片地址归一化：仅补 <c>//</c> 协议前缀（对应插件 f()）。</summary>
    private static string Image(string value) => value.Length > 0 && value.StartsWith("//", StringComparison.Ordinal) ? "https:" + value : value;

    /// <summary>相对地址归一化（对应插件 i()）。</summary>
    private string Absolute(string value)
    {
        if (value.Length == 0) return _base;
        if (value.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return value;
        if (value.StartsWith("//", StringComparison.Ordinal)) return "https:" + value;
        return value.StartsWith('/') ? _base + value : _base + "/" + value;
    }

    /// <summary>章节列表：<c>a.list-item[href^=/play/]</c>，标题取 title 属性、正文、再退化为「章节」。</summary>
    private static void AddChapters(string html, List<Episode> episodes)
    {
        foreach (Match tag in Pattern("<a\\b[^>]*>").Matches(html))
        {
            if (!Attribute(tag.Value, "class").Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("list-item")) continue;
            var href = Attribute(tag.Value, "href");
            if (href.Length == 0 || !href.Contains("/play/", StringComparison.Ordinal)) continue;
            var title = Attribute(tag.Value, "title").Trim();
            if (title.Length == 0) title = Text(Pattern("<a\\b[^>]*>(.*?)</a>").Match(html, tag.Index).Groups[1].Value).Trim();
            episodes.Add(new Episode(href, title.Length == 0 ? "章节" : title));
        }
    }

    /// <summary>分类/搜索/首页共用的书目卡片解析（对应插件 k()）。</summary>
    private static List<MediaItem> Books(string html)
    {
        var items = new List<MediaItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match tag in Pattern("<a\\b[^>]*>").Matches(html))
        {
            var href = Attribute(tag.Value, "href");
            if (!BookPath.IsMatch(href) || !seen.Add(href)) continue;
            var body = Pattern("<a\\b[^>]*>(.*?)</a>").Match(html, tag.Index).Groups[1].Value;
            var title = Text(Group(body, "<h2\\b[^>]*>(.*?)</h2>"));
            var remarks = "听书";
            var separator = title.IndexOf('|');
            if (separator > 0)
            {
                remarks = title[(separator + 1)..].Trim();
                title = title[..separator].Trim();
            }
            if (title.Length == 0) title = Text(body).Trim();
            var poster = Image(Attribute(Pattern("<img\\b[^>]*>").Match(body).Value, "src"));
            items.Add(new MediaItem { Id = href, Title = title, Poster = poster, Remarks = remarks });
            if (items.Count >= 95) break;
        }
        return items;
    }

    public Task<IReadOnlyList<Category>> GetCategoriesAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<Category>>(Categories.Select(c => new Category(c.Id, c.Name)).ToArray());
    }

    public async Task<MediaPage> GetHomeAsync(CancellationToken ct = default)
    {
        var html = await _fetch(Absolute("/category/6/2/1.html"), ct).ConfigureAwait(false);
        return new MediaPage(Books(html), 1, 1);
    }

    public async Task<MediaPage> GetItemsAsync(string categoryId, int page, IReadOnlyDictionary<string, string>? filters, CancellationToken ct = default)
    {
        if (page < 1) throw new InvalidDataException("页码无效。");
        if (categoryId.Length == 0 || !categoryId.All(char.IsAsciiDigit)) throw new InvalidDataException("TingBook 分类无效。");
        var html = await _fetch(Absolute($"/category/{categoryId}/2/{page}.html"), ct).ConfigureAwait(false);
        var items = Books(html);
        return new MediaPage(items, page, items.Count >= 24 ? page + 1 : page);
    }

    public async Task<MediaPage> SearchAsync(string query, int page, CancellationToken ct = default)
    {
        if (page < 1) throw new InvalidDataException("页码无效。");
        if (page > 1) return new MediaPage([], page, page);
        var html = await _fetch(Absolute("/pc/index/search/keyword/" + Uri.EscapeDataString(query) + ".html"), ct).ConfigureAwait(false);
        return new MediaPage(Books(html), 1, 1);
    }

    public async Task<MediaDetail> GetDetailAsync(string mediaId, CancellationToken ct = default)
    {
        if (mediaId.Length == 0) throw new InvalidDataException("TingBook 书籍编号无效。");
        // 站点偶发返回 System Error 页面，原插件重试 3 次；这里保留同样的重试与等待。
        var html = "";
        for (var attempt = 0; attempt < 3; attempt++)
        {
            html = await _fetch(Absolute(mediaId), ct).ConfigureAwait(false);
            if (!html.Contains("System Error", StringComparison.Ordinal)) break;
            if (attempt < 2) await Task.Delay(500, ct).ConfigureAwait(false);
        }
        var title = Text(Group(html, "<h2\\b[^>]*class=\"book-title\"[^>]*>(.*?)</h2>"));
        if (title.Length == 0) title = Text(Group(html, "<h1\\b[^>]*class=\"title\"[^>]*>(.*?)</h1>"));
        var poster = Image(Attribute(Pattern("<img\\b[^>]*class=\"[^\"]*img[^\"]*\"[^>]*>").Match(html).Value, "src"));
        if (poster.Length == 0) poster = Image(Attribute(Pattern("<img\\b[^>]*alt=\"[^\"]*\"[^>]*>").Match(html).Value, "src"));
        var description = Text(Group(html, "<[^>]*class=\"book-intro\"[^>]*>(.*?)</"));
        var actor = Extra(html, "作者");
        var typeName = Extra(html, "类型");
        var remarks = Extra(html, "状态");
        if (remarks.Length == 0) remarks = Extra(html, "原著");
        var director = Extra(html, "播音");

        var episodes = new List<Episode>();
        AddChapters(html, episodes);
        var pages = 1;
        foreach (Match link in Pattern("<li\\b[^>]*>\\s*<a\\b[^>]*href=\"[^\"]*\"[^>]*>").Matches(html))
        {
            var page = PageQuery.Match(link.Value);
            if (page.Success && int.TryParse(page.Groups[1].Value, out var number)) pages = Math.Max(pages, number);
        }
        var last = Math.Min(pages, 200);
        var join = mediaId.Contains('?') ? "&" : "?";
        for (var number = 2; number <= last; number++)
        {
            try
            {
                AddChapters(await _fetch(Absolute(mediaId + join + "page=" + number), ct).ConfigureAwait(false), episodes);
            }
            catch (Exception error) when (error is HttpRequestException or InvalidDataException or TaskCanceledException) { }
        }

        var item = new MediaItem { Id = mediaId, Title = title, Poster = poster, TypeName = typeName, Remarks = remarks };
        var lines = episodes.Count > 0 ? new[] { new PlaybackLine("六月", "六月", episodes) } : [];
        return new MediaDetail { Item = item, Description = description, Actor = actor, Director = director, Lines = lines };
    }

    private static string Group(string html, string pattern) => Pattern(pattern).Match(html).Groups[1].Value;

    /// <summary>详情页 <c>.extra</c> 行：以指定前缀开头则取 <c>span.text a</c> 或 <c>span.text</c> 文本。</summary>
    private static string Extra(string html, string prefix)
    {
        foreach (Match row in Pattern("<div\\b[^>]*class=\"extra\"[^>]*>(.*?)</div>").Matches(html))
        {
            if (!Text(row.Groups[1].Value).StartsWith(prefix, StringComparison.Ordinal)) continue;
            var link = Pattern("<span\\b[^>]*class=\"text\"[^>]*>\\s*<a\\b[^>]*>(.*?)</a>").Match(row.Groups[1].Value);
            if (link.Success) return Text(link.Groups[1].Value);
            var span = Pattern("<span\\b[^>]*class=\"text\"[^>]*>(.*?)</span>").Match(row.Groups[1].Value);
            return span.Success ? Text(span.Groups[1].Value) : "";
        }
        return "";
    }

    public async Task<PlaybackRequest> ResolvePlaybackAsync(string mediaId, string episodeId, CancellationToken ct = default)
    {
        var target = episodeId;
        var separator = target.LastIndexOf('$');
        if (separator >= 0) target = target[(separator + 1)..];
        if (target.Length == 0) throw new InvalidDataException("TingBook 播放标识无效。");
        var stripped = target.Replace("/play/", "").Replace(".html", "");
        var parts = stripped.Split('/');
        if (parts.Length >= 2)
        {
            try
            {
                var endpoint = $"{_base}/pc/index/getchapterurl/bookId/{parts[0]}/chapterId/{parts[1]}/timestamp/{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}.html";
                using var document = JsonDocument.Parse(await _fetch(endpoint, ct).ConfigureAwait(false));
                var src = Decode(document.RootElement.TryGetProperty("jsjm", out var mode) && mode.ValueKind == JsonValueKind.Number ? mode.GetInt32() : 0,
                    document.RootElement.TryGetProperty("src", out var node) && node.ValueKind == JsonValueKind.String ? node.GetString()! : "");
                if (src.Length > 0) return Request(mediaId, episodeId, src);
            }
            catch (Exception error) when (error is JsonException or HttpRequestException or InvalidDataException or TaskCanceledException or IOException) { }
        }
        return Request(mediaId, episodeId, Absolute(target.StartsWith('/') ? target : "/play/" + stripped + ".html"));
    }

    private PlaybackRequest Request(string mediaId, string episodeId, string uri) => new()
    {
        Uri = uri,
        SourceKey = Key,
        SourceName = Name,
        MediaId = mediaId,
        EpisodeId = episodeId,
        Title = Name,
        Headers = new()
        {
            ["User-Agent"] = Agent,
            ["Referer"] = _base + "/",
            ["Accept"] = "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8",
        },
    };

    /// <summary>播放地址解码：<c>jsjm==1</c> 或形如 <c>N*N*N</c> 时按码位还原，再补 <c>//</c> 前缀。</summary>
    internal static string Decode(int mode, string value)
    {
        if (value.Length == 0) return "";
        var text = value.Trim();
        if (mode == 1 || CharCodes.IsMatch(text))
        {
            var builder = new System.Text.StringBuilder(text.Length);
            foreach (var part in text.Split('*'))
            {
                if (part.Length == 0) continue;
                if (!int.TryParse(part, out var code)) return text;
                builder.Append((char)code);
            }
            text = builder.ToString();
        }
        return text.StartsWith("//", StringComparison.Ordinal) ? "https:" + text : text;
    }

}
