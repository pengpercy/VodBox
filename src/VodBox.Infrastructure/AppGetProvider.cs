using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VodBox.Core;

namespace VodBox.Infrastructure;

/// <summary>Independent asynchronous AppGet protocol adapter; never executes a JAR.</summary>
public sealed class AppGetProvider : IContentProvider
{
    private static readonly HttpClient DefaultHttp = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.All
    });
    private readonly HttpClient _http;
    private readonly Uri _entry;
    private readonly byte[] _key, _iv;
    private readonly bool _discover, _jsonRequests;
    private readonly string _apiPath, _userAgent, _version, _device, _initAction, _searchAction, _detailAction;
    private readonly SemaphoreSlim _requests = new(4, 4), _initialization = new(1, 1);
    private readonly ConcurrentDictionary<string, CachedDetail> _details = new(StringComparer.Ordinal);
    private Uri? _endpoint;
    private Home? _home;
    private bool _disposed;
    public string SourceId { get; }
    private sealed record Home(IReadOnlyList<Category> Categories, IReadOnlyList<MediaItem> Items, bool SearchVerification, DateTimeOffset Expires);
    private sealed record PlayEntry(string Url, string ParseApi, string Token, string UserAgent, string PlayerParseType, string ParseType, string ParseUrl);
    private sealed record CachedDetail(MediaDetail Detail, IReadOnlyDictionary<string, PlayEntry> Entries, DateTimeOffset Expires);

    public AppGetProvider(SourceDefinition source) : this(source, DefaultHttp) { }
    public AppGetProvider(SourceDefinition source, HttpClient http)
    {
        SourceId = source.Id; _http = http;
        _entry = HttpUri(source.Entry ?? "");
        string protocol = Option(source, "protocol");
        _jsonRequests = protocol switch
        {
            "v119" => false,
            "qiji-v122" => true,
            _ => throw new NotSupportedException("AppGet 需要显式 protocol=v119 或 qiji-v122；其他版本尚未核实。")
        };
        _initAction = _jsonRequests ? "initV122" : "initV119";
        _searchAction = _jsonRequests ? "searchList4" : "searchList";
        _detailAction = _jsonRequests ? "vodDetail2" : "vodDetail";
        _key = Encoding.UTF8.GetBytes(Option(source, "key"));
        if (_key.Length != 16) throw new InvalidDataException("AppGet key 必须是 16 个 UTF-8 字节。");
        _iv = Encoding.UTF8.GetBytes(Option(source, "iv", Option(source, "key")));
        if (_iv.Length != 16) throw new InvalidDataException("AppGet iv 必须是 16 个 UTF-8 字节。");
        _discover = source.Options.TryGetValue("discovery", out var discovery) && discovery.ValueKind == JsonValueKind.True;
        _apiPath = Option(source, "apiPath", _jsonRequests ? "/api.php/qijiappapi" : "/api.php/getappapi");
        if (!_apiPath.StartsWith('/') || _apiPath.Contains("..", StringComparison.Ordinal) || _apiPath.Length > 256 ||
            !_apiPath.All(x => char.IsAsciiLetterOrDigit(x) || x is '/' or '.' or '_' or '-'))
            throw new InvalidDataException("AppGet apiPath 无效。");
        _userAgent = Header(Option(source, "userAgent", "okhttp/3.14.9"));
        _version = Header(Option(source, "version", _jsonRequests ? "305" : "210"));
        _device = Header(Option(source, "deviceId", Guid.NewGuid().ToString("N")));
        if (!_discover) _endpoint = Origin(_entry);
    }

    public async Task<IReadOnlyList<Category>> GetCategoriesAsync(CancellationToken token) => (await HomeAsync(token).ConfigureAwait(false)).Categories;
    public Task<MediaPage> GetItemsAsync(string? categoryId, string? cursor, CancellationToken token) =>
        GetItemsFilteredAsync(categoryId, cursor, new Dictionary<string, string>(), token);
    public async Task<MediaPage> GetItemsFilteredAsync(string? categoryId, string? cursor, IReadOnlyDictionary<string, string> filters, CancellationToken token)
    {
        ThrowIfDisposed(); token.ThrowIfCancellationRequested();
        int page = PageNumber(cursor);
        if (categoryId is null)
        {
            if (filters.Count != 0) throw new InvalidDataException("AppGet 推荐页不支持分类筛选。");
            return new(page == 1 ? (await HomeAsync(token).ConfigureAwait(false)).Items : []);
        }
        ValidateId(categoryId);
        var body = new Dictionary<string, string> { ["type_id"] = categoryId, ["page"] = Number(page), ["sort"] = "最新", ["area"] = "全部", ["year"] = "全部", ["lang"] = "全部", ["class"] = "全部" };
        foreach (var filter in filters)
        {
            if (filter.Key is not ("sort" or "area" or "year" or "lang" or "class")) throw new NotSupportedException("AppGet 不支持该筛选字段。");
            if (filter.Value.Length > 128) throw new InvalidDataException("AppGet 筛选值过长。");
            body[filter.Key] = filter.Value;
        }
        using var data = await FetchAsync("typeFilterVodList", body, token).ConfigureAwait(false);
        return Page(data.RootElement, "recommend_list", page);
    }
    public Task<MediaPage> SearchAsync(string query, CancellationToken token) => SearchPageAsync(query, null, token);
    public async Task<MediaPage> SearchPageAsync(string query, string? cursor, CancellationToken token)
    {
        ThrowIfDisposed(); token.ThrowIfCancellationRequested();
        int page = PageNumber(cursor);
        if (string.IsNullOrWhiteSpace(query)) return new([]);
        if (query.Length > 256) throw new InvalidDataException("AppGet 搜索关键词过长。");
        if (_jsonRequests && (await HomeAsync(token).ConfigureAwait(false)).SearchVerification)
            throw new NotSupportedException("AppGet 此站点搜索需要验证码，交互验证尚未接入。");
        using var data = await FetchAsync(_searchAction, new() { ["keywords"] = query, ["type_id"] = "0", ["page"] = Number(page) }, token).ConfigureAwait(false);
        return Page(data.RootElement, "search_list", page);
    }

    public async Task<MediaDetail> GetDetailAsync(string mediaId, CancellationToken token) => (await DetailAsync(mediaId, token).ConfigureAwait(false)).Detail;
    private async Task<CachedDetail> DetailAsync(string mediaId, CancellationToken token, bool refresh = false)
    {
        ThrowIfDisposed(); token.ThrowIfCancellationRequested(); ValidateId(mediaId);
        if (!refresh && _details.TryGetValue(mediaId, out var cached) && cached.Expires > DateTimeOffset.UtcNow) return cached;
        using var document = await FetchAsync(_detailAction, new() { ["vod_id"] = mediaId }, token).ConfigureAwait(false);
        var root = document.RootElement;
        if (!root.TryGetProperty("vod", out var vod) || Text(vod, "vod_id") != mediaId) throw new InvalidDataException("AppGet 视频详情标识不一致。");
        var playlists = Array(root, "vod_play_list", 64);
        var lines = new List<PlaybackLine>(); var entries = new Dictionary<string, PlayEntry>(StringComparer.Ordinal);
        foreach (var playlist in playlists.EnumerateArray())
        {
            if (!playlist.TryGetProperty("player_info", out var player) || player.ValueKind != JsonValueKind.Object) throw new InvalidDataException("AppGet 缺少线路信息。");
            var episodes = new List<Episode>();
            foreach (var episode in Array(playlist, "urls", 10000).EnumerateArray())
            {
                if (entries.Count >= 10000) throw new InvalidDataException("AppGet 分集数量过多。");
                string url = Text(episode, "url");
                if (string.IsNullOrWhiteSpace(url) || url.Length > 16384) throw new InvalidDataException("AppGet 分集地址为空或过长。");
                string id = $"{lines.Count}:{episodes.Count}";
                episodes.Add(new(id, Text(episode, "name")));
                entries.Add(id, new(url, Text(player, "parse"), Text(episode, "token"), Text(player, "user_agent"),
                    Text(player, "player_parse_type"), Text(player, "parse_type"), Text(episode, "parse_api_url")));
            }
            if (episodes.Count > 0) lines.Add(new(Number(lines.Count), Text(player, "show"), episodes));
        }
        var result = new CachedDetail(new(Card(vod), WebUtility.HtmlDecode(Text(vod, "vod_content")), lines), entries, DateTimeOffset.UtcNow.AddMinutes(10));
        if (_details.Count >= 128) _details.Clear();
        _details[mediaId] = result;
        return result;
    }
    public async Task<PlaybackRequest> ResolvePlaybackAsync(string mediaId, string episodeId, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(20));
        // Detail responses can contain expiring media URLs and parser tokens. Refresh on every open.
        var detail = await DetailAsync(mediaId, deadline.Token, refresh: true).ConfigureAwait(false);
        if (!detail.Entries.TryGetValue(episodeId, out var entry)) throw new InvalidDataException("AppGet 所选分集不存在。");
        string url = entry.Url;
        if (!IsMedia(url) && entry.ParseType != "0")
        {
            if (entry.ParseType == "2") throw new NotSupportedException("AppGet 此线路要求网页解析，尚未接入。");
            if (entry.PlayerParseType == "2")
            {
                string location = string.IsNullOrWhiteSpace(entry.ParseUrl) ? entry.ParseApi + Uri.UnescapeDataString(url) : entry.ParseUrl;
                url = await ExternalParseAsync(location, entry.UserAgent, deadline.Token).ConfigureAwait(false);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(entry.ParseApi)) throw new NotSupportedException("AppGet 此线路缺少站内解析标识。");
                // A single form encoding is required; pre-escaping Base64 changes the ciphertext on the wire.
                using var parsed = await FetchAsync("vodParse", new()
                {
                    ["parse_api"] = entry.ParseApi, ["url"] = Encrypt(Uri.UnescapeDataString(url)), ["token"] = entry.Token,
                    ["player_parse_type"] = entry.PlayerParseType
                }, deadline.Token).ConfigureAwait(false);
                using var result = JsonDocument.Parse(Text(parsed.RootElement, "json"));
                url = Text(result.RootElement, "url");
            }
        }
        var episode = detail.Detail.PlaybackLines.SelectMany(x => x.Episodes).First(x => x.Id == episodeId);
        return new()
        {
            Uri = HttpUri(url).AbsoluteUri, Title = $"{detail.Detail.Item.Title} · {episode.Title}", SourceId = SourceId, MediaId = mediaId, EpisodeId = episodeId,
            Headers = new() { ["User-Agent"] = string.IsNullOrEmpty(entry.UserAgent) ? _userAgent : Header(entry.UserAgent) }
        };
    }

    private async Task<string> ExternalParseAsync(string location, string userAgent, CancellationToken token)
    {
        if (location.Length > 16384) throw new InvalidDataException("AppGet 外部解析地址过长。");
        var uri = HttpUri(location);
        await _requests.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            // The parser receives no API signature, device identity, Cookie or form token.
            request.Headers.TryAddWithoutValidation("User-Agent", string.IsNullOrWhiteSpace(userAgent) ? _userAgent : Header(userAgent));
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            CheckResponse(response, uri);
            await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var json = JsonDocument.Parse(await BoundedContent.ReadAsync(stream, 1024 * 1024, token).ConfigureAwait(false));
            string url = Text(json.RootElement, "url");
            if (url.Length == 0 && json.RootElement.TryGetProperty("data", out var data)) url = Text(data, "url");
            return HttpUri(url).AbsoluteUri;
        }
        finally { _requests.Release(); }
    }

    private async Task<Home> HomeAsync(CancellationToken token)
    {
        ThrowIfDisposed(); token.ThrowIfCancellationRequested();
        var home = Volatile.Read(ref _home);
        if (home is not null && home.Expires > DateTimeOffset.UtcNow) return home;
        await _initialization.WaitAsync(token).ConfigureAwait(false);
        try
        {
            home = Volatile.Read(ref _home);
            if (home is not null && home.Expires > DateTimeOffset.UtcNow) return home;
            using var data = await FetchAsync(_initAction, [], token).ConfigureAwait(false);
            var categories = new List<Category>(); var items = new List<MediaItem>();
            if (data.RootElement.TryGetProperty("banner_list", out _)) items.AddRange(Items(data.RootElement, "banner_list"));
            foreach (var type in Array(data.RootElement, "type_list", 256).EnumerateArray())
            {
                string name = Text(type, "type_name"), id = Text(type, "type_id");
                if (name is "全部" or "直播") continue;
                ValidateId(id); categories.Add(new(id, name));
                if (items.Count < 1000 && type.TryGetProperty("recommend_list", out _))
                    items.AddRange(Items(type, "recommend_list").Take(1000 - items.Count));
            }
            bool searchVerification = data.RootElement.TryGetProperty("config", out var config) && Flag(config, "system_search_verify_status");
            home = new(categories.DistinctBy(x => x.Id).ToArray(), items.DistinctBy(x => x.Id).Take(1000).ToArray(), searchVerification, DateTimeOffset.UtcNow.AddMinutes(10));
            Volatile.Write(ref _home, home); return home;
        }
        finally { _initialization.Release(); }
    }

    private async Task<JsonDocument> FetchAsync(string action, Dictionary<string, string> body, CancellationToken token)
    {
        ThrowIfDisposed();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(20));
        await _requests.WaitAsync(deadline.Token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var endpoint = Volatile.Read(ref _endpoint);
            if (endpoint is null)
            {
                // Discovery receives only User-Agent, never the signature, device identity or key.
                using var discovery = new HttpRequestMessage(HttpMethod.Get, _entry);
                discovery.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
                using var response = await _http.SendAsync(discovery, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
                CheckResponse(response, _entry);
                await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
                endpoint = Origin(HttpUri(Encoding.UTF8.GetString(await BoundedContent.ReadAsync(stream, 512, deadline.Token).ConfigureAwait(false)).Trim()));
                Interlocked.CompareExchange(ref _endpoint, endpoint, null);
            }
            var uri = new Uri(endpoint, _apiPath + ".index/" + action);
            using var request = new HttpRequestMessage(HttpMethod.Post, uri);
            request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
            if (_jsonRequests)
            {
                body["version"] = _version;
                request.Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(body, VodBoxJson.Default.DictionaryStringString));
                request.Content.Headers.ContentType = new("application/json");
            }
            else
            {
                request.Content = new FormUrlEncodedContent(body);
                string timestamp = Number(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                request.Headers.TryAddWithoutValidation("app-version-code", _version);
                request.Headers.TryAddWithoutValidation("app-ui-mode", "light");
                request.Headers.TryAddWithoutValidation("app-api-verify-time", timestamp);
                request.Headers.TryAddWithoutValidation("app-user-device-id", _device);
                request.Headers.TryAddWithoutValidation("app-api-verify-sign", Encrypt(timestamp));
            }
            using var result = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            CheckResponse(result, uri);
            await using var content = await result.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            using var envelope = JsonDocument.Parse(await BoundedContent.ReadAsync(content, 8 * 1024 * 1024, deadline.Token).ConfigureAwait(false));
            var root = envelope.RootElement;
            if (Text(root, "code") != "1" || !root.TryGetProperty("data", out var encrypted) || encrypted.ValueKind != JsonValueKind.String)
                throw new InvalidDataException($"AppGet {action} API 拒绝请求或未返回加密 data（code={Text(root, "code")}）；可能需要验证或登录。");
            byte[] decoded;
            try
            {
                using var aes = Aes.Create(); aes.Key = _key;
                decoded = aes.DecryptCbc(Convert.FromBase64String(encrypted.GetString()!), _iv, PaddingMode.PKCS7);
            }
            catch (Exception error) when (error is FormatException or CryptographicException)
            { throw new InvalidDataException("AppGet 加密响应或密钥无效。", error); }
            var document = JsonDocument.Parse(decoded);
            if (document.RootElement.ValueKind != JsonValueKind.Object) { document.Dispose(); throw new InvalidDataException("AppGet 解密结果不是对象。"); }
            return document;
        }
        finally { _requests.Release(); }
    }
    private string Encrypt(string text)
    {
        using var aes = Aes.Create(); aes.Key = _key;
        return Convert.ToBase64String(aes.EncryptCbc(Encoding.UTF8.GetBytes(text), _iv, PaddingMode.PKCS7));
    }
    private static void CheckResponse(HttpResponseMessage response, Uri expected)
    {
        if ((int)response.StatusCode is >= 300 and < 400) throw new InvalidDataException("AppGet 不允许 HTTP 重定向。");
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri is { } final && final != expected) throw new InvalidDataException("AppGet 请求地址发生重定向。");
    }
    private static MediaPage Page(JsonElement data, string key, int page)
    {
        var items = Items(data, key);
        // Some deployments omit totals. In that case an empty following page ends pagination.
        string count = Text(data, "pagecount");
        bool more = items.Count > 0 && page < 1000 && (count.Length == 0 || int.TryParse(count, out int pages) && page < pages);
        return new(items, more ? Number(page + 1) : null);
    }
    private static IReadOnlyList<MediaItem> Items(JsonElement data, string key) => Array(data, key, 1000).EnumerateArray().Select(Card).DistinctBy(x => x.Id).ToArray();
    private static MediaItem Card(JsonElement item)
    {
        string id = Text(item, "vod_id"); ValidateId(id);
        string pic = Text(item, "vod_pic"); if (pic.StartsWith("//", StringComparison.Ordinal)) pic = "https:" + pic;
        string? poster = Uri.TryCreate(pic, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" ? uri.AbsoluteUri : null;
        return new(id, WebUtility.HtmlDecode(Text(item, "vod_name")), poster, Text(item, "vod_remarks"));
    }
    private static JsonElement Array(JsonElement root, string name, int maximum) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array && list.GetArrayLength() <= maximum ? list : throw new InvalidDataException("AppGet 列表缺失或过长：" + name);
    private static string Text(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String or JsonValueKind.Number ? value.ToString() : "";
    private static bool Flag(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var value)) return false;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False or JsonValueKind.Null => false,
            JsonValueKind.Number when value.TryGetInt64(out long number) => number != 0,
            JsonValueKind.String when value.GetString() is "1" or "true" => true,
            JsonValueKind.String when value.GetString() is "0" or "false" or "" => false,
            _ => throw new InvalidDataException("AppGet 搜索验证状态格式不受支持。")
        };
    }
    private static string Option(SourceDefinition source, string name, string fallback = "") => source.Options.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : fallback;
    private static string Header(string value) => value.Length is > 0 and <= 2048 && value.All(x => x >= ' ' && x <= '~') ? value : throw new InvalidDataException("AppGet 请求头无效。");
    private static void ValidateId(string id) { if (id.Length is < 1 or > 128 || !id.All(char.IsAsciiDigit)) throw new InvalidDataException("AppGet 标识必须是数字。"); }
    private static Uri HttpUri(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0 ? uri : throw new InvalidDataException("AppGet 地址必须是无凭据的 HTTP/HTTPS URL。");
    private static Uri Origin(Uri uri) => uri.AbsolutePath is "" or "/" && uri.Query.Length == 0 && uri.Fragment.Length == 0 ? uri : throw new InvalidDataException("AppGet 服务 entry 必须是根地址；发现文件需显式 discovery=true。");
    private static bool IsMedia(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && Path.GetExtension(uri.AbsolutePath).ToLowerInvariant() is ".mp4" or ".m3u8" or ".flv" or ".mkv" or ".webm" or ".ts" or ".mov" or ".mpd";
    private static int PageNumber(string? cursor) => cursor is null ? 1 : int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out int page) && page is >= 1 and <= 1000 ? page : throw new InvalidDataException("AppGet 分页游标无效。");
    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
    public ValueTask DisposeAsync() { _disposed = true; _details.Clear(); Volatile.Write(ref _home, null); return ValueTask.CompletedTask; }
}
