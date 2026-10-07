using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using VodBox.Core;

namespace VodBox.Infrastructure;

/// <summary>
/// 6huo 荐影预告片爬虫（对应 TVBox <c>csp_YGP</c> / <c>csp_YGPGuard</c>）。
/// 无 JAR、无脚本执行：目录分页（movlist）+ 详情多线路 + 静态 ckplayer 媒体声明
/// （只解析静态字符串，不执行页面脚本）。旧实现通过真机 AOT 验证，此为其当前契约适配版。
/// 注意：预告片是正片推广素材，非完整正片。
/// </summary>
public sealed partial class TrailerSource : IResolvingContentSource, IDisposable
{
    private const string Home = "https://www.6huo.com/";
    private static readonly Uri HomeRoot = new(Home);
    private const string UserAgent = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36";
    private const string CategoryId = "trailers";

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _requests = new(4, 4);
    private bool _disposed;

    public string Key { get; }
    public string Name { get; }

    public TrailerSource(SourceInfo info) : this(info, CreateHttp()) { }

    internal TrailerSource(SourceInfo info, HttpClient http)
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

    // ---------- 分类（单一：预告片世界）----------

    public Task<IReadOnlyList<Category>> GetCategoriesAsync(CancellationToken ct = default)
    {
        Check(ct);
        return Task.FromResult<IReadOnlyList<Category>>([new Category(CategoryId, "预告片世界")]);
    }

    // ---------- 目录分页 ----------

    public async Task<MediaPage> GetHomeAsync(CancellationToken ct = default) =>
        await GetItemsAsync(CategoryId, 1, null, ct).ConfigureAwait(false);

    public async Task<MediaPage> GetItemsAsync(string categoryId, int page, IReadOnlyDictionary<string, string>? filters, CancellationToken ct = default)
    {
        if (categoryId is not (null or "" or CategoryId)) throw new InvalidDataException("预告片分类不存在。");
        if (page < 1 || page > 1000) throw new InvalidDataException("预告片页码无效。");
        string html = await ReadAsync($"movlist/____{page}", ct).ConfigureAwait(false);
        var items = Cards(html);
        bool more = page < 1000 && Anchors().Matches(html).Cast<Match>()
            .Any(x => Attribute(x.Groups["attrs"].Value, "href") == $"/movlist/____{page + 1}"
                   && Plain(x.Groups["body"].Value) == "下一页");
        // 页码递增、总数未知：有下一页链接则给下一页，末页保持当前页
        return new MediaPage(items, page, more ? page + 1 : page);
    }

    // ---------- 搜索 ----------

    public async Task<MediaPage> SearchAsync(string query, int page, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 200)
            throw new InvalidDataException("搜索词为空或过长。");
        var html = await ReadAsync("?keyword=" + Uri.EscapeDataString(query) + "&view=search", ct).ConfigureAwait(false);
        return new MediaPage(Cards(html), 1, 1);
    }

    private static IReadOnlyList<MediaItem> Cards(string html)
    {
        var list = MovieList().Match(html);
        if (!list.Success) throw new InvalidDataException("预告片目录结构改变。");
        var items = new List<MediaItem>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match card in Anchors().Matches(list.Groups[1].Value))
        {
            string id = Attribute(card.Groups["attrs"].Value, "href");
            if (!MoviePath().IsMatch(id)) continue;
            var title = ItemTitle().Match(card.Groups["body"].Value);
            if (!title.Success) throw new InvalidDataException("预告片缺少标题。");
            var image = Images().Match(card.Groups["body"].Value);
            if (!image.Success) throw new InvalidDataException("预告片缺少海报。");
            if (ids.Add(id))
                items.Add(new MediaItem
                {
                    Id = id,
                    Title = Plain(title.Groups[1].Value),
                    Poster = Url(Attribute(image.Groups[1].Value, "src")),
                });
        }
        if (items.Count > 1000) throw new InvalidDataException("预告片目录过长。");
        return items;
    }

    // ---------- 详情（多线路表 + 分集）----------

    public async Task<MediaDetail> GetDetailAsync(string mediaId, CancellationToken ct = default)
    {
        if (!MoviePath().IsMatch(mediaId)) throw new InvalidDataException("影片编号无效。");
        string html = await ReadAsync(mediaId.TrimStart('/'), ct).ConfigureAwait(false);
        var heading = Heading().Match(html);
        if (!heading.Success) throw new InvalidDataException("影片详情缺少标题。");

        var lines = new List<PlaybackLine>();
        int index = 0;
        foreach (Match table in Tables().Matches(html))
        {
            var episodes = new List<Episode>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match link in Anchors().Matches(table.Groups[1].Value))
            {
                if (!Attribute(link.Groups["attrs"].Value, "class")
                        .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                        .Contains("tlist-bbs-tdtitle")) continue;
                string id = Attribute(link.Groups["attrs"].Value, "href");
                if (!ShowPath().IsMatch(id)) throw new InvalidDataException("预告视频编号无效。");
                if (ids.Add(id)) episodes.Add(new Episode(id, Plain(link.Groups["body"].Value)));
            }
            if (episodes.Count == 0) continue;
            if (episodes.Count > 1000) throw new InvalidDataException("影片预告视频过多。");
            var name = TableTitle().Match(table.Value);
            lines.Add(new PlaybackLine(
                (index++).ToString(CultureInfo.InvariantCulture),
                name.Success ? Plain(name.Groups[1].Value) : "预告片",
                episodes));
        }
        if (lines.Count == 0) throw new InvalidDataException("该影片暂无可播放预告。");

        return new MediaDetail
        {
            Item = new MediaItem { Id = mediaId, Title = Plain(heading.Groups[1].Value) },
            Description = "",
            Lines = lines,
        };
    }

    // ---------- 播放地址即时解析（ckplayer videoObject 静态声明）----------

    public async Task<PlaybackRequest> ResolvePlaybackAsync(string mediaId, string episodeId, CancellationToken ct = default)
    {
        if (!ShowPath().IsMatch(episodeId)) throw new InvalidDataException("预告视频编号无效。");
        var detail = await GetDetailAsync(mediaId, ct).ConfigureAwait(false);
        var episode = detail.Lines.SelectMany(x => x.Episodes).FirstOrDefault(x => x.Id == episodeId)
            ?? throw new InvalidDataException("预告视频不属于当前影片。");

        string html = await ReadAsync(episodeId.TrimStart('/'), ct).ConfigureAwait(false);
        var declaration = PlayerObject().Match(html);
        if (!declaration.Success)
            throw new NotSupportedException("该预告尚无可解析的公开 ckplayer 媒体声明。");
        var media = Video().Match(declaration.Groups[1].Value);
        if (!media.Success) throw new InvalidDataException("预告播放地址缺失。");

        return new PlaybackRequest
        {
            Uri = Url(WebUtility.HtmlDecode(media.Groups[1].Value)),
            Title = detail.Item.Title + " · " + episode.Title,
            SourceKey = Key,
            SourceName = Name,
            MediaId = mediaId,
            EpisodeId = episodeId,
            Headers = new Dictionary<string, string> { ["User-Agent"] = UserAgent },
        };
    }

    // ---------- HTTP ----------

    private async Task<string> ReadAsync(string path, CancellationToken ct)
    {
        Check(ct);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        await _requests.WaitAsync(deadline.Token).ConfigureAwait(false);
        try
        {
            Check(deadline.Token);
            using var request = new HttpRequestMessage(HttpMethod.Get, Home + path);
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.RequestMessage?.RequestUri is { } final && final != request.RequestUri)
                throw new InvalidDataException("预告片页面发生重定向。");
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            return TextEncoding.Decode(await BoundedContent.ReadAsync(stream, 2 * 1024 * 1024, deadline.Token).ConfigureAwait(false));
        }
        finally { _requests.Release(); }
    }

    // ---------- 解析辅助 ----------

    internal static string Url(string value) =>
        value.Length > 0 && Uri.TryCreate(HomeRoot, value, out var uri) && uri.Scheme is "https" or "http" && uri.UserInfo.Length == 0
            ? uri.AbsoluteUri
            : throw new InvalidDataException("预告媒体地址无效。");

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

    [GeneratedRegex("\\A/movie/[0-9]+\\z", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex MoviePath();
    [GeneratedRegex("\\A/show/[0-9]+\\z", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex ShowPath();
    [GeneratedRegex("<div\\b[^>]*class=[\"']movlist[\"'][^>]*>\\s*<ul[^>]*>(.*?)</ul>", RegexOptions.Singleline | RegexOptions.IgnoreCase, 1000)]
    private static partial Regex MovieList();
    [GeneratedRegex("<a\\b(?<attrs>[^>]*)>(?<body>.*?)</a>", RegexOptions.Singleline | RegexOptions.IgnoreCase, 1000)]
    private static partial Regex Anchors();
    [GeneratedRegex("<span\\b[^>]*class=[\"']item-title[\"'][^>]*>(.*?)</span>", RegexOptions.Singleline | RegexOptions.IgnoreCase, 1000)]
    private static partial Regex ItemTitle();
    [GeneratedRegex("<img\\b([^>]*)>", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex Images();
    [GeneratedRegex("<h1\\b[^>]*class=[\"']movie-name[\"'][^>]*>(.*?)</h1>", RegexOptions.Singleline | RegexOptions.IgnoreCase, 1000)]
    private static partial Regex Heading();
    [GeneratedRegex("<table\\b[^>]*class=[\"']tlist[\"'][^>]*>(.*?)</table>", RegexOptions.Singleline | RegexOptions.IgnoreCase, 1000)]
    private static partial Regex Tables();
    [GeneratedRegex("<th\\b[^>]*>(.*?)</th>", RegexOptions.Singleline | RegexOptions.IgnoreCase, 1000)]
    private static partial Regex TableTitle();
    [GeneratedRegex("\\bvar\\s+videoObject\\s*=\\s*\\{([^}]+)\\}", RegexOptions.Singleline | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex PlayerObject();
    [GeneratedRegex("\\bvideo\\s*:\\s*['\"]([^'\"]+)['\"]", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Video();
    [GeneratedRegex("([a-zA-Z-]+)\\s*=\\s*[\"']([^\"']*)[\"']", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Attributes();
    [GeneratedRegex("<[^>]*>", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Tags();
}
