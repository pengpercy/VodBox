using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using VodBox.Core;

namespace VodBox.Infrastructure;

/// <summary>
/// 豆瓣数据客户端（LunaTV douban.client.ts + /api/douban*）：热门分类 recent_hot、标签列表 search_subjects、
/// 条件推荐 recommend、Top250、片名联想（海报刮削）。代理方式与 LunaTV 一致：
/// direct 本机直连（带豆瓣 Referer/Origin）、cmliussss 腾讯/阿里 CDN 镜像域名、cors 代理前缀、自定义代理前缀。
/// </summary>
public sealed partial class DoubanClient : IDisposable
{
    public const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/121.0.0.0 Safari/537.36";
    public const string Referer = "https://movie.douban.com/";
    private readonly HttpClient _http;
    private readonly Func<SiteSettings> _settings;

    public DoubanClient(Func<SiteSettings> settings, HttpMessageHandler? handler = null)
    {
        _settings = settings;
        _http = new HttpClient(handler ?? new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All, ConnectTimeout = TimeSpan.FromSeconds(6) })
            { Timeout = TimeSpan.FromSeconds(10) };
    }

    // ---------- 代理 ----------

    /// <summary>按数据代理方式改写豆瓣请求地址。</summary>
    public static string ProxyUrl(string target, string proxyType, string customProxy) => proxyType switch
    {
        DoubanProxyTypes.TencentCdn => target.Replace("://m.douban.com/", "://m.douban.cmliussss.net/").Replace("://movie.douban.com/", "://movie.douban.cmliussss.net/"),
        DoubanProxyTypes.AliCdn => target.Replace("://m.douban.com/", "://m.douban.cmliussss.com/").Replace("://movie.douban.com/", "://movie.douban.cmliussss.com/"),
        DoubanProxyTypes.CorsProxyZwei => "https://ciao-cors.is-an.org/" + Uri.EscapeDataString(target),
        DoubanProxyTypes.CorsAnywhere => "https://cors-anywhere.com/" + target,
        DoubanProxyTypes.Custom when !string.IsNullOrWhiteSpace(customProxy) => customProxy.Trim() + Uri.EscapeDataString(target),
        _ => target,
    };

    /// <summary>
    /// 豆瓣图片代理（LunaTV processImageUrl）：只处理 doubanio.com 图片。
    /// direct = 本机直连并附带豆瓣 Referer（用 <see cref="ImageAddress"/> 的 @Referer= 后缀交给海报缓存）。
    /// </summary>
    public static string ProcessImage(string? url, SiteSettings settings)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";
        url = url.Trim().Replace("http://", "https://");
        if (!url.Contains("doubanio.com", StringComparison.OrdinalIgnoreCase)) return url;
        return settings.DoubanImageProxyType switch
        {
            DoubanImageProxyTypes.TencentCdn => DoubanImageHost().Replace(url, "img.doubanio.cmliussss.net"),
            DoubanImageProxyTypes.AliCdn => DoubanImageHost().Replace(url, "img.doubanio.cmliussss.com"),
            DoubanImageProxyTypes.Custom when !string.IsNullOrWhiteSpace(settings.DoubanImageProxy) => settings.DoubanImageProxy.Trim() + Uri.EscapeDataString(url),
            _ => url + "@Referer=" + Referer,
        };
    }

    public string Image(string? url) => ProcessImage(url, _settings());

    /// <summary>
    /// 同一张豆瓣海报的大图地址（s/m 规格 → l，约 1080px 宽），用于整宽横幅；非豆瓣规格路径原样返回。
    /// 代理改写后的地址（换域名或整段转义）同样适用。
    /// </summary>
    public static string LargeImage(string? url) => string.IsNullOrEmpty(url) ? url ?? "" : DoubanPhotoSize().Replace(url, "$1view$1photo$1l$3$1");

    private async Task<string> GetAsync(string target, CancellationToken ct)
    {
        var settings = _settings();
        using var request = new HttpRequestMessage(HttpMethod.Get, ProxyUrl(target, settings.DoubanProxyType, settings.DoubanProxy));
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Headers.TryAddWithoutValidation("Referer", Referer);
        request.Headers.TryAddWithoutValidation("Origin", "https://movie.douban.com");
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"豆瓣请求失败：HTTP {(int)response.StatusCode}", null, response.StatusCode);
        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    private static string Rate(JsonElement item)
    {
        if (item.TryGetProperty("rating", out var rating) && rating.ValueKind == JsonValueKind.Object
            && rating.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Number && value.GetDouble() > 0)
            return value.GetDouble().ToString("0.0", CultureInfo.InvariantCulture);
        return "";
    }

    private static string Str(JsonElement item, string name) => item.TryGetProperty(name, out var value) ? value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.Number => value.GetRawText(),
        _ => "",
    } : "";

    private static string Pic(JsonElement item) => item.TryGetProperty("pic", out var pic) && pic.ValueKind == JsonValueKind.Object
        ? (Str(pic, "normal") is { Length: > 0 } normal ? normal : Str(pic, "large")) : "";

    // ---------- 接口 ----------

    /// <summary>热门分类（recent_hot）：kind=movie|tv；电影 category=热门/最新/豆瓣高分/冷门佳片，type=全部/华语/欧美/韩国/日本；剧集/综艺 category=tv|show，type=tv_xxx|show_xxx。</summary>
    public async Task<IReadOnlyList<DoubanItem>> GetCategoriesAsync(string kind, string category, string type, int limit = 25, int start = 0, CancellationToken ct = default)
    {
        var url = $"https://m.douban.com/rexxar/api/v2/subject/recent_hot/{kind}?start={start}&limit={limit}&category={Uri.EscapeDataString(category)}&type={Uri.EscapeDataString(type)}";
        using var document = JsonDocument.Parse(await GetAsync(url, ct).ConfigureAwait(false));
        if (!document.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return [];
        return items.EnumerateArray().Select(item => new DoubanItem(Str(item, "id"), Str(item, "title"), Image(Pic(item)), Rate(item),
            FirstYear(Str(item, "card_subtitle")))).Where(item => item.Id.Length > 0).ToArray();
    }

    /// <summary>标签列表（search_subjects，自定义分类用）。</summary>
    public async Task<IReadOnlyList<DoubanItem>> GetListAsync(string tag, string type, int limit = 25, int start = 0, CancellationToken ct = default)
    {
        var url = $"https://movie.douban.com/j/search_subjects?type={type}&tag={Uri.EscapeDataString(tag)}&sort=recommend&page_limit={limit}&page_start={start}";
        using var document = JsonDocument.Parse(await GetAsync(url, ct).ConfigureAwait(false));
        if (!document.RootElement.TryGetProperty("subjects", out var subjects) || subjects.ValueKind != JsonValueKind.Array) return [];
        return subjects.EnumerateArray().Select(item => new DoubanItem(Str(item, "id"), Str(item, "title"), Image(Str(item, "cover")), Str(item, "rate"), ""))
            .Where(item => item.Id.Length > 0).ToArray();
    }

    /// <summary>条件推荐（recommend）：与 LunaTV fetchDoubanRecommends 相同的 tags/selected_categories 组装。</summary>
    public async Task<IReadOnlyList<DoubanItem>> GetRecommendsAsync(string kind, DoubanRecommendQuery query, int limit = 25, int start = 0, CancellationToken ct = default)
    {
        static string Clean(string? value) => value is null or "all" or "全部" ? "" : value;
        var category = Clean(query.Category);
        var format = Clean(query.Format);
        var region = Clean(query.Region);
        var selected = new Dictionary<string, string> { ["类型"] = category };
        if (format.Length > 0) selected["形式"] = format;
        if (region.Length > 0) selected["地区"] = region;
        var tags = new List<string>();
        if (category.Length > 0) tags.Add(category);
        if (category.Length == 0 && format.Length > 0) tags.Add(format);
        foreach (var value in new[] { Clean(query.Label), region, Clean(query.Year), Clean(query.Platform) }) if (value.Length > 0) tags.Add(value);
        var parameters = new List<string>
        {
            "refresh=0", $"start={start}", $"count={limit}",
            // 与 JSON.stringify 一致：中文不转义（再整体做 URL 编码）。
            "selected_categories=" + Uri.EscapeDataString("{" + string.Join(",", selected.Select(pair => $"\"{JsonText(pair.Key)}\":\"{JsonText(pair.Value)}\"")) + "}"),
            "uncollect=false", "score_range=" + Uri.EscapeDataString("0,10"), "tags=" + Uri.EscapeDataString(string.Join(",", tags)),
        };
        if (query.Sort is { Length: > 0 } sort && sort != "T") parameters.Add("sort=" + sort);
        var url = $"https://m.douban.com/rexxar/api/v2/{kind}/recommend?" + string.Join("&", parameters);
        using var document = JsonDocument.Parse(await GetAsync(url, ct).ConfigureAwait(false));
        if (!document.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return [];
        return items.EnumerateArray().Where(item => Str(item, "type") is "movie" or "tv")
            .Select(item => new DoubanItem(Str(item, "id"), Str(item, "title"), Image(Pic(item)), Rate(item), Str(item, "year")))
            .Where(item => item.Id.Length > 0).ToArray();
    }

    /// <summary>豆瓣电影 Top250（HTML 页，每页 25 条）。</summary>
    public async Task<IReadOnlyList<DoubanItem>> GetTop250Async(int start = 0, CancellationToken ct = default)
    {
        var html = await GetAsync($"https://movie.douban.com/top250?start={start}&filter=", ct).ConfigureAwait(false);
        return Top250Item().Matches(html).Select(match => new DoubanItem(match.Groups[1].Value, WebUtility.HtmlDecode(match.Groups[2].Value),
            Image(match.Groups[3].Value.Replace("http:", "https:")), match.Groups[4].Value, "")).ToArray();
    }

    /// <summary>片名联想（subject_suggest）：采集源海报缺失/失效时据此刮削豆瓣海报与豆瓣 id。</summary>
    public async Task<IReadOnlyList<DoubanSuggestion>> SuggestAsync(string title, CancellationToken ct = default)
    {
        using var document = JsonDocument.Parse(await GetAsync("https://movie.douban.com/j/subject_suggest?q=" + Uri.EscapeDataString(title.Trim()), ct).ConfigureAwait(false));
        if (document.RootElement.ValueKind != JsonValueKind.Array) return [];
        return document.RootElement.EnumerateArray().Select(item => new DoubanSuggestion(Str(item, "id"), Str(item, "title"), Str(item, "sub_title"),
            Image(Str(item, "img")), Str(item, "year"), Str(item, "type"), Str(item, "episode"))).Where(item => item.Id.Length > 0).ToArray();
    }

    /// <summary>
    /// 海报刮削：按片名（+年份）在豆瓣联想里找最匹配的条目。标题一致优先，年份已知时须一致；找不到返回 null。
    /// </summary>
    public async Task<DoubanSuggestion?> ScrapeAsync(string title, string? year, CancellationToken ct = default)
    {
        var suggestions = await SuggestAsync(title, ct).ConfigureAwait(false);
        static string Normalize(string value) => value.Replace(" ", "").Trim();
        var known = year is { Length: 4 } ? year : null;
        return suggestions.FirstOrDefault(item => Normalize(item.Title) == Normalize(title) && (known is null || item.Year == known))
            ?? suggestions.FirstOrDefault(item => known is not null && item.Year == known && (Normalize(item.Title).Contains(Normalize(title)) || Normalize(title).Contains(Normalize(item.Title))))
            ?? (known is null ? suggestions.FirstOrDefault(item => Normalize(item.Title) == Normalize(title)) : null);
    }

    private static string JsonText(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string FirstYear(string text) => FourDigits().Match(text) is { Success: true } match ? match.Value : "";

    [GeneratedRegex(@"img\d+\.doubanio\.com")] private static partial Regex DoubanImageHost();
    [GeneratedRegex(@"(/|%2F)view\1photo\1(s|m)(_ratio_poster)?\1", RegexOptions.IgnoreCase)] private static partial Regex DoubanPhotoSize();
    [GeneratedRegex(@"(\d{4})")] private static partial Regex FourDigits();
    [GeneratedRegex(@"<div class=""item"">[\s\S]*?<a[^>]+href=""https?://movie\.douban\.com/subject/(\d+)/""[\s\S]*?<img[^>]+alt=""([^""]+)""[^>]*src=""([^""]+)""[\s\S]*?<span class=""rating_num""[^>]*>([^<]*)</span>[\s\S]*?</div>")]
    private static partial Regex Top250Item();

    public void Dispose() => _http.Dispose();
}

/// <summary>条件推荐筛选（全部值为空 / "all"）。</summary>
public sealed record DoubanRecommendQuery(string? Category = null, string? Format = null, string? Region = null, string? Year = null,
    string? Platform = null, string? Sort = null, string? Label = null);

public sealed record DoubanSuggestion(string Id, string Title, string SubTitle, string Poster, string Year, string Type, string Episode);

/// <summary>Bangumi 每日放送（api.bgm.tv/calendar）。</summary>
public sealed class BangumiClient(HttpClient? http = null) : IDisposable
{
    private readonly HttpClient _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

    public sealed record Day(string Weekday, string WeekdayCn, IReadOnlyList<DoubanItem> Items);

    public async Task<IReadOnlyList<Day>> GetCalendarAsync(CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.bgm.tv/calendar");
        request.Headers.TryAddWithoutValidation("User-Agent", DoubanClient.UserAgent);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var days = new List<Day>();
        foreach (var day in document.RootElement.EnumerateArray())
        {
            var weekday = day.GetProperty("weekday");
            var items = new List<DoubanItem>();
            foreach (var item in day.GetProperty("items").EnumerateArray())
            {
                if (!item.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Object) continue;
                string Image(string name) => images.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
                var poster = new[] { "large", "common", "medium", "small", "grid" }.Select(Image).FirstOrDefault(value => value.Length > 0) ?? "";
                var name = item.TryGetProperty("name_cn", out var cn) && cn.GetString() is { Length: > 0 } chinese ? chinese : item.GetProperty("name").GetString() ?? "";
                var score = item.TryGetProperty("rating", out var rating) && rating.ValueKind == JsonValueKind.Object && rating.TryGetProperty("score", out var s) && s.ValueKind == JsonValueKind.Number
                    ? s.GetDouble().ToString("0.0", CultureInfo.InvariantCulture) : "";
                var air = item.TryGetProperty("air_date", out var date) && date.ValueKind == JsonValueKind.String ? date.GetString() ?? "" : "";
                items.Add(new DoubanItem(item.GetProperty("id").GetRawText(), name, poster.Replace("http://", "https://"), score, air.Split('-')[0]));
            }
            days.Add(new Day(weekday.GetProperty("en").GetString() ?? "", weekday.TryGetProperty("cn", out var cnDay) ? cnDay.GetString() ?? "" : "", items));
        }
        return days;
    }

    public void Dispose() => _http.Dispose();
}
