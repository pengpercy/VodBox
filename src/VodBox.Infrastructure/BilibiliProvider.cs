using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VodBox.Core;

namespace VodBox.Infrastructure;

/// <summary>C# Spider for public Bilibili UGC videos. No JAR or script execution.</summary>
public sealed class BilibiliProvider : IContentProvider
{
    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/132.0.0.0 Safari/537.36";
    private static readonly Uri Api = new("https://api.bilibili.com/");
    private static readonly HttpClient DefaultHttp = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.All
    });
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _requests = new(4, 4);
    private readonly SemaphoreSlim _searchInitialization = new(1, 1);
    private readonly ConcurrentDictionary<string, CachedDetail> _details = new(StringComparer.Ordinal);
    private readonly IReadOnlyList<ConfiguredCategory> _categories;
    private readonly string? _cookie;
    private SearchSession? _searchSession;
    private bool _disposed;
    public string SourceId { get; }
    private sealed record ConfiguredCategory(Category Category, string? Query, IReadOnlyList<string>? Videos);
    private sealed record CachedDetail(MediaDetail Detail, DateTimeOffset Expires);
    private sealed record SearchSession(string Key, string Cookie, DateTimeOffset Expires);

    public BilibiliProvider(SourceDefinition source) : this(source, DefaultHttp) { }

    public BilibiliProvider(SourceDefinition source, HttpClient http)
    {
        SourceId = source.Id;
        _http = http;
        _cookie = source.Options.TryGetValue("cookie", out var cookie) ? cookie.GetString() : null;
        if (_cookie is { Length: > 32768 } || _cookie?.IndexOfAny(['\r', '\n', '\0']) >= 0)
            throw new InvalidDataException("哔哩哔哩 Cookie 无效。");
        var categories = new List<ConfiguredCategory>();
        if (source.Options.TryGetValue("categories", out var configured))
        {
            if (configured.ValueKind != JsonValueKind.Array || configured.GetArrayLength() > 100)
                throw new InvalidDataException("哔哩哔哩分类必须是最多 100 项的数组。");
            var ids = new HashSet<string>(StringComparer.Ordinal) { "popular" };
            foreach (var item in configured.EnumerateArray())
            {
                string id = Text(item, "id"), name = Text(item, "name"), query = Text(item, "query");
                if (string.IsNullOrWhiteSpace(id) || id.Length > 128 || !ids.Add(id) || string.IsNullOrWhiteSpace(name) || name.Length > 128)
                    throw new InvalidDataException("哔哩哔哩分类的 id/name 为空、重复或过长。");
                bool hasQuery = item.TryGetProperty("query", out var queryValue);
                bool hasVideos = item.TryGetProperty("videos", out var videos);
                if (hasQuery == hasVideos)
                    throw new InvalidDataException("哔哩哔哩分类必须且只能配置 query 或 videos。");
                if (hasQuery)
                {
                    if (queryValue.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(query) || query.Length > 256)
                        throw new InvalidDataException("哔哩哔哩分类 query 为空或过长。");
                    categories.Add(new(new(id, name), query, null));
                }
                else
                {
                    if (videos.ValueKind != JsonValueKind.Array || videos.GetArrayLength() is < 1 or > 2000)
                        throw new InvalidDataException("哔哩哔哩片单 videos 必须是 1 到 2000 项的数组。");
                    var videoIds = new List<string>();
                    var unique = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var video in videos.EnumerateArray())
                    {
                        if (video.ValueKind != JsonValueKind.String)
                            throw new InvalidDataException("哔哩哔哩片单视频必须是 BV 或 av 号字符串。");
                        string videoId = video.GetString()!;
                        _ = MediaParameters(videoId);
                        if (!unique.Add(videoId)) throw new InvalidDataException("哔哩哔哩片单视频标识重复。");
                        videoIds.Add(videoId);
                    }
                    categories.Add(new(new(id, name), null, videoIds));
                }
            }
        }
        _categories = categories;
    }

    public Task<IReadOnlyList<Category>> GetCategoriesAsync(CancellationToken token)
    {
        ThrowIfDisposed(); token.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<Category>>([new("popular", "热门"), .. _categories.Select(x => x.Category)]);
    }

    public async Task<MediaPage> GetItemsAsync(string? categoryId, string? cursor, CancellationToken token)
    {
        if (categoryId is not (null or "popular"))
        {
            var category = _categories.FirstOrDefault(x => x.Category.Id == categoryId)
                ?? throw new InvalidDataException("未知的哔哩哔哩分类。");
            return category.Videos is { } videos
                ? await CuratedPageAsync(videos, cursor, token).ConfigureAwait(false)
                : await SearchPageAsync(category.Query!, cursor, token).ConfigureAwait(false);
        }
        int page = PageNumber(cursor);
        using var document = await FetchAsync("x/web-interface/popular", new() { ["pn"] = Number(page), ["ps"] = "20" }, token).ConfigureAwait(false);
        var data = Data(document);
        var items = ReadItems(data, "list");
        bool more = data.TryGetProperty("no_more", out var noMore) && noMore.ValueKind == JsonValueKind.False;
        return new(items, more && items.Count > 0 && page < 1000 ? Number(page + 1) : null);
    }

    private async Task<MediaPage> CuratedPageAsync(IReadOnlyList<string> videos, string? cursor, CancellationToken token)
    {
        ThrowIfDisposed(); token.ThrowIfCancellationRequested();
        int page = PageNumber(cursor), offset = (page - 1) * 20;
        if (offset >= videos.Count) return new([]);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        // A small worker pool avoids scheduling every entry and preserves the configured order.
        var items = new MediaItem[Math.Min(20, videos.Count - offset)];
        // Inaccessible entries fail the page; never silently shift a partial playlist.
        await Parallel.ForEachAsync(Enumerable.Range(0, items.Length), new ParallelOptions
        {
            MaxDegreeOfParallelism = 4, CancellationToken = deadline.Token
        }, async (index, cancellation) =>
        {
            items[index] = (await GetDetailAsync(videos[offset + index], cancellation).ConfigureAwait(false)).Item;
        }).ConfigureAwait(false);
        return new(items, offset + items.Length < videos.Count ? Number(page + 1) : null);
    }

    public Task<MediaPage> SearchAsync(string query, CancellationToken token) => SearchPageAsync(query, null, token);
    public async Task<MediaPage> SearchPageAsync(string query, string? cursor, CancellationToken token)
    {
        ThrowIfDisposed(); token.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(query)) return new([]);
        if (query.Length > 256) throw new InvalidDataException("搜索关键词超过 256 字符。");
        int page = PageNumber(cursor);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        var session = await SearchSessionAsync(deadline.Token).ConfigureAwait(false);
        var parameters = new Dictionary<string, string>
        {
            ["search_type"] = "video", ["keyword"] = query, ["page"] = Number(page), ["page_size"] = "20", ["order"] = "totalrank",
            ["wts"] = Number(DateTimeOffset.UtcNow.ToUnixTimeSeconds())
        };
        foreach (string key in parameters.Keys.ToArray())
            parameters[key] = string.Concat(parameters[key].Where(x => x is not ('!' or '\'' or '(' or ')' or '*')));
        parameters["w_rid"] = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(Query(parameters) + session.Key)));
        using var document = await FetchAsync("x/web-interface/wbi/search/type", parameters, deadline.Token).ConfigureAwait(false);
        var data = Data(document);
        var items = ReadItems(data, "result");
        return new(items, page < Integer(data, "numPages") && items.Count > 0 && page < 1000 ? Number(page + 1) : null);
    }

    public async Task<MediaDetail> GetDetailAsync(string mediaId, CancellationToken token)
    {
        ThrowIfDisposed(); token.ThrowIfCancellationRequested();
        var parameters = MediaParameters(mediaId);
        if (_details.TryGetValue(mediaId, out var cached) && cached.Expires > DateTimeOffset.UtcNow) return cached.Detail;
        using var document = await FetchAsync("x/web-interface/view", parameters, token).ConfigureAwait(false);
        var data = Data(document);
        string bvid = Text(data, "bvid");
        if (parameters.ContainsKey("bvid") && bvid != mediaId) throw new InvalidDataException("视频详情标识与请求不一致。");
        if (parameters.ContainsKey("aid") && Integer(data, "aid") != long.Parse(parameters["aid"], CultureInfo.InvariantCulture))
            throw new InvalidDataException("视频详情标识与请求不一致。");
        if (!data.TryGetProperty("pages", out var pages) || pages.ValueKind != JsonValueKind.Array || pages.GetArrayLength() > 2048)
            throw new InvalidDataException("视频分集列表缺失或过长。");
        var episodes = new List<Episode>(); var cids = new HashSet<long>();
        foreach (var page in pages.EnumerateArray())
        {
            long cid = Integer(page, "cid");
            if (cid <= 0 || !cids.Add(cid)) throw new InvalidDataException("视频分集 cid 无效或重复。");
            string part = PlainText(Text(page, "part"));
            episodes.Add(new(Number(cid), string.IsNullOrWhiteSpace(part) ? $"P{episodes.Count + 1}" : part));
        }
        if (episodes.Count == 0) throw new InvalidDataException("视频没有可用分集。");
        var detail = new MediaDetail(Card(data) with { Id = mediaId }, PlainText(Text(data, "desc")), [new("ugc", "哔哩哔哩", episodes)]);
        if (_details.Count >= 128) _details.Clear();
        _details[mediaId] = new(detail, DateTimeOffset.UtcNow.AddMinutes(10));
        return detail;
    }

    public async Task<PlaybackRequest> ResolvePlaybackAsync(string mediaId, string episodeId, CancellationToken token)
    {
        var detail = await GetDetailAsync(mediaId, token).ConfigureAwait(false);
        var episode = detail.PlaybackLines.SelectMany(x => x.Episodes).FirstOrDefault(x => x.Id == episodeId)
            ?? throw new InvalidDataException("该视频没有所选分集。");
        var parameters = MediaParameters(mediaId);
        parameters["cid"] = episode.Id; parameters["qn"] = "16"; parameters["fnval"] = "1"; parameters["fourk"] = "0";
        // Signed CDN URLs expire. Resolve on every open; never cache a playback URL.
        using var document = await FetchAsync("x/player/playurl", parameters, token).ConfigureAwait(false);
        var data = Data(document);
        if (data.TryGetProperty("is_preview", out var preview) && (preview.ValueKind == JsonValueKind.True || preview.ValueKind == JsonValueKind.Number && preview.GetInt32() != 0))
            throw new NotSupportedException("此视频仅返回试看内容，需要有权限的账号。");
        if (!data.TryGetProperty("durl", out var durls) || durls.ValueKind != JsonValueKind.Array || durls.GetArrayLength() != 1)
            throw new NotSupportedException("此视频没有单段音画合一媒体；DASH 和多段拼接尚未接入。");
        string url = Text(durls[0], "url");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new InvalidDataException("哔哩哔哩返回的播放地址无效。");
        return new()
        {
            Uri = uri.AbsoluteUri, Title = $"{detail.Item.Title} · {episode.Title}", SourceId = SourceId, MediaId = mediaId, EpisodeId = episodeId,
            Headers = new() { ["User-Agent"] = UserAgent, ["Referer"] = "https://www.bilibili.com/" },
            DanmakuUri = $"https://comment.bilibili.com/{episode.Id}.xml"
        };
    }

    private async Task<JsonDocument> FetchAsync(string path, Dictionary<string, string> parameters, CancellationToken token)
    {
        ThrowIfDisposed();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        await _requests.WaitAsync(timeout.Token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            string query = Query(parameters);
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(Api, path + "?" + query));
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            request.Headers.Referrer = new Uri("https://www.bilibili.com/");
            string? cookie = CookieHeader();
            if (!string.IsNullOrWhiteSpace(cookie)) request.Headers.TryAddWithoutValidation("Cookie", cookie);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode is >= HttpStatusCode.MultipleChoices and < HttpStatusCode.BadRequest)
                throw new InvalidDataException("哔哩哔哩 API 返回了重定向。");
            response.EnsureSuccessStatusCode();
            if (response.RequestMessage?.RequestUri is { } finalUri && (finalUri.Host != Api.Host || finalUri.Scheme != "https"))
                throw new InvalidDataException("哔哩哔哩 API 重定向到了其他站点。");
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            return JsonDocument.Parse(await BoundedContent.ReadAsync(stream, 8 * 1024 * 1024, timeout.Token).ConfigureAwait(false));
        }
        finally { _requests.Release(); }
    }

    private async Task<SearchSession> SearchSessionAsync(CancellationToken token)
    {
        var current = Volatile.Read(ref _searchSession);
        if (current is not null && current.Expires > DateTimeOffset.UtcNow) return current;
        await _searchInitialization.WaitAsync(token).ConfigureAwait(false);
        try
        {
            current = Volatile.Read(ref _searchSession);
            if (current is not null && current.Expires > DateTimeOffset.UtcNow) return current;
            using var navigation = await FetchAsync("x/web-interface/nav", [], token).ConfigureAwait(false);
            var root = navigation.RootElement;
            // Anonymous nav returns -101 but still supplies the public WBI image keys.
            if (Integer(root, "code") is not (0 or -101) || !root.TryGetProperty("data", out var data) || !data.TryGetProperty("wbi_img", out var images))
                throw new InvalidDataException("无法取得哔哩哔哩搜索签名参数。");
            string raw = ImageKey(Text(images, "img_url")) + ImageKey(Text(images, "sub_url"));
            int[] permutation = [46, 47, 18, 2, 53, 8, 23, 32, 15, 50, 10, 31, 58, 3, 45, 35, 27, 43, 5, 49, 33, 9, 42, 19, 29, 28, 14, 39, 12, 38, 41, 13];
            var key = new StringBuilder(32);
            foreach (int index in permutation) key.Append(raw[index]);
            using var visitor = await FetchAsync("x/frontend/finger/spi", [], token).ConfigureAwait(false);
            var visitorData = Data(visitor);
            string buvid3 = Text(visitorData, "b_3"), buvid4 = Text(visitorData, "b_4");
            if (!CookieValue(buvid3) || !CookieValue(buvid4)) throw new InvalidDataException("哔哩哔哩访客会话参数无效。");
            current = new(key.ToString(), $"buvid3={buvid3}; buvid4={buvid4}", DateTimeOffset.UtcNow.AddHours(6));
            Volatile.Write(ref _searchSession, current);
            return current;
        }
        finally { _searchInitialization.Release(); }
    }

    private static string ImageKey(string location)
    {
        if (!Uri.TryCreate(location, UriKind.Absolute, out var uri) || uri.Scheme != "https") throw new InvalidDataException("WBI 图片地址无效。");
        string key = Path.GetFileNameWithoutExtension(uri.AbsolutePath);
        return key.Length == 32 && key.All(char.IsAsciiLetterOrDigit) ? key : throw new InvalidDataException("WBI 图片参数无效。");
    }
    private string? CookieHeader()
    {
        var session = Volatile.Read(ref _searchSession);
        if (session is null) return _cookie;
        var existing = (_cookie ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.Split('=', 2)[0].Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var visitor = session.Cookie.Split(';', StringSplitOptions.TrimEntries).Where(x => !existing.Contains(x.Split('=', 2)[0]));
        return string.Join("; ", new[] { _cookie }.Where(x => !string.IsNullOrWhiteSpace(x)).Concat(visitor));
    }
    private static bool CookieValue(string value) => value.Length is > 0 and <= 256 && value.All(x => char.IsAsciiLetterOrDigit(x) || x is '-' or '_' or '.' or '+' or '/' or '=');
    private static string Query(Dictionary<string, string> parameters) => string.Join('&', parameters.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => Uri.EscapeDataString(x.Key) + "=" + Uri.EscapeDataString(x.Value)));

    private static JsonElement Data(JsonDocument document)
    {
        var root = document.RootElement;
        if (!root.TryGetProperty("code", out var code) || code.ValueKind != JsonValueKind.Number || code.GetInt32() != 0)
            throw new InvalidDataException($"哔哩哔哩 API 错误 {Text(root, "code")}：{PlainText(Text(root, "message"))}");
        return root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
            ? data : throw new InvalidDataException("哔哩哔哩 API 没有返回 data。");
    }

    private static IReadOnlyList<MediaItem> ReadItems(JsonElement data, string key)
    {
        if (!data.TryGetProperty(key, out var list) || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() > 100)
            throw new InvalidDataException("视频列表缺失或过长。");
        return list.EnumerateArray().Where(x => ValidBvid(Text(x, "bvid"))).Select(Card).DistinctBy(x => x.Id).ToList();
    }
    private static MediaItem Card(JsonElement data)
    {
        string picture = Text(data, "pic");
        if (picture.StartsWith("//", StringComparison.Ordinal)) picture = "https:" + picture;
        string? poster = Uri.TryCreate(picture, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri.AbsoluteUri : null;
        long duration = Integer(data, "duration");
        string remarks = data.TryGetProperty("duration", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : $"{duration / 60}:{duration % 60:D2}";
        return new(Text(data, "bvid"), PlainText(Text(data, "title")), poster, remarks);
    }
    private static Dictionary<string, string> MediaParameters(string mediaId)
    {
        if (ValidBvid(mediaId)) return new() { ["bvid"] = mediaId };
        if (mediaId.StartsWith("av", StringComparison.Ordinal) && long.TryParse(mediaId.AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out long aid) && aid > 0)
            return new() { ["aid"] = Number(aid) };
        throw new InvalidDataException("视频标识必须是 BV 号或 av 号。");
    }
    private static bool ValidBvid(string value) => value.Length == 12 && value.StartsWith("BV", StringComparison.Ordinal) && value.All(char.IsAsciiLetterOrDigit);
    private static int PageNumber(string? cursor) => cursor is null ? 1 : int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out int page) && page is >= 1 and <= 1000 ? page : throw new InvalidDataException("无效分页游标。");
    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static string Text(JsonElement element, string key) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var value) && value.ValueKind is JsonValueKind.String or JsonValueKind.Number ? value.ToString() : "";
    private static long Integer(JsonElement element, string key) => long.TryParse(Text(element, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out long value) ? value : 0;
    private static string PlainText(string value)
    {
        var result = new StringBuilder(value.Length); bool tag = false;
        foreach (char character in value)
        {
            if (character == '<') { tag = true; continue; }
            if (character == '>' && tag) { tag = false; continue; }
            if (!tag) result.Append(character);
        }
        return WebUtility.HtmlDecode(result.ToString());
    }
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
    public ValueTask DisposeAsync() { _disposed = true; _details.Clear(); return ValueTask.CompletedTask; }
}
