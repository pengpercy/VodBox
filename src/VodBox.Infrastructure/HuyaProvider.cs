using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VodBox.Core;

namespace VodBox.Infrastructure;

/// <summary>Public room API with fresh anonymous FLV signatures.</summary>
public sealed class HuyaProvider(SourceDefinition source, HttpClient http) : IContentProvider
{
    private static readonly Category[] Categories = [new("2135", "一起看"), new("1663", "星秀"), new("2165", "户外"), new("1", "网游"), new("1732", "单机"), new("2336", "手游")];
    private readonly SemaphoreSlim _requests = new(4, 4);
    private bool _disposed;
    public string SourceId => source.Id;
    public Task<IReadOnlyList<Category>> GetCategoriesAsync(CancellationToken token) { Check(token); return Task.FromResult<IReadOnlyList<Category>>(Categories); }
    public async Task<MediaPage> GetItemsAsync(string? categoryId, string? cursor, CancellationToken token)
    {
        string category = categoryId ?? Categories[0].Id;
        if (Categories.All(x => x.Id != category)) throw new InvalidDataException("虎牙分类不存在。");
        int page = cursor is null ? 1 : int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out int p) && p is >= 1 and <= 1000 ? p : throw new InvalidDataException("虎牙页码无效。");
        using var document = await ReadAsync($"https://www.huya.com/cache.php?m=LiveList&do=getLiveListByPage&gameId={category}&tagAll=0&page={page}", token);
        var root = document.RootElement; Status(root); var data = root.GetProperty("data");
        var list = data.GetProperty("datas"); if (list.GetArrayLength() > 1000) throw new InvalidDataException("虎牙列表过长。");
        var items = list.EnumerateArray().Select(x => new MediaItem(Id(Field(x, "profileRoom")), Required(x, "introduction"), Url(Required(x, "screenshot")), Field(x, "nick"))).ToArray();
        return new(items, page < data.GetProperty("totalPage").GetInt32() && page < 1000 ? (page + 1).ToString(CultureInfo.InvariantCulture) : null);
    }
    public Task<MediaPage> SearchAsync(string query, CancellationToken token) => SearchPageAsync(query, null, token);
    public async Task<MediaPage> SearchPageAsync(string query, string? cursor, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 200) throw new InvalidDataException("搜索词为空或过长。");
        int start = cursor is null ? 0 : int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out int p) && p is >= 0 and <= 40000 && p % 40 == 0 ? p : throw new InvalidDataException("虎牙搜索页码无效。");
        using var document = await ReadAsync($"https://search.cdn.huya.com/?m=Search&do=getSearchContent&q={Uri.EscapeDataString(query)}&uid=0&v=4&typ=-5&livestate=0&rows=40&start={start}", token);
        var list = document.RootElement.GetProperty("response").GetProperty("3").GetProperty("docs");
        if (list.GetArrayLength() > 40) throw new InvalidDataException("虎牙搜索响应过长。");
        var items = list.EnumerateArray().Select(x => new MediaItem(Id(Field(x, "room_id")), Field(x, "game_introduction") is { Length: > 0 } title ? title : Required(x, "gameName"), Url(Required(x, "game_screenshot")), Field(x, "game_nick"))).ToArray();
        return new(items, items.Length == 40 && start < 40000 ? (start + 40).ToString(CultureInfo.InvariantCulture) : null);
    }
    private async Task<JsonDocument> RoomAsync(string id, CancellationToken token) => await ReadAsync("https://mp.huya.com/cache.php?m=Live&do=profileRoom&roomid=" + Id(id), token);
    public async Task<MediaDetail> GetDetailAsync(string mediaId, CancellationToken token)
    {
        using var document = await RoomAsync(mediaId, token); Status(document.RootElement);
        var data = document.RootElement.GetProperty("data"); EnsureLive(data); var live = data.GetProperty("liveData");
        var lines = data.GetProperty("stream").GetProperty("flv").GetProperty("multiLine");
        if (lines.GetArrayLength() is < 1 or > 20) throw new InvalidDataException("虎牙直播缺少有效线路。");
        return new(new(Id(mediaId), Required(live, "roomName"), Url(Required(live, "screenshot"))), Field(live, "contentIntro"),
            lines.EnumerateArray().Select((x, i) => new PlaybackLine(i.ToString(CultureInfo.InvariantCulture), Required(x, "cdnType"), [new(i.ToString(CultureInfo.InvariantCulture), "直播")])).ToArray());
    }
    public async Task<PlaybackRequest> ResolvePlaybackAsync(string mediaId, string episodeId, CancellationToken token)
    {
        if (!int.TryParse(episodeId, NumberStyles.None, CultureInfo.InvariantCulture, out int index) || index is < 0 or > 20) throw new InvalidDataException("虎牙线路编号无效。");
        using var document = await RoomAsync(mediaId, token); Status(document.RootElement);
        var data = document.RootElement.GetProperty("data"); EnsureLive(data);
        var lines = data.GetProperty("stream").GetProperty("flv").GetProperty("multiLine");
        if (index >= lines.GetArrayLength()) throw new InvalidDataException("虎牙线路不存在。");
        string media = Sign(Required(lines[index], "url"), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        return new() { Uri = media, SourceId = SourceId, MediaId = mediaId, EpisodeId = episodeId, Title = Required(data.GetProperty("liveData"), "roomName"), IsLive = true,
            Headers = new() { ["User-Agent"] = "Mozilla/5.0", ["Referer"] = "https://www.huya.com/" + mediaId } };
    }
    internal static string Sign(string value, long milliseconds)
    {
        var uri = new Uri(Url(value));
        var fields = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Split('=', 2)).ToDictionary(x => x[0], x => x.Length == 2 ? Uri.UnescapeDataString(x[1]) : "", StringComparer.Ordinal);
        if (!fields.TryGetValue("fm", out string? fm) || !fields.TryGetValue("wsTime", out string? time) || !fields.ContainsKey("ctype")) throw new InvalidDataException("虎牙播放签名字段缺失。");
        string template = Encoding.UTF8.GetString(Convert.FromBase64String(fm)), prefix = template.Split('_')[0];
        string stream = Path.GetFileNameWithoutExtension(uri.AbsolutePath), sequence = milliseconds.ToString(CultureInfo.InvariantCulture) + "0000";
        string signature = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes($"{prefix}_0_{stream}_{sequence}_{time}")));
        foreach (string key in new[] { "fm", "wsSecret", "wsTime", "u", "seqid" }) fields.Remove(key);
        return uri.GetLeftPart(UriPartial.Path) + $"?wsSecret={signature}&wsTime={Uri.EscapeDataString(time)}&u=0&seqid={sequence}&" + string.Join('&', fields.Select(x => Uri.EscapeDataString(x.Key) + "=" + Uri.EscapeDataString(x.Value)));
    }
    private async Task<JsonDocument> ReadAsync(string url, CancellationToken token)
    {
        Check(token); using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(20));
        await _requests.WaitAsync(deadline.Token);
        try
        {
            Check(deadline.Token); using var request = new HttpRequestMessage(HttpMethod.Get, url); request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token); response.EnsureSuccessStatusCode();
            if (response.RequestMessage?.RequestUri is { } final && final != request.RequestUri) throw new InvalidDataException("虎牙接口发生重定向。");
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            return JsonDocument.Parse(await BoundedContent.ReadAsync(stream, 2 * 1024 * 1024, deadline.Token));
        }
        finally { _requests.Release(); }
    }
    private static void Status(JsonElement root) { if (root.GetProperty("status").GetInt32() != 200) throw new InvalidDataException("虎牙接口返回失败。"); }
    private static void EnsureLive(JsonElement data) { if (Field(data, "liveStatus") != "ON") throw new InvalidDataException("该房间当前未开播。"); }
    private static string Id(string id) => id.Length is > 0 and <= 20 && id.All(char.IsAsciiDigit) ? id : throw new InvalidDataException("虎牙房间编号无效。");
    private static string Field(JsonElement item, string key) => item.TryGetProperty(key, out var value) ? value.ToString() : "";
    private static string Required(JsonElement item, string key) => Field(item, key) is { Length: > 0 } value ? value : throw new InvalidDataException($"虎牙缺少 {key}。");
    private static string Url(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" && uri.UserInfo.Length == 0 ? uri.AbsoluteUri : throw new InvalidDataException("虎牙媒体地址无效。");
    private void Check(CancellationToken token) { ObjectDisposedException.ThrowIf(_disposed, this); token.ThrowIfCancellationRequested(); }
    public ValueTask DisposeAsync() { _disposed = true; return ValueTask.CompletedTask; }
}
