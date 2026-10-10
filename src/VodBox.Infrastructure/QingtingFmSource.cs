using System.Text.Json;
using VodBox.Core;

namespace VodBox.Infrastructure;

/// <summary>蜻蜓 FM 的 radioPage、searchResultsPage 和 radio 详情接口。</summary>
public sealed class QingtingFmSource : IResolvingContentSource
{
    private readonly SourceInfo _site;
    private readonly Func<string, CancellationToken, Task<string>> _get;
    private readonly Func<string, CancellationToken, Task<string>> _post;
    public string Key => _site.Key;
    public string Name => _site.Name;
    public QingtingFmSource(SourceInfo site) : this(site, null, null) { }
    internal QingtingFmSource(SourceInfo site, Func<string, CancellationToken, Task<string>>? get, Func<string, CancellationToken, Task<string>>? post)
    { _site = site; _get = get ?? Get; _post = post ?? Post; }
    private static async Task<string> Get(string url, CancellationToken ct)
    { using var http = new DefaultHttp(); return DefaultHttp.Decode(await http.GetBoundedAsync(url, 4 * 1024 * 1024, ct)); }
    private static async Task<string> Post(string body, CancellationToken ct)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://webbff.qtfm.cn/www") { Content = content };
        request.Headers.Referrer = new Uri("https://www.qtfm.cn/");
        using var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }
    private static string Text(JsonElement item, string key) => AppRjSource.Text(item, key);
    private static MediaItem Item(JsonElement item) => new() { Id = Text(item, "id"), Title = Text(item, "title"), Poster = Picture(Text(item, "imgUrl"), Text(item, "cover")), Remarks = Text(item, "desc") };
    private static string Picture(string first, string second)
    { var value = first.Length > 0 ? first : second; return value.StartsWith("//", StringComparison.Ordinal) ? "https:" + value : value; }
    private async Task<JsonDocument> Graph(string query, CancellationToken ct)
    { using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(20)); return JsonDocument.Parse(await _post(JsonSerializer.Serialize(new QingtingGraphRequest(query), SportsJsonContext.Default.QingtingGraphRequest), timeout.Token)); }
    public Task<IReadOnlyList<Category>> GetCategoriesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Category>>(
        [new("217", "广东"), new("99", "浙江"), new("3", "北京"), new("5", "天津"), new("7", "河北"), new("83", "上海"), new("19", "山西"), new("31", "内蒙古"), new("44", "辽宁"), new("59", "吉林"), new("69", "黑龙江"), new("85", "江苏"), new("111", "安徽"), new("129", "福建"), new("139", "江西"), new("151", "山东"), new("169", "河南"), new("187", "湖北"), new("202", "湖南"), new("239", "广西"), new("254", "海南"), new("257", "重庆"), new("259", "四川"), new("281", "贵州"), new("291", "云南"), new("316", "陕西"), new("327", "甘肃"), new("351", "宁夏"), new("357", "新疆"), new("308", "西藏"), new("342", "青海"), new("433", "资讯"), new("442", "音乐"), new("429", "交通"), new("439", "经济"), new("432", "文艺"), new("441", "都市"), new("430", "体育"), new("431", "双语"), new("440", "综合"), new("438", "生活"), new("435", "旅游"), new("436", "曲艺"), new("434", "方言")]);
    public Task<MediaPage> GetHomeAsync(CancellationToken ct = default) => GetItemsAsync("217", 1, null, ct);
    public async Task<MediaPage> GetItemsAsync(string categoryId, int page, IReadOnlyDictionary<string, string>? filters, CancellationToken ct = default)
    {
        if (filters?.TryGetValue("cateId", out var selected) == true) categoryId = selected;
        if (!int.TryParse(categoryId, out var id) || id < 1 || page < 1) throw new InvalidDataException("蜻蜓分类或页码无效。");
        using var doc = await Graph($"{{ radioPage(cid:{id}, page:{page}){{ contents }} }}", ct);
        var list = doc.RootElement.GetProperty("data").GetProperty("radioPage").GetProperty("contents").GetProperty("items");
        return Page(list, page, 12);
    }
    public async Task<MediaPage> SearchAsync(string query, int page, CancellationToken ct = default)
    {
        if (page < 1) throw new InvalidDataException("蜻蜓页码无效。");
        var keyword = JsonSerializer.Serialize(query, SportsJsonContext.Default.String);
        using var doc = await Graph($"{{ searchResultsPage(keyword:{keyword}, page:{page}, include:\"channel_live\") {{ tdk searchData numFound }} }}", ct);
        return Page(doc.RootElement.GetProperty("data").GetProperty("searchResultsPage").GetProperty("searchData"), page, 12);
    }
    private static MediaPage Page(JsonElement list, int page, int size)
    {
        if (list.ValueKind != JsonValueKind.Array) throw new InvalidDataException("蜻蜓列表格式无效。");
        var items = list.EnumerateArray().Select(Item).Where(item => item.Id.Length > 0).ToArray();
        return new MediaPage(items, page, items.Length < size ? page : page + 1);
    }
    public async Task<MediaDetail> GetDetailAsync(string mediaId, CancellationToken ct = default)
    {
        if (!long.TryParse(mediaId, out var id) || id < 0) throw new InvalidDataException("蜻蜓电台编号无效。");
        using var doc = JsonDocument.Parse(await _get("https://webapi.qtfm.cn/api/pc/radio/" + id, ct));
        var album = doc.RootElement.GetProperty("album");
        var episodeId = Text(album, "id");
        if (!long.TryParse(episodeId, out _)) throw new InvalidDataException("蜻蜓电台详情缺少编号。");
        return new MediaDetail { Item = new MediaItem { Id = mediaId, Title = Text(album, "title"), Poster = Text(album, "cover") }, Description = Text(album, "description"), Lines = [new PlaybackLine("蜻蜓FM", "蜻蜓FM", [new Episode(episodeId, Text(album, "title"))])] };
    }
    public Task<PlaybackRequest> ResolvePlaybackAsync(string mediaId, string episodeId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!long.TryParse(episodeId, out var id) || id < 0) throw new InvalidDataException("蜻蜓电台编号无效。");
        return Task.FromResult(new PlaybackRequest { Uri = $"https://lhttp-hw.qtfm.cn/live/{id}/64k.mp3", SourceKey = Key, SourceName = Name, MediaId = mediaId, LineId = "蜻蜓FM", EpisodeId = episodeId, IsLive = true, Headers = new() { ["Referer"] = "https://www.qtfm.cn" } });
    }
}
