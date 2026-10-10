using System.Text.Json;
using VodBox.Core;

namespace VodBox.Infrastructure;

/// <summary>球通体育的 room/page 和 room/info JSON 接口。</summary>
public sealed class QiutongTySource : IResolvingContentSource
{
    private const string Api = "https://aapi2.xbncs.com/api";
    private readonly SourceInfo _site;
    private readonly Func<string, CancellationToken, Task<string>> _get;
    public string Key => _site.Key;
    public string Name => _site.Name;

    public QiutongTySource(SourceInfo site) : this(site, null) { }
    internal QiutongTySource(SourceInfo site, Func<string, CancellationToken, Task<string>>? get)
    { _site = site; _get = get ?? Get; }
    private static async Task<string> Get(string url, CancellationToken ct)
    { using var http = new DefaultHttp(); return DefaultHttp.Decode(await http.GetBoundedAsync(url, 4 * 1024 * 1024, ct)); }
    private async Task<JsonDocument> Request(string path, CancellationToken ct)
    { using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(20)); return JsonDocument.Parse(await _get(Api + path, timeout.Token)); }
    private static JsonElement Data(JsonDocument doc) => doc.RootElement.GetProperty("data");
    private static string Text(JsonElement item, string key) => AppRjSource.Text(item, key);
    private static MediaItem Item(JsonElement item) => new() { Id = Text(item, "roomId"), Title = Text(item, "title"), Poster = Text(item, "cover"), Remarks = Text(item, "navName") };

    public Task<IReadOnlyList<Category>> GetCategoriesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Category>>([new("-1", "全部"), new("1", "足球"), new("2", "篮球")]);
    public Task<MediaPage> GetHomeAsync(CancellationToken ct = default) => GetItemsAsync("-1", 1, null, ct);
    public async Task<MediaPage> GetItemsAsync(string categoryId, int page, IReadOnlyDictionary<string, string>? filters, CancellationToken ct = default)
    {
        if (page < 1 || categoryId is not ("-1" or "1" or "2")) throw new InvalidDataException("球通体育分类或页码无效。");
        using var doc = await Request($"/room/page?roomType=&navId={(categoryId == "-1" ? "" : categoryId)}&roomId=&word=&page={page}&pageSize=30&channelId=3&platform=1", ct);
        var list = Data(doc).GetProperty("list");
        if (list.ValueKind != JsonValueKind.Array) throw new InvalidDataException("球通体育列表格式无效。");
        var items = list.EnumerateArray().Select(Item).Where(item => item.Id.Length > 0).ToArray();
        return new MediaPage(items, page, items.Length < 30 ? page : page + 1);
    }
    public Task<MediaPage> SearchAsync(string query, int page, CancellationToken ct = default) =>
        throw new NotSupportedException("球通体育没有搜索入口。");
    public async Task<MediaDetail> GetDetailAsync(string mediaId, CancellationToken ct = default)
    {
        if (!long.TryParse(mediaId, out var id) || id < 0) throw new InvalidDataException("球通体育房间编号无效。");
        using var doc = await Request($"/room/info?roomId={id}&channelId=3&platform=1", ct);
        var data = Data(doc);
        var lines = new List<PlaybackLine>();
        foreach (var (field, label) in new[] { ("pushUrl", "flv"), ("pullUrl", "m3u8") })
        {
            var url = Text(data, field);
            if (Uri.TryCreate(url, UriKind.Absolute, out var parsed) && parsed.Scheme is "http" or "https")
                lines.Add(new PlaybackLine(label, label, [new Episode(label, label, url)]));
        }
        if (lines.Count == 0) throw new InvalidDataException("球通体育没有可播放地址。");
        return new MediaDetail { Item = new MediaItem { Id = mediaId, Title = Text(data, "title"), Poster = Text(data, "cover"), TypeName = Text(data, "nickName") }, Description = Text(data, "description"), Lines = lines };
    }
    public async Task<PlaybackRequest> ResolvePlaybackAsync(string mediaId, string episodeId, CancellationToken ct = default)
    {
        var detail = await GetDetailAsync(mediaId, ct);
        var line = detail.Lines.SingleOrDefault(line => line.Id == episodeId) ?? throw new InvalidDataException("球通体育线路不存在。");
        return new PlaybackRequest { Uri = line.Episodes[0].Uri!, Title = detail.Item.Title, SourceKey = Key, SourceName = Name, MediaId = mediaId, LineId = line.Id, EpisodeId = episodeId, IsLive = true };
    }
}
