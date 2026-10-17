using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VodBox.Core;

namespace VodBox.Infrastructure;

/// <summary>FTY LiveGz encrypted sports schedule and refreshable live lines.</summary>
public sealed class LiveGzSource : IResolvingContentSource
{
    private const string AesKey = "KANGEQIU@8868!~.";
    private const string AesIv = "0200010900030207";
    private static readonly Category[] Categories = [new("1-0-0", "热播"), new("0-37-0", "NBA"), new("0-0-1", "足球"), new("0-0-2", "篮球")];
    private static readonly HttpClient SharedHttp = new(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(20) };
    private readonly SourceInfo _site;
    private readonly Uri _host;
    private readonly HttpClient _http;
    public string Key => _site.Key;
    public string Name => _site.Name;
    public LiveGzSource(SourceInfo site) : this(site, SharedHttp) { }
    internal LiveGzSource(SourceInfo site, HttpClient http) { _site = site; _http = http; _host = Endpoint(site.Ext); }
    private static Uri Endpoint(string? ext)
    {
        var uri = HttpUri(string.IsNullOrWhiteSpace(ext) ? "https://api.moe3dze.com" : ext);
        if (uri.AbsolutePath != "/" || uri.Query.Length != 0) throw new InvalidDataException("LiveGz ext 必须是 HTTP(S) 根地址。");
        return uri;
    }
    internal static bool Supports(string? ext)
    { try { Endpoint(ext); return true; } catch (InvalidDataException) { return false; } }
    private async Task<JsonDocument> Request(string operation, string parameter, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_host, "/gz/live/" + operation + "?parameter=key"))
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["parameter"] = CatVodAes.Encrypt(parameter, AesKey, AesIv, wrapped: true) })
        };
        request.Headers.TryAddWithoutValidation("User-Agent", "okhttp/3.12.0");
        request.Headers.TryAddWithoutValidation("client-version", "3.0.4.0");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var envelope = JsonDocument.Parse(await BoundedContent.ReadAsync(stream, 4 * 1024 * 1024, timeout.Token).ConfigureAwait(false));
        timeout.Token.ThrowIfCancellationRequested();
        if (envelope.RootElement.ValueKind != JsonValueKind.Object || !envelope.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("LiveGz 响应缺少加密 data。");
        try { return JsonDocument.Parse(CatVodAes.Decrypt(data.GetString()!, AesKey, AesIv)); }
        catch (Exception error) when (error is FormatException or CryptographicException or DecoderFallbackException)
        { throw new InvalidDataException("LiveGz 响应解密失败。", error); }
    }
    public Task<IReadOnlyList<Category>> GetCategoriesAsync(CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); return Task.FromResult<IReadOnlyList<Category>>(Categories.ToArray()); }
    public Task<MediaPage> GetHomeAsync(CancellationToken ct = default) => GetItemsAsync("1-0-0", 1, null, ct);
    public async Task<MediaPage> GetItemsAsync(string categoryId, int page, IReadOnlyDictionary<string, string>? filters, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (page < 1) throw new ArgumentOutOfRangeException(nameof(page));
        if (!Categories.Any(category => category.Id == categoryId)) throw new InvalidDataException("LiveGz 分类不存在。");
        if (filters is { Count: > 0 }) throw new NotSupportedException("LiveGz 不支持额外筛选。");
        if (page > 1) return new([], page, page);
        var parts = categoryId.Split('-');
        using var document = await Request("sports", "{\"frame\":\"0\",\"hot\":\"" + parts[0] + "\",\"tag\":\"" + parts[1] + "\",\"type\":\"" + parts[2] + "\"}", ct).ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("LiveGz 赛程不是数组。");
        return new(document.RootElement.EnumerateArray().Where(match => Text(match, "m_status") != "2").Select(Item).ToArray(), 1, 1);
    }
    private static string Text(JsonElement value, string key) => AppRjSource.Text(value, key);
    private static MediaItem Item(JsonElement match)
    {
        var home = match.GetProperty("home"); var visiting = match.GetProperty("visiting");
        string id = Text(match, "mid") + '-' + Text(match, "match_id") + '-' + Text(match, "ball_type");
        Parts(id);
        string status = Text(match, "m_status"), remarks = status switch { "0" => "未开赛 ", "1" => "直播中 ", "2" => "已完赛 ", _ => "" };
        if (status == "0")
        {
            if (!long.TryParse(Text(match, "match_time"), CultureInfo.InvariantCulture, out long timestamp)) timestamp = 0;
            remarks += DateTimeOffset.FromUnixTimeSeconds(timestamp).ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture) + " ";
        }
        remarks += Text(match, "event_name");
        if (Text(match, "stage") is { Length: > 0 } stage) remarks += " " + stage;
        if (status is "1" or "2") remarks += " 比分" + Text(home, "score") + ":" + Text(visiting, "score");
        return new() { Id = id, Title = Text(home, "name") + " vs " + Text(visiting, "name"), Poster = Text(home, "logo"), Remarks = remarks };
    }
    private async Task<JsonDocument> Detail(string mediaId, CancellationToken ct)
    {
        var parts = Parts(mediaId);
        var document = await Request("detail", "{\"ball_type\":\"" + parts[2] + "\",\"match_id\":\"" + parts[1] + "\",\"mid\":\"" + parts[0] + "\"}", ct).ConfigureAwait(false);
        try
        {
            if (Item(document.RootElement).Id != mediaId) throw new InvalidDataException("LiveGz 返回了其他比赛。");
            return document;
        }
        catch { document.Dispose(); throw; }
    }
    private static IReadOnlyList<PlaybackLine> Lines(JsonElement match)
    {
        if (!match.TryGetProperty("live_line", out var remote) || remote.ValueKind == JsonValueKind.Null) return [];
        if (remote.ValueKind != JsonValueKind.Array) throw new InvalidDataException("LiveGz 直播线路不是数组。");
        var lines = new List<PlaybackLine>();
        foreach (var line in remote.EnumerateArray())
        {
            string flag = Text(line, "name").Trim(); if (flag.Length == 0) flag = "线路" + (lines.Count + 1);
            string url = Text(line, "m3u8"); if (url.Length == 0) continue;
            HttpUri(url);
            if (flag.Contains('\u0001')) throw new InvalidDataException("LiveGz 线路名称无效。");
            string id = lines.Any(existing => existing.Id == flag) ? flag + ":" + lines.Count : flag;
            lines.Add(new(id, flag, [new(flag + '\u0001' + url, (lines.Count + 1).ToString(CultureInfo.InvariantCulture))]));
        }
        return lines;
    }
    public async Task<MediaDetail> GetDetailAsync(string mediaId, CancellationToken ct = default)
    {
        using var document = await Detail(mediaId, ct).ConfigureAwait(false);
        return new() { Item = Item(document.RootElement), Lines = Lines(document.RootElement) };
    }
    public async Task<PlaybackRequest> ResolvePlaybackAsync(string mediaId, string episodeId, CancellationToken ct = default)
    {
        using var document = await Detail(mediaId, ct).ConfigureAwait(false);
        var lines = Lines(document.RootElement);
        var line = lines.FirstOrDefault(line => line.Episodes.Any(episode => episode.Id == episodeId));
        if (line is null && episodeId.IndexOf('\u0001') is var separator && separator > 0)
        {
            // One live stream per named line. The address changes on every detail fetch;
            // refresh that line instead of replaying the expired URL stored in history.
            var candidates = lines.Where(candidate => candidate.Name == episodeId[..separator]).ToArray();
            if (candidates.Length == 1) line = candidates[0];
        }
        if (line is null) throw new InvalidDataException("LiveGz 直播线路已失效，请刷新详情。");
        string refreshed = line.Episodes[0].Id;
        return new()
        {
            Uri = refreshed[(refreshed.IndexOf('\u0001') + 1)..], SourceKey = Key, SourceName = Name, MediaId = mediaId,
            LineId = line.Id, EpisodeId = episodeId, IsLive = true, Title = Item(document.RootElement).Title
        };
    }
    public Task<MediaPage> SearchAsync(string query, int page, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); if (page < 1) throw new ArgumentOutOfRangeException(nameof(page)); return Task.FromResult(new MediaPage([], page, page)); }
    private static string[] Parts(string id)
    {
        var parts = id.Split('-');
        if (parts.Length != 3 || parts.Any(part => part.Length is 0 or > 16 || !part.All(char.IsAsciiDigit))) throw new InvalidDataException("LiveGz 比赛标识无效。");
        return parts;
    }
    private static Uri HttpUri(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 ? uri : throw new InvalidDataException("LiveGz HTTP(S) 地址无效。");
}
