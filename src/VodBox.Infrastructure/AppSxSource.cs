using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using VodBox.Core;

namespace VodBox.Infrastructure;

/// <summary>FTY AppSx anonymous v119, without the later RSA/session runtime.</summary>
public sealed class AppSxSource : IResolvingContentSource
{
    private const string Agent = "okhttp/3.14.9";
    private const string Prefix = "/api.php/getappapi.index/";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly HttpClient SharedHttp = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.All, AllowAutoRedirect = false
    }) { Timeout = TimeSpan.FromSeconds(20) };
    private readonly SourceInfo _site;
    private readonly Configuration _config;
    private readonly HttpClient _http;
    private readonly Func<long> _clock;
    private readonly string _device;
    public string Key => _site.Key;
    public string Name => _site.Name;
    internal sealed record Configuration(Uri Host, string Key, string Iv, string Version);

    public AppSxSource(SourceInfo site) : this(site, SharedHttp, () => DateTimeOffset.UtcNow.ToUnixTimeSeconds(), Guid.NewGuid().ToString("N")) { }
    internal AppSxSource(SourceInfo site, HttpClient http, Func<long> clock, string device)
    {
        _site = site; _http = http; _clock = clock; _device = device;
        _config = ParseConfig(site.Ext);
    }

    internal static Configuration ParseConfig(string? ext)
    {
        using var document = JsonDocument.Parse(FtyExtCodec.JsonConfig(ext));
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("AppSx ext 不是对象。");
        foreach (var property in root.EnumerateObject())
            if (property.Name is not ("host" or "key" or "iv" or "version" or "ver"))
                throw new NotSupportedException("AppSx 当前仅接管已验证的匿名 v119 配置。");
        if (root.TryGetProperty("ver", out var version) && (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int number) || number != 119))
            throw new NotSupportedException("AppSx 当前仅支持 v119。");
        var host = HttpUri(Text(root, "host"));
        if (host.AbsolutePath != "/" || host.Query.Length != 0) throw new InvalidDataException("AppSx host 必须是接口根地址。");
        string key = Text(root, "key"), iv = Text(root, "iv");
        if (iv.Length == 0) iv = key;
        if (Utf8.GetByteCount(key) is not (16 or 24 or 32) || Utf8.GetByteCount(iv) != 16)
            throw new InvalidDataException("AppSx AES key/iv 长度无效。");
        string appVersion = Text(root, "version");
        if (appVersion.Length == 0) appVersion = "113";
        ValidateHeader(appVersion);
        return new(host, key, iv, appVersion);
    }

    internal static bool Supports(string? ext)
    {
        try { ParseConfig(ext); return true; }
        catch (Exception error) when (error is JsonException or InvalidDataException or NotSupportedException or EncoderFallbackException) { return false; }
    }

    internal static string Encrypt(string plaintext, string key, string iv, bool wrapped = false)
        => CatVodAes.Encrypt(plaintext, key, iv, wrapped);

    internal static string Decode(string value, string key, string iv)
    {
        if (value.Length > 8 * 1024 * 1024) throw new InvalidDataException("AppSx 响应过大。");
        if (value.StartsWith('{') || value.StartsWith('[')) return value;
        try
        {
            return CatVodAes.Decrypt(value.Replace("\\", "", StringComparison.Ordinal), key, iv);
        }
        catch (Exception error) when (error is FormatException or CryptographicException or DecoderFallbackException)
        { throw new InvalidDataException("AppSx 响应解密失败，协议版本或密钥不匹配。", error); }
    }

    private async Task<JsonDocument> Request(string operation, Dictionary<string, string>? fields, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_config.Host, Prefix + operation))
        {
            Content = fields is null ? new StringContent("", Encoding.UTF8, "application/json") : new FormUrlEncodedContent(fields)
        };
        string time = _clock().ToString(CultureInfo.InvariantCulture);
        request.Headers.TryAddWithoutValidation("User-Agent", Agent);
        request.Headers.TryAddWithoutValidation("app-user-device-id", _device);
        request.Headers.TryAddWithoutValidation("app-ui-mode", "light");
        request.Headers.TryAddWithoutValidation("app-version-code", _config.Version);
        request.Headers.TryAddWithoutValidation("app-api-verify-time", time);
        request.Headers.TryAddWithoutValidation("app-api-verify-sign", Encrypt(time, _config.Key, _config.Iv));
        using var envelope = await Send(request, ct).ConfigureAwait(false);
        var root = envelope.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("AppSx 接口未返回加密 data；可能要求验证或登录。");
        return JsonDocument.Parse(Decode(data.GetString()!, _config.Key, _config.Iv));
    }

    private async Task<JsonDocument> Send(HttpRequestMessage request, CancellationToken ct)
    {
        using var scope = CancellationTokenSource.CreateLinkedTokenSource(ct); scope.CancelAfter(TimeSpan.FromSeconds(20));
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, scope.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(scope.Token).ConfigureAwait(false);
        var bytes = await BoundedContent.ReadAsync(stream, 8 * 1024 * 1024, scope.Token).ConfigureAwait(false);
        scope.Token.ThrowIfCancellationRequested();
        return JsonDocument.Parse(bytes);
    }
    private static JsonElement Array(JsonElement item, string key) => item.ValueKind == JsonValueKind.Object &&
        item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Array ? value : throw new InvalidDataException("AppSx 缺少数组：" + key);
    private static string Text(JsonElement item, string key) => AppRjSource.Text(item, key);
    private MediaItem Item(JsonElement item)
    {
        var result = AppRjSource.Item(item);
        return result with { Poster = result.Poster is { Length: > 0 } pic ? new Uri(_config.Host, pic).AbsoluteUri : result.Poster };
    }
    private Task<JsonDocument> Init(CancellationToken ct) => Request("initV119", null, ct);
    public async Task<IReadOnlyList<Category>> GetCategoriesAsync(CancellationToken ct = default)
    {
        using var document = await Init(ct).ConfigureAwait(false);
        return Array(document.RootElement, "type_list").EnumerateArray().Select(type => new Category(Text(type, "type_id"), Text(type, "type_name"))).ToArray();
    }
    public async Task<MediaPage> GetHomeAsync(CancellationToken ct = default)
    {
        using var document = await Init(ct).ConfigureAwait(false);
        var items = Array(document.RootElement, "type_list").EnumerateArray().SelectMany(type => Array(type, "recommend_list").EnumerateArray()).Select(Item).ToArray();
        return new(items, 1, 1);
    }
    public async Task<IReadOnlyList<FilterGroup>> GetFiltersAsync(string categoryId, CancellationToken ct = default)
    {
        using var document = await Init(ct).ConfigureAwait(false);
        var type = Array(document.RootElement, "type_list").EnumerateArray().FirstOrDefault(type => Text(type, "type_id") == categoryId);
        if (type.ValueKind != JsonValueKind.Object) return [];
        var groups = new List<FilterGroup>();
        if (type.TryGetProperty("type_extend", out var extended) && extended.ValueKind == JsonValueKind.String && extended.GetString() is { Length: > 0 } text)
        {
            using var values = JsonDocument.Parse(text);
            foreach (var (key, label) in new[] { ("class", "类型"), ("area", "地区"), ("lang", "语言"), ("year", "年份") })
            {
                string options = Text(values.RootElement, key);
                if (options.Length > 0) groups.Add(new(key, label, options.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(value => new FilterValue(value, value)).ToList(), "全部"));
            }
        }
        groups.Add(new("sort", "排序", new[] { "最新", "最热", "最赞" }.Select(value => new FilterValue(value, value)).ToList(), "全部"));
        return groups;
    }
    public async Task<MediaPage> GetItemsAsync(string categoryId, int page, IReadOnlyDictionary<string, string>? filters, CancellationToken ct = default)
    {
        ValidatePage(page);
        var fields = new Dictionary<string, string> { ["type_id"] = categoryId, ["page"] = page.ToString(CultureInfo.InvariantCulture) };
        foreach (var key in new[] { "area", "year", "class", "lang", "sort" }) fields[key] = filters?.TryGetValue(key, out var value) == true && value.Length > 0 ? value : "全部";
        if (filters?.Keys.Any(key => !fields.ContainsKey(key)) == true) throw new NotSupportedException("AppSx 筛选参数无效。");
        using var document = await Request("typeFilterVodList", fields, ct).ConfigureAwait(false);
        var items = Array(document.RootElement, "recommend_list").EnumerateArray().Select(Item).ToArray();
        return new(items, page, items.Length == 30 ? checked(page + 1) : page);
    }
    public async Task<MediaPage> SearchAsync(string query, int page, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); ValidatePage(page);
        if (query.Length > 256) throw new ArgumentException("搜索关键词过长。", nameof(query));
        if (page > 1 || string.IsNullOrWhiteSpace(query)) return new([], page, page);
        using var document = await Request("searchList", new() { ["keywords"] = query, ["type_id"] = "0", ["page"] = "1" }, ct).ConfigureAwait(false);
        return new(Array(document.RootElement, "search_list").EnumerateArray().Select(Item).ToArray(), 1, 1);
    }
    private Task<JsonDocument> Detail(string id, CancellationToken ct) => Request("vodDetail", new() { ["vod_id"] = id }, ct);

    private sealed record Selection(string LineId, string LineName, string Id, string Title, string Url, string Parser, string PlayerType, string ParseType, string Agent, string Token, Dictionary<string, string> Headers);
    private static string Compact(JsonElement value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping })) value.WriteTo(writer);
        return Utf8.GetString(stream.ToArray());
    }
    private static IReadOnlyList<Selection> Selections(JsonElement data)
    {
        var output = new List<Selection>();
        foreach (var line in Array(data, "vod_play_list").EnumerateArray())
        {
            var player = line.GetProperty("player_info");
            string flag = Text(player, "show").Trim(), parser = Text(player, "parse"), playerType = Text(player, "player_parse_type"), agent = Text(player, "user_agent");
            string parseType = Text(player, "parse_type"); if (parseType.Length == 0) parseType = "1";
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string encodedHeaders = "";
            if (player.TryGetProperty("headers", out var extra) && extra.ValueKind == JsonValueKind.Object)
            {
                encodedHeaders = Convert.ToBase64String(Utf8.GetBytes(Compact(extra)));
                foreach (var pair in extra.EnumerateObject())
                {
                    if (pair.Value.ValueKind != JsonValueKind.String || pair.Name.Length == 0 || pair.Name.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')) throw new InvalidDataException("AppSx 播放请求头无效。");
                    headers[pair.Name] = ValidateHeader(pair.Value.GetString()!);
                }
            }
            if (flag.Length == 0 || flag.Contains('\u0001')) throw new InvalidDataException("AppSx 播放线路无效。");
            foreach (var episode in Array(line, "urls").EnumerateArray())
            {
                string url = Text(episode, "url"), token = Text(episode, "token");
                string[] components = [url, parser, playerType, agent, token, encodedHeaders, parseType];
                if (components.Any(value => value.Contains('@'))) throw new NotSupportedException("AppSx 播放标识包含有歧义的分隔符。");
                string id = flag + '\u0001' + string.Join('@', components);
                output.Add(new(flag, flag, id, Text(episode, "name"), url, parser, playerType, parseType, agent.Length > 0 ? ValidateHeader(agent) : Agent, token, headers));
            }
        }
        return output;
    }
    public async Task<MediaDetail> GetDetailAsync(string mediaId, CancellationToken ct = default)
    {
        using var document = await Detail(mediaId, ct).ConfigureAwait(false);
        var vod = document.RootElement.GetProperty("vod");
        return new()
        {
            Item = Item(vod) with { Id = mediaId }, Description = Text(vod, "vod_blurb").Trim(), Actor = Text(vod, "vod_actor"), Director = Text(vod, "vod_director"),
            Lines = Selections(document.RootElement).GroupBy(item => item.LineId).Select(group => new PlaybackLine(group.Key, group.First().LineName, group.Select(item => new Episode(item.Id, item.Title)).ToArray())).ToArray()
        };
    }
    public async Task<PlaybackRequest> ResolvePlaybackAsync(string mediaId, string episodeId, CancellationToken ct = default)
    {
        using var document = await Detail(mediaId, ct).ConfigureAwait(false);
        var selections = Selections(document.RootElement);
        var selected = selections.FirstOrDefault(item => item.Id == episodeId);
        if (selected is null) throw new InvalidDataException("AppSx 选集已失效，请刷新详情。");
        string url = selected.Url;
        var headers = new Dictionary<string, string>(selected.Headers, StringComparer.OrdinalIgnoreCase) { ["User-Agent"] = selected.Agent };
        var resolution = ResolutionKind.Direct;
        if (selected.PlayerType != "2" && selected.ParseType == "2")
        {
            url = selected.Parser.Length > 0 ? selected.Parser + WebUtility.UrlEncode(url) : url;
            resolution = ResolutionKind.Sniff;
        }
        else if (selected.PlayerType == "2")
        {
            string parser = selected.Parser;
            if (selected.Token.Length > 0 && parser.IndexOf('?') is var index && index >= 0)
                parser = parser[..(index + 1)] + "token=" + Uri.EscapeDataString(selected.Token) + "&" + parser[(index + 1)..];
            string endpoint = parser + WebUtility.UrlEncode(url);
            using var request = new HttpRequestMessage(HttpMethod.Post, HttpUri(endpoint)) { Content = new FormUrlEncodedContent(new Dictionary<string, string>()) };
            request.Headers.TryAddWithoutValidation("User-Agent", selected.Agent);
            using var parsed = await Send(request, ct).ConfigureAwait(false);
            url = ParsedUrl(parsed.RootElement);
        }
        else if (selected.ParseType == "1" || !MediaUrl(url))
        {
            using var parsed = await Request("vodParse", new()
            {
                ["parse_api"] = selected.Parser, ["url"] = Encrypt(url, _config.Key, _config.Iv, wrapped: true),
                ["player_parse_type"] = selected.PlayerType, ["token"] = selected.Token
            }, ct).ConfigureAwait(false);
            url = Text(parsed.RootElement, "play_url");
        }
        HttpUri(url);
        return new()
        {
            Uri = url, Resolution = resolution, Headers = headers, SourceKey = Key, SourceName = Name,
            MediaId = mediaId, EpisodeId = episodeId, LineId = selected.LineId,
            Title = Text(document.RootElement.GetProperty("vod"), "vod_name") + " · " + selected.Title
        };
    }
    private static string ParsedUrl(JsonElement data, int depth = 0)
    {
        if (depth > 3 || data.ValueKind != JsonValueKind.Object) throw new InvalidDataException("AppSx 解析响应无效。");
        if (data.TryGetProperty("data", out var nested) && nested.ValueKind == JsonValueKind.Object && Text(nested, "url") is { Length: > 0 } inner) return inner;
        if (Text(data, "url") is { Length: > 0 } url) return url;
        if (Text(data, "json") is { Length: > 0 } text)
        { using var parsed = JsonDocument.Parse(text); return ParsedUrl(parsed.RootElement, depth + 1); }
        throw new InvalidDataException("AppSx 解析器未返回媒体地址。");
    }
    private static bool MediaUrl(string url) => !url.Contains("url=http", StringComparison.Ordinal) && new[] { ".m3u8", ".mp4", ".avi", ".mov", ".wmv", ".flv", ".mkv", ".webm", ".rmvb" }.Any(ext => url.Contains(ext, StringComparison.OrdinalIgnoreCase));
    private static Uri HttpUri(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 ? uri : throw new InvalidDataException("AppSx HTTP(S) 地址无效。");
    private static string ValidateHeader(string value) => value.Length <= 8192 && value.IndexOfAny(['\r', '\n', '\0']) < 0 ? value : throw new InvalidDataException("AppSx 请求头无效。");
    private static void ValidatePage(int page) { if (page < 1 || page == int.MaxValue) throw new ArgumentOutOfRangeException(nameof(page)); }
}
