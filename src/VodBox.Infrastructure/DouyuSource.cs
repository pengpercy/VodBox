using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using VodBox.Core;

namespace VodBox.Infrastructure;

/// <summary>
/// 斗鱼直播爬虫（对应 TVBox 的斗鱼条目；饭太硬配置以 .js 入口出现，此处为原生 C# 实现）。
/// 无 JAR、无脚本执行：m.douyu.com 公共移动房间 API。
/// 播放解析标记 <see cref="ResolutionKind.Browser"/>（需 CDP 嗅探，S8 接入）——斗鱼的播放
/// 地址由页面脚本动态生成，纯 HTTP 无法稳定取得，故详情/列表/搜索可用，播放待嗅探层。
/// </summary>
public sealed partial class DouyuSource : IResolvingContentSource, IDisposable
{
    private const string Home = "https://m.douyu.com/";
    private const string UserAgent = "Mozilla/5.0 (iPhone; CPU iPhone OS 16_0 like Mac OS X) AppleWebKit/605.1.15 Version/16.0 Mobile/15E148 Safari/604.1";

    private static readonly Category[] Categories =
    [
        new("yqk", "一起看"), new("LOL", "网游竞技"), new("TVgame", "单机热游"), new("wzry", "手游休闲"),
        new("yz", "颜值"), new("smkj", "科技文化"), new("yyzs", "语音直播"), new("znl", "正能量"),
    ];

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _requests = new(4, 4);
    private bool _disposed;

    public string Key { get; }
    public string Name { get; }

    public DouyuSource(SourceInfo info) : this(info, CreateHttp()) { }

    internal DouyuSource(SourceInfo info, HttpClient http)
    {
        ArgumentNullException.ThrowIfNull(info);
        Key = info.Key;
        Name = info.Name;
        _http = http;
    }

    private static HttpClient CreateHttp() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.All,
    });

    // ---------- 分类 ----------

    public Task<IReadOnlyList<Category>> GetCategoriesAsync(CancellationToken ct = default)
    {
        Check(ct);
        return Task.FromResult<IReadOnlyList<Category>>(Categories);
    }

    // ---------- 房间列表（api/room/list，无总数，空页即止）----------

    public async Task<MediaPage> GetHomeAsync(CancellationToken ct = default) =>
        await GetItemsAsync("yqk", 1, null, ct).ConfigureAwait(false);

    public async Task<MediaPage> GetItemsAsync(string categoryId, int page, IReadOnlyDictionary<string, string>? filters, CancellationToken ct = default)
    {
        string category = string.IsNullOrEmpty(categoryId) ? "yqk" : categoryId;
        if (Categories.All(x => x.Id != category)) throw new InvalidDataException("斗鱼分类不存在。");
        if (page < 1 || page > 1000) throw new InvalidDataException("斗鱼页码无效。");

        using var document = JsonDocument.Parse(await ReadAsync($"api/room/list?page={page}&type={category}", null, ct).ConfigureAwait(false));
        var items = List(document.RootElement, "rid");
        // 公共接口无总数：本页非空则可能有下一页，空页即止
        return new MediaPage(items, page, items.Count > 0 && page < 1000 ? page + 1 : page);
    }

    // ---------- 搜索（POST api/search/liveRoom，20/页 offset）----------

    public async Task<MediaPage> SearchAsync(string query, int page, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 200) throw new InvalidDataException("搜索词为空或过长。");
        if (page < 1) page = 1;
        int offset = (page - 1) * 20;
        if (offset < 0 || offset > 20000) throw new InvalidDataException("斗鱼搜索页码无效。");

        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["did"] = "10000000000000000000000000001501",
            ["limit"] = "20",
            ["offset"] = offset.ToString(CultureInfo.InvariantCulture),
            ["sk"] = query,
        });
        using var document = JsonDocument.Parse(await ReadAsync("api/search/liveRoom", content, ct).ConfigureAwait(false));
        var items = List(document.RootElement, "roomId");
        return new MediaPage(items, page, items.Count == 20 && offset < 20000 ? page + 1 : page);
    }

    // ---------- 房间详情（vike_pageContext 内嵌 JSON）----------

    public async Task<MediaDetail> GetDetailAsync(string mediaId, CancellationToken ct = default)
    {
        string html = await ReadAsync(Id(mediaId), null, ct).ConfigureAwait(false);
        var context = PageContext().Match(html);
        if (!context.Success) throw new InvalidDataException("斗鱼房间页面数据缺失。");
        using var document = JsonDocument.Parse(context.Groups[1].Value);
        var room = document.RootElement.GetProperty("pageProps").GetProperty("room").GetProperty("roomInfo").GetProperty("roomInfo");
        if (Id(Field(room, "rid")) != mediaId) throw new InvalidDataException("斗鱼房间身份不符。");
        if (room.GetProperty("isLive").GetInt32() != 1) throw new InvalidDataException("该房间当前未开播。");

        return new MediaDetail
        {
            Item = new MediaItem { Id = mediaId, Title = Required(room, "roomName"), Poster = Url(Required(room, "roomSrc")), Remarks = Field(room, "nickname") },
            Description = Field(room, "notice"),
            Lines = [new PlaybackLine("live", "斗鱼直播", [new Episode(mediaId, "直播")])],
        };
    }

    // ---------- 播放地址解析（标记为浏览器嗅探）----------

    public async Task<PlaybackRequest> ResolvePlaybackAsync(string mediaId, string episodeId, CancellationToken ct = default)
    {
        if (Id(mediaId) != Id(episodeId)) throw new InvalidDataException("斗鱼直播分集不存在。");
        var detail = await GetDetailAsync(mediaId, ct).ConfigureAwait(false);
        return new PlaybackRequest
        {
            Uri = Home + mediaId,
            Title = detail.Item.Title,
            Poster = detail.Item.Poster,
            SourceKey = Key,
            SourceName = Name,
            MediaId = mediaId,
            LineId = "live",
            EpisodeId = episodeId,
            IsLive = true,
            Resolution = ResolutionKind.Sniff,
            Headers = new Dictionary<string, string>
            {
                ["User-Agent"] = UserAgent,
                ["Referer"] = Home,
            },
        };
    }

    // ---------- HTTP ----------

    private static IReadOnlyList<MediaItem> List(JsonElement root, string idKey)
    {
        string statusKey = idKey == "roomId" ? "error" : "code";
        if (root.GetProperty(statusKey).GetInt32() != 0) throw new InvalidDataException("斗鱼接口返回失败。");
        var list = root.GetProperty("data").GetProperty("list");
        if (list.GetArrayLength() > 1000) throw new InvalidDataException("斗鱼列表过长。");
        return list.EnumerateArray().Select(x => new MediaItem
        {
            Id = Id(Field(x, idKey)),
            Title = Required(x, "roomName"),
            Poster = Url(Required(x, "roomSrc")),
            Remarks = Field(x, "nickname"),
        }).ToArray();
    }

    private async Task<string> ReadAsync(string path, HttpContent? content, CancellationToken ct)
    {
        Check(ct);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        await _requests.WaitAsync(deadline.Token).ConfigureAwait(false);
        try
        {
            Check(deadline.Token);
            using var request = new HttpRequestMessage(content is null ? HttpMethod.Get : HttpMethod.Post, Home + path) { Content = content };
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            request.Headers.TryAddWithoutValidation("Referer", Home);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.RequestMessage?.RequestUri is { } final && final != request.RequestUri)
                throw new InvalidDataException("斗鱼接口发生重定向。");
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            return TextEncoding.Decode(await BoundedContent.ReadAsync(stream, 2 * 1024 * 1024, deadline.Token).ConfigureAwait(false));
        }
        finally { _requests.Release(); }
    }

    // ---------- 解析辅助 ----------

    internal static string Id(string id) =>
        id.Length is > 0 and <= 20 && id.All(char.IsAsciiDigit) ? id
        : throw new InvalidDataException("斗鱼房间编号无效。");

    private static string Field(JsonElement item, string key) =>
        item.TryGetProperty(key, out var value) ? value.ToString() : "";

    private static string Required(JsonElement item, string key) =>
        Field(item, key) is { Length: > 0 } value ? value : throw new InvalidDataException($"斗鱼缺少 {key}。");

    internal static string Url(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" && uri.UserInfo.Length == 0
            ? uri.AbsoluteUri
            : throw new InvalidDataException("斗鱼媒体地址无效。");

    private void Check(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();
    }

    public void Dispose() { _disposed = true; }

    [GeneratedRegex("<script\\b[^>]*id=[\"']vike_pageContext[\"'][^>]*>(.*?)</script>", RegexOptions.Singleline | RegexOptions.IgnoreCase, 1000)]
    private static partial Regex PageContext();
}
