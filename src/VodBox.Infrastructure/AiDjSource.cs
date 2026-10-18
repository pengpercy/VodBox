using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using VodBox.Core;

namespace VodBox.Infrastructure;

/// <summary>Native AiDj: SSR/App metadata, exact signed JSON requests, CENC loopback streaming.</summary>
public sealed partial class AiDjSource : IResolvingContentSource
{
    private const string Site = "https://hongguoduanju.com";
    private const string ApiHost = "https://api5-normal-sinfonlineb.fqnovel.com";
    private const string UserAgent = "Mozilla/5.0 (Linux; Android 12) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36";
    private const string AppUserAgent = "com.phoenix.read/71332 (Linux; U; Android 16; zh_CN; 25053RT47C; Build/BP2A.250605.031.A3; Cronet/TTNetVersion:04657795 2026-01-23 QuicVersion:c67e9834 2025-09-08)";
    private const string Prefix = "hg-episode-v1:";
    private static readonly HttpClient DefaultHttp = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.All });
    private readonly SourceInfo _source;
    private readonly HttpClient _http;
    private readonly Func<string, byte[], CancellationToken, Task<string>> _register;
    // 原插件每个实例生成一对随机 device_id/install_id 并在所有签名请求里复用。
    private readonly string _deviceId = RandomId();
    private readonly string _installId = RandomId();
    private static readonly IReadOnlyList<Category> Categories = [new("comic", "AI漫剧"), new("ai", "AI短剧")];
    public string Key => _source.Key;
    public string Name => _source.Name;
    public AiDjSource(SourceInfo source) : this(source, DefaultHttp, AiDjMediaProxy.Shared.RegisterAsync) { }
    internal AiDjSource(SourceInfo source, HttpClient http, Func<string, byte[], CancellationToken, Task<string>> register)
    {
        if (!Supports(source.Ext)) throw new InvalidDataException("AiDj ext 必须是固定插件的 JSON 配置对象；不执行外部脚本。");
        _source = source; _http = http; _register = register;
    }
    internal static bool Supports(string? ext)
    {
        if (string.IsNullOrWhiteSpace(ext)) return true;
        if (ext.Length > 64 * 1024) return false;
        try { using var doc = JsonDocument.Parse(ext); return doc.RootElement.ValueKind == JsonValueKind.Object && !doc.RootElement.TryGetProperty("modulePath", out _); }
        catch (JsonException) { return false; }
    }
    private static string RandomId() => (100_000_000_000_000_000L + (long)(BinaryRandom() % 900_000_000_000_000_000UL)).ToString(CultureInfo.InvariantCulture);
    private static ulong BinaryRandom() => System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(RandomNumberGenerator.GetBytes(8));
    public Task<IReadOnlyList<Category>> GetCategoriesAsync(CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); return Task.FromResult(Categories); }
    // The source's topic filters are not applied to either exposed AI category, so do not expose inert controls.
    public Task<IReadOnlyList<FilterGroup>> GetFiltersAsync(string categoryId, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); RequireCategory(categoryId); return Task.FromResult<IReadOnlyList<FilterGroup>>([]); }
    public Task<MediaPage> GetHomeAsync(CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); return Task.FromResult(new MediaPage([], 1, 1)); }

    public async Task<MediaPage> GetItemsAsync(string categoryId, int page, IReadOnlyDictionary<string, string>? filters, CancellationToken ct = default)
    {
        RequireCategory(categoryId); RequirePage(page);
        string genre = categoryId == "comic" ? "comic_series" : "ai_series";
        var body = Encoding.UTF8.GetBytes("{\"filter_ids\":\"\",\"req_scene\":\"" + genre + "\",\"offset\":" + ((page - 1) * 18).ToString(CultureInfo.InvariantCulture) +
            ",\"need_selector_panel\":false,\"limit\":18,\"select_items\":{\"category_dim_epoch\":[],\"online_time\":[],\"gender\":[],\"category_dim_role\":[],\"genre\":[\"" + genre +
            "\"],\"sort\":[\"hot_score\"],\"category_dim_theme\":[]},\"session_id\":\"\",\"req_type\":\"only_content\",\"client_req_type\":3}");
        try
        {
            using var result = await ApiAsync("/reading/distribution/category/landpage/v", body, ct).ConfigureAwait(false);
            var data = Object(result.RootElement, "data");
            var rows = Array(data, "video_data").Select(AppCard).ToArray();
            if (rows.Length > 0) return new(rows, page, True(data, "has_more") ? page + 1 : page);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or JsonException) { /* Python falls back to the public SSR category. */ }
        string route = categoryId == "comic" ? "comic-drama" : "ai-drama";
        using var router = await RouterAsync(Site + "/category/" + route + (page > 1 ? "?page=" + page.ToString(CultureInfo.InvariantCulture) : ""), ct).ConfigureAwait(false);
        var loader = Object(router.RootElement, "loaderData");
        var category = Object(loader, "category_$"); if (category.ValueKind != JsonValueKind.Object) category = Object(loader, "category_page");
        if (category.ValueKind != JsonValueKind.Object && loader.ValueKind == JsonValueKind.Object)
            category = loader.EnumerateObject().Select(p => p.Value).FirstOrDefault(v => v.ValueKind == JsonValueKind.Object && v.TryGetProperty("recommendList", out _));
        if (category.ValueKind != JsonValueKind.Object) throw new InvalidDataException("AiDj SSR category data missing.");
        var items = Array(category, "recommendList").Select(WebCard).ToArray();
        int pages = Number(Object(category, "pagination"), "totalPages", page);
        return new(items, page, Math.Clamp(pages, page, 10000));
    }
    public async Task<MediaPage> SearchAsync(string query, int page, CancellationToken ct = default)
    {
        RequirePage(page); if (string.IsNullOrWhiteSpace(query) || query.Length > 256) throw new InvalidDataException("AiDj 搜索词无效。");
        // Original SSR endpoint does not encode page; don't repeat the same ten results as new pages.
        if (page != 1) return new([], page, page);
        using var router = await RouterAsync(Site + "/search/" + Uri.EscapeDataString(query.Trim()), ct).ConfigureAwait(false);
        var search = Object(Object(router.RootElement, "loaderData"), "search_(keyword)/page");
        if (search.ValueKind != JsonValueKind.Object) throw new InvalidDataException("AiDj SSR search data missing.");
        return new(Array(search, "searchList").Select(WebCard).ToArray(), 1, 1);
    }
    private readonly DetailCache _details = new(TimeSpan.FromMinutes(5));
    public Task<MediaDetail> GetDetailAsync(string mediaId, CancellationToken ct = default) => _details.GetAsync(mediaId, LoadDetailAsync, ct);
    private async Task<MediaDetail> LoadDetailAsync(string mediaId, CancellationToken ct)
    {
        string sid = SeriesId(mediaId);
        var body = Encoding.UTF8.GetBytes("{\"biz_param\":{\"detail_page_version\":0,\"disable_digg_stat\":false,\"disable_video_relate_book\":false,\"need_all_video_definition\":false,\"need_mp4_align\":false,\"screen_width_px\":\"900\",\"source\":7,\"use_os_player\":false,\"use_server_dns\":false},\"series_id\":\"" + sid + "\"}");
        try
        {
            using var result = await ApiAsync("/novel/player/multi_video_detail/v1/", body, ct).ConfigureAwait(false);
            var data = Object(Object(Object(result.RootElement, "data"), sid), "video_data");
            var episodes = Array(data, "video_list").Select((e, index) => (Value: e, Index: Number(e, "vid_index", index + 1)))
                .Where(e => IsId(Str(e.Value, "vid"))).OrderBy(e => e.Index)
                .Select(e => new Episode("AI\u0001" + Prefix + Str(e.Value, "vid"), "第" + e.Index.ToString(CultureInfo.InvariantCulture) + "集")).ToArray();
            if (episodes.Length > 0) return Detail(mediaId, data, episodes, "series_title");
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or JsonException) { }
        using var router = await RouterAsync(Site + "/detail?series_id=" + sid, ct).ConfigureAwait(false);
        var series = Object(Object(Object(router.RootElement, "loaderData"), "detail_page"), "seriesDetail");
        var vids = Array(series, "vid_list").Select((v, index) => (Id: Scalar(v), Index: index + 1)).Where(v => IsId(v.Id))
            .Select(v => new Episode("AI\u0001" + Prefix + v.Id, "第" + v.Index.ToString(CultureInfo.InvariantCulture) + "集")).ToArray();
        if (vids.Length == 0) throw new InvalidDataException("AiDj 详情没有真实 vid 分集。");
        return Detail(mediaId, series, vids, "series_name");
    }
    private static MediaDetail Detail(string id, JsonElement data, IReadOnlyList<Episode> episodes, string titleKey)
    {
        string title = Str(data, titleKey); if (title.Length == 0) throw new InvalidDataException("AiDj 详情缺少标题。");
        return new() { Item = new() { Id = id, Title = title, Poster = Str(data, "series_cover"), Remarks = Number(data, "episode_cnt", 0) is var count && count > 0 ? $"全{count}集" : Str(data, "episode_right_text") },
            Description = Str(data, "series_intro"), Actor = string.Join(',', Array(data, "celebrities").Select(e => Str(e, "nickname")).Where(s => s.Length > 0)), Lines = [new("AI", "AI", episodes)] };
    }
    public async Task<PlaybackRequest> ResolvePlaybackAsync(string mediaId, string episodeId, CancellationToken ct = default)
    {
        string reference = episodeId.StartsWith("AI\u0001", StringComparison.Ordinal) ? episodeId[3..] : episodeId;
        if (!reference.StartsWith(Prefix, StringComparison.Ordinal) || !IsId(reference[Prefix.Length..])) throw new InvalidDataException("AiDj 分集引用无效。");
        string vid = reference[Prefix.Length..];
        var detail = await GetDetailAsync(mediaId, ct).ConfigureAwait(false);
        if (!detail.Lines[0].Episodes.Any(e => e.Id == "AI\u0001" + reference)) throw new InvalidDataException("AiDj 分集不属于当前剧目。");
        var body = Encoding.UTF8.GetBytes("{\"biz_param\":{\"detail_page_version\":0,\"device_level\":3,\"disable_digg_stat\":false,\"need_all_video_definition\":true,\"need_mp4_align\":false,\"use_os_player\":false,\"use_server_dns\":false,\"video_platform\":1024},\"mixed_video_id_map\":{\"1\":[\"" + vid + "\"]}}");
        using var result = await ApiAsync("/novel/player/multi_video_model/player/v1/", body, ct).ConfigureAwait(false);
        var data = Object(result.RootElement, "data"); var entry = Object(data, vid);
        if (entry.ValueKind != JsonValueKind.Object && data.ValueKind == JsonValueKind.Object)
            entry = data.EnumerateObject().Select(p => p.Value.ValueKind == JsonValueKind.Array ? p.Value.EnumerateArray().FirstOrDefault() : p.Value).FirstOrDefault(e => e.ValueKind == JsonValueKind.Object);
        var model = Object(entry, "video_model"); if (model.ValueKind == JsonValueKind.Undefined) model = Object(data, "video_model");
        using var parsed = model.ValueKind == JsonValueKind.String ? JsonDocument.Parse(model.GetString()!) : null;
        if (parsed is not null) model = parsed.RootElement;
        if (model.ValueKind != JsonValueKind.Object) throw new InvalidDataException("AiDj video_model missing.");
        var selected = SelectQuality(model); string url = FirstUrl(selected), spade = Str(selected, "spade_a");
        if (spade.Length == 0) spade = Str(Object(selected, "encrypt_info"), "spade_a");
        if (url.Length == 0 || spade.Length == 0) throw new InvalidDataException("AiDj model lacks URL or content key.");
        var keySeed = FindSeed(model);
        url = DecodeMediaUrl(url, keySeed);
        string local = await _register(url, AiDjCrypto.DeriveContentKey(spade), ct).ConfigureAwait(false);
        return new() { Uri = local, Title = detail.Item.Title, MediaId = mediaId, EpisodeId = episodeId, LineId = "AI", SourceKey = Key, SourceName = Name };
    }
    private async Task<JsonDocument> ApiAsync(string path, byte[] body, CancellationToken ct)
    {
        var device = new Dictionary<string, string>(StringComparer.Ordinal) { ["device_id"] = _deviceId, ["iid"] = _installId, ["install_id"] = _installId, ["device_brand"] = "Redmi", ["device_model"] = "25053RT47C", ["device_type"] = "25053RT47C", ["device_manufacturer"] = "Xiaomi", ["os_version"] = "16", ["version_name"] = "7.1.3.32", ["x_tt_dt"] = "", ["sec_device_token"] = "", ["device_sec_device_token"] = "", ["ua"] = AppUserAgent };
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["iid"] = _installId, ["device_id"] = device["device_id"], ["ac"] = "wifi", ["channel"] = "update_64", ["aid"] = "8662", ["app_name"] = "novelread", ["version_code"] = "71332", ["version_name"] = "7.1.3.32", ["device_platform"] = "android", ["os"] = "android", ["ssmix"] = "a", ["device_type"] = "25053RT47C", ["device_brand"] = "Redmi", ["language"] = "zh", ["os_api"] = "36", ["os_version"] = "16", ["manifest_version_code"] = "71332", ["resolution"] = "1280*2772", ["dpi"] = "520", ["update_version_code"] = "71332", ["host_abi"] = "arm64-v8a", ["dragon_device_type"] = "phone", ["pv_player"] = "71332", ["compliance_status"] = "0", ["need_personal_recommend"] = "1", ["player_so_load"] = "1", ["is_android_pad_screen"] = "0"
        };
        var headers = new Dictionary<string, string>(StringComparer.Ordinal) { ["User-Agent"] = AppUserAgent, ["Accept"] = "application/json; charset=utf-8,application/x-protobuf", ["Content-Type"] = "application/json; charset=UTF-8", ["x-xs-from-web"] = "0", ["x-ss-req-ticket"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture), ["x-tt-request-tag"] = "t=0;n=0", ["sdk-version"] = "2", ["passport-sdk-version"] = "50561", ["x-vc-bdturing-sdk-version"] = "3.7.2.cn" };
        var signed = AiDjSigning.Sign(ApiHost + path, parameters, device, body, headers);
        using var request = new HttpRequestMessage(HttpMethod.Post, signed.Url) { Content = new ByteArrayContent(body) };
        foreach (var (key, value) in signed.Headers)
            if (!request.Headers.TryAddWithoutValidation(key, value)) request.Content.Headers.TryAddWithoutValidation(key, value);
        return JsonDocument.Parse(await ReadAsync(request, ct).ConfigureAwait(false));
    }
    private async Task<JsonDocument> RouterAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url); request.Headers.TryAddWithoutValidation("User-Agent", UserAgent); request.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9");
        var html = Encoding.UTF8.GetString(await ReadAsync(request, ct).ConfigureAwait(false));
        var marker = RouterMarker().Match(html); if (!marker.Success) throw new InvalidDataException("AiDj SSR router data missing.");
        var utf8 = Encoding.UTF8.GetBytes(html[(marker.Index + marker.Length)..]); var reader = new Utf8JsonReader(utf8);
        return JsonDocument.ParseValue(ref reader);
    }
    private async Task<byte[]> ReadAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
        if ((int)response.StatusCode is >= 300 and < 400 || response.RequestMessage?.RequestUri is { } final && final != request.RequestUri) throw new InvalidDataException("AiDj unexpected endpoint redirect.");
        response.EnsureSuccessStatusCode(); await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
        return await BoundedContent.ReadAsync(stream, 8 * 1024 * 1024, deadline.Token).ConfigureAwait(false);
    }
    private static MediaItem AppCard(JsonElement data)
    { string id = Str(data, "series_id"); if (!IsId(id)) throw new InvalidDataException("AiDj invalid series identifier."); return new() { Id = id, Title = Str(data, "title") is { Length: > 0 } title ? title : "未命名", Poster = Str(data, "cover"), Remarks = Number(data, "episode_cnt", 0) is var count && count > 0 ? $"全{count}集" : "" }; }
    private static MediaItem WebCard(JsonElement row)
    {
        var nested = Object(row, "video_data"); var data = nested.ValueKind == JsonValueKind.Object ? nested : row;
        string id = Str(data, "series_id"); if (id.Length == 0) id = Str(row, "keyword"); if (!IsId(id)) throw new InvalidDataException("AiDj invalid SSR series identifier.");
        string title = Str(data, "series_title"); if (title.Length == 0) title = Str(data, "series_name"); if (title.Length == 0) title = Str(row, "name");
        return new() { Id = id, Title = title.Length > 0 ? title : "未命名", Poster = Str(data, "series_cover"), Remarks = Number(data, "episode_cnt", 0) is var count && count > 0 ? $"全{count}集" : "" };
    }
    private static JsonElement Object(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;
    private static IEnumerable<JsonElement> Array(JsonElement element, string name) => Object(element, name) is { ValueKind: JsonValueKind.Array } array ? array.EnumerateArray() : [];
    private static string Scalar(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ValueKind == JsonValueKind.Number ? value.GetRawText() : "";
    private static string Str(JsonElement value, string name) => Scalar(Object(value, name));
    private static int Number(JsonElement value, string name, int fallback) => int.TryParse(Str(value, name), NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : fallback;
    private static bool True(JsonElement value, string name) => Object(value, name).ValueKind == JsonValueKind.True;
    private static string SeriesId(string id) { var value = id.StartsWith("hg-series-v1:", StringComparison.Ordinal) ? id[13..] : id; return IsId(value) ? value : throw new InvalidDataException("AiDj 剧目编号无效。"); }
    private static bool IsId(string id) => id.Length is > 0 and <= 32 && id.All(char.IsAsciiDigit);
    private static void RequireCategory(string category) { if (category is not ("comic" or "ai")) throw new InvalidDataException("AiDj 分类无效。"); }
    private static void RequirePage(int page) { if (page is < 1 or > 10000) throw new InvalidDataException("AiDj 页码无效。"); }
    [GeneratedRegex(@"(?:window\.)?_ROUTER_DATA\s*=\s*", RegexOptions.CultureInvariant, 1000)] private static partial Regex RouterMarker();
    private static JsonElement SelectQuality(JsonElement model)
    {
        JsonElement List(JsonElement node, int depth)
        {
            if (depth > 8 || node.ValueKind != JsonValueKind.Object) return default;
            foreach (string key in new[] { "video_list", "dynamic_video_list" }) if (node.TryGetProperty(key, out var found)) return found;
            var dynamic = Object(node, "dynamic_video");
            foreach (string key in new[] { "dynamic_video_list", "video_list", "list" }) if (dynamic.ValueKind == JsonValueKind.Object && dynamic.TryGetProperty(key, out var found)) return found;
            var nested = List(Object(Object(node, "video_info"), "data"), depth + 1);
            return nested.ValueKind != JsonValueKind.Undefined ? nested : List(Object(node, "data"), depth + 1);
        }
        static string Quality(string value)
        {
            foreach (string q in new[] { "2160", "1440", "1080", "720", "576", "540", "480", "360" }) if (value.Contains(q, StringComparison.Ordinal)) return q;
            return "1080";
        }
        var rows = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        void Gather(JsonElement node, string hint, int depth)
        {
            if (depth > 16) throw new InvalidDataException("AiDj quality model nesting too deep.");
            if (node.ValueKind == JsonValueKind.Array) { foreach (var child in node.EnumerateArray()) Gather(child, hint, depth + 1); return; }
            if (node.ValueKind != JsonValueKind.Object) return;
            if (new[] { "main_url", "backup_url", "backup_url_1", "play_addr", "spade_a", "encrypt_info" }.Any(key => node.TryGetProperty(key, out _)))
            {
                string definition = Str(node, "definition"); if (definition.Length == 0) definition = Str(node, "vheight"); if (definition.Length == 0) definition = Str(Object(node, "video_meta"), "definition");
                rows[Quality(definition.Length > 0 ? definition : hint)] = node; return;
            }
            foreach (var property in node.EnumerateObject())
                if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    Gather(property.Value, property.Name is "dynamic_video" or "video_info" or "data" ? hint : Quality(property.Name), depth + 1);
        }
        Gather(List(model, 0), "", 0);
        foreach (string quality in new[] { "1080", "2160", "1440", "720", "576", "540", "480", "360" }) if (rows.TryGetValue(quality, out var row)) return row;
        throw new InvalidDataException("AiDj model has no playable quality.");
    }
    private static string FirstUrl(JsonElement node, int depth = 0)
    {
        if (depth > 12) throw new InvalidDataException("AiDj media URL nesting too deep.");
        if (node.ValueKind is JsonValueKind.String or JsonValueKind.Number) return Scalar(node);
        if (node.ValueKind == JsonValueKind.Array) { foreach (var child in node.EnumerateArray()) { var value = FirstUrl(child, depth + 1); if (value.Length > 0) return value; } }
        if (node.ValueKind == JsonValueKind.Object)
            foreach (string key in new[] { "main_url", "backup_url", "backup_url_1", "play_addr", "url", "uri", "src", "download_url", "url_list" })
                if (node.TryGetProperty(key, out var child)) { var value = FirstUrl(child, depth + 1); if (value.Length > 0) return value; }
        return "";
    }
    private static byte[] FindSeed(JsonElement model, int depth = 0)
    {
        if (depth > 8) throw new InvalidDataException("AiDj seed model nesting too deep.");
        string value = Str(model, "key_seed"); if (value.Length > 0) return Base64(value);
        foreach (string key in new[] { "dynamic_video", "video_info", "data" })
        {
            var nested = Object(model, key); if (nested.ValueKind != JsonValueKind.Object) continue;
            var result = FindSeed(nested, depth + 1); if (result.Length > 0) return result;
        }
        return [];
    }
    internal static string DecodeMediaUrl(string value, byte[] keySeed)
    {
        static bool Media(string text) => Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0;
        if (Media(value)) return value;
        var raw = Base64(value);
        if (raw.Length >= 20 && raw[0] == 0xa8 && raw[2] == 1 && raw[3] == 0)
        {
            if (keySeed.Length == 0) throw new InvalidDataException("AiDj encrypted URL lacks key_seed.");
            var constants = Convert.FromHexString("4dd4c2e6b83162090e52b3c7a6733ba41cb2462b829ab58a196b39db57177524f49baf7f08e8d68d26a72e37c1a95a2f1f05a51892aef2949732b62a38aadd58");
            var first = SHA512.HashData(keySeed); var second = SHA512.HashData(first.Concat(constants).ToArray());
            using var aes = Aes.Create(); aes.Key = second.AsSpan(0, 16).ToArray();
            int count = raw.Length - 4 - (raw.Length - 4) % 16;
            raw = aes.DecryptCbc(raw.AsSpan(4, count), second.AsSpan(16, 16), PaddingMode.None);
            if (raw.Length > 0 && raw[^1] is >= 1 and <= 16 && raw[^1] <= raw.Length) raw = raw[..^raw[^1]];
        }
        value = Encoding.UTF8.GetString(raw).TrimEnd('\0');
        return Media(value) ? value : throw new InvalidDataException("AiDj decoded media URL invalid.");
    }
    private static byte[] Base64(string value)
    {
        if (value.Length > 65536) throw new InvalidDataException("AiDj encoded URL too long.");
        value = value.Trim().Replace('-', '+').Replace('_', '/');
        try { return Convert.FromBase64String(value.PadRight(value.Length + (4 - value.Length % 4) % 4, '=')); }
        catch (FormatException ex) { throw new InvalidDataException("AiDj invalid Base64.", ex); }
    }
}
