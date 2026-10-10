using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VodBox.Core;

namespace VodBox.Infrastructure;

/// <summary>瓜子体育的加密赛程与详情接口。</summary>
public sealed class GuaziTySource : IResolvingContentSource
{
    private const string Api = "https://api.46d5umpk.com";
    private static readonly byte[] KeyBytes = Encoding.UTF8.GetBytes("KANGEQIU@8868!~.");
    private static readonly byte[] IvBytes = Encoding.UTF8.GetBytes("0200010900030207");
    private readonly SourceInfo _site;
    private readonly Func<string, string, CancellationToken, Task<string>> _post;
    private readonly Func<DateTimeOffset> _now;
    public string Key => _site.Key;
    public string Name => _site.Name;
    public GuaziTySource(SourceInfo site) : this(site, null, null) { }
    internal GuaziTySource(SourceInfo site, Func<string, string, CancellationToken, Task<string>>? post, Func<DateTimeOffset>? now)
    { _site = site; _post = post ?? Post; _now = now ?? (() => DateTimeOffset.UtcNow); }
    internal static string Encrypt(string plain)
    {
        using var aes = Aes.Create(); aes.Key = KeyBytes; aes.IV = IvBytes; aes.Mode = CipherMode.CBC;
        return Convert.ToBase64String(aes.EncryptCbc(Encoding.UTF8.GetBytes(plain), IvBytes, PaddingMode.PKCS7));
    }
    internal static string Decrypt(string cipher)
    {
        using var aes = Aes.Create(); aes.Key = KeyBytes; aes.IV = IvBytes; aes.Mode = CipherMode.CBC;
        return Encoding.UTF8.GetString(aes.DecryptCbc(Convert.FromBase64String(cipher), IvBytes, PaddingMode.PKCS7));
    }
    private static async Task<string> Post(string path, string cipher, CancellationToken ct)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        using var request = new HttpRequestMessage(HttpMethod.Post, Api + path + "?parameter=key") { Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["parameter"] = cipher }) };
        request.Headers.TryAddWithoutValidation("User-Agent", "okhttp/3.12.0");
        request.Headers.TryAddWithoutValidation("user-platform", "null");
        request.Headers.TryAddWithoutValidation("client-version", "3.0.1.1");
        using var response = await client.SendAsync(request, ct); response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }
    private async Task<JsonDocument> Request(string path, string plain, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var outer = JsonDocument.Parse(await _post(path, Encrypt(plain), timeout.Token));
        return JsonDocument.Parse(Decrypt(outer.RootElement.GetProperty("data").GetString()!));
    }
    private static string Text(JsonElement item, string key) => AppRjSource.Text(item, key);
    public Task<IReadOnlyList<Category>> GetCategoriesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Category>>([new("hot", "热门"), new("nba", "NBA"), new("football", "足球"), new("basketball", "篮球")]);
    public Task<MediaPage> GetHomeAsync(CancellationToken ct = default) => GetItemsAsync("hot", 1, null, ct);
    public async Task<MediaPage> GetItemsAsync(string categoryId, int page, IReadOnlyDictionary<string, string>? filters, CancellationToken ct = default)
    {
        if (page < 1 || categoryId is not ("hot" or "nba" or "football" or "basketball")) throw new InvalidDataException("瓜子体育分类或页码无效。");
        if (page > 1) return new MediaPage([], page, page);
        var (hot, tag, type) = categoryId switch { "nba" => ("0", "37", "0"), "football" => ("0", "0", "1"), "basketball" => ("0", "0", "2"), _ => ("1", "0", "0") };
        using var doc = await Request("/gz/live/sports", JsonSerializer.Serialize(new GuaziFilterRequest("0", hot, tag, type), SportsJsonContext.Default.GuaziFilterRequest), ct);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("瓜子体育赛程格式无效。");
        var items = new List<MediaItem>();
        foreach (var entry in doc.RootElement.EnumerateArray())
        {
            var time = DateTimeOffset.FromUnixTimeSeconds(entry.GetProperty("match_time").GetInt64());
            if (time < _now().AddDays(-1) || entry.GetProperty("m_status").GetInt32() >= 2) continue;
            var home = entry.GetProperty("home"); var away = entry.GetProperty("visiting");
            items.Add(new MediaItem { Id = Text(entry, "mid"), Title = Text(home, "name") + " vs " + Text(away, "name"), Poster = Text(home, "logo"), Remarks = Text(entry, "event_name") + " " + time.ToLocalTime().ToString("MM-dd HH:mm") + " " + Text(entry, "match_status_info") });
        }
        return new MediaPage(items, 1, 1);
    }
    public Task<MediaPage> SearchAsync(string query, int page, CancellationToken ct = default) => throw new NotSupportedException("瓜子体育没有搜索入口。");
    public async Task<MediaDetail> GetDetailAsync(string mediaId, CancellationToken ct = default)
    {
        if (!long.TryParse(mediaId, out var id) || id < 0) throw new InvalidDataException("瓜子体育比赛编号无效。");
        using var doc = await Request("/gz/live/detail", JsonSerializer.Serialize(new GuaziDetailRequest(id.ToString()), SportsJsonContext.Default.GuaziDetailRequest), ct);
        var entry = doc.RootElement; var home = entry.GetProperty("home"); var away = entry.GetProperty("visiting");
        var lines = entry.GetProperty("live_line").EnumerateArray().Select((line, index) => new Episode(index.ToString(), Text(line, "name"), Text(line, "m3u8"))).Where(episode => Uri.TryCreate(episode.Uri, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https").ToArray();
        if (lines.Length == 0) throw new InvalidDataException("瓜子体育没有可播放线路。");
        var title = Text(home, "name") + " vs " + Text(away, "name");
        return new MediaDetail { Item = new MediaItem { Id = mediaId, Title = title, Poster = Text(home, "logo"), Remarks = Text(entry, "match_status_info") }, Lines = [new PlaybackLine("瓜子", "瓜子", lines)] };
    }
    public async Task<PlaybackRequest> ResolvePlaybackAsync(string mediaId, string episodeId, CancellationToken ct = default)
    {
        var detail = await GetDetailAsync(mediaId, ct);
        var episode = detail.Lines[0].Episodes.SingleOrDefault(item => item.Id == episodeId) ?? throw new InvalidDataException("瓜子体育线路不存在。");
        return new PlaybackRequest { Uri = episode.Uri!, Title = detail.Item.Title, SourceKey = Key, SourceName = Name, MediaId = mediaId, LineId = "瓜子", EpisodeId = episodeId, IsLive = true, Headers = new() { ["User-Agent"] = "Lavf/57.83.100", ["Referer"] = "http://WJiZxLXA2.com/" } };
    }
}
