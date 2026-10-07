using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using VodBox.Core;

namespace VodBox.Infrastructure;

/// <summary>Public emergency lesson directory and HTML5 video; independently implemented in C#.</summary>
public sealed partial class FirstAidProvider(SourceDefinition source, HttpClient http) : IContentProvider
{
    private const string Home = "https://m.youlai.cn/";
    private static readonly string[] Names = ["急救技能", "家庭生活", "急危重症", "常见损伤", "动物致伤", "海洋急救", "中毒急救", "意外事故"];
    private readonly SemaphoreSlim _requests = new(4, 4);
    private bool _disposed;
    public string SourceId => source.Id;
    public Task<IReadOnlyList<Category>> GetCategoriesAsync(CancellationToken token)
    { Check(token); return Task.FromResult<IReadOnlyList<Category>>(Names.Select((name, i) => new Category(i.ToString(CultureInfo.InvariantCulture), name)).ToArray()); }
    public async Task<MediaPage> GetItemsAsync(string? categoryId, string? cursor, CancellationToken token)
    {
        Check(token);
        if (cursor is not null) throw new InvalidDataException("急救教学目录没有下一页。");
        int index = categoryId is null ? 0 : int.TryParse(categoryId, NumberStyles.None, CultureInfo.InvariantCulture, out int i) && i >= 0 && i < Names.Length ? i : throw new InvalidDataException("急救教学分类不存在。");
        var sections = await DirectoryAsync(token); return new(sections[index]);
    }
    public async Task<MediaPage> SearchAsync(string query, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 200) throw new InvalidDataException("搜索词为空或过长。");
        var sections = await DirectoryAsync(token);
        return new(sections.SelectMany(x => x).Where(x => x.Title.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase)).DistinctBy(x => x.Id).ToArray());
    }
    private async Task<IReadOnlyList<MediaItem>[]> DirectoryAsync(CancellationToken token)
    {
        string html = await ReadAsync("jijiu", token); var sections = Sections().Matches(html);
        if (sections.Count != Names.Length) throw new InvalidDataException("急救教学目录结构改变。");
        var result = new IReadOnlyList<MediaItem>[Names.Length];
        for (int i = 0; i < sections.Count; i++)
        {
            int start = sections[i].Index + sections[i].Length, end = i + 1 < sections.Count ? sections[i + 1].Index : html.Length;
            string section = html[start..end]; var items = new List<MediaItem>();
            foreach (Match row in Rows().Matches(section))
            {
                var link = Anchors().Match(row.Groups[1].Value); if (!link.Success) throw new InvalidDataException("急救教学课程缺少链接。");
                string id = Lesson(Attribute(link.Groups["attrs"].Value, "href")); string title = Plain(link.Groups["body"].Value);
                if (title.Length == 0) throw new InvalidDataException("急救教学课程缺少标题。");
                items.Add(new(id, title));
            }
            if (items.Count is < 1 or > 1000) throw new InvalidDataException("急救教学分类列表无效。");
            result[i] = items;
        }
        return result;
    }
    public async Task<MediaDetail> GetDetailAsync(string mediaId, CancellationToken token) => (await DetailAsync(mediaId, token)).Detail;
    private async Task<(MediaDetail Detail, string Url)> DetailAsync(string mediaId, CancellationToken token)
    {
        string html = await ReadAsync(Lesson(mediaId).TrimStart('/'), token);
        var video = Videos().Matches(html).Cast<Match>().FirstOrDefault(x => Attribute(x.Groups["attrs"].Value, "id") == "video") ?? throw new InvalidDataException("该教学课程没有公开视频。");
        string media = Attribute(video.Groups["attrs"].Value, "src");
        if (media.Length == 0 && Sources().Match(video.Groups["body"].Value) is { Success: true } tag) media = Attribute(tag.Groups[1].Value, "src");
        media = Url(media);
        var heading = Heading().Match(html); if (!heading.Success) throw new InvalidDataException("教学课程缺少标题。");
        string title = Plain(heading.Groups[1].Value);
        string poster = Attribute(video.Groups["attrs"].Value, "poster");
        return (new(new(mediaId, title, poster.Length == 0 ? null : Url(poster)), "", [new("main", "有来医生", [new(mediaId, title)])]), media);
    }
    public async Task<PlaybackRequest> ResolvePlaybackAsync(string mediaId, string episodeId, CancellationToken token)
    {
        if (Lesson(mediaId) != Lesson(episodeId)) throw new InvalidDataException("教学课程分集不匹配。");
        var result = await DetailAsync(mediaId, token);
        return new() { Uri = result.Url, Title = result.Detail.Item.Title, Poster = result.Detail.Item.Poster, SourceId = SourceId, MediaId = mediaId, EpisodeId = episodeId,
            Headers = new() { ["User-Agent"] = "Mozilla/5.0", ["Referer"] = new Uri(new Uri(Home), mediaId).AbsoluteUri } };
    }
    private async Task<string> ReadAsync(string path, CancellationToken token)
    {
        Check(token); using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(20)); await _requests.WaitAsync(deadline.Token);
        try
        {
            Check(deadline.Token); using var request = new HttpRequestMessage(HttpMethod.Get, Home + path); request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token); response.EnsureSuccessStatusCode();
            if (response.RequestMessage?.RequestUri is { } final && final != request.RequestUri) throw new InvalidDataException("教学页面发生重定向。");
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token); return TextEncoding.Decode(await BoundedContent.ReadAsync(stream, 2 * 1024 * 1024, deadline.Token));
        }
        finally { _requests.Release(); }
    }
    private static string Lesson(string id) => LessonPath().IsMatch(id) ? id : throw new InvalidDataException("教学课程编号不是站内课程路径。");
    private static string Url(string value) => value.Length > 0 && Uri.TryCreate(new Uri(Home), value, out var uri) && uri.Scheme is "https" or "http" && uri.UserInfo.Length == 0 ? uri.AbsoluteUri : throw new InvalidDataException("教学媒体地址无效。");
    private static string Plain(string value) => WebUtility.HtmlDecode(Tags().Replace(value, "")).Trim();
    private static string Attribute(string text, string name) => Attributes().Matches(text).Cast<Match>().FirstOrDefault(x => x.Groups[1].Value.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } match ? WebUtility.HtmlDecode(match.Groups[2].Value) : "";
    private void Check(CancellationToken token) { ObjectDisposedException.ThrowIf(_disposed, this); token.ThrowIfCancellationRequested(); }
    public ValueTask DisposeAsync() { _disposed = true; return ValueTask.CompletedTask; }
    [GeneratedRegex("\\A/jijiu/article/[A-Za-z0-9]+\\.html\\z", RegexOptions.CultureInvariant, 1000)] private static partial Regex LessonPath();
    [GeneratedRegex("<div\\b[^>]*class=[\"']jj-title-li[\"'][^>]*>", RegexOptions.IgnoreCase, 1000)] private static partial Regex Sections();
    [GeneratedRegex("<li\\b[^>]*class=[\"']list-br3[\"'][^>]*>(.*?)</li>", RegexOptions.Singleline | RegexOptions.IgnoreCase, 1000)] private static partial Regex Rows();
    [GeneratedRegex("<a\\b(?<attrs>[^>]*)>(?<body>.*?)</a>", RegexOptions.Singleline | RegexOptions.IgnoreCase, 1000)] private static partial Regex Anchors();
    [GeneratedRegex("<video\\b(?<attrs>[^>]*)>(?<body>.*?)</video>", RegexOptions.Singleline | RegexOptions.IgnoreCase, 1000)] private static partial Regex Videos();
    [GeneratedRegex("<source\\b([^>]*)>", RegexOptions.IgnoreCase, 1000)] private static partial Regex Sources();
    [GeneratedRegex("<(?:h[1-6]|div)\\b[^>]*class=[\"'][^\"']*\\bvideo-title\\b[^\"']*[\"'][^>]*>(.*?)</(?:h[1-6]|div)>", RegexOptions.Singleline | RegexOptions.IgnoreCase, 1000)] private static partial Regex Heading();
    [GeneratedRegex("([a-zA-Z-]+)\\s*=\\s*[\"']([^\"']*)[\"']", RegexOptions.CultureInvariant, 1000)] private static partial Regex Attributes();
    [GeneratedRegex("<[^>]*>", RegexOptions.CultureInvariant, 1000)] private static partial Regex Tags();
}
