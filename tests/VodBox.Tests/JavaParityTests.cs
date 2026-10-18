using VodBox.Core;
using VodBox.Infrastructure;
using Xunit;

namespace VodBox.Tests;

/// <summary>对照原 Java/Python 插件补齐的行为：导航跳转、/god 兜底、CDN 改写、详情缓存与图片请求头约定。</summary>
public sealed class JavaParityTests
{
    [Fact]
    public void SearchLinksCarryTitleAndImageHeadersSplitOff()
    {
        var id = TvBoxLinks.SearchId("流浪地球", "https://img/x.jpg");
        Assert.True(TvBoxLinks.TryGetSearchKeyword(id, out var keyword));
        Assert.Equal("流浪地球", keyword);
        Assert.False(TvBoxLinks.TryGetSearchKeyword("movie/7.html", out _));

        var (url, headers) = TvBoxLinks.SplitImageHeaders("https://img2.doubanio.com/p.jpg@Referer=https://api.douban.com/@User-Agent=Mozilla/5.0 (Windows NT 10.0)");
        Assert.Equal("https://img2.doubanio.com/p.jpg", url);
        Assert.Equal("https://api.douban.com/", headers["Referer"]);
        Assert.Equal("Mozilla/5.0 (Windows NT 10.0)", headers["User-Agent"]);
        Assert.Empty(TvBoxLinks.SplitImageHeaders("https://cdn/a@2x.png").Headers);
    }

    [Fact]
    public async Task DouDouListsDoubanWithSearchCardsAndJavaRouting()
    {
        const string items = """{"items":[{"title":"希望","type":"movie","pic":{"normal":"https://img2.doubanio.com/p.jpg"},"rating":{"value":5.5}},{"title":"片单","type":"playlist"},{"title":"","type":"movie"}]}""";
        var urls = new List<string>();
        var source = new DouDouNavigationSource(SyntheticSourceHelpers.Site("doudou", "csp_DouDouGuard"), (url, headers, _) =>
        {
            urls.Add(url);
            Assert.Equal("frodo.douban.com", headers["Host"]);
            return Task.FromResult(items);
        });
        var page = await source.GetItemsAsync("hot_gaia", 2, new Dictionary<string, string> { ["sort"] = "time" }, TestContext.Current.CancellationToken);
        var card = Assert.Single(page.Items);
        Assert.Equal("希望", card.Title);
        Assert.Equal("评分：5.5", card.Remarks);
        Assert.StartsWith("https://img2.doubanio.com/p.jpg@Referer=https://api.douban.com/", card.Poster);
        Assert.True(TvBoxLinks.TryGetSearchKeyword(card.Id, out var keyword) && keyword == "希望");
        Assert.Equal("https://frodo.douban.com/api/v2/movie/hot_gaia?apikey=0ac44ae016490db2204ce0a042db2916&sort=time&area=%E5%85%A8%E9%83%A8&start=20&count=20", urls[^1]);

        await source.GetItemsAsync("tv", 1, new Dictionary<string, string> { ["类型"] = "电视剧", ["sort"] = "R", ["地区"] = "美国" }, TestContext.Current.CancellationToken);
        Assert.Contains("/tv/recommend?apikey=", urls[^1]);
        Assert.Contains("&sort=R&tags=" + Uri.EscapeDataString("电视剧,美国") + "&start=0&count=20", urls[^1]);

        Assert.Equal(7, (await source.GetCategoriesAsync(TestContext.Current.CancellationToken)).Count);
        Assert.Contains((await source.GetFiltersAsync("tv", TestContext.Current.CancellationToken)), group => group.Key == "平台");
        Assert.IsAssignableFrom<INavigationContentSource>(source);
    }

    [Theory]
    [InlineData("https://v16m-default.tiktokcdn.com/x/tos-alisg-v-0000/abc.mp4", "https://p16-hera-sign-va.ibyteimg.com/obj/tos-alisg-v-0000/abc.mp4")]
    [InlineData("https://lf16-capcut.faceueditorv.com/tos-alisg-v-0000/abc", "http://lf77-geckocdn.bytegecko-i18n.com/obj/tos-alisg-v-0000/abc")]
    [InlineData("https://cdn-f19.example/tos-maliva-o-0000-us/abc", "http://lf16-cdn-solve.gauthmath.com/obj/tos-maliva-o-0000-us/abc")]
    [InlineData("https://plain.example/a.m3u8", "https://plain.example/a.m3u8")]
    public void YCyzGodUrlsFollowJavaCdnRewrite(string input, string expected) => Assert.Equal(expected, YCyzSource.GodUrl(input));

    [Fact]
    public async Task YCyzFallsBackToGodEndpointWhenLinesHaveNoAddress()
    {
        const string detail = """<h1>片</h1><div class="play-list"><a href="/movie/7/1-1.html">第1集</a></div>""";
        var posts = new List<(string Url, IReadOnlyDictionary<string, string> Fields)>();
        var source = new YCyzSource(SyntheticSourceHelpers.Site("ycyz", "csp_YCyzGuard"), (url, _) => Task.FromResult(
                url.Contains("/lines?", StringComparison.Ordinal) ? """{"data":{"url3":null,"m3u8":null,"m3u8_2":null,"ptoken":"888"}}""" :
                url.Contains("/movie/7/1-1.html", StringComparison.Ordinal) ? "<script>var pid = 'p9';</script>" : detail),
            () => 1792260000000L,
            (url, fields, _) => { posts.Add((url, fields)); return Task.FromResult("""{"url":"https://v16m-default.tiktokcdn.com/tos-alisg-v-0000/z.mp4"}"""); });
        var playback = await source.ResolvePlaybackAsync("movie/7.html", "自动\u0001movie/7/1-1.html", TestContext.Current.CancellationToken);
        Assert.Equal("https://p16-hera-sign-va.ibyteimg.com/obj/tos-alisg-v-0000/z.mp4", playback.Uri);
        var (godUrl, fields) = Assert.Single(posts);
        Assert.EndsWith("//god/p9", godUrl);
        Assert.Equal("888", fields["verifyCode"]);
        Assert.Equal(YCyzSource.Sign("p9", 1792260000000L), fields["sg"]);
    }

    [Fact]
    public async Task DetailCacheServesPlaybackAfterOpeningDetailsWithinLifetime()
    {
        var cache = new DetailCache(TimeSpan.FromMinutes(5), capacity: 2);
        int loads = 0;
        Task<MediaDetail> Load(string id, CancellationToken _) { loads++; return Task.FromResult(new MediaDetail { Item = new() { Id = id, Title = id } }); }
        await cache.GetAsync("a", Load, TestContext.Current.CancellationToken);
        await cache.GetAsync("a", Load, TestContext.Current.CancellationToken);
        Assert.Equal(1, loads);
        Assert.Equal("a", cache.TryGet("a")?.Item.Title);
        await cache.GetAsync("b", Load, TestContext.Current.CancellationToken);
        await cache.GetAsync("c", Load, TestContext.Current.CancellationToken);
        Assert.Null(cache.TryGet("a"));
        Assert.Equal(3, loads);
        Assert.Null(new DetailCache(TimeSpan.Zero).TryGet("a"));
    }

    [Fact]
    public async Task LibvioSkipsDownloadPanelsAndRoutesBbaThroughYdParser()
    {
        const string detail = """
            <h1>片</h1>
            <div class="playlist-panel"><div class="panel-head">下载</div><a href="/play/9-2-1.html">下载1</a></div>
            <div class="playlist-panel"><div class="panel-head">BBA</div><a href="/play/9-1-1.html">第1集</a></div>
            """;
        var rec = new SyntheticSourceHelpers.Recorder((url, _) => Task.FromResult(
            url.Contains("/play/9-1-1.html", StringComparison.Ordinal) ? """<script>var player_aaaa={"url":"abc","from":"BBA","nid":1,"id":9};</script>""" :
            url.Contains("vid/yd.php", StringComparison.Ordinal) ? "urls: 'https://cdn.example/a.m3u8'" : detail));
        var source = new LibvioSource(SyntheticSourceHelpers.Site("lib", "csp_LibvioGuard", """{"siteUrl":"https://lib.example"}"""), (url, ct) => rec.Fetch(url, ct));
        var lines = (await source.GetDetailAsync("https://lib.example/detail/9.html", TestContext.Current.CancellationToken)).Lines;
        var line = Assert.Single(lines);
        var playback = await source.ResolvePlaybackAsync("https://lib.example/detail/9.html", line.Episodes[0].Id, TestContext.Current.CancellationToken);
        Assert.Equal("https://cdn.example/a.m3u8", playback.Uri);
        Assert.Contains(rec.Urls, url => url.Contains("vid/yd.php?url=abc", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NewCzUsesDoubanRemarksAndT0o1PlayerBranch()
    {
        const string detail = """
            <div class="moviedteail_tt"><h1>电影甲</h1></div>
            <ul class="moviedteail_list"><li>豆瓣：8.1</li><li>年份：2024</li></ul>
            <div class="paly_list_btn"><a href="https://www.4kcz.com/v_play/1.html">正片</a></div>
            """;
        const string play = """<iframe src="https://t0o1.czzy.fun:321/?url=https://cdn.example/b.m3u8"></iframe>""";
        var rec = new SyntheticSourceHelpers.Recorder((url, _) => Task.FromResult(url.Contains("v_play", StringComparison.Ordinal) ? play : detail));
        var source = new NewCzSource(SyntheticSourceHelpers.Site("newcz", "csp_NewCzGuard"), (url, ct) => rec.Fetch(url, ct));
        var result = await source.GetDetailAsync("https://www.4kcz.com/movie/1.html", TestContext.Current.CancellationToken);
        Assert.Equal("8.1", result.Item.Remarks);
        var playback = await source.ResolvePlaybackAsync("https://www.4kcz.com/movie/1.html", result.Lines[0].Episodes[0].Id, TestContext.Current.CancellationToken);
        Assert.Equal("https://cdn.example/b.m3u8", playback.Uri);
        Assert.Equal("https://t0o1.czzy.fun:321", playback.Headers["Origin"]);
    }
}
