using System.Globalization;
using VodBox.Core;

namespace VodBox.Infrastructure;

/// <summary>
/// 豆瓣浏览源（LunaTV /douban 页）：电影 / 剧集 / 综艺 / 动漫 / Top250 / 自定义分类，筛选项与 LunaTV 选择器一致。
/// 卡片的来源键为 <see cref="MediaItem.DoubanSourceKey"/>：打开时按片名聚合搜索全部采集源再播放。
/// 筛选规则：「类型/年代/平台/排序」等多级条件非默认时走 recommend 条件推荐，否则走 recent_hot 热门分类。
/// </summary>
public sealed class DoubanBrowseSource(DoubanClient douban, BangumiClient bangumi, Func<IReadOnlyList<CustomCategory>> customCategories, Func<DateTimeOffset>? now = null) : IContentSource
{
    public const int PageSize = 25;
    public string Key => MediaItem.DoubanSourceKey;
    public string Name => "豆瓣";
    private readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.Now);

    public Task<IReadOnlyList<Category>> GetCategoriesAsync(CancellationToken ct = default)
    {
        var list = new List<Category> { new("movie", "电影"), new("tv", "剧集"), new("show", "综艺"), new("anime", "动漫"), new("top250", "Top250") };
        foreach (var category in customCategories()) list.Add(new Category($"custom:{category.Type}:{category.Query}", category.DisplayName));
        return Task.FromResult<IReadOnlyList<Category>>(list);
    }

    private static FilterValue V(string name, string? value = null) => new(name, value ?? name);
    private static FilterGroup G(string key, string name, IEnumerable<FilterValue> values, string? init = null)
    {
        var list = values.ToList();
        return new FilterGroup(key, name, list, init ?? list[0].Value);
    }

    private IEnumerable<FilterValue> Years()
    {
        yield return V("全部", "all");
        var year = _now().Year;
        for (var y = year; y > year - 6; y--) yield return V(y.ToString(CultureInfo.InvariantCulture));
        foreach (var decade in new[] { "2020年代", "2010年代", "2000年代", "90年代", "80年代", "70年代", "60年代", "更早" }) yield return V(decade);
    }

    private static readonly FilterValue[] Sorts = [V("综合排序", "T"), V("近期热度", "U"), V("首播时间", "R"), V("高分优先", "S")];
    private static FilterValue[] All(params string[] names) => [V("全部", "all"), .. names.Select(name => V(name))];
    private static readonly string[] MovieRegions = ["华语", "欧美", "韩国", "日本", "中国大陆", "美国", "中国香港", "中国台湾", "英国", "法国", "德国", "意大利", "西班牙", "印度", "泰国", "俄罗斯", "加拿大", "澳大利亚", "爱尔兰", "瑞典", "巴西", "丹麦"];
    private static readonly string[] TvRegions = ["华语", "欧美", "国外", "韩国", "日本", "中国大陆", "中国香港", "美国", "英国", "泰国", "中国台湾", "意大利", "法国", "德国", "西班牙", "俄罗斯", "瑞典", "巴西", "丹麦", "印度", "加拿大", "爱尔兰", "澳大利亚"];
    private static readonly string[] Platforms = ["腾讯视频", "爱奇艺", "优酷", "湖南卫视", "Netflix", "HBO", "BBC", "NHK", "CBS", "NBC", "tvN"];

    public Task<IReadOnlyList<FilterGroup>> GetFiltersAsync(string categoryId, CancellationToken ct = default)
    {
        IReadOnlyList<FilterGroup> groups = categoryId switch
        {
            "movie" =>
            [
                G("category", "分类", [V("热门"), V("最新"), V("豆瓣高分"), V("冷门佳片")]),
                G("type", "地区", [V("全部"), V("华语"), V("欧美"), V("韩国"), V("日本")]),
                G("genre", "类型", All("喜剧", "爱情", "动作", "科幻", "悬疑", "犯罪", "惊悚", "冒险", "音乐", "历史", "奇幻", "恐怖", "战争", "传记", "歌舞", "武侠", "情色", "灾难", "西部", "纪录片", "短片")),
                G("region", "国家", All(MovieRegions)),
                G("year", "年代", Years()),
                G("sort", "排序", Sorts),
            ],
            "tv" =>
            [
                G("type", "分类", [V("全部", "tv"), V("国产", "tv_domestic"), V("欧美", "tv_american"), V("日本", "tv_japanese"), V("韩国", "tv_korean"), V("动漫", "tv_animation"), V("纪录片", "tv_documentary")]),
                G("genre", "类型", All("喜剧", "爱情", "悬疑", "武侠", "古装", "家庭", "犯罪", "科幻", "恐怖", "历史", "战争", "动作", "冒险", "传记", "剧情", "奇幻", "惊悚", "灾难", "歌舞", "音乐")),
                G("region", "地区", All(TvRegions)),
                G("year", "年代", Years()),
                G("platform", "平台", All(Platforms)),
                G("sort", "排序", Sorts),
            ],
            "show" =>
            [
                G("type", "分类", [V("全部", "show"), V("国内", "show_domestic"), V("国外", "show_foreign")]),
                G("genre", "类型", All("真人秀", "脱口秀", "音乐", "歌舞")),
                G("region", "地区", All(TvRegions)),
                G("year", "年代", Years()),
                G("platform", "平台", All(Platforms)),
                G("sort", "排序", Sorts),
            ],
            "anime" =>
            [
                G("category", "分类", [V("每日放送"), V("番剧"), V("剧场版")]),
                G("weekday", "星期", [V("周一", "Mon"), V("周二", "Tue"), V("周三", "Wed"), V("周四", "Thu"), V("周五", "Fri"), V("周六", "Sat"), V("周日", "Sun")],
                    _now().DayOfWeek switch { DayOfWeek.Monday => "Mon", DayOfWeek.Tuesday => "Tue", DayOfWeek.Wednesday => "Wed", DayOfWeek.Thursday => "Thu", DayOfWeek.Friday => "Fri", DayOfWeek.Saturday => "Sat", _ => "Sun" }),
                G("label", "类型", All("黑色幽默", "历史", "歌舞", "励志", "恶搞", "治愈", "运动", "后宫", "情色", "国漫", "人性", "悬疑", "恋爱", "魔幻", "科幻")),
                G("region", "地区", All(TvRegions)),
                G("year", "年代", Years()),
                G("sort", "排序", Sorts),
            ],
            _ => [],
        };
        return Task.FromResult(groups);
    }

    public async Task<MediaPage> GetHomeAsync(CancellationToken ct = default) => await GetItemsAsync("movie", 1, null, ct).ConfigureAwait(false);

    public Task<MediaDetail> GetDetailAsync(string mediaId, CancellationToken ct = default) =>
        Task.FromException<MediaDetail>(new NotSupportedException("豆瓣条目没有直接详情，请通过聚合搜索播放。"));

    public Task<MediaPage> SearchAsync(string query, int page, CancellationToken ct = default) =>
        Task.FromResult(new MediaPage([], page, page));

    private static string Get(IReadOnlyDictionary<string, string>? filters, string key, string fallback) =>
        filters is not null && filters.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : fallback;

    private static bool IsSet(IReadOnlyDictionary<string, string>? filters, params string[] keys) =>
        keys.Any(key => Get(filters, key, "all") is not ("all" or "T"));

    public async Task<MediaPage> GetItemsAsync(string categoryId, int page, IReadOnlyDictionary<string, string>? filters, CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        var start = (page - 1) * PageSize;
        var kind = categoryId is "movie" ? "movie" : "tv";
        IReadOnlyList<DoubanItem> items;
        var bangumiDay = false;
        switch (categoryId)
        {
            case "movie":
                items = IsSet(filters, "genre", "region", "year", "sort")
                    ? await douban.GetRecommendsAsync("movie", new DoubanRecommendQuery(Get(filters, "genre", ""), "", Region(filters), Get(filters, "year", ""), "", Get(filters, "sort", "T")), PageSize, start, ct).ConfigureAwait(false)
                    : await douban.GetCategoriesAsync("movie", Get(filters, "category", "热门"), Get(filters, "type", "全部"), PageSize, start, ct).ConfigureAwait(false);
                break;
            case "tv":
            case "show":
                items = IsSet(filters, "genre", "region", "year", "platform", "sort")
                    ? await douban.GetRecommendsAsync("tv", new DoubanRecommendQuery(Get(filters, "genre", ""), categoryId == "show" ? "综艺" : "电视剧", Get(filters, "region", ""), Get(filters, "year", ""), Get(filters, "platform", ""), Get(filters, "sort", "T")), PageSize, start, ct).ConfigureAwait(false)
                    : await douban.GetCategoriesAsync("tv", categoryId, Get(filters, "type", categoryId), PageSize, start, ct).ConfigureAwait(false);
                break;
            case "anime":
                var animeCategory = Get(filters, "category", "每日放送");
                if (animeCategory == "每日放送")
                {
                    bangumiDay = true;
                    var weekday = Get(filters, "weekday", "Mon");
                    var days = page == 1 ? await bangumi.GetCalendarAsync(ct).ConfigureAwait(false) : [];
                    items = days.FirstOrDefault(day => day.Weekday == weekday)?.Items ?? [];
                }
                else
                {
                    var series = animeCategory == "番剧";
                    kind = series ? "tv" : "movie";
                    items = await douban.GetRecommendsAsync(kind, new DoubanRecommendQuery("动画", series ? "电视剧" : "", Get(filters, "region", ""), Get(filters, "year", ""), "", Get(filters, "sort", "T"), Get(filters, "label", "")), PageSize, start, ct).ConfigureAwait(false);
                }
                break;
            case "top250":
                items = start >= 250 ? [] : await douban.GetTop250Async(start, ct).ConfigureAwait(false);
                break;
            default:
                if (!categoryId.StartsWith("custom:", StringComparison.Ordinal)) return new MediaPage([], page, page);
                var parts = categoryId.Split(':', 3);
                kind = parts[1] is "tv" ? "tv" : "movie";
                items = await douban.GetListAsync(parts.Length > 2 ? parts[2] : "", kind, PageSize, start, ct).ConfigureAwait(false);
                break;
        }
        var cards = items.Select(item => ToCard(item, kind, bangumiDay)).ToArray();
        var hasMore = !bangumiDay && items.Count >= PageSize;
        return new MediaPage(cards, page, hasMore ? page + 1 : page);
    }

    /// <summary>电影的「地区」与「国家」合并：国家优先，否则用地区快捷项。</summary>
    private static string Region(IReadOnlyDictionary<string, string>? filters)
    {
        var country = Get(filters, "region", "all");
        if (country != "all") return country;
        var quick = Get(filters, "type", "全部");
        return quick == "全部" ? "" : quick;
    }

    public static MediaItem ToCard(DoubanItem item, string kind, bool bangumi = false) => new()
    {
        Id = item.Id,
        Title = item.Title,
        Poster = item.Poster,
        Year = item.Year.Length > 0 ? item.Year : null,
        Rating = item.Rate.Length > 0 ? item.Rate : null,
        Remarks = item.Rate.Length > 0 ? "★ " + item.Rate : null,
        SourceKey = MediaItem.DoubanSourceKey,
        SourceName = bangumi ? "Bangumi" : "豆瓣",
        DoubanId = bangumi ? null : item.Id,
        Kind = kind,
    };
}
