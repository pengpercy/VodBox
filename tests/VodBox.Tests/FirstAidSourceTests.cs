using System.Net;
using System.Text;
using VodBox.Core;
using VodBox.Infrastructure;
using Xunit;

namespace VodBox.Tests;

/// <summary>
/// 有来医生急救教学爬虫离线回归：合成 fixture 锁定 HTML 结构与解析契约，
/// 真实站点验证另由诊断工装执行（旧实现记录真机 AOT 通过）。
/// </summary>
public class FirstAidSourceTests
{
    private static SourceInfo Info() => new()
    {
        Key = "csp_FirstAid", Name = "急救教学", Runtime = SourceRuntime.NativeSpider, Api = "csp_FirstAid",
    };

    /// <summary>按路径分发合成页面的假 handler。</summary>
    private sealed class PageHandler : HttpMessageHandler
    {
        private readonly Func<string, string> _route;
        public PageHandler(Func<string, string> route) => _route = route;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath.TrimStart('/');
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_route(path), Encoding.UTF8, "text/html"),
                RequestMessage = request,
            });
        }
    }

    /// <summary>合成 8 段目录页（每段若干课程行）。</summary>
    private static string DirectoryPage(int perSection = 2)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 8; i++)
        {
            sb.Append($"<div class=\"jj-title-li\"><h2>分类{i}</h2></div><ul>");
            for (int j = 0; j < perSection; j++)
                sb.Append($"<li class=\"list-br3\"><a href=\"/jijiu/article/Art{i}{j}X.html\">课程{i}_{j}</a></li>");
            sb.Append("</ul>");
        }
        return sb.ToString();
    }

    private static string LessonPage(string id, string title, string src, string poster = "") => $"""
        <html><body>
          <h1 class="video-title">{title}</h1>
          <video id="video" src="{src}"{(poster.Length > 0 ? $" poster=\"{poster}\"" : "")}></video>
        </body></html>
        """;

    private static FirstAidSource Create(Func<string, string> route) =>
        new(Info(), new HttpClient(new PageHandler(route)));

    [Fact]
    public async Task Categories_AreEightEmergencySections()
    {
        using var source = Create(_ => DirectoryPage());
        var categories = await source.GetCategoriesAsync(ct: TestContext.Current.CancellationToken);
        Assert.Equal(8, categories.Count);
        Assert.Equal("急救技能", categories[0].Name);
        Assert.Equal("意外事故", categories[^1].Name);
    }

    [Fact]
    public async Task GetItems_SlicesDirectoryIntoEightSections()
    {
        using var source = Create(_ => DirectoryPage(perSection: 3));
        var page = await source.GetItemsAsync("0", 1, null, ct: TestContext.Current.CancellationToken);
        Assert.Equal(3, page.Items.Count);
        Assert.Equal("课程0_0", page.Items[0].Title);
        Assert.Equal("/jijiu/article/Art00X.html", page.Items[0].Id);
        Assert.Equal(1, page.Page); // 无下一页
    }

    [Fact]
    public async Task GetItems_RejectsInvalidCategoryAndExtraPage()
    {
        using var source = Create(_ => DirectoryPage());
        await Assert.ThrowsAsync<InvalidDataException>(() => source.GetItemsAsync("99", 1, null, ct: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidDataException>(() => source.GetItemsAsync("0", 2, null, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Search_FiltersByTitleCaseInsensitive()
    {
        using var source = Create(_ => DirectoryPage(perSection: 2));
        var page = await source.SearchAsync("课程3", 1, ct: TestContext.Current.CancellationToken);
        Assert.Equal(2, page.Items.Count);
        Assert.All(page.Items, i => Assert.Contains("课程3", i.Title));
    }

    [Fact]
    public async Task Search_RejectsEmptyOrOverlong()
    {
        using var source = Create(_ => DirectoryPage());
        await Assert.ThrowsAsync<InvalidDataException>(() => source.SearchAsync("", 1, ct: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidDataException>(() => source.SearchAsync(new string('x', 201), 1, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Detail_ExtractsHtml5VideoSrc()
    {
        var routes = new Dictionary<string, string>
        {
            ["jijiu"] = DirectoryPage(),
            ["jijiu/article/Art00X.html"] = LessonPage("Art00X", "人工呼吸法", "https://vod.youlai.cn/a/b-sd.mp4", "https://i.example.com/p.jpg"),
        };
        using var source = Create(path => routes.TryGetValue(path, out var p) ? p : DirectoryPage());
        var detail = await source.GetDetailAsync("/jijiu/article/Art00X.html", ct: TestContext.Current.CancellationToken);

        Assert.Equal("人工呼吸法", detail.Item.Title);
        Assert.Equal("https://i.example.com/p.jpg", detail.Item.Poster);
        var line = Assert.Single(detail.Lines);
        Assert.Equal("有来医生", line.Name);
        var ep = Assert.Single(line.Episodes);
        Assert.Null(ep.Uri); // 地址由 ResolvePlayback 现取，不预置
    }

    [Fact]
    public async Task Detail_UsesSourceTagWhenVideoSrcEmpty()
    {
        const string page = """
        <html><body>
          <h1 class="video-title">婴儿心脏骤停</h1>
          <video id="video"><source src="https://vod.youlai.cn/heart-sd.mp4" /></video>
        </body></html>
        """;
        using var source = Create(path => path == "jijiu/article/Heart9X.html" ? page : DirectoryPage());
        var detail = await source.GetDetailAsync("/jijiu/article/Heart9X.html", ct: TestContext.Current.CancellationToken);
        Assert.Equal("婴儿心脏骤停", detail.Item.Title);
    }

    [Fact]
    public async Task ResolvePlayback_ReturnsMediaUrlWithHeaders()
    {
        var routes = new Dictionary<string, string>
        {
            ["jijiu"] = DirectoryPage(),
            ["jijiu/article/Art00X.html"] = LessonPage("Art00X", "人工呼吸法", "https://vod.youlai.cn/a/b-sd.mp4"),
        };
        using var source = Create(path => routes.TryGetValue(path, out var p) ? p : DirectoryPage());
        var play = await source.ResolvePlaybackAsync("/jijiu/article/Art00X.html", "/jijiu/article/Art00X.html", ct: TestContext.Current.CancellationToken);

        Assert.Equal("https://vod.youlai.cn/a/b-sd.mp4", play.Uri);
        Assert.Equal("csp_FirstAid", play.SourceKey);
        Assert.Equal("急救教学", play.SourceName);
        Assert.Equal("main", play.LineId);
        Assert.Contains("Referer", play.Headers.Keys);
        Assert.Contains("User-Agent", play.Headers.Keys);
    }

    [Fact]
    public async Task ResolvePlayback_RejectsMismatchedEpisode()
    {
        using var source = Create(_ => DirectoryPage());
        await Assert.ThrowsAsync<InvalidDataException>(
            () => source.ResolvePlaybackAsync("/jijiu/article/Art00X.html", "/jijiu/article/Other9X.html", ct: TestContext.Current.CancellationToken));
    }

    // ---------- 解析辅助 ----------

    [Fact]
    public void Lesson_ValidatesPathShape()
    {
        Assert.Equal("/jijiu/article/AbC123.html", FirstAidSource.Lesson("/jijiu/article/AbC123.html"));
        Assert.Throws<InvalidDataException>(() => FirstAidSource.Lesson("https://evil.com/x.html"));
        Assert.Throws<InvalidDataException>(() => FirstAidSource.Lesson("/jijiu/other/123.html"));
        Assert.Throws<InvalidDataException>(() => FirstAidSource.Lesson("任意"));
    }

    [Fact]
    public void Url_RejectsUserInfoAndNonHttp()
    {
        Assert.Equal("https://m.youlai.cn/x.mp4", FirstAidSource.Url("/x.mp4"));
        Assert.Equal("https://vod.youlai.cn/a.mp4", FirstAidSource.Url("https://vod.youlai.cn/a.mp4"));
        Assert.Throws<InvalidDataException>(() => FirstAidSource.Url("https://user:pass@vod.youlai.cn/a.mp4"));
        Assert.Throws<InvalidDataException>(() => FirstAidSource.Url("ftp://x/a.mp4"));
        Assert.Throws<InvalidDataException>(() => FirstAidSource.Url(""));
    }
}
