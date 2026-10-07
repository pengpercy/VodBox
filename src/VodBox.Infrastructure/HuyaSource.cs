using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VodBox.Core;

namespace VodBox.Infrastructure;

/// <summary>
/// 虎牙直播爬虫（对应 TVBox 的虎牙条目；饭太硬配置以 .js 入口出现，此处为原生 C# 实现）。
/// 无 JAR、无脚本执行：公共房间 API + 匿名 FLV 签名（fm 模板 → MD5(prefix_0_stream_seqid_wsTime)）。
/// 旧实现通过真机 AOT 验证，此为其当前契约适配版。直播源：<see cref="PlaybackRequest.IsLive"/> = true。
/// </summary>
public sealed class HuyaSource : IResolvingContentSource, IDisposable
{
    private static readonly Category[] Categories =
    [
        new("2135", "一起看"), new("1663", "星秀"), new("2165", "户外"),
        new("1", "网游"), new("1732", "单机"), new("2336", "手游"),
    ];

    private const string UserAgent = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36";

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _requests = new(4, 4);
    private bool _disposed;

    public string Key { get; }
    public string Name { get; }

    public HuyaSource(SourceInfo info) : this(info, CreateHttp()) { }

    internal HuyaSource(SourceInfo info, HttpClient http)
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

    // ---------- 房间列表（分类 + 分页）----------

    public async Task<MediaPage> GetHomeAsync(CancellationToken ct = default) =>
        await GetItemsAsync(Categories[0].Id, 1, null, ct).ConfigureAwait(false);

    public async Task<MediaPage> GetItemsAsync(string categoryId, int page, IReadOnlyDictionary<string, string>? filters, CancellationToken ct = default)
    {
        string category = string.IsNullOrEmpty(categoryId) ? Categories[0].Id : categoryId;
        if (Categories.All(x => x.Id != category)) throw new InvalidDataException("虎牙分类不存在。");
        if (page < 1 || page > 1000) throw new InvalidDataException("虎牙页码无效。");

        using var document = await ReadAsync(
            $"https://www.huya.com/cache.php?m=LiveList&do=getLiveListByPage&gameId={category}&tagAll=0&page={page}", ct).ConfigureAwait(false);
        var root = document.RootElement;
        Status(root);
        var data = root.GetProperty("data");
        var list = data.GetProperty("datas");
        if (list.GetArrayLength() > 1000) throw new InvalidDataException("虎牙列表过长。");

        var items = list.EnumerateArray().Select(x => new MediaItem
        {
            Id = Id(Field(x, "profileRoom")),
            Title = Required(x, "introduction"),
            Poster = Url(Required(x, "screenshot")),
            Remarks = Field(x, "nick"),
        }).ToArray();
        int totalPage = data.GetProperty("totalPage").GetInt32();
        // 页码递增、总数未知：未达 totalPage 则给下一页，末页保持当前页
        return new MediaPage(items, page, page < totalPage && page < 1000 ? page + 1 : page);
    }

    // ---------- 搜索（start 以 40 为步长偏移）----------

    public async Task<MediaPage> SearchAsync(string query, int page, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 200) throw new InvalidDataException("搜索词为空或过长。");
        int start = (page - 1) * 40;
        if (start < 0 || start > 40000) throw new InvalidDataException("虎牙搜索页码无效。");

        using var document = await ReadAsync(
            $"https://search.cdn.huya.com/?m=Search&do=getSearchContent&q={Uri.EscapeDataString(query)}&uid=0&v=4&typ=-5&livestate=0&rows=40&start={start}", ct).ConfigureAwait(false);
        var list = document.RootElement.GetProperty("response").GetProperty("3").GetProperty("docs");
        if (list.GetArrayLength() > 40) throw new InvalidDataException("虎牙搜索响应过长。");

        var items = list.EnumerateArray().Select(x => new MediaItem
        {
            Id = Id(Field(x, "room_id")),
            Title = Field(x, "game_introduction") is { Length: > 0 } title ? title : Required(x, "gameName"),
            Poster = Url(Required(x, "game_screenshot")),
            Remarks = Field(x, "game_nick"),
        }).ToArray();
        bool more = items.Length == 40 && start < 40000;
        return new MediaPage(items, page, more ? page + 1 : page);
    }

    // ---------- 房间详情（多 CDN 线路）----------

    private async Task<JsonDocument> RoomAsync(string id, CancellationToken ct) =>
        await ReadAsync("https://mp.huya.com/cache.php?m=Live&do=profileRoom&roomid=" + Id(id), ct).ConfigureAwait(false);

    public async Task<MediaDetail> GetDetailAsync(string mediaId, CancellationToken ct = default)
    {
        using var document = await RoomAsync(mediaId, ct).ConfigureAwait(false);
        Status(document.RootElement);
        var data = document.RootElement.GetProperty("data");
        EnsureLive(data);
        var live = data.GetProperty("liveData");
        var lines = data.GetProperty("stream").GetProperty("flv").GetProperty("multiLine");
        if (lines.GetArrayLength() is < 1 or > 20) throw new InvalidDataException("虎牙直播缺少有效线路。");

        var playbackLines = lines.EnumerateArray().Select((x, i) => new PlaybackLine(
            i.ToString(CultureInfo.InvariantCulture),
            Required(x, "cdnType"),
            [new Episode(i.ToString(CultureInfo.InvariantCulture), "直播")])).ToArray();

        return new MediaDetail
        {
            Item = new MediaItem { Id = Id(mediaId), Title = Required(live, "roomName"), Poster = Url(Required(live, "screenshot")) },
            Description = Field(live, "contentIntro"),
            Lines = playbackLines,
        };
    }

    // ---------- 播放地址即时解析（匿名 FLV 签名）----------

    public async Task<PlaybackRequest> ResolvePlaybackAsync(string mediaId, string episodeId, CancellationToken ct = default)
    {
        if (!int.TryParse(episodeId, NumberStyles.None, CultureInfo.InvariantCulture, out int index) || index is < 0 or > 20)
            throw new InvalidDataException("虎牙线路编号无效。");

        using var document = await RoomAsync(mediaId, ct).ConfigureAwait(false);
        Status(document.RootElement);
        var data = document.RootElement.GetProperty("data");
        EnsureLive(data);
        var lines = data.GetProperty("stream").GetProperty("flv").GetProperty("multiLine");
        if (index >= lines.GetArrayLength()) throw new InvalidDataException("虎牙线路不存在。");

        string media = Sign(Required(lines[index], "url"), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        return new PlaybackRequest
        {
            Uri = media,
            Title = Required(data.GetProperty("liveData"), "roomName"),
            SourceKey = Key,
            SourceName = Name,
            MediaId = mediaId,
            LineId = index.ToString(CultureInfo.InvariantCulture),
            EpisodeId = episodeId,
            IsLive = true,
            Headers = new Dictionary<string, string>
            {
                ["User-Agent"] = UserAgent,
                ["Referer"] = "https://www.huya.com/" + mediaId,
            },
        };
    }

    /// <summary>
    /// 匿名 FLV 签名：fm 模板 base64 解出 prefix，MD5(prefix_0_stream_seqid_wsTime) 生成 wsSecret。
    /// 这是旧实现的关键协议逻辑，旧测试用独立 OpenSSL 向量锁定。
    /// </summary>
    internal static string Sign(string value, long milliseconds)
    {
        var uri = new Uri(Url(value));
        var fields = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Split('=', 2))
            .ToDictionary(x => x[0], x => x.Length == 2 ? Uri.UnescapeDataString(x[1]) : "", StringComparer.Ordinal);
        if (!fields.TryGetValue("fm", out string? fm) || !fields.TryGetValue("wsTime", out string? time) || !fields.ContainsKey("ctype"))
            throw new InvalidDataException("虎牙播放签名字段缺失。");

        string template = Encoding.UTF8.GetString(Convert.FromBase64String(fm));
        string prefix = template.Split('_')[0];
        string stream = Path.GetFileNameWithoutExtension(uri.AbsolutePath);
        string sequence = milliseconds.ToString(CultureInfo.InvariantCulture) + "0000";
        string signature = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes($"{prefix}_0_{stream}_{sequence}_{time}")));

        foreach (string key in new[] { "fm", "wsSecret", "wsTime", "u", "seqid" }) fields.Remove(key);
        return uri.GetLeftPart(UriPartial.Path)
            + $"?wsSecret={signature}&wsTime={Uri.EscapeDataString(time)}&u=0&seqid={sequence}&"
            + string.Join('&', fields.Select(x => Uri.EscapeDataString(x.Key) + "=" + Uri.EscapeDataString(x.Value)));
    }

    // ---------- HTTP ----------

    private async Task<JsonDocument> ReadAsync(string url, CancellationToken ct)
    {
        Check(ct);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        await _requests.WaitAsync(deadline.Token).ConfigureAwait(false);
        try
        {
            Check(deadline.Token);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.RequestMessage?.RequestUri is { } final && final != request.RequestUri)
                throw new InvalidDataException("虎牙接口发生重定向。");
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            return JsonDocument.Parse(await BoundedContent.ReadAsync(stream, 2 * 1024 * 1024, deadline.Token).ConfigureAwait(false));
        }
        finally { _requests.Release(); }
    }

    // ---------- 解析辅助 ----------

    private static void Status(JsonElement root)
    {
        if (root.GetProperty("status").GetInt32() != 200) throw new InvalidDataException("虎牙接口返回失败。");
    }

    private static void EnsureLive(JsonElement data)
    {
        if (Field(data, "liveStatus") != "ON") throw new InvalidDataException("该房间当前未开播。");
    }

    internal static string Id(string id) =>
        id.Length is > 0 and <= 20 && id.All(char.IsAsciiDigit) ? id
        : throw new InvalidDataException("虎牙房间编号无效。");

    private static string Field(JsonElement item, string key) =>
        item.TryGetProperty(key, out var value) ? value.ToString() : "";

    private static string Required(JsonElement item, string key) =>
        Field(item, key) is { Length: > 0 } value ? value : throw new InvalidDataException($"虎牙缺少 {key}。");

    internal static string Url(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" && uri.UserInfo.Length == 0
            ? uri.AbsoluteUri
            : throw new InvalidDataException("虎牙媒体地址无效。");

    private void Check(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();
    }

    public void Dispose() { _disposed = true; }
}
