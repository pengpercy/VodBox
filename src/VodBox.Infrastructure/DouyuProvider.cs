using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using VodBox.Core;

namespace VodBox.Infrastructure;

/// <summary>Public mobile room directory. Playback uses the existing isolated browser resolver.</summary>
public sealed partial class DouyuProvider(SourceDefinition source, HttpClient http) : IContentProvider
{
    private const string Home = "https://m.douyu.com/";
    private const string UserAgent = "Mozilla/5.0 (iPhone; CPU iPhone OS 16_0 like Mac OS X) AppleWebKit/605.1.15 Version/16.0 Mobile/15E148 Safari/604.1";
    private static readonly Category[] Categories = [new("yqk", "一起看"), new("LOL", "网游竞技"), new("TVgame", "单机热游"), new("wzry", "手游休闲"), new("yz", "颜值"), new("smkj", "科技文化"), new("yyzs", "语音直播"), new("znl", "正能量")];
    private readonly SemaphoreSlim _requests = new(4, 4);
    private bool _disposed;
    public string SourceId => source.Id;
    public Task<IReadOnlyList<Category>> GetCategoriesAsync(CancellationToken token) { Check(token); return Task.FromResult<IReadOnlyList<Category>>(Categories); }
    public async Task<MediaPage> GetItemsAsync(string? categoryId, string? cursor, CancellationToken token)
    {
        string category = categoryId ?? "yqk";
        if (Categories.All(x => x.Id != category)) throw new InvalidDataException("斗鱼分类不存在。");
        int page = cursor is null ? 1 : int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out int p) && p is >= 1 and <= 1000 ? p : throw new InvalidDataException("斗鱼页码无效。");
        using var document = JsonDocument.Parse(await ReadAsync($"api/room/list?page={page}&type={category}", null, token));
        var items = List(document.RootElement, "rid");
        // Public endpoint has no total count; stop on its first empty page.
        return new(items, items.Count > 0 && page < 1000 ? (page + 1).ToString(CultureInfo.InvariantCulture) : null);
    }
    public Task<MediaPage> SearchAsync(string query, CancellationToken token) => SearchPageAsync(query, null, token);
    public async Task<MediaPage> SearchPageAsync(string query, string? cursor, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 200) throw new InvalidDataException("搜索词为空或过长。");
        int offset = cursor is null ? 0 : int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out int p) && p is >= 0 and <= 20000 && p % 20 == 0 ? p : throw new InvalidDataException("斗鱼搜索页码无效。");
        using var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["did"] = "10000000000000000000000000001501", ["limit"] = "20", ["offset"] = offset.ToString(CultureInfo.InvariantCulture), ["sk"] = query });
        using var document = JsonDocument.Parse(await ReadAsync("api/search/liveRoom", content, token));
        var items = List(document.RootElement, "roomId");
        return new(items, items.Count == 20 && offset < 20000 ? (offset + 20).ToString(CultureInfo.InvariantCulture) : null);
    }
    public async Task<MediaDetail> GetDetailAsync(string mediaId, CancellationToken token)
    {
        string html = await ReadAsync(Id(mediaId), null, token); var context = PageContext().Match(html);
        if (!context.Success) throw new InvalidDataException("斗鱼房间页面数据缺失。");
        using var document = JsonDocument.Parse(context.Groups[1].Value);
        var room = document.RootElement.GetProperty("pageProps").GetProperty("room").GetProperty("roomInfo").GetProperty("roomInfo");
        if (Id(Field(room, "rid")) != mediaId) throw new InvalidDataException("斗鱼房间身份不符。");
        if (room.GetProperty("isLive").GetInt32() != 1) throw new InvalidDataException("该房间当前未开播。");
        return new(new(mediaId, Required(room, "roomName"), Url(Required(room, "roomSrc")), Field(room, "nickname")), Field(room, "notice"), [new("live", "斗鱼直播", [new(mediaId, "直播")])]);
    }
    public async Task<PlaybackRequest> ResolvePlaybackAsync(string mediaId, string episodeId, CancellationToken token)
    {
        if (Id(mediaId) != Id(episodeId)) throw new InvalidDataException("斗鱼直播分集不存在。");
        var detail = await GetDetailAsync(mediaId, token);
        return new() { Uri = Home + mediaId, SourceId = SourceId, MediaId = mediaId, EpisodeId = episodeId, Title = detail.Item.Title, Poster = detail.Item.Poster, IsLive = true,
            ResolutionKind = ResolutionKind.Browser, Headers = new() { ["User-Agent"] = UserAgent, ["Referer"] = Home } };
    }
    private static IReadOnlyList<MediaItem> List(JsonElement root, string idKey)
    {
        string statusKey = idKey == "roomId" ? "error" : "code";
        if (root.GetProperty(statusKey).GetInt32() != 0) throw new InvalidDataException("斗鱼接口返回失败。");
        var list = root.GetProperty("data").GetProperty("list");
        if (list.GetArrayLength() > 1000) throw new InvalidDataException("斗鱼列表过长。");
        return list.EnumerateArray().Select(x => new MediaItem(Id(Field(x, idKey)), Required(x, "roomName"), Url(Required(x, "roomSrc")), Field(x, "nickname"))).ToArray();
    }
    private async Task<string> ReadAsync(string path, HttpContent? content, CancellationToken token)
    {
        Check(token); using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(20));
        await _requests.WaitAsync(deadline.Token);
        try
        {
            Check(deadline.Token); using var request = new HttpRequestMessage(content is null ? HttpMethod.Get : HttpMethod.Post, Home + path) { Content = content };
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent); request.Headers.TryAddWithoutValidation("Referer", Home);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token); response.EnsureSuccessStatusCode();
            if (response.RequestMessage?.RequestUri is { } final && final != request.RequestUri) throw new InvalidDataException("斗鱼接口发生重定向。");
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            return TextEncoding.Decode(await BoundedContent.ReadAsync(stream, 2 * 1024 * 1024, deadline.Token));
        }
        finally { _requests.Release(); }
    }
    private static string Id(string id) => id.Length is > 0 and <= 20 && id.All(char.IsAsciiDigit) ? id : throw new InvalidDataException("斗鱼房间编号无效。");
    private static string Field(JsonElement item, string key) => item.TryGetProperty(key, out var value) ? value.ToString() : "";
    private static string Required(JsonElement item, string key) => Field(item, key) is { Length: > 0 } value ? value : throw new InvalidDataException($"斗鱼缺少 {key}。");
    private static string Url(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" && uri.UserInfo.Length == 0 ? uri.AbsoluteUri : throw new InvalidDataException("斗鱼媒体地址无效。");
    private void Check(CancellationToken token) { ObjectDisposedException.ThrowIf(_disposed, this); token.ThrowIfCancellationRequested(); }
    public ValueTask DisposeAsync() { _disposed = true; return ValueTask.CompletedTask; }
    [GeneratedRegex("<script\\b[^>]*id=[\"']vike_pageContext[\"'][^>]*>(.*?)</script>", RegexOptions.Singleline | RegexOptions.IgnoreCase, 1000)] private static partial Regex PageContext();
}
