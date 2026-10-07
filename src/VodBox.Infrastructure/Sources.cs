using System.Globalization;
using System.Text;
using System.Text.Json;
using VodBox.Core;

namespace VodBox.Infrastructure;

/// <summary>苹果 CMS V10 JSON 采集站点源（site.type=0，api 形如 https://xx/api.php/provide/vod）。</summary>
public sealed class MacCmsSource : IContentSource
{
    private readonly DefaultHttp _http;
    private readonly SourceInfo _site;

    public string Key => _site.Key;
    public string Name => _site.Name;

    public MacCmsSource(SourceInfo site, DefaultHttp http)
    {
        _site = site;
        _http = http;
    }

    private string Api(string action, IReadOnlyDictionary<string, string> args)
    {
        var query = string.Join("&", args.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));
        var api = _site.Api!;
        if (!api.Contains('?')) api += "?";
        else api += "&";
        return $"{api}ac={action}&{query}";
    }

    public async Task<IReadOnlyList<Category>> GetCategoriesAsync(CancellationToken ct = default)
    {
        var url = Api("list", new Dictionary<string, string>());
        var response = await _http.GetStringAsync(url, ct: ct);
        var parsed = JsonSerializer.Deserialize<MacCmsCategoryResponse>(response, Json.Options)
                     ?? throw new InvalidDataException("分类响应解析失败");
        return parsed.Classes
            .Where(c => !string.IsNullOrWhiteSpace(c.TypeId))
            .Select(c => new Category(c.TypeId, c.TypeName))
            .ToList();
    }

    public async Task<MediaPage> GetHomeAsync(CancellationToken ct = default) => await GetItemsAsync("1", 1, null, ct);

    public async Task<MediaPage> GetItemsAsync(string categoryId, int page, IReadOnlyDictionary<string, string>? filters, CancellationToken ct = default)
    {
        var args = new Dictionary<string, string> { ["pg"] = page.ToString(CultureInfo.InvariantCulture) };
        if (categoryId != "0" && !string.IsNullOrEmpty(categoryId)) args["t"] = categoryId;
        if (filters is not null)
        {
            if (filters.TryGetValue("year", out var year) && !string.IsNullOrEmpty(year)) args["year"] = year;
            if (filters.TryGetValue("area", out var area) && !string.IsNullOrEmpty(area)) args["area"] = area;
            if (filters.TryGetValue("lang", out var lang) && !string.IsNullOrEmpty(lang)) args["lang"] = lang;
            if (filters.TryGetValue("class", out var cls) && !string.IsNullOrEmpty(cls)) args["class"] = cls;
        }
        var response = await _http.GetStringAsync(Api("videolist", args), ct: ct);
        var parsed = JsonSerializer.Deserialize<MacCmsListResponse>(response, Json.Options)
                     ?? throw new InvalidDataException("列表响应解析失败");
        var page_ = parsed.Page <= 0 ? page : parsed.Page;
        return new MediaPage(parsed.List.Select(ToItem).ToList(), page_, parsed.PageCount);
    }

    public async Task<MediaDetail> GetDetailAsync(string mediaId, CancellationToken ct = default)
    {
        var response = await _http.GetStringAsync(Api("videolist", new Dictionary<string, string> { ["ids"] = mediaId }), ct: ct);
        var parsed = JsonSerializer.Deserialize<MacCmsDetailResponse>(response, Json.Options)
                     ?? throw new InvalidDataException("详情响应解析失败");
        var vod = parsed.List.FirstOrDefault() ?? throw new InvalidDataException($"未找到条目 {mediaId}");
        return ToDetail(vod);
    }

    public async Task<MediaPage> SearchAsync(string query, int page, CancellationToken ct = default)
    {
        var args = new Dictionary<string, string> { ["wd"] = query, ["pg"] = page.ToString(CultureInfo.InvariantCulture) };
        var response = await _http.GetStringAsync(Api("videolist", args), ct: ct);
        var parsed = JsonSerializer.Deserialize<MacCmsSearchResponse>(response, Json.Options)
                     ?? throw new InvalidDataException("搜索响应解析失败");
        return new MediaPage(parsed.List.Select(ToItem).ToList(), page, page <= 1 ? 1 : page);
    }

    internal static MediaItem ToItem(MacCmsVod vod) => new()
    {
        Id = vod.VodId,
        Title = string.IsNullOrWhiteSpace(vod.VodName) ? "(未命名)" : vod.VodName,
        Poster = vod.VodPic,
        Remarks = vod.VodRemarks,
        Year = vod.VodYear,
        Area = vod.VodArea,
        TypeName = vod.TypeName,
    };

    /// <summary>拆分 vod_play_from / vod_play_url（$$$ 分隔线路、# 分隔集、$ 分隔名与地址）。</summary>
    internal static MediaDetail ToDetail(MacCmsVod vod)
    {
        var lines = new List<PlaybackLine>();
        var froms = (vod.VodPlayFrom ?? "").Split("$$$", StringSplitOptions.RemoveEmptyEntries);
        var urls = (vod.VodPlayUrl ?? "").Split("$$$", StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < froms.Length && i < urls.Length; i++)
        {
            var episodes = new List<Episode>();
            var pairs = urls[i].Split('#', StringSplitOptions.RemoveEmptyEntries);
            foreach (var pair in pairs)
            {
                var sep = pair.IndexOf('$');
                if (sep <= 0) continue;
                var title = pair[..sep].Trim();
                var uri = pair[(sep + 1)..].Trim();
                if (uri.Length > 0) episodes.Add(new Episode(uri, title, uri));
            }
            if (episodes.Count > 0) lines.Add(new PlaybackLine(froms[i].Trim(), froms[i].Trim(), episodes));
        }
        var content = (vod.VodContent ?? "").Trim();
        if (content.StartsWith("<") || content.Contains("<p>")) content = StripTags(content);
        return new MediaDetail
        {
            Item = ToItem(vod),
            Description = content,
            Director = vod.VodDirector,
            Actor = vod.VodActor,
            Lines = lines,
        };
    }

    private static string StripTags(string html) => html
        .Replace("<br>", "\n", StringComparison.OrdinalIgnoreCase)
        .Replace("<br/>", "\n", StringComparison.OrdinalIgnoreCase)
        .Replace("<p>", "\n", StringComparison.OrdinalIgnoreCase)
        .Replace("</p>", "", StringComparison.OrdinalIgnoreCase)
        .Split('<')[0].Trim();
}

/// <summary>直播源工厂：把 TVBox lives 配置转为 LiveGroup 集合。</summary>
public sealed class LiveSources(DefaultHttp http)
{
    public async Task<List<LiveGroup>> LoadAsync(string url, CancellationToken ct = default)
    {
        var text = url.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase) || await IsM3u(url)
            ? await http.GetStringAsync(url, ct: ct)
            : await http.GetStringAsync(url, ct: ct);
        return M3uParser.IsM3u(text) ? M3uParser.ParseGroups(text) : TxtLiveParser.Parse(text);
    }

    private async Task<bool> IsM3u(string url) => url.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase)
        || url.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
        || url.Contains("m3u", StringComparison.OrdinalIgnoreCase);
}

/// <summary>M3U 直播列表解析（#EXTINF + group-title + tvg-logo / tvg-id / #EXTVLCOPT）。</summary>
public static class M3uParser
{
    public static bool IsM3u(string text) => text.Contains("#EXTM3U", StringComparison.OrdinalIgnoreCase);

    public static List<LiveGroup> ParseGroups(string text)
    {
        var channels = new List<LiveChannel>();
        string? pendingName = null, pendingGroup = "未分组", pendingLogo = null, pendingTvg = null;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim().TrimEnd('\r');
            if (line.Length == 0) continue;
            if (line.StartsWith("#EXTINF", StringComparison.OrdinalIgnoreCase))
            {
                pendingName = null; pendingGroup = "未分组"; pendingLogo = null; pendingTvg = null;
                var comma = line.IndexOf(',');
                var attrs = comma > 0 ? line[..comma] : line;
                if (comma > 0 && comma + 1 < line.Length) pendingName = line[(comma + 1)..].Trim();
                pendingGroup = Attr(attrs, "group-title") ?? pendingGroup;
                pendingLogo = Attr(attrs, "tvg-logo");
                pendingTvg = Attr(attrs, "tvg-id");
            }
            else if (!line.StartsWith('#'))
            {
                var name = string.IsNullOrWhiteSpace(pendingName) ? GuessName(line) : pendingName;
                channels.Add(new LiveChannel
                {
                    Name = name,
                    Uris = [line],
                    Logo = pendingLogo,
                    Group = pendingGroup,
                    TvgId = pendingTvg,
                });
                pendingName = null; pendingLogo = null; pendingTvg = null;
            }
        }
        return Group(channels);
    }

    private static string? Attr(string attrs, string key)
    {
        var marker = key + "=\"";
        var start = attrs.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return null;
        start += marker.Length;
        var end = attrs.IndexOf('"', start);
        return end > start ? attrs[start..end] : null;
    }

    private static string GuessName(string url)
    {
        var tail = url.Split('/').LastOrDefault() ?? url;
        var dot = tail.IndexOf('.');
        return dot > 0 ? tail[..dot] : tail;
    }

    internal static List<LiveGroup> Group(IReadOnlyList<LiveChannel> channels)
    {
        var number = 0;
        return channels
            .GroupBy(c => c.Group)
            .OrderBy(g => g.Key == "未分组" ? 1 : 0)
            .Select(g => new LiveGroup(g.Key, g.Select(c => c with { Number = ++number }).ToList(), false))
            .ToList();
    }
}

/// <summary>TXT 直播列表解析（「分组,#genre#」+「频道名,地址」格式，FongMi LiveParser 等价）。</summary>
public static class TxtLiveParser
{
    public static List<LiveGroup> Parse(string text)
    {
        var channels = new List<LiveChannel>();
        string group = "未分组";
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim().TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith("//") || line.StartsWith(";")) continue;
            var comma = line.IndexOf(',');
            if (comma <= 0) continue;
            var left = line[..comma].Trim();
            var right = line[(comma + 1)..].Trim();
            if (right.Contains("#genre#", StringComparison.OrdinalIgnoreCase))
            {
                group = string.IsNullOrWhiteSpace(left) ? "未分组" : left;
                continue;
            }
            if (right.Length == 0) continue;
            var uris = right.Split('$').Where(u => !string.IsNullOrWhiteSpace(u) && u.Contains("://")).Select(u => u.Trim()).ToArray();
            if (uris.Length == 0) continue;
            channels.Add(new LiveChannel { Name = left, Uris = uris, Group = group });
        }
        return M3uParser.Group(channels);
    }
}
