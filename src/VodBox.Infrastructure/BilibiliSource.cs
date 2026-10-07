using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VodBox.Core;

namespace VodBox.Infrastructure;

/// <summary>
/// 哔哩哔哩公开投稿 C# 爬虫（对应 TVBox <c>csp_Bili</c> / <c>csp_BiliGuard</c>）。
/// 无 JAR、无脚本执行。支持：热门、按关键词分类、BV/av 固定片单、搜索分页、详情分集、
/// 实时播放地址解析、cid 对应的 XML 弹幕地址。
///
/// 分类来自站点 <c>ext.json</c>（<c>{"class":[{type_name,type_id}],...}</c>），
/// 其 <c>type_id</c> 作为搜索关键词——这正是 16 个 csp_Bili 条目共用同一爬虫的原因。
/// </summary>
public sealed class BilibiliSource : IResolvingContentSource, IDisposable
{
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/132.0.0.0 Safari/537.36";

    private const string PopularCategoryId = "popular";

    /// <summary>匿名搜索风控重试次数（实测约 1/4 概率命中 v_voucher）。</summary>
    private const int SearchRetries = 3;

    private static readonly Uri ApiRoot = new("https://api.bilibili.com/");
    private static readonly Uri Referer = new("https://www.bilibili.com/");

    // 独立 HttpClient：故意不跟随重定向（防止被重定向到第三方站点），由 FetchAsync 显式校验最终 URI。
    private static readonly HttpClient SharedHttp = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.All,
    });

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _requests = new(4, 4);
    private readonly SemaphoreSlim _categoryGate = new(1, 1);
    private readonly SemaphoreSlim _searchGate = new(1, 1);
    private readonly ConcurrentDictionary<string, CachedDetail> _details = new(StringComparer.Ordinal);

    private readonly string? _extJsonUrl;
    private readonly string? _cookie;

    private IReadOnlyList<ConfiguredCategory>? _categories;
    private SearchSession? _searchSession;
    private bool _disposed;

    public string Key { get; }
    public string Name { get; }

    public BilibiliSource(SourceInfo info) : this(info, SharedHttp) { }

    public BilibiliSource(SourceInfo info, HttpClient http)
    {
        ArgumentNullException.ThrowIfNull(info);
        Key = info.Key;
        Name = info.Name;
        _http = http;
        (_extJsonUrl, _cookie) = ReadExt(info.Ext);
        if (_cookie is { Length: > 32768 } || _cookie?.IndexOfAny(['\r', '\n', '\0']) >= 0)
            throw new InvalidDataException("哔哩哔哩 Cookie 无效。");
    }

    /// <summary>解析站点 ext：<c>{"json":"分类地址","cookie":"..."}</c>，或直接是分类地址字符串。</summary>
    internal static (string? JsonUrl, string? Cookie) ReadExt(string? ext)
    {
        if (string.IsNullOrWhiteSpace(ext)) return (null, null);
        var trimmed = ext.Trim();
        if (trimmed.StartsWith('{'))
        {
            try
            {
                using var document = JsonDocument.Parse(trimmed);
                var root = document.RootElement;
                string? json = root.TryGetProperty("json", out var j) && j.ValueKind == JsonValueKind.String ? j.GetString() : null;
                string? cookie = root.TryGetProperty("cookie", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
                return (json, cookie);
            }
            catch (JsonException) { return (null, null); }
        }
        // ext 直接是分类地址
        return (trimmed, null);
    }

    // ---------- 分类 ----------

    public async Task<IReadOnlyList<Category>> GetCategoriesAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        var categories = await EnsureCategoriesAsync(ct).ConfigureAwait(false);
        return [new Category(PopularCategoryId, "热门"), .. categories.Select(x => x.Category)];
    }

    private async Task<IReadOnlyList<ConfiguredCategory>> EnsureCategoriesAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref _categories) is { } cached) return cached;
        await _categoryGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _categories) is { } again) return again;
            var result = new List<ConfiguredCategory>();
            if (!string.IsNullOrWhiteSpace(_extJsonUrl))
            {
                var bytes = await FetchRawAsync(_extJsonUrl!, ct).ConfigureAwait(false);
                result.AddRange(ParseCategories(bytes));
            }
            _categories = result;
            return result;
        }
        finally { _categoryGate.Release(); }
    }

    /// <summary>解析 ext.json 的 class 数组为可配置分类（type_id 作为搜索关键词）。</summary>
    internal static List<ConfiguredCategory> ParseCategories(byte[] bytes)
    {
        var text = TextEncoding.Decode(bytes);
        var start = text.IndexOf('{');
        if (start < 0) return [];
        try
        {
            using var document = JsonDocument.Parse(text[start..]);
            if (!document.RootElement.TryGetProperty("class", out var classes) || classes.ValueKind != JsonValueKind.Array)
                return [];
            var result = new List<ConfiguredCategory>();
            var seen = new HashSet<string>(StringComparer.Ordinal) { PopularCategoryId };
            foreach (var item in classes.EnumerateArray())
            {
                if (result.Count >= 500) break;
                if (item.ValueKind != JsonValueKind.Object) continue;
                string id = Text(item, "type_id"), name = Text(item, "type_name");
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name)) continue;
                if (id.Length > 128 || name.Length > 128 || !seen.Add(id)) continue;
                result.Add(new ConfiguredCategory(new Category(id, name), id));
            }
            return result;
        }
        catch (JsonException) { return []; }
    }

    // ---------- 列表 ----------

    public async Task<MediaPage> GetHomeAsync(CancellationToken ct = default) =>
        await GetItemsAsync(PopularCategoryId, 1, null, ct).ConfigureAwait(false);

    public async Task<MediaPage> GetItemsAsync(string categoryId, int page, IReadOnlyDictionary<string, string>? filters, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (page < 1) page = 1;
        if (categoryId is null or "" or PopularCategoryId)
            return await PopularAsync(page, ct).ConfigureAwait(false);
        var categories = await EnsureCategoriesAsync(ct).ConfigureAwait(false);
        var category = categories.FirstOrDefault(x => x.Category.Id == categoryId)
            ?? throw new InvalidDataException($"未知的哔哩哔哩分类：{categoryId}");
        return await SearchPageAsync(category.Query, page, ct).ConfigureAwait(false);
    }

    private async Task<MediaPage> PopularAsync(int page, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        using var document = await FetchAsync("x/web-interface/popular",
            new() { ["pn"] = Number(page), ["ps"] = "20" }, deadline.Token).ConfigureAwait(false);
        var data = Data(document);
        var items = ReadItems(data, "list");
        bool more = data.TryGetProperty("no_more", out var noMore) && noMore.ValueKind == JsonValueKind.False;
        // 页码递增、总数未知：有更多则给下一页，末页保持当前页（不再伪造页数）
        return new MediaPage(items, page, more && items.Count > 0 ? page + 1 : page);
    }

    // ---------- 搜索 ----------

    public async Task<MediaPage> SearchAsync(string query, int page, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(query)) return new MediaPage([], 1, 1);
        if (page < 1) page = 1;
        return await SearchPageAsync(query, page, ct).ConfigureAwait(false);
    }

    private async Task<MediaPage> SearchPageAsync(string query, int page, CancellationToken ct)
    {
        if (query.Length > 256) throw new InvalidDataException("搜索关键词超过 256 字符。");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        var session = await SearchSessionAsync(deadline.Token).ConfigureAwait(false);
        var parameters = new Dictionary<string, string>
        {
            ["search_type"] = "video",
            ["keyword"] = query,
            ["page"] = Number(page),
            ["page_size"] = "20",
            ["order"] = "totalrank",
            ["wts"] = Number(DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
        };
        // WBI 签名前需剔除值里的 ! ' ( ) * 字符
        foreach (var key in parameters.Keys.ToArray())
            parameters[key] = string.Concat(parameters[key].Where(x => x is not ('!' or '\'' or '(' or ')' or '*')));
        parameters["w_rid"] = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(Query(parameters) + session.Key)));

        // 匿名搜索会间歇触发风控：code=0 但 data 只有 v_voucher（验证码凭证挑战）而没有 result。
        // 实测 8 次中 2 次命中，故有限次重试；仍失败则给出可诊断的明确错误，而不是伪装成「无结果」。
        JsonDocument? document = null;
        JsonElement data = default;
        for (var attempt = 1; attempt <= SearchRetries; attempt++)
        {
            document?.Dispose();
            document = await FetchAsync("x/web-interface/wbi/search/type", parameters, deadline.Token).ConfigureAwait(false);
            data = Data(document);
            if (!IsRiskControl(data)) break;
            if (attempt == SearchRetries)
                throw new NotSupportedException("哔哩哔哩搜索触发风控验证（v_voucher），已重试仍未放行；请稍后再试或在站点配置中提供登录 Cookie。");
            await Task.Delay(TimeSpan.FromMilliseconds(300 * attempt), deadline.Token).ConfigureAwait(false);
        }

        var items = ReadItems(data!, "result");
        int totalPages = (int)Math.Max(1, Integer(data, "numPages"));
        int nextPage = page < totalPages && items.Count > 0 ? page + 1 : page;
        return new MediaPage(items, page, nextPage);
    }

    // ---------- 详情 ----------

    public async Task<MediaDetail> GetDetailAsync(string mediaId, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        var parameters = MediaParameters(mediaId);
        if (_details.TryGetValue(mediaId, out var cached) && cached.Expires > DateTimeOffset.UtcNow)
            return cached.Detail;

        using var document = await FetchAsync("x/web-interface/view", parameters, ct).ConfigureAwait(false);
        var data = Data(document);
        string bvid = Text(data, "bvid");
        if (parameters.ContainsKey("bvid") && bvid != mediaId)
            throw new InvalidDataException("视频详情标识与请求不一致。");
        if (parameters.ContainsKey("aid") && Integer(data, "aid") != long.Parse(parameters["aid"], CultureInfo.InvariantCulture))
            throw new InvalidDataException("视频详情标识与请求不一致。");
        if (!data.TryGetProperty("pages", out var pages) || pages.ValueKind != JsonValueKind.Array || pages.GetArrayLength() > 2048)
            throw new InvalidDataException("视频分集列表缺失或过长。");

        var episodes = new List<Episode>();
        var cids = new HashSet<long>();
        foreach (var part in pages.EnumerateArray())
        {
            long cid = Integer(part, "cid");
            if (cid <= 0 || !cids.Add(cid)) throw new InvalidDataException("视频分集 cid 无效或重复。");
            string title = PlainText(Text(part, "part"));
            episodes.Add(new Episode(Number(cid), string.IsNullOrWhiteSpace(title) ? $"P{episodes.Count + 1}" : title));
        }
        if (episodes.Count == 0) throw new InvalidDataException("视频没有可用分集。");

        var detail = new MediaDetail
        {
            Item = Card(data) with { Id = mediaId },
            Description = PlainText(Text(data, "desc")),
            Lines = [new PlaybackLine("ugc", "哔哩哔哩", episodes)],
        };
        if (_details.Count >= 128) _details.Clear();
        _details[mediaId] = new CachedDetail(detail, DateTimeOffset.UtcNow.AddMinutes(10));
        return detail;
    }

    // ---------- 播放地址即时解析 ----------

    public async Task<PlaybackRequest> ResolvePlaybackAsync(string mediaId, string episodeId, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        var detail = await GetDetailAsync(mediaId, ct).ConfigureAwait(false);
        var episode = detail.Lines.SelectMany(x => x.Episodes).FirstOrDefault(x => x.Id == episodeId)
            ?? throw new InvalidDataException("该视频没有所选分集。");

        var parameters = MediaParameters(mediaId);
        parameters["cid"] = episode.Id;
        parameters["qn"] = "16";   // 360P 单段音画合一
        parameters["fnval"] = "1"; // 强制 durl（非 DASH）
        parameters["fourk"] = "0";
        // 签名 CDN 地址会过期：每次打开都现取，绝不缓存播放地址
        using var document = await FetchAsync("x/player/playurl", parameters, ct).ConfigureAwait(false);
        var data = Data(document);
        if (data.TryGetProperty("is_preview", out var preview)
            && (preview.ValueKind == JsonValueKind.True || (preview.ValueKind == JsonValueKind.Number && preview.GetInt32() != 0)))
            throw new NotSupportedException("此视频仅返回试看内容，需要有权限的账号。");
        if (!data.TryGetProperty("durl", out var durls) || durls.ValueKind != JsonValueKind.Array || durls.GetArrayLength() != 1)
            throw new NotSupportedException("此视频没有单段音画合一媒体；DASH 与多段拼接尚未接入。");

        string url = Text(durls[0], "url");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new InvalidDataException("哔哩哔哩返回的播放地址无效。");

        return new PlaybackRequest
        {
            Uri = uri.AbsoluteUri,
            Title = $"{detail.Item.Title} · {episode.Title}",
            SourceKey = Key,
            SourceName = Name,
            MediaId = mediaId,
            LineId = "ugc",
            EpisodeId = episodeId,
            Poster = detail.Item.Poster,
            Remarks = detail.Item.Remarks,
            Headers = new Dictionary<string, string>
            {
                ["User-Agent"] = UserAgent,
                ["Referer"] = "https://www.bilibili.com/",
            },
            DanmakuUri = $"https://comment.bilibili.com/{episode.Id}.xml",
        };
    }

    // ---------- HTTP ----------

    private async Task<JsonDocument> FetchAsync(string path, Dictionary<string, string> parameters, CancellationToken ct)
    {
        ThrowIfDisposed();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        await _requests.WaitAsync(timeout.Token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(ApiRoot, path + "?" + Query(parameters)));
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            request.Headers.Referrer = Referer;
            string? cookie = CookieHeader();
            if (!string.IsNullOrWhiteSpace(cookie)) request.Headers.TryAddWithoutValidation("Cookie", cookie);

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode is >= HttpStatusCode.MultipleChoices and < HttpStatusCode.BadRequest)
                throw new InvalidDataException("哔哩哔哩 API 返回了重定向。");
            response.EnsureSuccessStatusCode();
            // 即使不自动跟随重定向，也校验最终 URI 未被改写
            if (response.RequestMessage?.RequestUri is { } finalUri && (finalUri.Host != ApiRoot.Host || finalUri.Scheme != "https"))
                throw new InvalidDataException("哔哩哔哩 API 重定向到了其他站点。");
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            return JsonDocument.Parse(await BoundedContent.ReadAsync(stream, 8 * 1024 * 1024, timeout.Token).ConfigureAwait(false));
        }
        finally { _requests.Release(); }
    }

    /// <summary>抓取非 API 地址（ext.json 分类配置），限制体积与超时。</summary>
    private async Task<byte[]> FetchRawAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new InvalidDataException("哔哩哔哩分类配置地址无效。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        return await BoundedContent.ReadAsync(stream, 2 * 1024 * 1024, timeout.Token).ConfigureAwait(false);
    }

    private async Task<SearchSession> SearchSessionAsync(CancellationToken ct)
    {
        var current = Volatile.Read(ref _searchSession);
        if (current is not null && current.Expires > DateTimeOffset.UtcNow) return current;
        await _searchGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            current = Volatile.Read(ref _searchSession);
            if (current is not null && current.Expires > DateTimeOffset.UtcNow) return current;

            using var navigation = await FetchAsync("x/web-interface/nav", [], ct).ConfigureAwait(false);
            var root = navigation.RootElement;
            // 匿名 nav 返回 -101，但仍提供公开的 WBI 图片密钥
            if (Integer(root, "code") is not (0 or -101) || !root.TryGetProperty("data", out var data) || !data.TryGetProperty("wbi_img", out var images))
                throw new InvalidDataException("无法取得哔哩哔哩搜索签名参数。");
            string raw = ImageKey(Text(images, "img_url")) + ImageKey(Text(images, "sub_url"));
            int[] permutation =
            [
                46, 47, 18, 2, 53, 8, 23, 32, 15, 50, 10, 31, 58, 3, 45, 35, 27, 43, 5, 49,
                33, 9, 42, 19, 29, 28, 14, 39, 12, 38, 41, 13,
            ];
            var key = new StringBuilder(32);
            foreach (int index in permutation) key.Append(raw[index]);

            using var visitor = await FetchAsync("x/frontend/finger/spi", [], ct).ConfigureAwait(false);
            var visitorData = Data(visitor);
            string buvid3 = Text(visitorData, "b_3"), buvid4 = Text(visitorData, "b_4");
            if (!CookieValue(buvid3) || !CookieValue(buvid4))
                throw new InvalidDataException("哔哩哔哩访客会话参数无效。");

            current = new SearchSession(key.ToString(), $"buvid3={buvid3}; buvid4={buvid4}", DateTimeOffset.UtcNow.AddHours(6));
            Volatile.Write(ref _searchSession, current);
            return current;
        }
        finally { _searchGate.Release(); }
    }

    private string? CookieHeader()
    {
        var session = Volatile.Read(ref _searchSession);
        if (session is null) return _cookie;
        var existing = (_cookie ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.Split('=', 2)[0].Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var visitor = session.Cookie.Split(';', StringSplitOptions.TrimEntries)
            .Where(x => !existing.Contains(x.Split('=', 2)[0]));
        return string.Join("; ", new[] { _cookie }.Where(x => !string.IsNullOrWhiteSpace(x)).Concat(visitor));
    }

    // ---------- 解析辅助 ----------

    private static string ImageKey(string location)
    {
        if (!Uri.TryCreate(location, UriKind.Absolute, out var uri) || uri.Scheme != "https")
            throw new InvalidDataException("WBI 图片地址无效。");
        string key = Path.GetFileNameWithoutExtension(uri.AbsolutePath);
        return key.Length == 32 && key.All(char.IsAsciiLetterOrDigit) ? key : throw new InvalidDataException("WBI 图片参数无效。");
    }

    private static bool CookieValue(string value) =>
        value.Length is > 0 and <= 256 && value.All(x => char.IsAsciiLetterOrDigit(x) || x is '-' or '_' or '.' or '+' or '/' or '=');

    private static string Query(Dictionary<string, string> parameters) =>
        string.Join('&', parameters.OrderBy(x => x.Key, StringComparer.Ordinal)
            .Select(x => Uri.EscapeDataString(x.Key) + "=" + Uri.EscapeDataString(x.Value)));

    private static JsonElement Data(JsonDocument document)
    {
        var root = document.RootElement;
        if (!root.TryGetProperty("code", out var code) || code.ValueKind != JsonValueKind.Number || code.GetInt32() != 0)
            throw new InvalidDataException($"哔哩哔哩 API 错误 {Text(root, "code")}：{PlainText(Text(root, "message"))}");
        return root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
            ? data : throw new InvalidDataException("哔哩哔哩 API 没有返回 data。");
    }

    /// <summary>
    /// 判定响应是否为风控挑战：<c>code=0</c> 但 <c>data</c> 只带 <c>v_voucher</c>（无 result 数组）。
    /// 这是哔哩哔哩对匿名搜索的间歇性验证要求，不是「无结果」，也不是协议错误。
    /// </summary>
    internal static bool IsRiskControl(JsonElement data) =>
        data.ValueKind == JsonValueKind.Object
        && data.TryGetProperty("v_voucher", out var voucher)
        && voucher.ValueKind == JsonValueKind.String
        && !data.TryGetProperty("result", out _);

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
        string remarks = data.TryGetProperty("duration", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : $"{duration / 60}:{duration % 60:D2}";
        return new MediaItem
        {
            Id = Text(data, "bvid"),
            Title = PlainText(Text(data, "title")),
            Poster = poster,
            Remarks = remarks,
        };
    }

    private static Dictionary<string, string> MediaParameters(string mediaId)
    {
        if (ValidBvid(mediaId)) return new() { ["bvid"] = mediaId };
        if (mediaId.StartsWith("av", StringComparison.Ordinal)
            && long.TryParse(mediaId.AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out long aid) && aid > 0)
            return new() { ["aid"] = Number(aid) };
        throw new InvalidDataException("视频标识必须是 BV 号或 av 号。");
    }

    private static bool ValidBvid(string value) =>
        value.Length == 12 && value.StartsWith("BV", StringComparison.Ordinal) && value.All(char.IsAsciiLetterOrDigit);

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Text(JsonElement element, string key) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var value)
        && value.ValueKind is JsonValueKind.String or JsonValueKind.Number ? value.ToString() : "";

    private static long Integer(JsonElement element, string key) =>
        long.TryParse(Text(element, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out long value) ? value : 0;

    /// <summary>去除 HTML 标签并解码实体（B 站标题/简介含 &lt;em&gt; 高亮等）。</summary>
    internal static string PlainText(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var result = new StringBuilder(value.Length);
        bool tag = false;
        foreach (char character in value)
        {
            if (character == '<') { tag = true; continue; }
            if (character == '>' && tag) { tag = false; continue; }
            if (!tag) result.Append(character);
        }
        return WebUtility.HtmlDecode(result.ToString());
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose() { _disposed = true; _details.Clear(); }

    /// <summary>ext.json 中配置的一个分类（type_id 作为搜索关键词）。</summary>
    internal sealed record ConfiguredCategory(Category Category, string Query);

    private sealed record CachedDetail(MediaDetail Detail, DateTimeOffset Expires);
    private sealed record SearchSession(string Key, string Cookie, DateTimeOffset Expires);
}
