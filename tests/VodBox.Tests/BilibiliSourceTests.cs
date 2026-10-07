using System.Net;
using System.Text;
using VodBox.Core;
using VodBox.Infrastructure;
using Xunit;

namespace VodBox.Tests;

/// <summary>
/// 哔哩哔哩爬虫离线回归：用真实抓取的响应 fixture + 假 HttpMessageHandler 锁定协议正确性。
/// fixture 来源为 fixtures/bili/*.json（真实 api.bilibili.com 响应）。
/// 真实网络端到端验证另由诊断工装执行（见 docs/SALVAGE.md）；此处保证协议不回归。
/// </summary>
public class BilibiliSourceTests
{
    private static readonly string FixtureDir =
        Path.Combine(AppContext.BaseDirectory, "fixtures", "bili");

    private static string Fixture(string name)
    {
        var path = Path.Combine(FixtureDir, name);
        Assert.True(File.Exists(path), $"缺少测试样本：{path}");
        return File.ReadAllText(path);
    }

    private static SourceInfo SampleInfo(string? ext = null) => new()
    {
        Key = "csp_Bili", Name = "哔哩┃综合", Runtime = SourceRuntime.NativeSpider, Api = "csp_Bili",
        Ext = ext ?? """{"json":"","cookie":""}""",
    };

    /// <summary>按 API 路径分发 fixture 的假 handler；记录请求以校验签名/头。</summary>
    private sealed class RouteHandler : HttpMessageHandler
    {
        private readonly Func<string, string> _route;
        public List<HttpRequestMessage> Requests { get; } = [];

        public RouteHandler(Func<string, string> route) => _route = route;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            var path = request.RequestUri!.AbsolutePath.TrimStart('/');
            var body = _route(path);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
                RequestMessage = request,
            });
        }
    }

    private static BilibiliSource Create(RouteHandler handler, string? ext = null) =>
        new(SampleInfo(ext), new HttpClient(handler));

    // ---------- ext 解析 ----------

    [Fact]
    public void ReadExt_ParsesJsonObject()
    {
        var (json, cookie) = BilibiliSource.ReadExt("""{"json":"https://x/c.json","cookie":"a=b"}""");
        Assert.Equal("https://x/c.json", json);
        Assert.Equal("a=b", cookie);
    }

    [Fact]
    public void ReadExt_PlainUrlIsJson()
    {
        var (json, cookie) = BilibiliSource.ReadExt("https://x/c.json");
        Assert.Equal("https://x/c.json", json);
        Assert.Null(cookie);
    }

    [Fact]
    public void ReadExt_EmptyAndInvalidYieldNull()
    {
        Assert.Equal((null, null), BilibiliSource.ReadExt(null));
        Assert.Equal((null, null), BilibiliSource.ReadExt(""));
        Assert.Equal((null, null), BilibiliSource.ReadExt("{not json"));
    }

    // ---------- 分类（ext.json → 搜索关键词）----------

    [Fact]
    public void ParseCategories_ReadsClassArrayAndDropsInvalid()
    {
        var json = """
        {"class":[
          {"type_name":"登陆配置","type_id":"peizhi"},
          {"type_name":"动漫","type_id":"动漫合集"},
          {"type_name":"","type_id":"empty-name"},
          {"type_name":"无id","type_id":""},
          {"type_name":"动漫","type_id":"动漫合集"}
        ]}
        """;
        var categories = BilibiliSource.ParseCategories(Encoding.UTF8.GetBytes(json));
        // 有效 2 个（空名/空 id 被丢弃，重复被丢弃）
        Assert.Equal(2, categories.Count);
        Assert.Equal("动漫合集", categories[1].Query); // type_id 作为搜索关键词
    }

    [Fact]
    public async Task GetCategories_PrependsPopularAndFetchesExtOnce()
    {
        const string extJson = """{"class":[{"type_name":"动漫","type_id":"动漫合集"}]}""";
        var handler = new ExtHandler(extJson);
        // ext.json 通过独立 https 地址抓取（FetchRawAsync 只允许 http/https）
        using var source = new BilibiliSource(
            SampleInfo("""{"json":"https://ext.example.com/c.json","cookie":""}"""),
            new HttpClient(handler));

        var categories = await source.GetCategoriesAsync();
        Assert.Equal("popular", categories[0].Id);   // 「热门」始终置顶
        Assert.Equal("热门", categories[0].Name);
        Assert.Contains(categories, c => c.Id == "动漫合集" && c.Name == "动漫");

        // 分类结果被缓存：二次调用不再抓取 ext.json
        var second = await source.GetCategoriesAsync();
        Assert.Equal(categories.Count, second.Count);
        Assert.Equal(1, handler.ExtRequests);
    }

    [Fact]
    public async Task GetCategories_WithoutExtYieldsPopularOnly()
    {
        using var source = Create(new RouteHandler(_ => "{}"));
        var categories = await source.GetCategoriesAsync();
        Assert.Equal(["popular"], categories.Select(c => c.Id).ToArray());
    }

    /// <summary>只对 ext.json 地址应答的假 handler，并统计抓取次数。</summary>
    private sealed class ExtHandler : HttpMessageHandler
    {
        private readonly string _body;
        public int ExtRequests;
        public ExtHandler(string body) => _body = body;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref ExtRequests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
                RequestMessage = request,
            });
        }
    }

    // ---------- 热门（真实 fixture）----------

    [Fact]
    public async Task GetHome_ParsesRealPopularFixture()
    {
        var handler = new RouteHandler(_ => Fixture("popular.json"));
        using var source = Create(handler);
        var page = await source.GetHomeAsync();
        Assert.True(page.Items.Count > 0, "热门应解析出条目");
        Assert.Equal(1, page.Page);
        var first = page.Items[0];
        Assert.StartsWith("BV", first.Id);
        Assert.False(string.IsNullOrWhiteSpace(first.Title));
        Assert.NotNull(first.Poster);
        // 分页：有更多则给下一页
        Assert.Equal(2, page.PageCount);
    }

    [Fact]
    public async Task GetHome_SendsUserAgentAndReferer()
    {
        var handler = new RouteHandler(_ => Fixture("popular.json"));
        using var source = Create(handler);
        await source.GetHomeAsync();
        var req = Assert.Single(handler.Requests);
        Assert.Contains("bilibili", req.Headers.Referrer!.Host);
        Assert.True(req.Headers.UserAgent.ToString().Length > 0);
    }

    // ---------- WBI 搜索签名（真实 nav + spi fixture）----------

    [Fact]
    public async Task Search_SignsWithWbiFromRealFixtures()
    {
        var handler = new RouteHandler(path => path switch
        {
            "x/web-interface/nav" => Fixture("nav.json"),
            "x/frontend/finger/spi" => Fixture("spi.json"),
            "x/web-interface/wbi/search/type" => """{"code":0,"data":{"numPages":1,"result":[]}}""",
            _ => "{}",
        });
        using var source = Create(handler);
        await source.SearchAsync("演唱会", 1);

        var search = handler.Requests.Last(r => r.RequestUri!.AbsolutePath.Contains("wbi/search/type"));
        var query = search.RequestUri!.Query;
        // WBI 签名参数必须存在
        Assert.Contains("w_rid=", query);
        Assert.Contains("wts=", query);
        // w_rid 为 32 位小写十六进制（MD5）
        var wrid = query.Split('&').First(p => p.StartsWith("w_rid="))["w_rid=".Length..];
        Assert.Equal(32, wrid.Length);
        Assert.All(wrid, c => Assert.True(Uri.IsHexDigit(c) && (!char.IsLetter(c) || char.IsLower(c))));
        // 访客会话 cookie 被带上（buvid3/buvid4 来自 spi fixture）
        Assert.Contains("buvid3=", search.Headers.GetValues("Cookie").First());
    }

    [Fact]
    public async Task Search_RejectsRedirectAndNonApiHost()
    {
        // 重定向（3xx）应被拒绝
        var redirect = new RedirectHandler();
        using var source = new BilibiliSource(SampleInfo("""{"json":"","cookie":""}"""), new HttpClient(redirect));
        await Assert.ThrowsAsync<InvalidDataException>(() => source.SearchAsync("x", 1));
    }

    private sealed class RedirectHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath.TrimStart('/');
            if (path == "x/web-interface/nav")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found) { RequestMessage = request });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"), RequestMessage = request,
            });
        }
    }

    // ---------- HTML 标签清洗（标题高亮）----------

    [Fact]
    public void PlainText_StripsTagsAndDecodesEntities()
    {
        Assert.Equal("演唱会现场", BilibiliSource.PlainText("<em class=\"keyword\">演唱会</em>现场"));
        Assert.Equal("A & B", BilibiliSource.PlainText("A &amp; B"));
        Assert.Equal("", BilibiliSource.PlainText(""));
    }

    // ---------- 播放解析 ----------

    [Fact]
    public async Task ResolvePlayback_ReturnsSignedUrlAndDanmaku()
    {
        const string view = """
        {"code":0,"data":{"bvid":"BV1WSHL66EdZ","title":"测试视频","desc":"简介",
          "pages":[{"cid":123456789,"part":"P1"}]}}
        """;
        const string playurl = """
        {"code":0,"data":{"durl":[{"url":"https://upos.example.com/video.m4s?deadline=1&gen=play"}]}}
        """;
        var handler = new RouteHandler(path => path switch
        {
            "x/web-interface/view" => view,
            "x/player/playurl" => playurl,
            _ => "{}",
        });
        using var source = Create(handler);
        var play = await source.ResolvePlaybackAsync("BV1WSHL66EdZ", "123456789");

        Assert.StartsWith("https://upos.example.com/video.m4s", play.Uri);
        Assert.Equal("https://comment.bilibili.com/123456789.xml", play.DanmakuUri);
        Assert.Equal("csp_Bili", play.SourceKey);
        Assert.Equal("123456789", play.EpisodeId);
        Assert.Equal("ugc", play.LineId);
        Assert.Contains("Referer", play.Headers.Keys);
        Assert.Contains("User-Agent", play.Headers.Keys);
    }

    [Fact]
    public async Task ResolvePlayback_RejectsPreviewOnlyAndMultiSegment()
    {
        const string view = """{"code":0,"data":{"bvid":"BV1WSHL66EdZ","title":"t","desc":"d","pages":[{"cid":1,"part":"P1"}]}}""";
        // 试看
        var preview = new RouteHandler(p => p switch
        {
            "x/web-interface/view" => view,
            "x/player/playurl" => """{"code":0,"data":{"is_preview":true,"durl":[{"url":"https://x/v.m4s"}]}}""",
            _ => "{}",
        });
        using (var s1 = Create(preview))
            await Assert.ThrowsAsync<NotSupportedException>(() => s1.ResolvePlaybackAsync("BV1WSHL66EdZ", "1"));
        // 多段（DASH 未接入）
        var multi = new RouteHandler(p => p switch
        {
            "x/web-interface/view" => view,
            "x/player/playurl" => """{"code":0,"data":{"durl":[{"url":"https://x/a.m4s"},{"url":"https://x/b.m4s"}]}}""",
            _ => "{}",
        });
        using (var s2 = Create(multi))
            await Assert.ThrowsAsync<NotSupportedException>(() => s2.ResolvePlaybackAsync("BV1WSHL66EdZ", "1"));
    }

    // ---------- 媒体标识校验 ----------

    [Fact]
    public async Task Detail_RejectsMismatchedBvid()
    {
        var handler = new RouteHandler(_ => """{"code":0,"data":{"bvid":"BV1DIFFERENT","title":"t","desc":"d","pages":[{"cid":1,"part":"P1"}]}}""");
        using var source = Create(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => source.GetDetailAsync("BV1WSHL66EdZ"));
    }

    [Fact]
    public async Task Detail_RejectsDuplicateOrInvalidCid()
    {
        var handler = new RouteHandler(_ => """{"code":0,"data":{"bvid":"BV1WSHL66EdZ","title":"t","desc":"d","pages":[{"cid":5,"part":"P1"},{"cid":5,"part":"P2"}]}}""");
        using var source = Create(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => source.GetDetailAsync("BV1WSHL66EdZ"));
    }

    [Fact]
    public async Task InvalidMediaId_Throws()
    {
        using var source = Create(new RouteHandler(_ => "{}"));
        await Assert.ThrowsAsync<InvalidDataException>(() => source.GetDetailAsync("not-a-bvid"));
    }
}
