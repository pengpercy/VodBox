using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using VodBox.Core;

namespace VodBox.Infrastructure;

/// <summary>6huo public trailer catalogue and static ckplayer media declarations.</summary>
public sealed partial class TrailerProvider(SourceDefinition source, HttpClient http) : IContentProvider
{
    private const string Home = "https://www.6huo.com/";
    private readonly SemaphoreSlim _requests = new(4, 4);
    private bool _disposed;
    public string SourceId => source.Id;
    public Task<IReadOnlyList<Category>> GetCategoriesAsync(CancellationToken token) { Check(token); return Task.FromResult<IReadOnlyList<Category>>([new("trailers", "预告片世界")]); }
    public async Task<MediaPage> GetItemsAsync(string? categoryId, string? cursor, CancellationToken token)
    {
        if (categoryId is not (null or "trailers")) throw new InvalidDataException("预告片分类不存在。");
        int page = cursor is null ? 1 : int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out int p) && p is >= 1 and <= 1000 ? p : throw new InvalidDataException("预告片页码无效。");
        string html = await ReadAsync($"movlist/____{page}", token); var items = Cards(html);
        bool more = page < 1000 && Anchors().Matches(html).Cast<Match>().Any(x => Attribute(x.Groups["attrs"].Value, "href") == $"/movlist/____{page + 1}" && Plain(x.Groups["body"].Value) == "下一页");
        return new(items, more ? (page + 1).ToString(CultureInfo.InvariantCulture) : null);
    }
    public async Task<MediaPage> SearchAsync(string query, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 200) throw new InvalidDataException("搜索词为空或过长。");
        return new(Cards(await ReadAsync("?keyword=" + Uri.EscapeDataString(query) + "&view=search", token)));
    }
    private static IReadOnlyList<MediaItem> Cards(string html)
    {
        var list = MovieList().Match(html); if (!list.Success) throw new InvalidDataException("预告片目录结构改变。");
        var items = new List<MediaItem>(); var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match card in Anchors().Matches(list.Groups[1].Value))
        {
            string id = Attribute(card.Groups["attrs"].Value, "href"); if (!MoviePath().IsMatch(id)) continue;
            var title = ItemTitle().Match(card.Groups["body"].Value); if (!title.Success) throw new InvalidDataException("预告片缺少标题。");
            var image = Images().Match(card.Groups["body"].Value); if (!image.Success) throw new InvalidDataException("预告片缺少海报。");
            if (ids.Add(id)) items.Add(new(id, Plain(title.Groups[1].Value), Url(Attribute(image.Groups[1].Value, "src"))));
        }
        if (items.Count > 1000) throw new InvalidDataException("预告片目录过长。");
        return items;
    }
    public async Task<MediaDetail> GetDetailAsync(string mediaId, CancellationToken token)
    {
        if (!MoviePath().IsMatch(mediaId)) throw new InvalidDataException("影片编号无效。");
        string html = await ReadAsync(mediaId.TrimStart('/'), token); var heading = Heading().Match(html);
        if (!heading.Success) throw new InvalidDataException("影片详情缺少标题。");
        var tables = Tables().Matches(html); var lines = new List<PlaybackLine>(); int index = 0;
        foreach (Match table in tables)
        {
            var episodes = new List<Episode>(); var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match link in Anchors().Matches(table.Groups[1].Value))
            {
                if (!Attribute(link.Groups["attrs"].Value, "class").Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("tlist-bbs-tdtitle")) continue;
                string id = Attribute(link.Groups["attrs"].Value, "href"); if (!ShowPath().IsMatch(id)) throw new InvalidDataException("预告视频编号无效。");
                if (ids.Add(id)) episodes.Add(new(id, Plain(link.Groups["body"].Value)));
            }
            if (episodes.Count == 0) continue;
            if (episodes.Count > 1000) throw new InvalidDataException("影片预告视频过多。");
            var name = TableTitle().Match(table.Value); lines.Add(new((index++).ToString(CultureInfo.InvariantCulture), name.Success ? Plain(name.Groups[1].Value) : "预告片", episodes));
        }
        if (lines.Count == 0) throw new InvalidDataException("该影片暂无可播放预告。");
        return new(new(mediaId, Plain(heading.Groups[1].Value)), "", lines);
    }
    public async Task<PlaybackRequest> ResolvePlaybackAsync(string mediaId, string episodeId, CancellationToken token)
    {
        if (!ShowPath().IsMatch(episodeId)) throw new InvalidDataException("预告视频编号无效。");
        var detail = await GetDetailAsync(mediaId, token);
        var episode = detail.PlaybackLines.SelectMany(x => x.Episodes).FirstOrDefault(x => x.Id == episodeId) ?? throw new InvalidDataException("预告视频不属于当前影片。");
        string html = await ReadAsync(episodeId.TrimStart('/'), token); var declaration = PlayerObject().Match(html);
        if (!declaration.Success) throw new NotSupportedException("该预告尚无可解析的公开 ckplayer 媒体声明。");
        var media = Video().Match(declaration.Groups[1].Value); if (!media.Success) throw new InvalidDataException("预告播放地址缺失。");
        return new() { Uri = Url(WebUtility.HtmlDecode(media.Groups[1].Value)), SourceId = SourceId, MediaId = mediaId, EpisodeId = episodeId, Title = detail.Item.Title + " · " + episode.Title,
            Headers = new() { ["User-Agent"] = "Mozilla/5.0" } };
    }
    private async Task<string> ReadAsync(string path, CancellationToken token)
    {
        Check(token); using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(20)); await _requests.WaitAsync(deadline.Token);
        try
        {
            Check(deadline.Token); using var request = new HttpRequestMessage(HttpMethod.Get, Home + path); request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token); response.EnsureSuccessStatusCode();
            if (response.RequestMessage?.RequestUri is { } final && final != request.RequestUri) throw new InvalidDataException("预告片页面发生重定向。");
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token); return TextEncoding.Decode(await BoundedContent.ReadAsync(stream, 2 * 1024 * 1024, deadline.Token));
        }
        finally { _requests.Release(); }
    }
    private static string Url(string value) => value.Length > 0 && Uri.TryCreate(new Uri(Home), value, out var uri) && uri.Scheme is "https" or "http" && uri.UserInfo.Length == 0 ? uri.AbsoluteUri : throw new InvalidDataException("预告媒体地址无效。");
    private static string Plain(string value) => WebUtility.HtmlDecode(Tags().Replace(value, "")).Trim();
    private static string Attribute(string text, string name) => Attributes().Matches(text).Cast<Match>().FirstOrDefault(x => x.Groups[1].Value.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } match ? WebUtility.HtmlDecode(match.Groups[2].Value) : "";
    private void Check(CancellationToken token) { ObjectDisposedException.ThrowIf(_disposed, this); token.ThrowIfCancellationRequested(); }
    public ValueTask DisposeAsync() { _disposed = true; return ValueTask.CompletedTask; }
    [GeneratedRegex("\\A/movie/[0-9]+\\z", RegexOptions.CultureInvariant, 1000)] private static partial Regex MoviePath();
    [GeneratedRegex("\\A/show/[0-9]+\\z", RegexOptions.CultureInvariant, 1000)] private static partial Regex ShowPath();
    [GeneratedRegex("<div\\b[^>]*class=[\"']movlist[\"'][^>]*>\\s*<ul[^>]*>(.*?)</ul>", RegexOptions.Singleline | RegexOptions.IgnoreCase, 1000)] private static partial Regex MovieList();
    [GeneratedRegex("<a\\b(?<attrs>[^>]*)>(?<body>.*?)</a>", RegexOptions.Singleline | RegexOptions.IgnoreCase, 1000)] private static partial Regex Anchors();
    [GeneratedRegex("<span\\b[^>]*class=[\"']item-title[\"'][^>]*>(.*?)</span>", RegexOptions.Singleline | RegexOptions.IgnoreCase, 1000)] private static partial Regex ItemTitle();
    [GeneratedRegex("<img\\b([^>]*)>", RegexOptions.IgnoreCase, 1000)] private static partial Regex Images();
    [GeneratedRegex("<h1\\b[^>]*class=[\"']movie-name[\"'][^>]*>(.*?)</h1>", RegexOptions.Singleline | RegexOptions.IgnoreCase, 1000)] private static partial Regex Heading();
    [GeneratedRegex("<table\\b[^>]*class=[\"']tlist[\"'][^>]*>(.*?)</table>", RegexOptions.Singleline | RegexOptions.IgnoreCase, 1000)] private static partial Regex Tables();
    [GeneratedRegex("<th\\b[^>]*>(.*?)</th>", RegexOptions.Singleline | RegexOptions.IgnoreCase, 1000)] private static partial Regex TableTitle();
    [GeneratedRegex("\\bvar\\s+videoObject\\s*=\\s*\\{([^}]+)\\}", RegexOptions.Singleline | RegexOptions.CultureInvariant, 1000)] private static partial Regex PlayerObject();
    [GeneratedRegex("\\bvideo\\s*:\\s*['\"]([^'\"]+)['\"]", RegexOptions.CultureInvariant, 1000)] private static partial Regex Video();
    [GeneratedRegex("([a-zA-Z-]+)\\s*=\\s*[\"']([^\"']*)[\"']", RegexOptions.CultureInvariant, 1000)] private static partial Regex Attributes();
    [GeneratedRegex("<[^>]*>", RegexOptions.CultureInvariant, 1000)] private static partial Regex Tags();
}
