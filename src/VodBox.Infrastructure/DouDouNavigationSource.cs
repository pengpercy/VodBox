using System.Globalization;
using System.Text.Json;
using VodBox.Core;

namespace VodBox.Infrastructure;

/// <summary>
/// DouDou：原插件是豆瓣（frodo 小程序接口）热播/筛选/榜单导航，不提供视频地址。
/// 卡片按 TVBox 约定用 <c>msearch:###片名###图片</c>，点击后桌面端以片名发起全局搜索（见 <see cref="TvBoxLinks"/>）。
/// </summary>
public sealed class DouDouNavigationSource : INavigationContentSource
{
    private const string Api = "https://frodo.douban.com/api/v2/";
    private const string ApiKey = "0ac44ae016490db2204ce0a042db2916";
    // 原插件 vod_pic 后缀：豆瓣图床对无 Referer 的请求返回 418。
    private const string PosterHeaders = "@Referer=https://api.douban.com/@User-Agent=Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/110.0.0.0 Safari/537.36";
    private static readonly IReadOnlyDictionary<string, string> Headers = new Dictionary<string, string>
    {
        ["Host"] = "frodo.douban.com",
        ["Referer"] = "https://servicewechat.com/wx2f9b06c1de1ccfca/84/page-frame.html",
        ["User-Agent"] = "Mozilla/5.0 (Windows NT 6.1; WOW64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/53.0.2785.143 Safari/537.36 MicroMessenger/7.0.9.501 NetType/WIFI MiniProgramEnv/Windows WindowsWechat",
        ["Connection"] = "Keep-Alive",
    };
    private static readonly IReadOnlyList<Category> Categories =
    [
        new("hot_gaia", "热播电影"), new("tv_hot", "热播剧集"), new("show_hot", "热播综艺"),
        new("movie", "电影筛选"), new("tv", "电视筛选"), new("rank_list_movie", "电影榜单"), new("rank_list_tv", "电视剧榜单"),
    ];
    private static readonly Lazy<IReadOnlyDictionary<string, IReadOnlyList<FilterGroup>>> Filters = new(LoadFilters);

    private readonly SourceInfo _site;
    private readonly Func<string, IReadOnlyDictionary<string, string>, CancellationToken, Task<string>> _fetch;
    public string Key => _site.Key;
    public string Name => _site.Name;

    public DouDouNavigationSource(SourceInfo site) : this(site, null) { }
    internal DouDouNavigationSource(SourceInfo site, Func<string, IReadOnlyDictionary<string, string>, CancellationToken, Task<string>>? fetch)
    {
        _site = site;
        _fetch = fetch ?? (async (url, headers, ct) => DefaultHttp.Decode(await DefaultHttp.Shared.GetBoundedAsync(url, 4 * 1024 * 1024, ct, headers).ConfigureAwait(false)));
    }

    internal static bool Supports(string? ext)
    {
        if (string.IsNullOrWhiteSpace(ext)) return true;
        try { using var json = JsonDocument.Parse(FtyExtCodec.JsonConfig(ext)); return json.RootElement.ValueKind == JsonValueKind.Object && !json.RootElement.EnumerateObject().Any(); }
        catch (Exception e) when (e is JsonException or InvalidDataException or FormatException) { return false; }
    }

    public Task<IReadOnlyList<Category>> GetCategoriesAsync(CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); return Task.FromResult(Categories); }

    public Task<IReadOnlyList<FilterGroup>> GetFiltersAsync(string categoryId, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); return Task.FromResult(Filters.Value.TryGetValue(categoryId, out var groups) ? groups : (IReadOnlyList<FilterGroup>)[]); }

    /// <summary>原插件首页：豆瓣实时热门（subject_real_time_hotest）。</summary>
    public async Task<MediaPage> GetHomeAsync(CancellationToken ct = default)
    {
        var items = await Fetch("subject_collection/subject_real_time_hotest/items?apikey=" + ApiKey, "subject_collection_items", ct).ConfigureAwait(false);
        return new(items, 1, 1);
    }

    public async Task<MediaPage> GetItemsAsync(string categoryId, int page, IReadOnlyDictionary<string, string>? filters, CancellationToken ct = default)
    {
        if (page < 1 || page > 500) throw new ArgumentOutOfRangeException(nameof(page));
        if (!Categories.Any(c => c.Id == categoryId)) throw new InvalidDataException("DouDou 分类无效。");
        string Value(string key, string fallback) => filters is not null && filters.TryGetValue(key, out var value) && value.Length > 0 ? value : fallback;
        string paging = "&start=" + ((page - 1) * 20).ToString(CultureInfo.InvariantCulture) + "&count=20";
        // 原插件 tags = 除 sort 外所有筛选值按选择顺序以逗号拼接。
        string tags = Uri.EscapeDataString(string.Join(',', (filters ?? new Dictionary<string, string>()).Where(p => p.Key != "sort" && p.Value.Length > 0).Select(p => p.Value)));
        var (path, key) = categoryId switch
        {
            "hot_gaia" => ("movie/hot_gaia?apikey=" + ApiKey + "&sort=" + Uri.EscapeDataString(Value("sort", "recommend")) + "&area=" + Uri.EscapeDataString(Value("area", "全部")) + paging, "items"),
            "tv_hot" => ("subject_collection/" + Segment(Value("type", "tv_hot")) + "/items?apikey=" + ApiKey + paging, "subject_collection_items"),
            "show_hot" => ("subject_collection/" + Segment(Value("type", "show_hot")) + "/items?apikey=" + ApiKey + paging, "subject_collection_items"),
            "tv" => ("tv/recommend?apikey=" + ApiKey + "&sort=" + Uri.EscapeDataString(Value("sort", "T")) + "&tags=" + tags + paging, "items"),
            "rank_list_movie" => ("subject_collection/" + Segment(Value("榜单", "movie_real_time_hotest")) + "/items?apikey=" + ApiKey + paging, "subject_collection_items"),
            "rank_list_tv" => ("subject_collection/" + Segment(Value("榜单", "tv_real_time_hotest")) + "/items?apikey=" + ApiKey + paging, "subject_collection_items"),
            _ => ("movie/recommend?apikey=" + ApiKey + "&sort=" + Uri.EscapeDataString(Value("sort", "T")) + "&tags=" + tags + paging, "items"),
        };
        var items = await Fetch(path, key, ct).ConfigureAwait(false);
        // 原插件 pagecount 固定 999；空页即止，避免无限翻页。
        return new(items, page, items.Count > 0 ? page + 1 : page);
    }

    /// <summary>导航源本身不搜索；卡片点击后由全局搜索查找可播放资源。</summary>
    public Task<MediaPage> SearchAsync(string query, int page, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); return Task.FromResult(new MediaPage([], Math.Max(1, page), Math.Max(1, page))); }

    public Task<MediaDetail> GetDetailAsync(string mediaId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        throw new NotSupportedException(TvBoxLinks.TryGetSearchKeyword(mediaId, out var keyword)
            ? "豆瓣导航条目没有视频地址，请用全局搜索查找「" + keyword + "」。" : "豆瓣导航条目没有视频地址。");
    }

    private static string Segment(string value) =>
        value.Length is > 0 and <= 64 && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') ? value : throw new InvalidDataException("DouDou 片单标识无效。");

    private async Task<IReadOnlyList<MediaItem>> Fetch(string path, string key, CancellationToken ct)
    {
        using var document = JsonDocument.Parse(await _fetch(Api + path, Headers, ct).ConfigureAwait(false));
        if (!document.RootElement.TryGetProperty(key, out var rows) || rows.ValueKind != JsonValueKind.Array) return [];
        var items = new List<MediaItem>();
        foreach (var row in rows.EnumerateArray())
        {
            string type = AppRjSource.Text(row, "type"), title = AppRjSource.Text(row, "title");
            // 原插件跳过片单与广告条目。
            if (type is "playlist" or "ad" || title.Length == 0) continue;
            string pic = row.TryGetProperty("pic", out var picture) && picture.ValueKind == JsonValueKind.Object ? AppRjSource.Text(picture, "normal") : "";
            string poster = pic.Length > 0 ? pic + PosterHeaders : "";
            string rating = row.TryGetProperty("rating", out var score) && score.ValueKind == JsonValueKind.Object ? AppRjSource.Text(score, "value") : "";
            items.Add(new() { Id = TvBoxLinks.SearchId(title, poster), Title = title, Poster = poster.Length > 0 ? poster : null, Remarks = rating.Length > 0 && rating != "0" ? "评分：" + rating : "" });
        }
        return items;
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<FilterGroup>> LoadFilters()
    {
        using var document = JsonDocument.Parse(FilterJson);
        var result = new Dictionary<string, IReadOnlyList<FilterGroup>>(StringComparer.Ordinal);
        foreach (var category in document.RootElement.EnumerateObject())
            result[category.Name] = category.Value.EnumerateArray().Select(group =>
            {
                var values = group.GetProperty("value").EnumerateArray().Select(v => new FilterValue(v.GetProperty("n").GetString() ?? "", v.GetProperty("v").GetString() ?? "")).ToList();
                return new FilterGroup(group.GetProperty("key").GetString() ?? "", group.GetProperty("name").GetString() ?? "", values, values.FirstOrDefault()?.Value ?? "");
            }).ToArray();
        return result;
    }

    // 原插件 homeContent 的 filters 原样搬运（键名含中文：类型/地区/年代/平台/榜单 等）。
    private const string FilterJson="{\"hot_gaia\":[{\"key\":\"sort\",\"name\":\"排序\",\"value\":[{\"n\":\"热度\",\"v\":\"recommend\"},{\"n\":\"最新\",\"v\":\"time\"},{\"n\":\"评分\",\"v\":\"rank\"}]},{\"key\":\"area\",\"name\":\"地区\",\"value\":[{\"n\":\"全部\",\"v\":\"全部\"},{\"n\":\"华语\",\"v\":\"华语\"},{\"n\":\"欧美\",\"v\":\"欧美\"},{\"n\":\"韩国\",\"v\":\"韩国\"},{\"n\":\"日本\",\"v\":\"日本\"}]}],\"tv_hot\":[{\"key\":\"type\",\"name\":\"分类\",\"value\":[{\"n\":\"综合\",\"v\":\"tv_hot\"},{\"n\":\"国产剧\",\"v\":\"tv_domestic\"},{\"n\":\"欧美剧\",\"v\":\"tv_american\"},{\"n\":\"日剧\",\"v\":\"tv_japanese\"},{\"n\":\"韩剧\",\"v\":\"tv_korean\"},{\"n\":\"动画\",\"v\":\"tv_animation\"}]}],\"show_hot\":[{\"key\":\"type\",\"name\":\"分类\",\"value\":[{\"n\":\"综合\",\"v\":\"show_hot\"},{\"n\":\"国内\",\"v\":\"show_domestic\"},{\"n\":\"国外\",\"v\":\"show_foreign\"}]}],\"movie\":[{\"key\":\"类型\",\"name\":\"类型\",\"value\":[{\"n\":\"全部类型\",\"v\":\"\"},{\"n\":\"喜剧\",\"v\":\"喜剧\"},{\"n\":\"爱情\",\"v\":\"爱情\"},{\"n\":\"动作\",\"v\":\"动作\"},{\"n\":\"科幻\",\"v\":\"科幻\"},{\"n\":\"动画\",\"v\":\"动画\"},{\"n\":\"悬疑\",\"v\":\"悬疑\"},{\"n\":\"犯罪\",\"v\":\"犯罪\"},{\"n\":\"惊悚\",\"v\":\"惊悚\"},{\"n\":\"冒险\",\"v\":\"冒险\"},{\"n\":\"音乐\",\"v\":\"音乐\"},{\"n\":\"历史\",\"v\":\"历史\"},{\"n\":\"奇幻\",\"v\":\"奇幻\"},{\"n\":\"恐怖\",\"v\":\"恐怖\"},{\"n\":\"战争\",\"v\":\"战争\"},{\"n\":\"传记\",\"v\":\"传记\"},{\"n\":\"歌舞\",\"v\":\"歌舞\"},{\"n\":\"武侠\",\"v\":\"武侠\"},{\"n\":\"灾难\",\"v\":\"灾难\"},{\"n\":\"西部\",\"v\":\"西部\"},{\"n\":\"纪录片\",\"v\":\"纪录片\"},{\"n\":\"短片\",\"v\":\"短片\"}]},{\"key\":\"地区\",\"name\":\"地区\",\"value\":[{\"n\":\"全部地区\",\"v\":\"\"},{\"n\":\"华语\",\"v\":\"华语\"},{\"n\":\"欧美\",\"v\":\"欧美\"},{\"n\":\"韩国\",\"v\":\"韩国\"},{\"n\":\"日本\",\"v\":\"日本\"},{\"n\":\"中国大陆\",\"v\":\"中国大陆\"},{\"n\":\"美国\",\"v\":\"美国\"},{\"n\":\"中国香港\",\"v\":\"中国香港\"},{\"n\":\"中国台湾\",\"v\":\"中国台湾\"},{\"n\":\"英国\",\"v\":\"英国\"},{\"n\":\"法国\",\"v\":\"法国\"},{\"n\":\"德国\",\"v\":\"德国\"},{\"n\":\"意大利\",\"v\":\"意大利\"},{\"n\":\"西班牙\",\"v\":\"西班牙\"},{\"n\":\"印度\",\"v\":\"印度\"},{\"n\":\"泰国\",\"v\":\"泰国\"},{\"n\":\"俄罗斯\",\"v\":\"俄罗斯\"},{\"n\":\"加拿大\",\"v\":\"加拿大\"},{\"n\":\"澳大利亚\",\"v\":\"澳大利亚\"},{\"n\":\"爱尔兰\",\"v\":\"爱尔兰\"},{\"n\":\"瑞典\",\"v\":\"瑞典\"},{\"n\":\"巴西\",\"v\":\"巴西\"},{\"n\":\"丹麦\",\"v\":\"丹麦\"}]},{\"key\":\"sort\",\"name\":\"排序\",\"value\":[{\"n\":\"近期热度\",\"v\":\"T\"},{\"n\":\"首映时间\",\"v\":\"R\"},{\"n\":\"高分优先\",\"v\":\"S\"}]},{\"key\":\"年代\",\"name\":\"年代\",\"value\":[{\"n\":\"全部年代\",\"v\":\"\"},{\"n\":\"2026\",\"v\":\"2026\"},{\"n\":\"2025\",\"v\":\"2025\"},{\"n\":\"2024\",\"v\":\"2024\"},{\"n\":\"2023\",\"v\":\"2023\"},{\"n\":\"2022\",\"v\":\"2022\"},{\"n\":\"2021\",\"v\":\"2021\"},{\"n\":\"2020\",\"v\":\"2020\"},{\"n\":\"2019\",\"v\":\"2019\"},{\"n\":\"2010年代\",\"v\":\"2010年代\"},{\"n\":\"2000年代\",\"v\":\"2000年代\"},{\"n\":\"90年代\",\"v\":\"90年代\"},{\"n\":\"80年代\",\"v\":\"80年代\"},{\"n\":\"70年代\",\"v\":\"70年代\"},{\"n\":\"60年代\",\"v\":\"60年代\"},{\"n\":\"更早\",\"v\":\"更早\"}]}],\"tv\":[{\"key\":\"类型\",\"name\":\"类型\",\"value\":[{\"n\":\"不限\",\"v\":\"\"},{\"n\":\"电视剧\",\"v\":\"电视剧\"},{\"n\":\"综艺\",\"v\":\"综艺\"}]},{\"key\":\"电视剧形式\",\"name\":\"电视剧形式\",\"value\":[{\"n\":\"不限\",\"v\":\"\"},{\"n\":\"喜剧\",\"v\":\"喜剧\"},{\"n\":\"爱情\",\"v\":\"爱情\"},{\"n\":\"悬疑\",\"v\":\"悬疑\"},{\"n\":\"动画\",\"v\":\"动画\"},{\"n\":\"武侠\",\"v\":\"武侠\"},{\"n\":\"古装\",\"v\":\"古装\"},{\"n\":\"家庭\",\"v\":\"家庭\"},{\"n\":\"犯罪\",\"v\":\"犯罪\"},{\"n\":\"科幻\",\"v\":\"科幻\"},{\"n\":\"恐怖\",\"v\":\"恐怖\"},{\"n\":\"历史\",\"v\":\"历史\"},{\"n\":\"战争\",\"v\":\"战争\"},{\"n\":\"动作\",\"v\":\"动作\"},{\"n\":\"冒险\",\"v\":\"冒险\"},{\"n\":\"传记\",\"v\":\"传记\"},{\"n\":\"剧情\",\"v\":\"剧情\"},{\"n\":\"奇幻\",\"v\":\"奇幻\"},{\"n\":\"惊悚\",\"v\":\"惊悚\"},{\"n\":\"灾难\",\"v\":\"灾难\"},{\"n\":\"歌舞\",\"v\":\"歌舞\"},{\"n\":\"音乐\",\"v\":\"音乐\"}]},{\"key\":\"综艺形式\",\"name\":\"综艺形式\",\"value\":[{\"n\":\"不限\",\"v\":\"\"},{\"n\":\"真人秀\",\"v\":\"真人秀\"},{\"n\":\"脱口秀\",\"v\":\"脱口秀\"},{\"n\":\"音乐\",\"v\":\"音乐\"},{\"n\":\"歌舞\",\"v\":\"歌舞\"}]},{\"key\":\"地区\",\"name\":\"地区\",\"value\":[{\"n\":\"全部地区\",\"v\":\"\"},{\"n\":\"华语\",\"v\":\"华语\"},{\"n\":\"欧美\",\"v\":\"欧美\"},{\"n\":\"国外\",\"v\":\"国外\"},{\"n\":\"韩国\",\"v\":\"韩国\"},{\"n\":\"日本\",\"v\":\"日本\"},{\"n\":\"中国大陆\",\"v\":\"中国大陆\"},{\"n\":\"中国香港\",\"v\":\"中国香港\"},{\"n\":\"美国\",\"v\":\"美国\"},{\"n\":\"英国\",\"v\":\"英国\"},{\"n\":\"泰国\",\"v\":\"泰国\"},{\"n\":\"中国台湾\",\"v\":\"中国台湾\"},{\"n\":\"意大利\",\"v\":\"意大利\"},{\"n\":\"法国\",\"v\":\"法国\"},{\"n\":\"德国\",\"v\":\"德国\"},{\"n\":\"西班牙\",\"v\":\"西班牙\"},{\"n\":\"俄罗斯\",\"v\":\"俄罗斯\"},{\"n\":\"瑞典\",\"v\":\"瑞典\"},{\"n\":\"巴西\",\"v\":\"巴西\"},{\"n\":\"丹麦\",\"v\":\"丹麦\"},{\"n\":\"印度\",\"v\":\"印度\"},{\"n\":\"加拿大\",\"v\":\"加拿大\"},{\"n\":\"爱尔兰\",\"v\":\"爱尔兰\"},{\"n\":\"澳大利亚\",\"v\":\"澳大利亚\"}]},{\"key\":\"sort\",\"name\":\"排序\",\"value\":[{\"n\":\"近期热度\",\"v\":\"T\"},{\"n\":\"首播时间\",\"v\":\"R\"},{\"n\":\"高分优先\",\"v\":\"S\"}]},{\"key\":\"年代\",\"name\":\"年代\",\"value\":[{\"n\":\"全部\",\"v\":\"\"},{\"n\":\"2026\",\"v\":\"2026\"},{\"n\":\"2025\",\"v\":\"2025\"},{\"n\":\"2024\",\"v\":\"2024\"},{\"n\":\"2023\",\"v\":\"2023\"},{\"n\":\"2022\",\"v\":\"2022\"},{\"n\":\"2021\",\"v\":\"2021\"},{\"n\":\"2020\",\"v\":\"2020\"},{\"n\":\"2019\",\"v\":\"2019\"},{\"n\":\"2010年代\",\"v\":\"2010年代\"},{\"n\":\"2000年代\",\"v\":\"2000年代\"},{\"n\":\"90年代\",\"v\":\"90年代\"},{\"n\":\"80年代\",\"v\":\"80年代\"},{\"n\":\"70年代\",\"v\":\"70年代\"},{\"n\":\"60年代\",\"v\":\"60年代\"},{\"n\":\"更早\",\"v\":\"更早\"}]},{\"key\":\"平台\",\"name\":\"平台\",\"value\":[{\"n\":\"全部\",\"v\":\"\"},{\"n\":\"腾讯视频\",\"v\":\"腾讯视频\"},{\"n\":\"爱奇艺\",\"v\":\"爱奇艺\"},{\"n\":\"优酷\",\"v\":\"优酷\"},{\"n\":\"湖南卫视\",\"v\":\"湖南卫视\"},{\"n\":\"Netflix\",\"v\":\"Netflix\"},{\"n\":\"HBO\",\"v\":\"HBO\"},{\"n\":\"BBC\",\"v\":\"BBC\"},{\"n\":\"NHK\",\"v\":\"NHK\"},{\"n\":\"CBS\",\"v\":\"CBS\"},{\"n\":\"NBC\",\"v\":\"NBC\"},{\"n\":\"tvN\",\"v\":\"tvN\"}]}],\"rank_list_movie\":[{\"key\":\"榜单\",\"name\":\"榜单\",\"value\":[{\"n\":\"实时热门电影\",\"v\":\"movie_real_time_hotest\"},{\"n\":\"一周口碑电影榜\",\"v\":\"movie_weekly_best\"},{\"n\":\"豆瓣电影Top250\",\"v\":\"movie_top250\"}]}],\"rank_list_tv\":[{\"key\":\"榜单\",\"name\":\"榜单\",\"value\":[{\"n\":\"实时热门电视\",\"v\":\"tv_real_time_hotest\"},{\"n\":\"华语口碑剧集榜\",\"v\":\"tv_chinese_best_weekly\"},{\"n\":\"全球口碑剧集榜\",\"v\":\"tv_global_best_weekly\"},{\"n\":\"国内口碑综艺榜\",\"v\":\"show_chinese_best_weekly\"},{\"n\":\"国外口碑综艺榜\",\"v\":\"show_global_best_weekly\"}]}]}";
}
