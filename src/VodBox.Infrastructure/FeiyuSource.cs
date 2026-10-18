using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VodBox.Core;

namespace VodBox.Infrastructure;

/// <summary>
/// 飞鱼（宝盒 <c>csp_Feiyu</c>）JSON 原生实现，按随宝盒下发的 <c>bh0822.jar</c> 反编译源码
/// （<c>com.github.catvod.spider.Feiyu</c>）重写。接口固定为 <c>https://4kyszx.top</c> 并按插件算法签名；
/// ext 不参与寻址（原插件 init 忽略 ext），仅空/缺省时才接管。
/// </summary>
public sealed class FeiyuSource : IResolvingContentSource
{
    private const string Origin = "https://4kyszx.top";
    private const string Device = "f45a775875e2e004adbcea78e3312218";
    private const string Salt = "cms_device_salt_v1_2024cms_app_sign_key_v1_2024_secure";
    private const string AppVersion = "2.6.8+1";
    private const string Platform = "android";
    private const string SignAgent = "Dart/3.10 (dart:io)";
    private const string Alnum = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    private readonly SourceInfo _site;
    private readonly string _key;
    private readonly Func<string, IReadOnlyDictionary<string, string>, CancellationToken, Task<string>> _fetch;
    private readonly Func<string> _nonce;
    private readonly Func<long> _clock;
    private readonly Func<IReadOnlyList<(string Key, string Value)>, string> _query;

    public string Key => _site.Key;
    public string Name => _site.Name;

    public FeiyuSource(SourceInfo site) : this(site, null, null, null, null) { }

    internal FeiyuSource(
        SourceInfo site,
        Func<string, IReadOnlyDictionary<string, string>, CancellationToken, Task<string>>? fetch,
        Func<string>? nonce,
        Func<long>? clock,
        Func<IReadOnlyList<(string Key, string Value)>, string>? query)
    {
        _site = site;
        if (!Supports(site.Ext)) throw new NotSupportedException("Feiyu ext 配置不受支持。");
        _key = SignatureKey();
        _fetch = fetch ?? Fetch;
        _nonce = nonce ?? RandomNonce;
        _clock = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        _query = query ?? JavaUrlEncode;
    }

    internal static bool Supports(string? ext) => string.IsNullOrWhiteSpace(ext);

    /// <summary>插件 init 计算：HMAC-SHA256(key=salt, msg=deviceId) 的十六进制摘要。</summary>
    internal static string SignatureKey() => Hmac(Salt, Device);

    private static string Hmac(string key, string message)
    {
        using var mac = new HMACSHA256(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(mac.ComputeHash(Encoding.UTF8.GetBytes(message))).ToLowerInvariant();
    }

    private static string RandomNonce()
    {
        var builder = new StringBuilder(16);
        for (var i = 0; i < 16; i++) builder.Append(Alnum[Random.Shared.Next(Alnum.Length)]);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(builder.ToString()));
    }

    /// <summary>与 Java URLEncoder.encode(value,"UTF-8") 一致的表单编码（空格为 +，保留 - _ . *）。</summary>
    internal static string JavaUrlEncode(IReadOnlyList<(string Key, string Value)> values)
    {
        var builder = new StringBuilder();
        foreach (var (key, value) in values)
        {
            if (builder.Length > 0) builder.Append('&');
            builder.Append(key).Append('=').Append(Encode(value));
        }
        return builder.ToString();
    }

    private static string Encode(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var builder = new StringBuilder(bytes.Length);
        foreach (var b in bytes)
        {
            var c = (char)b;
            if (c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_' or '.' or '*') builder.Append(c);
            else if (c == ' ') builder.Append('+');
            else builder.Append('%').Append(((int)b).ToString("X2", CultureInfo.InvariantCulture));
        }
        return builder.ToString();
    }

    private static async Task<string> Fetch(string url, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        foreach (var (name, value) in headers)
        {
            if (name.Equals("host", StringComparison.OrdinalIgnoreCase)) request.Headers.Host = value;
            else if (name.Equals("accept", StringComparison.OrdinalIgnoreCase)) request.Headers.Accept.ParseAdd(value);
            else request.Headers.TryAddWithoutValidation(name, value);
        }
        using var response = await DefaultHttp.PlainClient.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    /// <summary>按插件 e() 组装签名头并请求。encode 为 true 时签名串用原文、查询串用编码值。</summary>
    private async Task<JsonElement?> Request(string path, IReadOnlyList<(string Key, string Value)>? parameters, bool encode, CancellationToken ct)
    {
        var list = parameters ?? [];
        var timestamp = _clock().ToString(CultureInfo.InvariantCulture);
        var nonce = _nonce();
        var signatureParts = new List<string>(list.Count);
        foreach (var (key, value) in list) signatureParts.Add(key + "=" + (encode ? value : Encode(value)));
        var canonical = "GET\n" + path + "\n" + string.Join("&", signatureParts) + "\n" + timestamp + "\n" + nonce + "\n" + AppVersion;
        var signature = Hmac(_key, canonical);
        var query = _query(list);
        var url = Origin + path + (query.Length == 0 ? "" : "?" + query);
        var headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["x-signature"] = signature,
            ["user-agent"] = SignAgent,
            ["x-nonce"] = nonce,
            ["accept"] = "application/json",
            ["x-timestamp"] = timestamp,
            ["host"] = "4kyszx.top",
            ["x-device-id"] = Device,
            ["content-type"] = "application/json",
            ["x-app-version"] = AppVersion,
            ["x-platform"] = Platform,
        };
        var text = await _fetch(url, headers, ct).ConfigureAwait(false);
        if (text.Trim().Length == 0) return null;
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private static string Text(JsonElement node, string key) => AppRjSource.Text(node, key);

    private static string First(JsonElement node, params string[] keys)
    {
        foreach (var key in keys)
        {
            var value = Text(node, key);
            if (value.Length > 0) return value;
        }
        return "";
    }

    private static IEnumerable<JsonElement> Enumerate(JsonElement data)
    {
        if (data.ValueKind == JsonValueKind.Array) return data.EnumerateArray();
        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("list", out var list) && list.ValueKind == JsonValueKind.Array)
            return list.EnumerateArray();
        return [];
    }

    public async Task<IReadOnlyList<Category>> GetCategoriesAsync(CancellationToken ct = default)
    {
        var root = await Request("/api/app/categories", null, false, ct).ConfigureAwait(false);
        var categories = new List<Category>();
        if (root is not { ValueKind: JsonValueKind.Object } document || !document.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return categories;
        foreach (var entry in data.EnumerateArray())
        {
            var id = First(entry, "id");
            if (id.Length > 0) categories.Add(new Category(id, First(entry, "name", "title")));
        }
        return categories;
    }

    public async Task<MediaPage> GetHomeAsync(CancellationToken ct = default)
    {
        var root = await Request("/api/app/ranking/list", [("category", "1")], false, ct).ConfigureAwait(false);
        return new MediaPage(RankingItems(root), 1, 1);
    }

    private static List<MediaItem> RankingItems(JsonElement? root)
    {
        var items = new List<MediaItem>();
        if (root is not { ValueKind: JsonValueKind.Object } document || !document.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return items;
        foreach (var entry in data.EnumerateArray())
        {
            var id = First(entry, "id");
            if (id.Length == 0) continue;
            items.Add(new MediaItem
            {
                Id = id,
                Title = First(entry, "title", "name"),
                Poster = First(entry, "cover", "pic"),
                Remarks = First(entry, "subtitle", "remarks"),
            });
        }
        return items;
    }

    public async Task<MediaPage> GetItemsAsync(string categoryId, int page, IReadOnlyDictionary<string, string>? filters, CancellationToken ct = default)
    {
        if (page < 1) throw new InvalidDataException("页码无效。");
        if (categoryId.Length == 0) throw new InvalidDataException("Feiyu 分类无效。");
        var parameters = new List<(string Key, string Value)>
        {
            ("page", page.ToString(CultureInfo.InvariantCulture)),
            ("page_size", "20"),
        };
        if (filters is not null)
            foreach (var key in new[] { "class", "year", "area", "lang" })
                if (filters.TryGetValue(key, out var value) && value.Length > 0) parameters.Add((key, value));
        var root = await Request("/api/app/categories/" + Uri.EscapeDataString(categoryId) + "/videos", parameters, true, ct).ConfigureAwait(false);
        var items = new List<MediaItem>();
        if (root is { ValueKind: JsonValueKind.Object } document && document.TryGetProperty("data", out var data))
            foreach (var entry in Enumerate(data))
            {
                var id = First(entry, "id");
                if (id.Length == 0) continue;
                items.Add(new MediaItem { Id = id, Title = First(entry, "name", "title"), Poster = First(entry, "pic", "cover"), Remarks = First(entry, "remarks", "subTitle") });
            }
        return new MediaPage(items, page, 999);
    }

    public async Task<MediaPage> SearchAsync(string query, int page, CancellationToken ct = default)
    {
        if (page < 1) throw new InvalidDataException("页码无效。");
        if (page > 1) return new MediaPage([], page, page);
        var root = await Request("/api/app/videos/search", [("keyword", query), ("page", "1"), ("page_size", "20")], true, ct).ConfigureAwait(false);
        var items = new List<MediaItem>();
        if (root is { ValueKind: JsonValueKind.Object } document && document.TryGetProperty("data", out var data))
            foreach (var entry in Enumerate(data))
            {
                var id = First(entry, "id");
                if (id.Length == 0) continue;
                items.Add(new MediaItem { Id = id, Title = First(entry, "name", "title"), Poster = First(entry, "pic", "cover"), Remarks = First(entry, "remarks", "subTitle") });
            }
        return new MediaPage(items, 1, 1);
    }

    /// <summary>线路排序权重（插件 c()）：4k &gt; 蓝光/2k &gt; 高清/hd &gt; m3u8/采集/资源/zy &gt; 其它。</summary>
    internal static int LineRank(string name)
    {
        var value = name.ToLowerInvariant();
        if (value.Contains("4k", StringComparison.Ordinal)) return 100;
        if (value.Contains("藍光", StringComparison.Ordinal) || value.Contains("2k", StringComparison.Ordinal)) return 90;
        if (value.Contains("高清", StringComparison.Ordinal) || value.Contains("hd", StringComparison.Ordinal)) return 80;
        return value.Contains("m3u8", StringComparison.Ordinal) || value.Contains("採集", StringComparison.Ordinal) ||
            value.Contains("資源", StringComparison.Ordinal) || value.Contains("zy", StringComparison.Ordinal) ? 10 : 50;
    }

    public async Task<MediaDetail> GetDetailAsync(string mediaId, CancellationToken ct = default)
    {
        if (mediaId.Length == 0) throw new InvalidDataException("Feiyu 影片编号无效。");
        var root = await Request("/api/app/videos/" + Uri.EscapeDataString(mediaId), null, false, ct).ConfigureAwait(false);
        if (root is not { ValueKind: JsonValueKind.Object } document || !document.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Feiyu 详情响应缺少 data。");
        var lines = new List<PlaybackLine>();
        if (data.TryGetProperty("playGroups", out var groups) && groups.ValueKind == JsonValueKind.Array)
        {
            var ordered = groups.EnumerateArray().ToList();
            ordered.Sort((left, right) => LineRank(First(right, "name", "code")).CompareTo(LineRank(First(left, "name", "code"))));
            foreach (var group in ordered)
            {
                var parse = Text(group, "parseApi");
                var episodes = new List<Episode>();
                if (group.TryGetProperty("playUrls", out var urls) && urls.ValueKind == JsonValueKind.Array)
                    foreach (var entry in urls.EnumerateArray())
                    {
                        var url = Text(entry, "url");
                        if (url.Length == 0) continue;
                        episodes.Add(new Episode(parse + "||" + url, First(entry, "name")));
                    }
                if (episodes.Count == 0) continue;
                var name = First(group, "name", "code");
                lines.Add(new PlaybackLine(name, name, episodes));
            }
        }
        var item = new MediaItem
        {
            Id = First(data, "id"),
            Title = First(data, "name"),
            Poster = First(data, "pic"),
            TypeName = First(data, "categoryName"),
            Year = First(data, "year"),
            Area = First(data, "area"),
            Remarks = First(data, "remarks", "subTitle"),
        };
        return new MediaDetail
        {
            Item = item,
            Description = First(data, "content"),
            Actor = First(data, "actor"),
            Director = First(data, "director"),
            Lines = lines,
        };
    }

    public async Task<PlaybackRequest> ResolvePlaybackAsync(string mediaId, string episodeId, CancellationToken ct = default)
    {
        var parts = episodeId.Split("||");
        var parse = parts.Length > 0 ? parts[0] : "";
        var url = parts.Length > 1 ? parts[1] : "";
        if (url.Length == 0) throw new InvalidDataException("Feiyu 剧集地址无效。");
        var request = new PlaybackRequest
        {
            Uri = url,
            SourceKey = Key,
            SourceName = Name,
            MediaId = mediaId,
            EpisodeId = episodeId,
            Title = Name,
            Headers = new() { ["User-Agent"] = SignAgent },
        };
        if (parse.Length == 0) return request;
        // 插件 parse=1：{parseApi}{url} 交给 JSON 解析器；解析地址非空时按解析链处理。
        request = request with { ParseEndpoint = parse, Jx = "1" };
        if (parse.Contains("json", StringComparison.OrdinalIgnoreCase) || parse.Contains("api", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var text = await _fetch(parse + url, new Dictionary<string, string> { ["User-Agent"] = "Mozilla/5.0" }, ct).ConfigureAwait(false);
                using var document = JsonDocument.Parse(text);
                if (document.RootElement.TryGetProperty("url", out var resolved) && resolved.ValueKind == JsonValueKind.String &&
                    Uri.TryCreate(resolved.GetString(), UriKind.Absolute, out var address) && address.Scheme is "http" or "https")
                    return request with { Uri = address.AbsoluteUri, ParseEndpoint = null, Jx = null };
            }
            catch (Exception error) when (error is JsonException or HttpRequestException or InvalidDataException or TaskCanceledException or IOException) { }
        }
        return request;
    }
}
