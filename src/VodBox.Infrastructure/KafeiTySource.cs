using System.Text.Json;
using VodBox.Core;

namespace VodBox.Infrastructure;

/// <summary>咖啡体育赛程和房间的 JSON 接口。</summary>
public sealed class KafeiTySource : IResolvingContentSource
{
    private const string Host = "https://kafeizhibo.cc";
    private readonly SourceInfo _site;
    private readonly Func<string, CancellationToken, Task<string>> _get;
    public string Key => _site.Key;
    public string Name => _site.Name;
    public KafeiTySource(SourceInfo site) : this(site, null) { }
    internal KafeiTySource(SourceInfo site, Func<string, CancellationToken, Task<string>>? get)
    { _site = site; _get = get ?? Get; }
    private static async Task<string> Get(string url, CancellationToken ct)
    { using var http = new DefaultHttp(); return DefaultHttp.Decode(await http.GetBoundedAsync(url, 4 * 1024 * 1024, ct)); }
    private async Task<JsonDocument> Request(string path, CancellationToken ct)
    { using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(20)); return JsonDocument.Parse(await _get(Host + path, timeout.Token)); }
    private static string Text(JsonElement item, string key) => AppRjSource.Text(item, key);
    public Task<IReadOnlyList<Category>> GetCategoriesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Category>>([new("hot", "热门"), new("1", "足球"), new("2", "篮球")]);
    public Task<MediaPage> GetHomeAsync(CancellationToken ct = default) => GetItemsAsync("hot", 1, null, ct);
    public async Task<MediaPage> GetItemsAsync(string categoryId, int page, IReadOnlyDictionary<string, string>? filters, CancellationToken ct = default)
    {
        if (categoryId is not ("hot" or "1" or "2") || page < 1) throw new InvalidDataException("咖啡体育分类或页码无效。");
        using var doc = await Request($"/api/v1/schedule?type={categoryId}&page={page}&size=30", ct);
        var root = doc.RootElement;
        if (root.GetProperty("code").GetInt32() != 200) throw new InvalidDataException("咖啡体育赛程接口失败。");
        var list = root.GetProperty("data");
        if (list.ValueKind != JsonValueKind.Array) throw new InvalidDataException("咖啡体育赛程格式无效。");
        var items = list.EnumerateArray().Select(item => new MediaItem { Id = item.TryGetProperty("archor", out var anchor) && anchor.ValueKind == JsonValueKind.Object ? Text(anchor, "room_id") : "", Title = Text(item, "home_team") + " VS " + Text(item, "away_team"), Poster = Text(item, "home_team_logo"), Remarks = Text(item, "league_name") }).Where(item => item.Id.Length > 0).ToArray();
        return new MediaPage(items, page, items.Length < 30 ? page : page + 1);
    }
    public Task<MediaPage> SearchAsync(string query, int page, CancellationToken ct = default) => throw new NotSupportedException("咖啡体育没有搜索入口。");
    public async Task<MediaDetail> GetDetailAsync(string mediaId, CancellationToken ct = default)
    {
        if (!long.TryParse(mediaId, out var id) || id < 0) throw new InvalidDataException("咖啡体育房间编号无效。");
        using var doc = await Request("/api/v1/room/" + id, ct);
        var root = doc.RootElement;
        if (root.GetProperty("code").GetInt32() != 200) throw new InvalidDataException("咖啡体育房间接口失败。");
        var data = root.GetProperty("data"); var room = data.GetProperty("room_info");
        var streams = data.GetProperty("signals");
        if (streams.ValueKind != JsonValueKind.Array) throw new InvalidDataException("咖啡体育线路格式无效。");
        var episodes = streams.EnumerateArray().Select((signal, index) => new Episode(index.ToString(), Text(signal, "name"), Text(signal, "stream_url"))).Where(episode => Uri.TryCreate(episode.Uri, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https").ToArray();
        if (episodes.Length == 0) throw new InvalidDataException("咖啡体育没有可播放线路。");
        return new MediaDetail { Item = new MediaItem { Id = mediaId, Title = Text(room, "title"), Remarks = Text(room, "league") }, Description = Text(room, "home_team") + " VS " + Text(room, "away_team"), Lines = [new PlaybackLine("咖啡体育", "咖啡体育", episodes)] };
    }
    public async Task<PlaybackRequest> ResolvePlaybackAsync(string mediaId, string episodeId, CancellationToken ct = default)
    {
        var detail = await GetDetailAsync(mediaId, ct);
        var episode = detail.Lines[0].Episodes.SingleOrDefault(item => item.Id == episodeId) ?? throw new InvalidDataException("咖啡体育线路不存在。");
        return new PlaybackRequest { Uri = episode.Uri!, Title = detail.Item.Title, SourceKey = Key, SourceName = Name, MediaId = mediaId, LineId = "咖啡体育", EpisodeId = episodeId, IsLive = true };
    }
}
