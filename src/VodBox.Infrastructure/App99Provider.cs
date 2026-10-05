using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VodBox.Core;

namespace VodBox.Infrastructure;

/// <summary>Anonymous App99 BN v2 protocol. No account login, JAR or script execution.</summary>
public sealed class App99Provider : IContentProvider
{
    private static readonly HttpClient DefaultHttp = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.All
    });
    private readonly HttpClient _http;
    private readonly string _base, _appKey, _uuid, _userAgent, _apiVersion, _name, _versionName, _buildSignature;
    private readonly byte[] _key;
    private readonly SemaphoreSlim _requests = new(4, 4), _initialization = new(1, 1);
    private readonly ConcurrentDictionary<string, DetailEntry> _details = new(StringComparer.Ordinal);
    private Home? _home;
    private bool _disposed;
    public string SourceId { get; }
    private sealed record Player(string Name, bool Parse, IReadOnlyList<string> ParserIds);
    private sealed record Parser(string Id, string Api);
    private sealed record Home(IReadOnlyList<Category> Categories, IReadOnlyDictionary<string, Player> Players, IReadOnlyList<Parser> Parsers);
    private sealed record EpisodeEntry(string Url, string Code);
    private sealed record DetailEntry(MediaDetail Detail, IReadOnlyDictionary<string, EpisodeEntry> Episodes, DateTimeOffset Expires);

    public App99Provider(SourceDefinition source) : this(source, DefaultHttp) { }
    public App99Provider(SourceDefinition source, HttpClient http)
    {
        if (Option(source, "protocol") != "bn-v2") throw new NotSupportedException("App99 需要显式 protocol=bn-v2；Guard 尚未核实。");
        SourceId = source.Id; _http = http;
        var uri = HttpUri(source.Entry ?? "");
        if (uri.Query.Length != 0 || uri.Fragment.Length != 0) throw new InvalidDataException("App99 entry 不允许查询或片段。");
        _base = uri.AbsoluteUri.TrimEnd('/');
        _appKey = Header(Option(source, "appkey"));
        _uuid = Option(source, "uuid", Guid.NewGuid().ToString("D"));
        if (_uuid.Length is not (32 or 36) || !Guid.TryParse(_uuid, out _)) throw new InvalidDataException("App99 uuid 必须是 32/36 字符 GUID。");
        // The protocol uses the 32 ASCII UUID characters as AES-256 key, not 16 decoded hex bytes.
        _key = Encoding.ASCII.GetBytes(_uuid.Replace("-", "", StringComparison.Ordinal));
        _userAgent = Header(Option(source, "userAgent", "Mozilla/5.0"));
        _apiVersion = Header(Option(source, "apiVersion", "0b4328287a5d953e"));
        _name = Option(source, "name"); _versionName = Option(source, "versionName"); _buildSignature = Option(source, "buildSignature");
        if (new[] { _name, _versionName, _buildSignature }.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 2048))
            throw new InvalidDataException("App99 name/versionName/buildSignature 缺失或过长。");
    }

    public async Task<IReadOnlyList<Category>> GetCategoriesAsync(CancellationToken token) => (await HomeAsync(token).ConfigureAwait(false)).Categories;
    public Task<MediaPage> GetItemsAsync(string? categoryId, string? cursor, CancellationToken token) => GetItemsFilteredAsync(categoryId, cursor, new Dictionary<string, string>(), token);
    public async Task<MediaPage> GetItemsFilteredAsync(string? categoryId, string? cursor, IReadOnlyDictionary<string, string> filters, CancellationToken token)
    {
        int page = PageNumber(cursor);
        var home = await HomeAsync(token).ConfigureAwait(false);
        string category = categoryId ?? home.Categories.FirstOrDefault()?.Id ?? "1"; Id(category);
        var body = new Dictionary<string, JsonElement>
        {
            ["kw"] = Value(""), ["page"] = Value(Number(page)), ["limit"] = Value(21), ["pid"] = Value(category), ["orderBy"] = Value("time"), ["isCategory"] = Value(1)
        };
        foreach (var filter in filters)
        {
            if (filter.Key is not ("class" or "area" or "lang" or "year")) throw new NotSupportedException("App99 不支持该筛选字段。");
            if (filter.Value.Length > 128) throw new InvalidDataException("App99 筛选值过长。");
            body[filter.Key] = Value(filter.Value);
        }
        using var data = await PostAsync("/vod/search", body, token).ConfigureAwait(false);
        return Page(data.RootElement, page);
    }
    public Task<MediaPage> SearchAsync(string query, CancellationToken token) => SearchPageAsync(query, null, token);
    public async Task<MediaPage> SearchPageAsync(string query, string? cursor, CancellationToken token)
    {
        ThrowIfDisposed(); token.ThrowIfCancellationRequested();
        int page = PageNumber(cursor);
        if (string.IsNullOrWhiteSpace(query)) return new([]);
        if (query.Length > 256) throw new InvalidDataException("App99 搜索关键词过长。");
        await HomeAsync(token).ConfigureAwait(false);
        using var data = await PostAsync("/vod/search", new()
        {
            ["kw"] = Value(query), ["page"] = Value(page), ["limit"] = Value(21), ["orderBy"] = Value("vod_hits_month"), ["sort"] = Value("desc")
        }, token).ConfigureAwait(false);
        return Page(data.RootElement, page);
    }

    private async Task<Home> HomeAsync(CancellationToken token)
    {
        ThrowIfDisposed(); token.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _home) is { } cached) return cached;
        await _initialization.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _home) is { } current) return current;
            using var data = await PostAsync("/app/systemInit", new()
            {
                ["v"] = Value(_versionName), ["n"] = Value(_name), ["s"] = Value(_buildSignature), ["pl"] = Value("1"), ["apiVersion"] = Value("v2")
            }, token).ConfigureAwait(false);
            var root = data.RootElement;
            if (!root.TryGetProperty("categorys", out var categories)) throw new InvalidDataException("App99 初始化没有分类。");
            if (categories.ValueKind == JsonValueKind.Object && categories.TryGetProperty("data", out var nested)) categories = nested;
            Array(categories, 256);
            var classes = categories.EnumerateArray().Select(x => { string id = Text(x, "id"); Id(id); return new Category(id, Text(x, "name")); }).DistinctBy(x => x.Id).ToArray();
            var players = new Dictionary<string, Player>(StringComparer.Ordinal);
            if (root.TryGetProperty("player", out var playerData) && playerData.ValueKind == JsonValueKind.Object)
            {
                foreach (var player in playerData.EnumerateObject())
                {
                    if (players.Count >= 256) throw new InvalidDataException("App99 播放器定义过多。");
                    string name = Text(player.Value, "name");
                    players.Add(player.Name, new(name.Length == 0 ? player.Name : name, Text(player.Value, "type") is not ("" or "0"),
                        Text(player.Value, "parseUrl").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)));
                }
            }
            var parsers = new List<Parser>();
            if (root.TryGetProperty("parser_api", out var parserData))
            {
                Array(parserData, 64);
                foreach (var parser in parserData.EnumerateArray()) parsers.Add(new(Text(parser, "id"), Text(parser, "api_url")));
            }
            var home = new Home(classes, players, parsers); Volatile.Write(ref _home, home); return home;
        }
        finally { _initialization.Release(); }
    }

    public async Task<MediaDetail> GetDetailAsync(string mediaId, CancellationToken token) => (await DetailAsync(mediaId, token, false).ConfigureAwait(false)).Detail;
    private async Task<DetailEntry> DetailAsync(string mediaId, CancellationToken token, bool refresh)
    {
        ThrowIfDisposed(); token.ThrowIfCancellationRequested(); Id(mediaId);
        if (!refresh && _details.TryGetValue(mediaId, out var cached) && cached.Expires > DateTimeOffset.UtcNow) return cached;
        var home = await HomeAsync(token).ConfigureAwait(false);
        using var data = await PostAsync("/vod/detail", new() { ["id"] = Value(mediaId), ["eps"] = Value("1"), ["v"] = Value("2.0.0"), ["pl"] = Value(1) }, token).ConfigureAwait(false);
        if (!data.RootElement.TryGetProperty("data", out var vod) || vod.ValueKind != JsonValueKind.Object || Text(vod, "id") != mediaId)
            throw new InvalidDataException("App99 视频详情标识不一致。");
        string[] codes = Text(vod, "play_from").Split("$$$", StringSplitOptions.None), blocks = Text(vod, "play_url").Split("$$$", StringSplitOptions.None);
        if (blocks.Length > 64) throw new InvalidDataException("App99 播放线路过多。");
        var lines = new List<PlaybackLine>(); var entries = new Dictionary<string, EpisodeEntry>(StringComparer.Ordinal);
        for (int line = 0; line < blocks.Length; line++)
        {
            string code = line < codes.Length ? codes[line] : "";
            var episodes = new List<Episode>();
            foreach (string segment in blocks[line].Split('#', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                int separator = segment.IndexOf('$');
                if (separator <= 0 || separator == segment.Length - 1) throw new InvalidDataException("App99 分集格式无效。");
                if (entries.Count >= 10000) throw new InvalidDataException("App99 分集过多。");
                string id = $"{line}:{episodes.Count}", url = segment[(separator + 1)..];
                if (url.Length > 16384) throw new InvalidDataException("App99 分集地址过长。");
                episodes.Add(new(id, segment[..separator])); entries.Add(id, new(url, code));
            }
            if (episodes.Count > 0) lines.Add(new(Number(line), home.Players.TryGetValue(code, out var player) ? player.Name : code, episodes));
        }
        var result = new DetailEntry(new(Card(vod), WebUtility.HtmlDecode(Text(vod, "content")), lines), entries, DateTimeOffset.UtcNow.AddMinutes(10));
        if (_details.Count >= 128) _details.Clear(); _details[mediaId] = result; return result;
    }
    public async Task<PlaybackRequest> ResolvePlaybackAsync(string mediaId, string episodeId, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(20));
        var detail = await DetailAsync(mediaId, deadline.Token, true).ConfigureAwait(false);
        if (!detail.Episodes.TryGetValue(episodeId, out var entry)) throw new InvalidDataException("App99 所选分集不存在。");
        var home = await HomeAsync(deadline.Token).ConfigureAwait(false); string url = entry.Url;
        bool knownPlayer = home.Players.TryGetValue(entry.Code, out var player);
        if (!knownPlayer && !IsMedia(url)) throw new NotSupportedException("App99 未识别该线路的播放协议。");
        if (knownPlayer && player!.Parse)
        {
            var parsers = home.Parsers.Where(x => player.ParserIds.Count == 0 || player.ParserIds.Contains(x.Id)).Take(3).ToArray();
            if (parsers.Length == 0) throw new NotSupportedException("App99 此线路没有可用 JSON 解析器。");
            Exception? failure = null; string? parsed = null;
            foreach (var parser in parsers)
            {
                string target = parser.Api.Contains("{url}", StringComparison.Ordinal) ? parser.Api.Replace("{url}", Uri.EscapeDataString(url), StringComparison.Ordinal) : parser.Api + Uri.EscapeDataString(url);
                try { parsed = await ParseAsync(target, deadline.Token).ConfigureAwait(false); break; }
                catch (Exception error) when (error is HttpRequestException or InvalidDataException or JsonException) { failure = error; }
            }
            url = parsed ?? throw new InvalidDataException("App99 线路解析失败。", failure);
        }
        var episode = detail.Detail.PlaybackLines.SelectMany(x => x.Episodes).First(x => x.Id == episodeId);
        return new() { Uri = HttpUri(url).AbsoluteUri, Title = $"{detail.Detail.Item.Title} · {episode.Title}", SourceId = SourceId, MediaId = mediaId, EpisodeId = episodeId,
            Headers = new() { ["User-Agent"] = _userAgent } };
    }

    private async Task<string> ParseAsync(string location, CancellationToken token)
    {
        if (location.Length > 16384) throw new InvalidDataException("App99 解析地址过长。");
        var uri = HttpUri(location); await _requests.WaitAsync(token).ConfigureAwait(false);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri); request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false); CheckResponse(response, uri);
            await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var data = JsonDocument.Parse(await BoundedContent.ReadAsync(stream, 1024 * 1024, token).ConfigureAwait(false));
            var root = data.RootElement;
            string url = Url(root);
            if (url.Length == 0 && root.TryGetProperty("data", out var nested)) url = Url(nested);
            if (url.Length == 0 && root.TryGetProperty("result", out var result)) url = Url(result);
            return HttpUri(url).AbsoluteUri;
        }
        finally { _requests.Release(); }
    }

    private async Task<JsonDocument> PostAsync(string path, Dictionary<string, JsonElement> payload, CancellationToken token)
    {
        ThrowIfDisposed();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(20));
        await _requests.WaitAsync(deadline.Token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); string timestamp = Number(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
            payload["timestamp"] = Value(timestamp); payload["nonce"] = Value(nonce); payload["token"] = Value("");
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(payload, VodBoxJson.Default.DictionaryStringJsonElement), iv = RandomNumberGenerator.GetBytes(16);
            using var aes = Aes.Create(); aes.Key = _key; byte[] encrypted = aes.EncryptCbc(json, iv);
            var wire = new byte[iv.Length + encrypted.Length]; iv.CopyTo(wire, 0); encrypted.CopyTo(wire, 16);
            string body = Convert.ToBase64String(wire), signature = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{body}:{timestamp}:{nonce}::{_appKey}")));
            var uri = new Uri(_base + path);
            using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            foreach (var header in new Dictionary<string, string>
            {
                ["User-Agent"] = _userAgent, ["Accept"] = "application/json", ["client_type"] = "android", ["uuid"] = _uuid,
                ["timestamp"] = timestamp, ["sign"] = signature, ["nonce"] = nonce, ["appkey"] = _appKey, ["version"] = _apiVersion, ["api_version"] = "v1"
            }) request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false); CheckResponse(response, uri);
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            byte[] encoded = await BoundedContent.ReadAsync(stream, 8 * 1024 * 1024, deadline.Token).ConfigureAwait(false);
            byte[] decoded;
            try
            {
                byte[] bytes = Convert.FromBase64String(Encoding.UTF8.GetString(encoded));
                if (bytes.Length < 32 || (bytes.Length - 16) % 16 != 0) throw new InvalidDataException("App99 密文长度无效。");
                decoded = aes.DecryptCbc(bytes.AsSpan(16), bytes.AsSpan(0, 16), PaddingMode.None);
            }
            catch (Exception error) when (error is FormatException or CryptographicException) { throw new InvalidDataException("App99 加密响应无效。", error); }
            JsonDocument document;
            if (decoded.Length >= 2 && (decoded[0] & 15) == 8 && ((decoded[0] << 8) + decoded[1]) % 31 == 0)
            {
                using var compressed = new MemoryStream(decoded, false); await using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
                document = JsonDocument.Parse(await BoundedContent.ReadAsync(zlib, 8 * 1024 * 1024, deadline.Token).ConfigureAwait(false));
            }
            else
            {
                int padding = decoded[^1];
                if (padding is < 1 or > 16 || decoded.AsSpan(decoded.Length - padding).IndexOfAnyExcept((byte)padding) >= 0)
                    throw new InvalidDataException("App99 响应填充无效。");
                document = JsonDocument.Parse(decoded.AsMemory(0, decoded.Length - padding));
            }
            if (document.RootElement.ValueKind != JsonValueKind.Object || Text(document.RootElement, "code") != "0")
            { document.Dispose(); throw new InvalidDataException("App99 API 拒绝请求；匿名访问、版本或签名可能不受支持。"); }
            return document;
        }
        finally { _requests.Release(); }
    }

    private static MediaPage Page(JsonElement root, int page)
    {
        if (!root.TryGetProperty("data", out var list)) throw new InvalidDataException("App99 列表响应缺少 data。"); Array(list, 1000);
        var items = list.EnumerateArray().Select(Card).DistinctBy(x => x.Id).ToArray();
        string count = Text(root, "page_count"); if (count.Length == 0) count = Text(root, "pagecount"); if (count.Length == 0) count = Text(root, "pageCount");
        bool more = items.Length > 0 && page < 1000 && (count.Length == 0 || int.TryParse(count, out int pages) && page < pages);
        return new(items, more ? Number(page + 1) : null);
    }
    private static MediaItem Card(JsonElement item)
    {
        string id = Text(item, "id"); Id(id); string pic = Text(item, "pic"); if (pic.StartsWith("//", StringComparison.Ordinal)) pic = "https:" + pic;
        string? poster = Uri.TryCreate(pic, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri.AbsoluteUri : null;
        return new(id, WebUtility.HtmlDecode(Text(item, "name")), poster, Text(item, "remarks"));
    }
    private static string Url(JsonElement root) { foreach (string key in new[] { "url", "playUrl", "play_url" }) { string url = Text(root, key); if (url.Length > 0) return url; } return ""; }
    private static void CheckResponse(HttpResponseMessage response, Uri expected) { if ((int)response.StatusCode is >= 300 and < 400 || response.RequestMessage?.RequestUri is { } final && final != expected) throw new InvalidDataException("App99 不允许重定向。"); response.EnsureSuccessStatusCode(); }
    private static void Array(JsonElement element, int maximum) { if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() > maximum) throw new InvalidDataException("App99 列表缺失或过长。"); }
    private static string Text(JsonElement root, string key) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(key, out var value) && value.ValueKind is JsonValueKind.String or JsonValueKind.Number ? value.ToString() : "";
    private static string Option(SourceDefinition source, string key, string fallback = "") => source.Options.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : fallback;
    private static string Header(string value) => value.Length is > 0 and <= 2048 && value.All(x => x >= ' ' && x <= '~') ? value : throw new InvalidDataException("App99 请求头无效。");
    private static void Id(string value) { if (value.Length is < 1 or > 128 || !value.All(x => char.IsAsciiLetterOrDigit(x) || x is '-' or '_')) throw new InvalidDataException("App99 标识无效。"); }
    private static Uri HttpUri(string value) => value.Length <= 16384 && Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0 ? uri : throw new InvalidDataException("App99 地址必须是无凭据的 HTTP/HTTPS URL。");
    private static bool IsMedia(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && Path.GetExtension(uri.AbsolutePath).ToLowerInvariant() is ".mp4" or ".m3u8" or ".mpd" or ".flv" or ".mkv" or ".webm" or ".ts";
    private static int PageNumber(string? cursor) => cursor is null ? 1 : int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out int page) && page is >= 1 and <= 1000 ? page : throw new InvalidDataException("App99 分页游标无效。");
    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static JsonElement Value(string value) => JsonSerializer.SerializeToElement(value, VodBoxJson.Default.String);
    private static JsonElement Value(int value) => JsonSerializer.SerializeToElement(value, VodBoxJson.Default.Int32);
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
    public ValueTask DisposeAsync() { _disposed = true; _details.Clear(); Volatile.Write(ref _home, null); return ValueTask.CompletedTask; }
}
