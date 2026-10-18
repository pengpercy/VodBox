using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VodBox.Core;

namespace VodBox.Infrastructure;

/// <summary>FTY 275 audio protocol; native_ting_md5 is reproduced from the v8 guard.</summary>
public sealed class Tingshu275Source : IResolvingContentSource
{
    private const string Discovery = "http://117.72.112.234:8090/config/tingchina2025.txt";
    private const string Agent = "TingShiJie/1.8.8 (m.i275.com)";
    private const string MediaAgent = "listen/1.8.9 (Linux;Android 12) ExoPlayerLib/2.8.4";
    private static readonly HttpClient SharedHttp = new(new HttpClientHandler
    { AutomaticDecompression = DecompressionMethods.All, UseCookies = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly Category[] Categories = new[] { "玄幻奇幻", "都市言情", "宫斗女频", "官场商战", "武侠仙侠", "刑侦推理", "探险科幻", "重生穿越", "恐怖惊悚", "文学历史", "两性情感" }
        .Select((name, index) => new Category((index == 10 ? 49 : index + 6).ToString(CultureInfo.InvariantCulture), name)).ToArray();
    private readonly SourceInfo _site;
    private readonly HttpClient _http;
    private readonly Func<long> _clock;
    private readonly Uri? _fixture;
    public string Key => _site.Key;
    public string Name => _site.Name;
    public Tingshu275Source(SourceInfo site) : this(site, SharedHttp, () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) { }
    internal Tingshu275Source(SourceInfo site, HttpClient http, Func<long> clock, Uri? fixture = null)
    {
        if (!Supports(site.Ext)) throw new NotSupportedException("此版本听书插件不使用 ext 配置。");
        _site = site; _http = http; _clock = clock; _fixture = fixture;
    }
    internal static bool Supports(string? ext) => string.IsNullOrWhiteSpace(ext) || ext == "null";

    internal static string Sign(string timestamp)
    {
        // REA 0x10fe6c: lowerhex(MD5(lowerhex(MD5(input + salt)) + salt)).
        const string salt = "J9gSpfUlzYxE8Hn5IXiGaD2jVMrwAm0K";
        static string Md5(string value) => Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(value)));
        return Md5(Md5(timestamp + salt) + salt);
    }
    private static Uri HttpUri(string text) => Uri.TryCreate(text, UriKind.Absolute, out var value) && value.Scheme is "http" or "https" && value.UserInfo.Length == 0 && value.Fragment.Length == 0
        ? value : throw new InvalidDataException("听书接口地址无效。");
    private async Task<byte[]> Read(Uri address, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        request.Headers.TryAddWithoutValidation("User-Agent", Agent);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        return await BoundedContent.ReadAsync(stream, 8 * 1024 * 1024, timeout.Token).ConfigureAwait(false);
    }
    // 原插件只在 init 里读一次发现配置；这里同样每个实例只解析一次，失败不缓存。
    private Task<Uri>? _host;
    private readonly Lock _hostGate = new();
    private async Task<Uri> Host(CancellationToken ct)
    {
        if (_fixture is not null) return _fixture;
        Task<Uri> pending;
        lock (_hostGate) pending = _host ??= Discover(CancellationToken.None);
        try { return await pending.WaitAsync(ct).ConfigureAwait(false); }
        catch when (pending.IsFaulted || pending.IsCanceled) { lock (_hostGate) if (ReferenceEquals(_host, pending)) _host = null; throw; }
    }
    private async Task<Uri> Discover(CancellationToken ct)
    {
        string value = Encoding.UTF8.GetString(await Read(new Uri(Discovery), ct).ConfigureAwait(false)).Trim();
        var host = HttpUri(value);
        if (host.Query.Length > 0 || !host.AbsolutePath.EndsWith('/')) throw new InvalidDataException("听书配置不是接口目录。");
        return host;
    }
    private async Task<JsonDocument> Request(string path, CancellationToken ct)
    {
        var host = await Host(ct).ConfigureAwait(false);
        return JsonDocument.Parse(await Read(new Uri(host, path), ct).ConfigureAwait(false));
    }
    private static string Text(JsonElement item, string name) => AppRjSource.Text(item, name);
    private static JsonElement Required(JsonElement item, string name, JsonValueKind kind) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) && value.ValueKind == kind
        ? value : throw new InvalidDataException("听书响应缺少字段：" + name);
    private static MediaItem Item(JsonElement item) => new() { Id = Text(item, "id"), Title = Text(item, "bookTitle"), Poster = Text(item, "bookImage"), Remarks = Text(item, "bookAnchor") };
    public Task<IReadOnlyList<Category>> GetCategoriesAsync(CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); return Task.FromResult<IReadOnlyList<Category>>(Categories.ToArray()); }
    public Task<MediaPage> GetHomeAsync(CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); return Task.FromResult(new MediaPage([], 1, 1)); }
    public async Task<MediaPage> GetItemsAsync(string categoryId, int page, IReadOnlyDictionary<string, string>? filters, CancellationToken ct = default)
    {
        Page(page);
        if (!Categories.Any(category => category.Id == categoryId)) throw new InvalidDataException("听书分类不存在。");
        if (filters?.Keys.Any(key => key != "cateId") == true) throw new NotSupportedException("听书筛选参数无效。");
        string category = filters?.TryGetValue("cateId", out var selected) == true && selected.Length > 0 ? selected : categoryId;
        using var document = await Request($"categoryList?size=10&page={page.ToString(CultureInfo.InvariantCulture)}&categoryId={Uri.EscapeDataString(category)}", ct).ConfigureAwait(false);
        var items = Required(Required(document.RootElement, "data", JsonValueKind.Object), "list", JsonValueKind.Array).EnumerateArray().Select(Item).ToArray();
        return new(items, page, items.Length >= 10 ? checked(page + 1) : page);
    }
    private readonly DetailCache _details = new(TimeSpan.FromMinutes(5));
    public Task<MediaDetail> GetDetailAsync(string mediaId, CancellationToken ct = default) => _details.GetAsync(mediaId, LoadDetailAsync, ct);
    private async Task<MediaDetail> LoadDetailAsync(string mediaId, CancellationToken ct)
    {
        using var document = await Request("book?bookId=" + Uri.EscapeDataString(mediaId), ct).ConfigureAwait(false);
        var book = Required(Required(document.RootElement, "data", JsonValueKind.Object), "bookData", JsonValueKind.Object);
        using var chapters = await Request("chapter?size=9999&page=1&sort=asc&bookId=" + Uri.EscapeDataString(mediaId), ct).ConfigureAwait(false);
        var episodes = Required(Required(chapters.RootElement, "data", JsonValueKind.Object), "list", JsonValueKind.Array).EnumerateArray()
            .Select(chapter => new Episode("直链\u0001" + Text(chapter, "chapterId") + "|||" + mediaId, Text(chapter, "title"))).ToArray();
        return new()
        {
            Item = Item(book) with { Id = mediaId, TypeName = Text(book, "categoryName") }, Description = Text(book, "bookDesc"),
            Actor = Text(book, "bookAnchor"), Director = Text(book, "bookAnchor"),
            Lines = episodes.Length > 0 ? [new("直链", "直链", episodes)] : []
        };
    }
    public async Task<PlaybackRequest> ResolvePlaybackAsync(string mediaId, string episodeId, CancellationToken ct = default)
    {
        // 原插件 playerContent 直接用 chapterId|||bookId 取地址，不回抓书籍详情与整本章节表。
        if (!episodeId.StartsWith("直链\u0001", StringComparison.Ordinal)) throw new InvalidDataException("听书选集已失效。");
        string[] parts = episodeId[3..].Split("|||", StringSplitOptions.None);
        if (parts.Length != 2 || parts[1] != mediaId) throw new InvalidDataException("听书选集标识无效。");
        string timestamp = _clock().ToString(CultureInfo.InvariantCulture);
        using var parsed = await Request("AppGetChapterUrl2023?timeStamp=" + timestamp + "&uid=&chapterId=" + Uri.EscapeDataString(parts[0]) + "&addItParapet=" + Sign(timestamp) + "&bookId=" + Uri.EscapeDataString(mediaId), ct).ConfigureAwait(false);
        string url = HttpUri(Text(parsed.RootElement, "src")).AbsoluteUri;
        return new() { Uri = url, SourceKey = Key, SourceName = Name, MediaId = mediaId, EpisodeId = episodeId, LineId = "直链", Title = _details.TryGet(mediaId)?.Item.Title ?? Name, Headers = new Dictionary<string, string> { ["User-Agent"] = MediaAgent } };
    }
    public async Task<MediaPage> SearchAsync(string query, int page, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); Page(page);
        if (page > 1 || string.IsNullOrWhiteSpace(query)) return new([], page, page);
        var host = await Host(ct).ConfigureAwait(false);
        // Bytecode concatenates the discovered directory and this leading-slash path.
        // Preserve the double slash: resolving it as an origin-root path changes the wire contract.
        using var document = JsonDocument.Parse(await Read(HttpUri(host.AbsoluteUri + "/listen/apphaidao/appSearch?search=" + Uri.EscapeDataString(query)), ct).ConfigureAwait(false));
        return new(Required(Required(document.RootElement, "data", JsonValueKind.Object), "bookData", JsonValueKind.Array).EnumerateArray().Select(Item).ToArray(), 1, 1);
    }
    private static void Page(int value) { if (value < 1 || value == int.MaxValue) throw new ArgumentOutOfRangeException(nameof(value)); }
}
