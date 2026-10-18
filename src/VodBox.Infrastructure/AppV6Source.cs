using System.Text.Json;
using VodBox.Core;

namespace VodBox.Infrastructure;

/// <summary>
/// 麦田 AppV6（宝盒 <c>csp_AppV6</c>）JSON 原生实现，按随宝盒下发的 <c>bh0822.jar</c> 反编译源码
/// （<c>com.github.catvod.spider.AppV6</c>）重写。ext 即接口根地址（原插件直接 <c>ext + "/v6/…"</c>）。
/// </summary>
public sealed class AppV6Source : IResolvingContentSource
{
    private const string Agent = "okhttp/4.9.3";
    private const string PlayFailure = "播放链接解析失败,请更换其他源播放";

    private static readonly Dictionary<string, string> FilterNames = new(StringComparer.Ordinal)
    {
        ["class"] = "类型",
        ["area"] = "地区",
        ["year"] = "年份",
        ["lang"] = "语言",
    };

    private readonly SourceInfo _site;
    private readonly string _base;
    private readonly Func<string, CancellationToken, Task<string>> _fetch;

    public string Key => _site.Key;
    public string Name => _site.Name;

    public AppV6Source(SourceInfo site) : this(site, null) { }

    internal AppV6Source(SourceInfo site, Func<string, CancellationToken, Task<string>>? fetch)
    {
        _site = site;
        if (!Supports(site.Ext)) throw new NotSupportedException("AppV6 ext 不是有效的 HTTP(S) 接口地址。");
        _base = site.Ext!.Trim().TrimEnd('/');
        _fetch = fetch ?? Fetch;
    }

    internal static bool Supports(string? ext)
    {
        if (!Uri.TryCreate(ext?.Trim(), UriKind.Absolute, out var uri)) return false;
        return uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0;
    }

    private static async Task<string> Fetch(string url, CancellationToken ct) =>
        DefaultHttp.Decode(await DefaultHttp.Shared.GetBoundedAsync(url, 8 * 1024 * 1024, ct,
            new Dictionary<string, string> { ["User-Agent"] = Agent }));

    private async Task<JsonElement> GetAsync(string path, CancellationToken ct)
    {
        using var document = JsonDocument.Parse(await _fetch(_base + path, ct).ConfigureAwait(false));
        return document.RootElement.Clone();
    }

    private static string Text(JsonElement item, string key) => AppRjSource.Text(item, key);

    private static List<MediaItem> Items(JsonElement list)
    {
        var items = new List<MediaItem>();
        if (list.ValueKind != JsonValueKind.Array) return items;
        foreach (var entry in list.EnumerateArray())
        {
            var id = Text(entry, "vod_id");
            if (id.Length == 0) continue;
            items.Add(new MediaItem { Id = id, Title = Text(entry, "vod_name"), Poster = Text(entry, "vod_pic"), Remarks = Text(entry, "vod_remarks") });
        }
        return items;
    }

    public async Task<IReadOnlyList<Category>> GetCategoriesAsync(CancellationToken ct = default)
    {
        var data = await GetAsync("/v6/nav", ct).ConfigureAwait(false);
        var categories = new List<Category>();
        if (!data.TryGetProperty("data", out var list) || list.ValueKind != JsonValueKind.Array) return categories;
        foreach (var entry in list.EnumerateArray())
        {
            var id = Text(entry, "type_id");
            if (id.Length == 0) continue;
            categories.Add(new Category(id, Text(entry, "type_name")));
        }
        return categories;
    }

    public async Task<IReadOnlyList<FilterGroup>> GetFiltersAsync(string categoryId, CancellationToken ct = default)
    {
        var data = await GetAsync("/v6/nav", ct).ConfigureAwait(false);
        if (!data.TryGetProperty("data", out var list) || list.ValueKind != JsonValueKind.Array) return [];
        foreach (var entry in list.EnumerateArray())
        {
            if (Text(entry, "type_id") != categoryId) continue;
            if (!entry.TryGetProperty("type_extend", out var extend) || extend.ValueKind != JsonValueKind.Object) return [];
            var groups = new List<FilterGroup>();
            foreach (var property in extend.EnumerateObject())
            {
                if (!FilterNames.TryGetValue(property.Name, out var name) || property.Value.ValueKind != JsonValueKind.String) continue;
                var values = property.Value.GetString()!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(value => new FilterValue(value, value)).ToList();
                if (values.Count > 1) groups.Add(new FilterGroup(property.Name, name, values, ""));
            }
            return groups;
        }
        return [];
    }

    public async Task<MediaPage> GetHomeAsync(CancellationToken ct = default)
    {
        var data = await GetAsync("/v6/index_video", ct).ConfigureAwait(false);
        if (!data.TryGetProperty("data", out var list) || list.ValueKind != JsonValueKind.Array) return new MediaPage([], 1, 1);
        // 原插件在循环里持续覆盖 vodList，最终返回最后一个非空分组的列表；保持同一结果。
        List<MediaItem> items = [];
        foreach (var group in list.EnumerateArray())
        {
            if (!group.TryGetProperty("vlist", out var vlist) || vlist.ValueKind != JsonValueKind.Array) continue;
            items = Items(vlist);
        }
        return new MediaPage(items, 1, 1);
    }

    public async Task<MediaPage> GetItemsAsync(string categoryId, int page, IReadOnlyDictionary<string, string>? filters, CancellationToken ct = default)
    {
        if (page < 1) throw new InvalidDataException("页码无效。");
        if (categoryId.Length == 0) throw new InvalidDataException("AppV6 分类无效。");
        var url = $"/v6/video?pg={page}&tid={Uri.EscapeDataString(categoryId)}";
        foreach (var key in new[] { "class", "area", "lang", "year" })
            if (filters is not null && filters.TryGetValue(key, out var value) && value.Length > 0)
                url += $"&{key}={Uri.EscapeDataString(value)}";
        url += "&order=time&token=&";
        var data = await GetAsync(url, ct).ConfigureAwait(false);
        var items = data.TryGetProperty("data", out var list) ? Items(list) : [];
        return new MediaPage(items, page, items.Count > 0 ? page + 1 : page);
    }

    public async Task<MediaPage> SearchAsync(string query, int page, CancellationToken ct = default)
    {
        if (page < 1) throw new InvalidDataException("页码无效。");
        if (page > 1) return new MediaPage([], page, page);
        var data = await GetAsync("/v6/search?pg=1&tid=0&text=" + Uri.EscapeDataString(query), ct).ConfigureAwait(false);
        return new MediaPage(data.TryGetProperty("data", out var list) ? Items(list) : [], 1, 1);
    }

    public async Task<MediaDetail> GetDetailAsync(string mediaId, CancellationToken ct = default)
    {
        if (mediaId.Length == 0) throw new InvalidDataException("AppV6 影片编号无效。");
        var data = await GetAsync("/v6/video_detail?id=" + Uri.EscapeDataString(mediaId), ct).ConfigureAwait(false);
        if (!data.TryGetProperty("data", out var root) || !root.TryGetProperty("vod_info", out var info) || info.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("AppV6 详情响应缺少 vod_info。");
        var lines = new List<PlaybackLine>();
        if (info.TryGetProperty("vod_url_with_player", out var groups) && groups.ValueKind == JsonValueKind.Array)
        {
            foreach (var group in groups.EnumerateArray())
            {
                var parse = Text(group, "parse_api");
                var episodes = new List<Episode>();
                foreach (var part in Text(group, "url").Split('#'))
                {
                    var split = part.Split('$');
                    if (split.Length < 2 || split[1].Length == 0) continue;
                    episodes.Add(new Episode(parse + split[1], split[0]));
                }
                if (episodes.Count == 0) continue;
                var name = Text(group, "name");
                lines.Add(new PlaybackLine(name, name, episodes));
            }
        }
        var item = new MediaItem
        {
            Id = mediaId,
            Title = Text(info, "vod_name"),
            Poster = Text(info, "vod_pic"),
            Remarks = Text(info, "vod_remarks"),
            TypeName = Text(info, "vod_class"),
        };
        return new MediaDetail
        {
            Item = item,
            Description = Text(info, "vod_content").Trim(),
            Actor = Text(info, "vod_actor"),
            Director = Text(info, "vod_director"),
            Lines = lines,
        };
    }

    public async Task<PlaybackRequest> ResolvePlaybackAsync(string mediaId, string episodeId, CancellationToken ct = default)
    {
        if (episodeId.Length == 0) throw new InvalidDataException(PlayFailure);
        var target = episodeId;
        var fromQuery = target.Contains("?url=", StringComparison.Ordinal) || target.Contains("?key=", StringComparison.Ordinal);
        if (fromQuery)
        {
            try
            {
                using var document = JsonDocument.Parse(await _fetch(target, ct).ConfigureAwait(false));
                target = Text(document.RootElement, "url");
            }
            catch (JsonException) { throw new InvalidDataException(PlayFailure); }
        }
        else if (!System.Text.RegularExpressions.Regex.IsMatch(target, "(m3u8|mp4|mkv)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            throw new InvalidDataException(PlayFailure);
        }
        if (target.Length == 0 || !Uri.TryCreate(target, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new InvalidDataException(PlayFailure);
        return new PlaybackRequest
        {
            Uri = target,
            SourceKey = Key,
            SourceName = Name,
            MediaId = mediaId,
            EpisodeId = episodeId,
            Title = Name,
            Headers = new() { ["User-Agent"] = Agent },
        };
    }
}
