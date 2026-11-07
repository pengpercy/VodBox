using Xunit;
using VodBox.Core;
using VodBox.Infrastructure;

namespace VodBox.Tests;

public sealed class DoubanProxyTests
{
    [Theory]
    [InlineData(DoubanProxyTypes.Direct, "", "https://m.douban.com/rexxar/api/v2/x?a=1")]
    [InlineData(DoubanProxyTypes.TencentCdn, "", "https://m.douban.cmliussss.net/rexxar/api/v2/x?a=1")]
    [InlineData(DoubanProxyTypes.AliCdn, "", "https://m.douban.cmliussss.com/rexxar/api/v2/x?a=1")]
    [InlineData(DoubanProxyTypes.CorsAnywhere, "", "https://cors-anywhere.com/https://m.douban.com/rexxar/api/v2/x?a=1")]
    [InlineData(DoubanProxyTypes.CorsProxyZwei, "", "https://ciao-cors.is-an.org/https%3A%2F%2Fm.douban.com%2Frexxar%2Fapi%2Fv2%2Fx%3Fa%3D1")]
    [InlineData(DoubanProxyTypes.Custom, "https://p.example/?u=", "https://p.example/?u=https%3A%2F%2Fm.douban.com%2Frexxar%2Fapi%2Fv2%2Fx%3Fa%3D1")]
    public void RewritesDataRequests(string type, string custom, string expected) =>
        Assert.Equal(expected, DoubanClient.ProxyUrl("https://m.douban.com/rexxar/api/v2/x?a=1", type, custom));

    [Fact]
    public void RewritesOnlyDoubanImages()
    {
        const string poster = "https://img3.doubanio.com/view/photo/s_ratio_poster/public/p1.jpg";
        Assert.Equal("https://img.doubanio.cmliussss.net/view/photo/s_ratio_poster/public/p1.jpg", DoubanClient.ProcessImage(poster, new SiteSettings()));
        Assert.Equal(poster + "@Referer=https://movie.douban.com/", DoubanClient.ProcessImage(poster, new SiteSettings { DoubanImageProxyType = DoubanImageProxyTypes.Direct }));
        Assert.Equal("https://other.example/a.jpg", DoubanClient.ProcessImage("http://other.example/a.jpg", new SiteSettings()));
        var (url, headers) = ImageAddress.SplitHeaders(DoubanClient.ProcessImage(poster, new SiteSettings { DoubanImageProxyType = DoubanImageProxyTypes.Direct }));
        Assert.Equal((poster, "https://movie.douban.com/"), (url, headers["Referer"]));
    }

    [Fact]
    public async Task BrowseSourceMapsCategoriesAndRecommendFilters()
    {
        var handler = new FakeHandler(request => FakeHandler.Text(request.RequestUri!.AbsolutePath.Contains("recommend")
            ? """{"items":[{"id":"1","title":"影片","year":"2024","type":"movie","pic":{"normal":"https://img1.doubanio.com/p.jpg"},"rating":{"value":8.25}},{"id":"x","type":"doulist"}]}"""
            : """{"items":[{"id":"2","title":"热门","card_subtitle":"2025 / 中国","pic":{"large":"https://img2.doubanio.com/q.jpg"},"rating":{"value":0}}]}"""));
        using var douban = new DoubanClient(() => new SiteSettings { DoubanProxyType = DoubanProxyTypes.Direct }, handler);
        using var bangumi = new BangumiClient(new HttpClient(handler));
        var source = new DoubanBrowseSource(douban, bangumi, () => [new CustomCategory { Name = "经典", Query = "经典" }]);
        var categories = await source.GetCategoriesAsync(TestContext.Current.CancellationToken);
        Assert.Contains(categories, category => category.Id == "custom:movie:经典");

        var hot = await source.GetItemsAsync("movie", 1, null, TestContext.Current.CancellationToken);
        var card = Assert.Single(hot.Items);
        Assert.Equal(("2", "2025", MediaItem.DoubanSourceKey, "movie"), (card.Id, card.Year, card.SourceKey, card.Kind));
        Assert.Contains("recent_hot/movie?start=0&limit=25&category=%E7%83%AD%E9%97%A8&type=%E5%85%A8%E9%83%A8", handler.Requests[^1]);

        var filtered = await source.GetItemsAsync("tv", 2, new Dictionary<string, string> { ["genre"] = "喜剧", ["year"] = "2024", ["sort"] = "S" }, TestContext.Current.CancellationToken);
        Assert.Equal("8.3", Assert.Single(filtered.Items).Rating);
        var url = Uri.UnescapeDataString(handler.Requests[^1]);
        Assert.Contains("/tv/recommend?", url);
        Assert.Contains("start=25", url);
        Assert.Contains("tags=喜剧,2024", url);
        Assert.Contains("\"形式\":\"电视剧\"", url);
        Assert.Contains("sort=S", url);
    }
}

public sealed class HlsAdFilterTests
{
    private const string Playlist = """
        #EXTM3U
        #EXT-X-VERSION:3
        #EXT-X-TARGETDURATION:6
        #EXT-X-KEY:METHOD=AES-128,URI="/v/hls/key.key"
        #EXTINF:4,
        /v/hls/a1.ts
        #EXTINF:4,
        /v/hls/a2.ts
        #EXT-X-DISCONTINUITY
        #EXT-X-KEY:METHOD=NONE
        #EXTINF:5,
        https://ads.example/20261031/ad/hls/x1.ts
        #EXTINF:5,
        https://ads.example/20261031/ad/hls/x2.ts
        #EXT-X-DISCONTINUITY
        #EXT-X-KEY:METHOD=AES-128,URI="/v/hls/key.key"
        #EXTINF:4,
        /v/hls/a3.ts
        #EXTINF:4,
        /v/hls/a4.ts
        #EXT-X-ENDLIST
        """;

    [Fact]
    public void RemovesShortForeignDiscontinuitySections()
    {
        var filtered = HlsAdFilter.Filter(Playlist, new Uri("https://cdn.example/v/hls/index.m3u8"), out var removed);
        Assert.Equal(2, removed);
        Assert.DoesNotContain("ads.example", filtered);
        Assert.Contains("/v/hls/a3.ts", filtered);
        Assert.Contains("#EXT-X-ENDLIST", filtered);
        Assert.DoesNotContain("METHOD=NONE", filtered);
    }

    [Fact]
    public void CarriesKeyStateWhenNextSectionDoesNotRedeclare()
    {
        var playlist = Playlist.Replace("#EXT-X-DISCONTINUITY\n#EXT-X-KEY:METHOD=AES-128,URI=\"/v/hls/key.key\"\n#EXTINF:4,\n/v/hls/a3.ts", "#EXT-X-DISCONTINUITY\n#EXTINF:4,\n/v/hls/a3.ts");
        var filtered = HlsAdFilter.Filter(playlist, new Uri("https://cdn.example/v/hls/index.m3u8"));
        var lines = filtered.Split('\n');
        var a3 = Array.IndexOf(lines, "/v/hls/a3.ts");
        Assert.Equal("#EXT-X-KEY:METHOD=NONE", lines[a3 - 2]);
    }

    [Fact]
    public void LeavesMasterPlaylistsAndCleanPlaylistsUntouched()
    {
        const string master = "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1\nhd.m3u8\n#EXT-X-DISCONTINUITY\n";
        Assert.Same(master, HlsAdFilter.Filter(master, new Uri("https://a/")));
        const string clean = "#EXTM3U\n#EXTINF:4,\na.ts\n#EXTINF:4,\nb.ts\n#EXT-X-ENDLIST\n";
        Assert.Same(clean, HlsAdFilter.Filter(clean, new Uri("https://a/")));
    }
}

public sealed class SpeedTesterTests
{
    [Fact]
    public void ScoresLikeLunaTv()
    {
        var fast1080 = new SpeedTestResult("1080p", 2048, 100);
        var slow4k = new SpeedTestResult("4K", 256, 900);
        Assert.Equal(90, SourceSpeedTester.Score(fast1080, 2048, 100, 900));
        Assert.Equal(45, SourceSpeedTester.Score(slow4k, 2048, 100, 900));
        Assert.Equal("1080p", SourceSpeedTester.QualityOf(1920));
        Assert.Equal("2.0 MB/s", fast1080.SpeedLabel);
    }

    [Fact]
    public void PicksHighestBandwidthVariant()
    {
        var (variant, width) = SourceSpeedTester.PickVariant("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=800000,RESOLUTION=1280x720\n720/index.m3u8\n#EXT-X-STREAM-INF:BANDWIDTH=2000000,RESOLUTION=1920x1080\n1080/index.m3u8\n",
            new Uri("https://cdn.example/v/index.m3u8"));
        Assert.Equal(("https://cdn.example/v/1080/index.m3u8", 1920), (variant!.AbsoluteUri, width));
    }

    [Fact]
    public async Task MeasuresPlaylistAndFirstSegment()
    {
        var handler = new FakeHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/index.m3u8" => FakeHandler.Text("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1,RESOLUTION=1920x1080\nhd/index.m3u8\n", type: "application/vnd.apple.mpegurl"),
            "/hd/index.m3u8" => FakeHandler.Text("#EXTM3U\n#EXTINF:4,\nseg1.ts\n", type: "application/vnd.apple.mpegurl"),
            _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[256 * 1024]) },
        });
        using var tester = new SourceSpeedTester(handler);
        var result = await tester.TestAsync("https://cdn.example/index.m3u8", TestContext.Current.CancellationToken);
        Assert.False(result.Failed, result.Error);
        Assert.Equal("1080p", result.Quality);
        Assert.True(result.SpeedKBps > 0);
        Assert.Equal("https://cdn.example/hd/seg1.ts", handler.Requests[^1]);
    }
}
