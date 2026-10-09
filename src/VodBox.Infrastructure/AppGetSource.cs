using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VodBox.Core;

namespace VodBox.Infrastructure;

/// <summary>
/// csp_AppGet 原生适配（AppGet 协议，AES-128-CBC/PKCS7 加密封包）。
/// 双分支：
/// 1. <b>v119（form 签名）</b>——表单请求 + 时间戳签名头（app-api-verify-sign），
///    initV119/searchList/vodDetail；咕咕真机 JIT+AOT 验证通过的组合；
/// 2. <b>qiji-v122（JSON）</b>——JSON body + version 字段，initV122/searchList4/vodDetail2。
/// 分支由 TVBox ext 第 3 段版本号选择（V119→v119，V122→qiji-v122）。
///
/// ext 支持两种形态（真实宝盒配置实测）：
/// - 管道 <c>url|key|version|ua</c>（1~4 段，宝盒 4 条目全是此形态）；
/// - JSON（公开协议参考：host/key/get_type/path/version/deviceId/playheaders）。
/// url 非根地址（如 xxx.txt）自动开启发现模式（入口 GET 探测真实 host，只发 UA）。
/// </summary>
public sealed class AppGetSource : IResolvingContentSource, IDisposable
{
    private static readonly HttpClient SharedHttp = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.All
    });

    private readonly HttpClient _http;
    private readonly Uri _entry;
    private readonly byte[] _key, _iv;
    private readonly bool _jsonRequests;
    private readonly string _apiPath, _userAgent, _version, _device, _initAction, _searchAction, _detailAction;
    private readonly string _sourceKey, _sourceName;
    private readonly SemaphoreSlim _requests = new(4, 4), _initialization = new(1, 1);
    private readonly ConcurrentDictionary<string, CachedDetail> _details = new(StringComparer.Ordinal);
    private Uri? _endpoint;
    private Home? _home;
    private bool _disposed;

    public string Key => _sourceKey;
    public string Name => _sourceName;

    private sealed record Home(IReadOnlyList<Category> Categories, IReadOnlyList<MediaItem> Items, bool SearchVerification, DateTimeOffset Expires);
    private sealed record PlayEntry(string Url, string ParseApi, string Token, string UserAgent, string PlayerParseType, string ParseType, string ParseUrl);
    private sealed record CachedDetail(MediaDetail Detail, IReadOnlyDictionary<string, PlayEntry> Entries, DateTimeOffset Expires);

    public AppGetSource(SourceInfo info) : this(info, SharedHttp) { }

    public AppGetSource(SourceInfo info, HttpClient http)
    {
        _http = http;
        _sourceKey = info.Key;
        _sourceName = info.Name;
        var cfg = ReadExt(info.Ext);
        _entry = HttpUri(cfg.Entry);
        _jsonRequests = cfg.JsonV122;
        _initAction = _jsonRequests ? "initV122" : "initV119";
        _searchAction = _jsonRequests ? "searchList4" : "searchList";
        _detailAction = _jsonRequests ? "vodDetail2" : "vodDetail";
        _key = Encoding.UTF8.GetBytes(cfg.Key);
        if (_key.Length != 16) throw new InvalidDataException("AppGet key 必须是 16 个 UTF-8 字节。");
        _iv = Encoding.UTF8.GetBytes(cfg.Iv);
        if (_iv.Length != 16) throw new InvalidDataException("AppGet iv 必须是 16 个 UTF-8 字节。");
        _apiPath = cfg.ApiPath;
        _userAgent = Header(cfg.UserAgent);
        _version = Header(cfg.Version);
        _device = Header(cfg.DeviceId);
        if (!cfg.Discovery) _endpoint = Origin(_entry);
    }

    // ---------- TVBox ext 解析（管道 / JSON 两形态） ----------

    internal sealed record ExtConfig(string Entry, string Key, string Iv, bool JsonV122, string ApiPath,
        string Version, string UserAgent, string DeviceId, bool Discovery);

    /// <summary>解析 ext：管道 <c>url|key|version|ua</c> 或 JSON（host/key/get_type/...）。</summary>
    internal static ExtConfig ReadExt(string? ext)
    {
        var text = (ext ?? "").Trim();
        if (text.Length == 0)
            throw new NotSupportedException("csp_AppGet 缺少 ext（需 url|key[|version|ua] 或 JSON 配置）。");

        if (text.StartsWith('{'))
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            string entry = JsonText(root, "host");
            if (entry.Length == 0) entry = JsonText(root, "entry");
            if (entry.Length == 0) entry = JsonText(root, "url");
            string key = JsonText(root, "key");
            if (entry.Length == 0 || key.Length == 0)
                throw new InvalidDataException("AppGet JSON ext 缺少 host 或 key。");
            // 显式 protocol 优先；否则 get_type 真值 → qiji-v122（公开协议参考金牌APP.js 分支）
            string protocol = JsonText(root, "protocol");
            bool jsonV122 = protocol.Length > 0
                ? protocol is "qiji-v122"
                    ? true
                    : protocol is "v119"
                        ? false
                        : throw new NotSupportedException($"AppGet protocol={protocol} 不受支持（仅 v119 / qiji-v122）。")
                : Flag(root, "get_type");
            string path = JsonText(root, "path");
            string apiPath = path.Length > 0 ? ValidateApiPath(path)
                : jsonV122 ? "/api.php/qijiappapi" : "/api.php/getappapi";
            string version = JsonText(root, "version");
            if (version.Length == 0) version = jsonV122 ? "305" : "210";
            bool discovery = Flag(root, "discovery");
            if (!discovery && !IsRootEntry(HttpUri(entry))) discovery = true;
            return new ExtConfig(entry, key, Text(root, "iv").Length > 0 ? JsonText(root, "iv") : key,
                jsonV122, apiPath, version,
                JsonText(root, "userAgent") is { Length: > 0 } ua ? ua : "okhttp/3.14.9",
                JsonText(root, "deviceId").Length > 0 ? JsonText(root, "deviceId") : Guid.NewGuid().ToString("N"),
                discovery);
        }

        // 管道形态：url|key[|version|ua]
        var parts = text.Split('|', StringSplitOptions.None);
        if (parts.Length is < 1 or > 4)
            throw new InvalidDataException("AppGet ext 管道格式应为 url|key[|version|ua]（1~4 段）。");
        string url = parts[0].Trim(), pipeKey = parts.Length > 1 ? parts[1].Trim() : "";
        string pipeVersion = parts.Length > 2 ? parts[2].Trim() : "";
        string pipeUa = parts.Length > 3 ? parts[3].Trim() : "";
        if (url.Length == 0 || pipeKey.Length == 0)
            throw new InvalidDataException("AppGet ext 管道缺少 url 或 key。");
        // 版本段选择分支：V122 → qiji-v122 JSON；其余（含缺失）→ v119 form（真机验证分支）
        bool json122 = pipeVersion.Contains("122", StringComparison.Ordinal);
        var entryUri = HttpUri(url);
        return new ExtConfig(url, pipeKey, pipeKey, json122,
            json122 ? "/api.php/qijiappapi" : "/api.php/getappapi",
            json122 ? "305" : "210",
            pipeUa.Length > 0 ? pipeUa : "okhttp/3.14.9",
            Guid.NewGuid().ToString("N"),
            Discovery: !IsRootEntry(entryUri));
    }

    // ---------- IContentSource ----------

    public async Task<IReadOnlyList<Category>> GetCategoriesAsync(CancellationToken ct = default) =>
        (await HomeAsync(ct).ConfigureAwait(false)).Categories;

    public async Task<MediaPage> GetHomeAsync(CancellationToken ct = default)
    {
        var home = await HomeAsync(ct).ConfigureAwait(false);
        return new MediaPage(home.Items, 1, 1);
    }

    public async Task<MediaPage> GetItemsAsync(string categoryId, int page, IReadOnlyDictionary<string, string>? filters, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();
        ValidatePage(page);
        if (string.IsNullOrWhiteSpace(categoryId))
        {
            if (filters is { Count: > 0 }) throw new InvalidDataException("AppGet 推荐页不支持分类筛选。");
            return page == 1 ? await GetHomeAsync(ct).ConfigureAwait(false) : new MediaPage([], page, page);
        }
        ValidateId(categoryId);
        var body = new Dictionary<string, string>
        {
            ["type_id"] = categoryId, ["page"] = Number(page), ["sort"] = "最新",
            ["area"] = "全部", ["year"] = "全部", ["lang"] = "全部", ["class"] = "全部"
        };
        if (filters is not null)
        {
            foreach (var filter in filters)
            {
                if (filter.Key is not ("sort" or "area" or "year" or "lang" or "class"))
                    throw new NotSupportedException("AppGet 不支持该筛选字段。");
                if (filter.Value.Length > 128) throw new InvalidDataException("AppGet 筛选值过长。");
                body[filter.Key] = filter.Value;
            }
        }
        using var data = await FetchAsync("typeFilterVodList", body, ct).ConfigureAwait(false);
        return Page(data.RootElement, "recommend_list", page);
    }

    public async Task<MediaDetail> GetDetailAsync(string mediaId, CancellationToken ct = default) =>
        (await DetailAsync(mediaId, ct).ConfigureAwait(false)).Detail;

    public async Task<MediaPage> SearchAsync(string query, int page, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();
        ValidatePage(page);
        if (string.IsNullOrWhiteSpace(query)) return new MediaPage([], page, page);
        if (query.Length > 256) throw new InvalidDataException("AppGet 搜索关键词过长。");
        if (_jsonRequests && (await HomeAsync(ct).ConfigureAwait(false)).SearchVerification)
            throw new NotSupportedException("AppGet 此站点搜索需要验证码，交互验证尚未接入。");
        using var data = await FetchAsync(_searchAction,
            new() { ["keywords"] = query, ["type_id"] = "0", ["page"] = Number(page) }, ct).ConfigureAwait(false);
        return Page(data.RootElement, "search_list", page);
    }

    // ---------- IResolvingContentSource ----------

    public async Task<PlaybackRequest> ResolvePlaybackAsync(string mediaId, string episodeId, CancellationToken ct = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        // 详情响应可能含过期媒体 URL 与解析 token，每次打开都刷新
        var detail = await DetailAsync(mediaId, deadline.Token, refresh: true).ConfigureAwait(false);
        if (!detail.Entries.TryGetValue(episodeId, out var entry))
            throw new InvalidDataException("AppGet 所选分集不存在。");
        string url = entry.Url;
        if (!IsMedia(url) && entry.ParseType != "0")
        {
            if (entry.ParseType == "2") throw new NotSupportedException("AppGet 此线路要求网页解析，尚未接入。");
            if (entry.PlayerParseType == "2")
            {
                // player_parse_type=2：外部 JSON 解析器（不带 API 签名头）
                string location = string.IsNullOrWhiteSpace(entry.ParseUrl)
                    ? entry.ParseApi + Uri.UnescapeDataString(url)
                    : entry.ParseUrl;
                url = await ExternalParseAsync(location, entry.UserAgent, deadline.Token).ConfigureAwait(false);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(entry.ParseApi))
                    throw new NotSupportedException("AppGet 此线路缺少站内解析标识。");
                // 单次表单编码即可；预先 URL 编码密文会改变线上密文字节
                using var parsed = await FetchAsync("vodParse", new()
                {
                    ["parse_api"] = entry.ParseApi,
                    ["url"] = Encrypt(Uri.UnescapeDataString(url)),
                    ["token"] = entry.Token,
                    ["player_parse_type"] = entry.PlayerParseType
                }, deadline.Token).ConfigureAwait(false);
                using var result = JsonDocument.Parse(Text(parsed.RootElement, "json"));
                url = Text(result.RootElement, "url");
            }
        }
        var episode = detail.Detail.Lines.SelectMany(x => x.Episodes).First(x => x.Id == episodeId);
        // 线路/分集 id 形如 "0:1"（老契约沿用）：线路号在冒号前
        string lineId = episodeId.Contains(':') ? episodeId[..episodeId.IndexOf(':')] : "0";
        return new PlaybackRequest
        {
            Uri = HttpUri(url).AbsoluteUri,
            Title = $"{detail.Detail.Item.Title} · {episode.Title}",
            Resolution = ResolutionKind.Direct,
            SourceKey = _sourceKey,
            SourceName = _sourceName,
            MediaId = mediaId,
            LineId = $"line{lineId}",
            EpisodeId = episodeId,
            Headers = new()
            {
                ["User-Agent"] = string.IsNullOrEmpty(entry.UserAgent) ? _userAgent : Header(entry.UserAgent)
            }
        };
    }

    // ---------- 内部实现 ----------

    private async Task<string> ExternalParseAsync(string location, string userAgent, CancellationToken ct)
    {
        if (location.Length > 16384) throw new InvalidDataException("AppGet 外部解析地址过长。");
        var uri = HttpUri(location);
        await _requests.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            // 外部解析器：不带 API 签名、设备标识、Cookie 或表单 token
            request.Headers.TryAddWithoutValidation("User-Agent",
                string.IsNullOrWhiteSpace(userAgent) ? _userAgent : Header(userAgent));
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            CheckResponse(response, uri);
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var json = JsonDocument.Parse(await BoundedContent.ReadAsync(stream, 1024 * 1024, ct).ConfigureAwait(false));
            string url = Text(json.RootElement, "url");
            if (url.Length == 0 && json.RootElement.TryGetProperty("data", out var data)) url = Text(data, "url");
            return HttpUri(url).AbsoluteUri;
        }
        finally { _requests.Release(); }
    }

    private async Task<Home> HomeAsync(CancellationToken ct)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();
        var home = Volatile.Read(ref _home);
        if (home is not null && home.Expires > DateTimeOffset.UtcNow) return home;
        await _initialization.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            home = Volatile.Read(ref _home);
            if (home is not null && home.Expires > DateTimeOffset.UtcNow) return home;
            using var data = await FetchAsync(_initAction, new(), ct).ConfigureAwait(false);
            var categories = new List<Category>();
            var items = new List<MediaItem>();
            if (data.RootElement.TryGetProperty("banner_list", out _))
                items.AddRange(Items(data.RootElement, "banner_list"));
            foreach (var type in Array(data.RootElement, "type_list", 256).EnumerateArray())
            {
                string name = Text(type, "type_name"), id = Text(type, "type_id");
                if (name is "全部" or "直播") continue;
                ValidateId(id);
                categories.Add(new(id, name));
                if (items.Count < 1000 && type.TryGetProperty("recommend_list", out _))
                    items.AddRange(Items(type, "recommend_list").Take(1000 - items.Count));
            }
            bool searchVerification = data.RootElement.TryGetProperty("config", out var config) &&
                                      Flag(config, "system_search_verify_status");
            home = new(categories.DistinctBy(x => x.Id).ToArray(),
                items.DistinctBy(x => x.Id).Take(1000).ToArray(),
                searchVerification, DateTimeOffset.UtcNow.AddMinutes(10));
            Volatile.Write(ref _home, home);
            return home;
        }
        finally { _initialization.Release(); }
    }

    private async Task<CachedDetail> DetailAsync(string mediaId, CancellationToken ct, bool refresh = false)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();
        ValidateId(mediaId);
        if (!refresh && _details.TryGetValue(mediaId, out var cached) && cached.Expires > DateTimeOffset.UtcNow)
            return cached;
        using var document = await FetchAsync(_detailAction, new() { ["vod_id"] = mediaId }, ct).ConfigureAwait(false);
        var root = document.RootElement;
        if (!root.TryGetProperty("vod", out var vod) || Text(vod, "vod_id") != mediaId)
            throw new InvalidDataException("AppGet 视频详情标识不一致。");
        var playlists = Array(root, "vod_play_list", 64);
        var lines = new List<PlaybackLine>();
        var entries = new Dictionary<string, PlayEntry>(StringComparer.Ordinal);
        foreach (var playlist in playlists.EnumerateArray())
        {
            if (!playlist.TryGetProperty("player_info", out var player) || player.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("AppGet 缺少线路信息。");
            var episodes = new List<Episode>();
            foreach (var episode in Array(playlist, "urls", 10000).EnumerateArray())
            {
                if (entries.Count >= 10000) throw new InvalidDataException("AppGet 分集数量过多。");
                string url = Text(episode, "url");
                if (string.IsNullOrWhiteSpace(url) || url.Length > 16384)
                    throw new InvalidDataException("AppGet 分集地址为空或过长。");
                string id = $"{lines.Count}:{episodes.Count}";
                episodes.Add(new(id, Text(episode, "name")));
                entries.Add(id, new(url, Text(player, "parse"), Text(episode, "token"), Text(player, "user_agent"),
                    Text(player, "player_parse_type"), Text(player, "parse_type"), Text(episode, "parse_api_url")));
            }
            if (episodes.Count > 0) lines.Add(new(Number(lines.Count), Text(player, "show"), episodes));
        }
        var detail = new MediaDetail
        {
            Item = Card(vod),
            Description = WebUtility.HtmlDecode(Text(vod, "vod_content")),
            Lines = lines
        };
        var result = new CachedDetail(detail, entries, DateTimeOffset.UtcNow.AddMinutes(10));
        if (_details.Count >= 128) _details.Clear();
        _details[mediaId] = result;
        return result;
    }

    private async Task<JsonDocument> FetchAsync(string action, Dictionary<string, string> body, CancellationToken ct)
    {
        ThrowIfDisposed();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        await _requests.WaitAsync(deadline.Token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var endpoint = Volatile.Read(ref _endpoint);
            if (endpoint is null)
            {
                // 发现入口：只发 User-Agent，不带签名/设备标识/密钥；最多读 512 字节
                using var discovery = new HttpRequestMessage(HttpMethod.Get, _entry);
                discovery.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
                using var response = await _http.SendAsync(discovery, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
                    .ConfigureAwait(false);
                CheckResponse(response, _entry);
                await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
                endpoint = Origin(HttpUri(Encoding.UTF8
                    .GetString(await BoundedContent.ReadAsync(stream, 512, deadline.Token).ConfigureAwait(false)).Trim()));
                Interlocked.CompareExchange(ref _endpoint, endpoint, null);
            }
            var uri = new Uri(endpoint, _apiPath + ".index/" + action);
            using var request = new HttpRequestMessage(HttpMethod.Post, uri);
            request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
            if (_jsonRequests)
            {
                body["version"] = _version;
                request.Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(body, Json.TypeInfo<Dictionary<string, string>>()));
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
            using var result = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
                .ConfigureAwait(false);
            CheckResponse(result, uri);
            await using var content = await result.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            using var envelope = JsonDocument.Parse(
                await BoundedContent.ReadAsync(content, 8 * 1024 * 1024, deadline.Token).ConfigureAwait(false));
            var root = envelope.RootElement;
            if (Text(root, "code") != "1" || !root.TryGetProperty("data", out var encrypted) ||
                encrypted.ValueKind != JsonValueKind.String)
                throw new InvalidDataException(
                    $"AppGet {action} API 拒绝请求或未返回加密 data（code={Text(root, "code")}）；可能需要验证或登录。");
            byte[] decoded;
            try
            {
                using var aes = Aes.Create();
                aes.Key = _key;
                decoded = aes.DecryptCbc(Convert.FromBase64String(encrypted.GetString()!), _iv, PaddingMode.PKCS7);
            }
            catch (Exception error) when (error is FormatException or CryptographicException)
            {
                throw new InvalidDataException("AppGet 加密响应或密钥无效。", error);
            }
            var document = JsonDocument.Parse(decoded);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                document.Dispose();
                throw new InvalidDataException("AppGet 解密结果不是对象。");
            }
            return document;
        }
        finally { _requests.Release(); }
    }

    private string Encrypt(string text)
    {
        using var aes = Aes.Create();
        aes.Key = _key;
        return Convert.ToBase64String(aes.EncryptCbc(Encoding.UTF8.GetBytes(text), _iv, PaddingMode.PKCS7));
    }

    // ---------- 小工具 ----------

    private static void CheckResponse(HttpResponseMessage response, Uri expected)
    {
        if ((int)response.StatusCode is >= 300 and < 400)
            throw new InvalidDataException("AppGet 不允许 HTTP 重定向。");
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri is { } final && final != expected)
            throw new InvalidDataException("AppGet 请求地址发生重定向。");
    }

    private static MediaPage Page(JsonElement data, string key, int page)
    {
        var items = Items(data, key);
        // 部分部署不返回总页数：此时空页结束分页（PageCount=page 表示无下一页）
        string count = Text(data, "pagecount");
        bool more = items.Count > 0 && page < 1000 &&
                    (count.Length == 0 || int.TryParse(count, out int pages) && page < pages);
        return new MediaPage(items, page, more ? page + 1 : page);
    }

    private static IReadOnlyList<MediaItem> Items(JsonElement data, string key) =>
        Array(data, key, 1000).EnumerateArray().Select(Card).DistinctBy(x => x.Id).ToArray();

    private static MediaItem Card(JsonElement item)
    {
        string id = Text(item, "vod_id");
        ValidateId(id);
        string pic = Text(item, "vod_pic");
        if (pic.StartsWith("//", StringComparison.Ordinal)) pic = "https:" + pic;
        string? poster = Uri.TryCreate(pic, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http"
            ? uri.AbsoluteUri : null;
        return new MediaItem
        {
            Id = id,
            Title = WebUtility.HtmlDecode(Text(item, "vod_name")),
            Poster = poster,
            Remarks = Text(item, "vod_remarks") is { Length: > 0 } r ? r : null,
            Year = Text(item, "vod_year") is { Length: > 0 } y ? y : null,
            Area = Text(item, "vod_area") is { Length: > 0 } a ? a : null,
            TypeName = Text(item, "vod_class") is { Length: > 0 } c ? c : null
        };
    }

    private static JsonElement Array(JsonElement root, string name, int maximum) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var list) &&
        list.ValueKind == JsonValueKind.Array && list.GetArrayLength() <= maximum
            ? list
            : throw new InvalidDataException("AppGet 列表缺失或过长：" + name);

    private static string Text(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) &&
        value.ValueKind is JsonValueKind.String or JsonValueKind.Number ? value.ToString() : "";

    private static string JsonText(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

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
            // get_type 等字段允许数字字符串（"2" = 真值，公开协议参考）
            JsonValueKind.String when long.TryParse(value.GetString(), out long n) => n != 0,
            _ => throw new InvalidDataException("AppGet 布尔配置格式不受支持。")
        };
    }

    private static string ValidateApiPath(string path) =>
        path.StartsWith('/') && !path.Contains("..", StringComparison.Ordinal) && path.Length <= 256 &&
        path.All(x => char.IsAsciiLetterOrDigit(x) || x is '/' or '.' or '_' or '-')
            ? path
            : throw new InvalidDataException("AppGet apiPath 无效。");

    private static string Header(string value) =>
        value.Length is > 0 and <= 2048 && value.All(x => x >= ' ' && x <= '~')
            ? value
            : throw new InvalidDataException("AppGet 请求头无效。");

    private static void ValidateId(string id)
    {
        if (id.Length is < 1 or > 128 || !id.All(char.IsAsciiDigit))
            throw new InvalidDataException("AppGet 标识必须是数字。");
    }

    private static void ValidatePage(int page)
    {
        if (page is < 1 or > 1000) throw new InvalidDataException("AppGet 分页页码无效。");
    }

    private static Uri HttpUri(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" &&
        uri.UserInfo.Length == 0
            ? uri
            : throw new InvalidDataException("AppGet 地址必须是无凭据的 HTTP/HTTPS URL。");

    private static bool IsRootEntry(Uri uri) => uri.AbsolutePath is "" or "/" && uri.Query.Length == 0;

    private static Uri Origin(Uri uri) =>
        IsRootEntry(uri) && uri.Fragment.Length == 0
            ? uri
            : throw new InvalidDataException("AppGet 服务 entry 必须是根地址；发现文件需走发现模式。");

    private static bool IsMedia(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        Path.GetExtension(uri.AbsolutePath).ToLowerInvariant()
            is ".mp4" or ".m3u8" or ".flv" or ".mkv" or ".webm" or ".ts" or ".mov" or ".mpd";

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        _disposed = true;
        _details.Clear();
        Volatile.Write(ref _home, null);
    }
}
