using System.Text.Json;
using VodBox.Core;

namespace VodBox.Infrastructure;

/// <summary>919 体育的赛事列表和直播线路接口。</summary>
public sealed class Sports919Source : IResolvingContentSource
{
    private const string Host = "https://01cs01.fusk39cd.com";
    private readonly SourceInfo _site;
    private readonly Func<string, CancellationToken, Task<string>> _get;
    public string Key => _site.Key;
    public string Name => _site.Name;
    public Sports919Source(SourceInfo site) : this(site, null) { }
    internal Sports919Source(SourceInfo site, Func<string, CancellationToken, Task<string>>? get)
    { _site = site; _get = get ?? Get; }
    private static async Task<string> Get(string url, CancellationToken ct)
    { using var http = new DefaultHttp(); return DefaultHttp.Decode(await http.GetBoundedAsync(url, 4 * 1024 * 1024, ct)); }
    private async Task<JsonElement> Request(string path, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var doc = JsonDocument.Parse(await _get(Host + path, timeout.Token));
        if (doc.RootElement.GetProperty("code").GetInt32() != 200) throw new InvalidDataException("919 体育接口失败。");
        return doc.RootElement.GetProperty("data").Clone();
    }
    private static string Text(JsonElement item, string key) => AppRjSource.Text(item, key);
    public Task<IReadOnlyList<Category>> GetCategoriesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Category>>([new("1", "全部"), new("2", "足球"), new("3", "篮球")]);
    public Task<MediaPage> GetHomeAsync(CancellationToken ct = default) => GetItemsAsync("1", 1, null, ct);
    public async Task<MediaPage> GetItemsAsync(string categoryId, int page, IReadOnlyDictionary<string, string>? filters, CancellationToken ct = default)
    {
        if (categoryId is not ("1" or "2" or "3") || page < 1) throw new InvalidDataException("919 体育分类或页码无效。");
        if (page > 1) return new MediaPage([], page, page);
        var data = (await Request("/api/web/live_lists/" + categoryId, ct)).GetProperty("data");
        if (data.ValueKind != JsonValueKind.Array) throw new InvalidDataException("919 体育赛事列表格式无效。");
        var items = data.EnumerateArray().Where(item => item.TryGetProperty("tournament_id", out _)).Select(item => new MediaItem { Id = Text(item, "type") + "|" + Text(item, "tournament_id") + "|" + Text(item, "member_id"), Title = Text(item, "home_team_zh") + " VS " + Text(item, "away_team_zh"), Poster = Text(item, "cover"), Remarks = Text(item, "league_name_zh") }).ToArray();
        return new MediaPage(items, 1, 1);
    }
    public Task<MediaPage> SearchAsync(string query, int page, CancellationToken ct = default) => throw new NotSupportedException("919 体育没有搜索入口。");
    public async Task<MediaDetail> GetDetailAsync(string mediaId, CancellationToken ct = default)
    {
        var parts = mediaId.Split('|');
        if (parts.Length != 3 || !System.Text.RegularExpressions.Regex.IsMatch(parts[0], "^[a-zA-Z0-9_-]+$") || parts.Skip(1).Any(part => !long.TryParse(part, out var value) || value < 0)) throw new InvalidDataException("919 体育赛事编号无效。");
        var data = await Request($"/api/web/live_lists/{parts[0]}/detail/{parts[1]}?member_id={parts[2]}", ct);
        var detail = data.GetProperty("detail"); var more = data.GetProperty("more");
        if (more.ValueKind != JsonValueKind.Array) throw new InvalidDataException("919 体育线路格式无效。");
        var lines = new List<PlaybackLine>();
        foreach (var (stream, index) in more.EnumerateArray().Select((stream, index) => (stream, index)))
        {
            var episodes = new List<Episode>();
            foreach (var (field, label) in new[] { ("screen_url", "线路一"), ("screen_url_m3u8", "线路二") })
            {
                var url = Text(stream, field);
                if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") episodes.Add(new Episode(index + ":" + label, label, url));
            }
            if (episodes.Count > 0) lines.Add(new PlaybackLine(index.ToString(), Text(stream, "username"), episodes));
        }
        if (lines.Count == 0) throw new InvalidDataException("919 体育没有可播放线路。");
        return new MediaDetail { Item = new MediaItem { Id = mediaId, Title = Text(detail, "home_team_zh") + " VS " + Text(detail, "away_team_zh") }, Description = Text(detail, "room_notice"), Lines = lines };
    }
    public async Task<PlaybackRequest> ResolvePlaybackAsync(string mediaId, string episodeId, CancellationToken ct = default)
    {
        var detail = await GetDetailAsync(mediaId, ct);
        var parts = episodeId.Split(':');
        if (parts.Length != 2) throw new InvalidDataException("919 体育选集编号无效。");
        var line = detail.Lines.SingleOrDefault(item => item.Id == parts[0]);
        var episode = line?.Episodes.SingleOrDefault(item => item.Id == episodeId) ?? throw new InvalidDataException("919 体育选集不存在。");
        return new PlaybackRequest { Uri = episode.Uri!, Title = detail.Item.Title, SourceKey = Key, SourceName = Name, MediaId = mediaId, LineId = line!.Id, EpisodeId = episodeId, IsLive = true };
    }
}
