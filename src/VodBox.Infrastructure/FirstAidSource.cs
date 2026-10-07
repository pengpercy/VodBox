using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using VodBox.Core;

namespace VodBox.Infrastructure;

/// <summary>
/// 有来医生急救教学爬虫（对应 TVBox <c>csp_FirstAid</c> / <c>csp_FirstAidGuard</c>）。
/// 无 JAR、无脚本执行：HTML 抓取 m.youlai.cn/jijiu 目录 + HTML5 video 直接媒体。
/// 旧实现已用独立 C# 正则（源码生成）完成并通过真机 AOT 验证，此为其当前契约适配版。
/// </summary>
public sealed partial class FirstAidSource : IResolvingContentSource, IDisposable
{
    private const string Home = "https://m.youlai.cn/";
    private static readonly Uri HomeRoot = new(Home);
    private const string UserAgent = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36";

    private static readonly string[] CategoryNames =
        ["急救技能", "家庭生活", "急危重症", "常见损伤", "动物致伤", "海洋急救", "中毒急救", "意外事故"];

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _requests = new(4, 4);
    private readonly SemaphoreSlim _directoryGate = new(1, 1);
    private IReadOnlyList<MediaItem>[]? _directory;
    private bool _disposed;

    public string Key { get; }
    public string Name { get; }

    public FirstAidSource(SourceInfo info) : this(info, CreateHttp()) { }

    internal FirstAidSource(SourceInfo info, HttpClient http)
    {
        ArgumentNullException.ThrowIfNull(info);
        Key = info.Key;
        Name = info.Name;
        _http = http;
    }

    private static HttpClient CreateHttp() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,   // 教学页面不应重定向；显式校验
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.All,
    });

    // ---------- 分类 ----------

    public Task<IReadOnlyList<Category>> GetCategoriesAsync(CancellationToken ct = default)
    {
        Check(ct);
        return Task.FromResult<IReadOnlyList<Category>>(
            CategoryNames.Select((name, i) => new Category(i.ToString(CultureInfo.InvariantCulture), name)).ToArray());
    }

    // ---------- 目录（8 个分类各一页，无分页游标）----------

    public async Task<MediaPage> GetHomeAsync(CancellationToken ct = default) =>
        await GetItemsAsync("0", 1, null, ct).ConfigureAwait(false);

    public async Task<MediaPage> GetItemsAsync(string categoryId, int page, IReadOnlyDictionary<string, string>? filters, CancellationToken ct = default)
    {
        Check(ct);
        if (page is not (0 or 1)) throw new InvalidDataException("急救教学目录没有下一页。");
        int index = ParseCategory(categoryId);
        var directory = await DirectoryAsync(ct).ConfigureAwait(false);
        return new MediaPage(directory[index], 1, 1);
    }

    private static int ParseCategory(string? categoryId) =>
        categoryId is null ? 0
        : int.TryParse(categoryId, NumberStyles.None, CultureInfo.InvariantCulture, out int i) && i >= 0 && i < CategoryNames.Length
            ? i
            : throw new InvalidDataException("急救教学分类不存在。");

    /// <summary>抓取整目录一次并按 8 个分类切片，结果缓存（目录基本不变化）。</summary>
    private async Task<IReadOnlyList<MediaItem>[]> DirectoryAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref _directory) is { } cached) return cached;
        await _directoryGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _directory) is { } again) return again;
            var result = await FetchDirectoryAsync(ct).ConfigureAwait(false);
            Volatile.Write(ref _directory, result);
            return result;
        }
        finally { _directoryGate.Release(); }
    }

    private async Task<IReadOnlyList<MediaItem>[]> FetchDirectoryAsync(CancellationToken ct)
    {
        string html = await ReadAsync("jijiu", ct).ConfigureAwait(false);
        var sections = Sections().Matches(html);
        if (sections.Count != CategoryNames.Length)
            throw new InvalidDataException($"急救教学目录结构改变（期望 {CategoryNames.Length} 段，实际 {sections.Count} 段）。");

        var result = new IReadOnlyList<MediaItem>[CategoryNames.Length];
        for (int i = 0; i < sections.Count; i++)
        {
            int start = sections[i].Index + sections[i].Length;
            int end = i + 1 < sections.Count ? sections[i + 1].Index : html.Length;
            string section = html[start..end];
            var items = new List<MediaItem>();
            foreach (Match row in Rows().Matches(section))
            {
                var link = Anchors().Match(row.Groups[1].Value);
                if (!link.Success) throw new InvalidDataException("急救教学课程缺少链接。");
                string id = Lesson(Attribute(link.Groups["attrs"].Value, "href"));
                string title = Plain(link.Groups["body"].Value);
                if (title.Length == 0) throw new InvalidDataException("急救教学课程缺少标题。");
                items.Add(new MediaItem { Id = id, Title = title });
            }
            if (items.Count is < 1 or > 1000) throw new InvalidDataException("急救教学分类列表无效。");
            result[i] = items;
        }
        return result;
    }

    // ---------- 搜索（目录内标题包含，大小写不敏感）----------

    public async Task<MediaPage> SearchAsync(string query, int page, CancellationToken ct = default)
    {
        Check(ct);
        if (string.IsNullOrWhiteSpace(query) || query.Length > 200)
            throw new InvalidDataException("搜索词为空或过长。");
        var directory = await DirectoryAsync(ct).ConfigureAwait(false);
        var hits = directory.SelectMany(x => x)
            .Where(x => x.Title.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase))
            .DistinctBy(x => x.Id)
            .ToArray();
        return new MediaPage(hits, 1, 1);
    }

    // ---------- 详情（HTML5 video / source 直接媒体）----------

    public async Task<MediaDetail> GetDetailAsync(string mediaId, CancellationToken ct = default) =>
        (await DetailAsync(mediaId, ct).ConfigureAwait(false)).Detail;

    private async Task<(MediaDetail Detail, string MediaUrl)> DetailAsync(string mediaId, CancellationToken ct)
    {
        string html = await ReadAsync(Lesson(mediaId).TrimStart('/'), ct).ConfigureAwait(false);
        var video = Videos().Matches(html).Cast<Match>().FirstOrDefault(x => Attribute(x.Groups["attrs"].Value, "id") == "video")
            ?? throw new InvalidDataException("该教学课程没有公开视频。");

        string media = Attribute(video.Groups["attrs"].Value, "src");
        if (media.Length == 0 && Sources().Match(video.Groups["body"].Value) is { Success: true } tag)
            media = Attribute(tag.Groups[1].Value, "src");
        media = Url(media);

        var heading = Heading().Match(html);
        if (!heading.Success) throw new InvalidDataException("教学课程缺少标题。");
        string title = Plain(heading.Groups[1].Value);
        string poster = Attribute(video.Groups["attrs"].Value, "poster");

        var detail = new MediaDetail
        {
            Item = new MediaItem { Id = mediaId, Title = title, Poster = poster.Length == 0 ? null : Url(poster) },
            Description = "",
            Lines = [new PlaybackLine("main", "有来医生", [new Episode(mediaId, title)])],
        };
        return (detail, media);
    }

    // ---------- 播放地址即时解析 ----------

    public async Task<PlaybackRequest> ResolvePlaybackAsync(string mediaId, string episodeId, CancellationToken ct = default)
    {
        if (Lesson(mediaId) != Lesson(episodeId)) throw new InvalidDataException("教学课程分集不匹配。");
        var result = await DetailAsync(mediaId, ct).ConfigureAwait(false);
        return new PlaybackRequest
        {
            Uri = result.MediaUrl,
            Title = result.Detail.Item.Title,
            Poster = result.Detail.Item.Poster,
            SourceKey = Key,
            SourceName = Name,
            MediaId = mediaId,
            LineId = "main",
            EpisodeId = episodeId,
            Headers = new Dictionary<string, string>
            {
                ["User-Agent"] = UserAgent,
                ["Referer"] = new Uri(HomeRoot, mediaId).AbsoluteUri,
            },
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
                throw new InvalidDataException("教学页面发生重定向。");
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            return TextEncoding.Decode(await BoundedContent.ReadAsync(stream, 2 * 1024 * 1024, deadline.Token).ConfigureAwait(false));
        }
        finally { _requests.Release(); }
    }

    // ---------- 解析辅助 ----------

    /// <summary>校验课程标识是站内课程路径（/jijiu/article/{id}.html）。</summary>
    internal static string Lesson(string id) => LessonPath().IsMatch(id) ? id
        : throw new InvalidDataException("教学课程编号不是站内课程路径。");

    /// <summary>把可能为相对路径的地址解析为合法 http(s) 绝对地址（拒绝带 userinfo）。</summary>
    internal static string Url(string value) =>
        value.Length > 0 && Uri.TryCreate(HomeRoot, value, out var uri) && uri.Scheme is "https" or "http" && uri.UserInfo.Length == 0
            ? uri.AbsoluteUri
            : throw new InvalidDataException("教学媒体地址无效。");

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

    [GeneratedRegex("\\A/jijiu/article/[A-Za-z0-9]+\\.html\\z", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex LessonPath();
    [GeneratedRegex("<div\\b[^>]*class=[\"']jj-title-li[\"'][^>]*>", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex Sections();
    [GeneratedRegex("<li\\b[^>]*class=[\"']list-br3[\"'][^>]*>(.*?)</li>", RegexOptions.Singleline | RegexOptions.IgnoreCase, 1000)]
    private static partial Regex Rows();
    [GeneratedRegex("<a\\b(?<attrs>[^>]*)>(?<body>.*?)</a>", RegexOptions.Singleline | RegexOptions.IgnoreCase, 1000)]
    private static partial Regex Anchors();
    [GeneratedRegex("<video\\b(?<attrs>[^>]*)>(?<body>.*?)</video>", RegexOptions.Singleline | RegexOptions.IgnoreCase, 1000)]
    private static partial Regex Videos();
    [GeneratedRegex("<source\\b([^>]*)>", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex Sources();
    [GeneratedRegex("<(?:h[1-6]|div)\\b[^>]*class=[\"'][^\"']*\\bvideo-title\\b[^\"']*[\"'][^>]*>(.*?)</(?:h[1-6]|div)>", RegexOptions.Singleline | RegexOptions.IgnoreCase, 1000)]
    private static partial Regex Heading();
    [GeneratedRegex("([a-zA-Z-]+)\\s*=\\s*[\"']([^\"']*)[\"']", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Attributes();
    [GeneratedRegex("<[^>]*>", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Tags();
}
