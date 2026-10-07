using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using VodBox.Core;

namespace VodBox.Infrastructure;

/// <summary>
/// 兔小贝儿童启蒙爬虫（对应 TVBox 的兔小贝条目；饭太硬配置以 .js 入口出现，此处为原生 C# 实现）。
/// 无 JAR、无脚本执行：MIP 公开 API（list/mip-data JSON）+ HTML5 mip-search-video 直接媒体。
/// 旧实现通过真机 AOT 验证，此为其当前契约适配版。
/// </summary>
public sealed partial class TuxiaobeiSource : IResolvingContentSource, IDisposable
{
    private const string Home = "https://www.tuxiaobei.com/";
    private static readonly Uri HomeRoot = new(Home);
    private const string UserAgent = "Mozilla/5.0 (iPhone; CPU iPhone OS 16_0 like Mac OS X) AppleWebKit/605.1.15 Version/16.0 Mobile/15E148 Safari/604.1";

    private static readonly Category[] Categories =
        [new("2", "儿歌"), new("3", "故事"), new("4", "国学"), new("25", "启蒙")];

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _requests = new(4, 4);
    private bool _disposed;

    public string Key { get; }
    public string Name { get; }

    public TuxiaobeiSource(SourceInfo info) : this(info, CreateHttp()) { }

    internal TuxiaobeiSource(SourceInfo info, HttpClient http)
    {
        ArgumentNullException.ThrowIfNull(info);
        Key = info.Key;
        Name = info.Name;
        _http = http;
    }

    private static HttpClient CreateHttp() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.All,
    });

    // ---------- 分类 ----------

    public Task<IReadOnlyList<Category>> GetCategoriesAsync(CancellationToken ct = default)
    {
        Check(ct);
        return Task.FromResult<IReadOnlyList<Category>>(Categories);
    }

    // ---------- 列表（MIP JSON API，30/页）----------

    public async Task<MediaPage> GetHomeAsync(CancellationToken ct = default) =>
        await GetItemsAsync("2", 1, null, ct).ConfigureAwait(false);

    public async Task<MediaPage> GetItemsAsync(string categoryId, int page, IReadOnlyDictionary<string, string>? filters, CancellationToken ct = default)
    {
        string category = string.IsNullOrEmpty(categoryId) ? "2" : categoryId;
        if (!Categories.Any(x => x.Id == category)) throw new InvalidDataException("兔小贝分类不存在。");
        if (page < 1 || page > 1000) throw new InvalidDataException("兔小贝页码无效。");

        string text = (await ReadAsync($"list/mip-data?typeId={category}&page={page}&callback=", ct).ConfigureAwait(false)).Trim();
        // JSONP 包裹：( {...} );
        if (text.StartsWith('(') && text.EndsWith(");", StringComparison.Ordinal)) text = text[1..^2];
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        if (root.GetProperty("status").GetInt32() != 0) throw new InvalidDataException("兔小贝分类接口返回失败。");
        var items = root.GetProperty("data").GetProperty("items");
        if (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() > 1000)
            throw new InvalidDataException("兔小贝列表格式无效。");

        var results = items.EnumerateArray().Select(item => new MediaItem
        {
            Id = Id(item.GetProperty("video_id").ToString()),
            Title = Required(item, "name"),
            Poster = Url(Required(item, "image")),
            Remarks = Field(item, "duration_string"),
        }).ToArray();
        // 页码递增、总数未知：满 30 项则可能有下一页
        return new MediaPage(results, page, results.Length == 30 && page < 1000 ? page + 1 : page);
    }

    // ---------- 搜索（HTML 抓取）----------

    public async Task<MediaPage> SearchAsync(string query, int page, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 200) throw new InvalidDataException("搜索词为空或过长。");
        string html = await ReadAsync("search/index?key=" + Uri.EscapeDataString(query), ct).ConfigureAwait(false);
        if (!html.Contains("list-con", StringComparison.Ordinal)) throw new InvalidDataException("兔小贝搜索页面格式无效。");

        var items = new List<MediaItem>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match card in SearchCards().Matches(html))
        {
            string id = Id(card.Groups["id"].Value);
            var title = Titles().Match(card.Value);
            if (!title.Success) continue;
            var image = Images().Match(card.Value);
            var duration = Durations().Match(card.Value);
            if (ids.Add(id))
                items.Add(new MediaItem
                {
                    Id = id,
                    Title = Plain(title.Groups[1].Value),
                    Poster = image.Success ? Url(WebUtility.HtmlDecode(image.Groups[1].Value)) : null,
                    Remarks = duration.Success ? Plain(duration.Groups[1].Value) : null,
                });
        }
        return new MediaPage(items, 1, 1);
    }

    // ---------- 详情（mip-search-video 直接媒体）----------

    public async Task<MediaDetail> GetDetailAsync(string mediaId, CancellationToken ct = default) =>
        (await ReadDetailAsync(mediaId, ct).ConfigureAwait(false)).Detail;

    private async Task<(MediaDetail Detail, string Media)> ReadDetailAsync(string mediaId, CancellationToken ct)
    {
        mediaId = Id(mediaId);
        string html = await ReadAsync("play/" + mediaId, ct).ConfigureAwait(false);
        var player = Players().Matches(html).Cast<Match>().FirstOrDefault(x => Attribute(x.Groups[1].Value, "id") == "videoWrap")
            ?? throw new InvalidDataException("兔小贝视频不可用。");
        string media = Url(Attribute(player.Groups[1].Value, "video-src"));
        var heading = PageTitle().Match(html);
        if (!heading.Success) throw new InvalidDataException("兔小贝视频缺少标题。");
        string title = Plain(heading.Groups[1].Value).Split('-')[0];

        var detail = new MediaDetail
        {
            Item = new MediaItem { Id = mediaId, Title = title },
            Description = "",
            Lines = [new PlaybackLine("main", "兔小贝", [new Episode(mediaId, title)])],
        };
        return (detail, media);
    }

    // ---------- 播放地址即时解析 ----------

    public async Task<PlaybackRequest> ResolvePlaybackAsync(string mediaId, string episodeId, CancellationToken ct = default)
    {
        if (Id(mediaId) != Id(episodeId)) throw new InvalidDataException("兔小贝分集不存在。");
        var result = await ReadDetailAsync(mediaId, ct).ConfigureAwait(false);
        return new PlaybackRequest
        {
            Uri = result.Media,
            Title = result.Detail.Item.Title,
            SourceKey = Key,
            SourceName = Name,
            MediaId = mediaId,
            LineId = "main",
            EpisodeId = episodeId,
            Headers = new Dictionary<string, string>
            {
                ["User-Agent"] = UserAgent,
                ["Referer"] = Home + "play/" + mediaId,
            },
        };
    }

    // ---------- HTTP（≤3 次同源重定向）----------

    private async Task<string> ReadAsync(string path, CancellationToken ct)
    {
        Check(ct);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        await _requests.WaitAsync(deadline.Token).ConfigureAwait(false);
        try
        {
            var uri = new Uri(Home + path);
            for (int redirects = 0; redirects <= 3; redirects++)
            {
                Check(deadline.Token);
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
                if (response.RequestMessage?.RequestUri is { } final && final != uri)
                    throw new InvalidDataException("兔小贝页面发生隐式重定向。");
                if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
                {
                    if (redirects == 3 || response.Headers.Location is null)
                        throw new InvalidDataException("兔小贝页面重定向过多或缺少目标。");
                    var next = new Uri(uri, response.Headers.Location);
                    if (next.Scheme != uri.Scheme || next.Host != uri.Host || next.Port != uri.Port || next.UserInfo.Length != 0)
                        throw new InvalidDataException("兔小贝页面重定向离开原站。");
                    uri = next;
                    continue;
                }
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
                return TextEncoding.Decode(await BoundedContent.ReadAsync(stream, 2 * 1024 * 1024, deadline.Token).ConfigureAwait(false));
            }
            throw new InvalidDataException("兔小贝重定向失败。");
        }
        finally { _requests.Release(); }
    }

    // ---------- 解析辅助 ----------

    internal static string Id(string id) =>
        id.Length is > 0 and <= 12 && id.All(char.IsAsciiDigit) ? id
        : throw new InvalidDataException("兔小贝视频编号无效。");

    private static string Field(JsonElement item, string key) =>
        item.TryGetProperty(key, out var value) ? value.ToString() : "";

    private static string Required(JsonElement item, string key) =>
        Field(item, key) is { Length: > 0 } value ? value : throw new InvalidDataException($"兔小贝缺少 {key}。");

    internal static string Url(string value) =>
        Uri.TryCreate(HomeRoot, value, out var uri) && value.Length > 0 && uri.Scheme is "https" or "http" && uri.UserInfo.Length == 0
            ? uri.AbsoluteUri
            : throw new InvalidDataException("兔小贝媒体地址无效。");

    private static string Plain(string value) => WebUtility.HtmlDecode(Tags().Replace(value, "")).Trim();

    private static string Attribute(string text, string name) =>
        Attributes().Matches(text).Cast<Match>()
            .FirstOrDefault(x => x.Groups[1].Value.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } match
            ? WebUtility.HtmlDecode(match.Groups[2].Value)
            : "";

    private void Check(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();
    }

    public void Dispose() { _disposed = true; }

    [GeneratedRegex("<div\\b[^>]*class=[\"']items[\"'][^>]*>\\s*<div\\b[^>]*>\\s*<a\\b[^>]*href=[\"']/play/(?<id>[0-9]+)[\"'].*?<!--items end-->", RegexOptions.Singleline | RegexOptions.IgnoreCase, 1000)]
    private static partial Regex SearchCards();
    [GeneratedRegex("<p\\b[^>]*class=[\"']title[\"'][^>]*>(.*?)</p>", RegexOptions.Singleline | RegexOptions.IgnoreCase, 1000)]
    private static partial Regex Titles();
    [GeneratedRegex("<mip-img\\b[^>]*src=[\"']([^\"']+)[\"']", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex Images();
    [GeneratedRegex("<span\\b[^>]*class=[\"']time[\"'][^>]*>(.*?)</span>", RegexOptions.Singleline | RegexOptions.IgnoreCase, 1000)]
    private static partial Regex Durations();
    [GeneratedRegex("<mip-search-video\\b([^>]*)>", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex Players();
    [GeneratedRegex("([a-zA-Z-]+)\\s*=\\s*[\"']([^\"']*)[\"']", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Attributes();
    [GeneratedRegex("<title>(.*?)</title>", RegexOptions.Singleline | RegexOptions.IgnoreCase, 1000)]
    private static partial Regex PageTitle();
    [GeneratedRegex("<[^>]*>", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Tags();
}
