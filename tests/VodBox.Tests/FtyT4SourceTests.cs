using System.Text.Json;
using VodBox.Core;
using VodBox.Infrastructure;
using Xunit;

namespace VodBox.Tests;

public sealed class FtyT4SourceTests
{
    private const string Home = """
        {"class":[{"type_id":1,"type_name":"动作片"},{"type_id":2,"type_name":"喜剧片"},{"type_id":3,"type_name":"其他"}],
         "list":[{"vod_id":7,"vod_name":"中文电影","vod_pic":"https://cdn.example/cover.jpg"}],"pagecount":5}
        """;
    private const string Detail = """
        {"list":[{"vod_id":7,"vod_name":"中文电影","vod_content":"<p>剧情</p>","vod_play_from":"直连$$$解析",
         "vod_play_url":"第一集$https://cdn.example/a.m3u8#第二集$https://cdn.example/b.mp4$$$正片$https://web.example/watch/7"}]}
        """;
    private static SourceInfo Site(string? ext = null) => new()
    {
        Key = "t4", Name = "T4", Api = "csp_T4Guard", Runtime = SourceRuntime.NativeSpider,
        Ext = ext ?? """{"type":1,"siteUrl":"https://api.example/api.php/provide/vod/?ac=list&custom=keep","categories":["动作片","喜剧片"],"rmCategories":["喜剧片"]}"""
    };

    [Fact]
    public void BothRealEncryptedDefinitionsHaveTheExpectedProtocol()
    {
        var config = ConfigLoader.ParseBytes(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "fty-in.bmp")))!;
        var sites = config.Sites.Where(site => site.Api == "csp_T4Guard").ToArray();
        Assert.Equal(2, sites.Length);
        var first = FtyT4Source.ParseConfig(sites[0].Ext!.Value.GetString());
        var second = FtyT4Source.ParseConfig(sites[1].Ext!.Value.GetString());
        Assert.Equal("api.wsyzy.net", first.Endpoint.Host);
        Assert.Equal("api.wsyzy.net", first.SearchHome!.Host);
        Assert.Equal("caiji.dyttzyapi.com", second.Endpoint.Host);
        Assert.Null(second.SearchHome);
        Assert.Contains("动作片", first.Categories);
        Assert.Equal(1, first.Categories.Count(name => name == "欧美动漫"));
        Assert.All(sites, site => Assert.True(NativeSpiders.IsSupported(site)));
    }

    [Fact]
    public async Task JsonProtocolPreservesQueriesCategoriesPaginationAndHistoryIdentity()
    {
        var urls = new List<Uri>();
        var source = new FtyT4Source(Site(), (url, _, _) =>
        {
            urls.Add(new Uri(url));
            return Task.FromResult(url.Contains("ids=", StringComparison.Ordinal) ? Detail : Home);
        });
        Assert.Equal(new Category("1", "动作片"), Assert.Single(await source.GetCategoriesAsync(TestContext.Current.CancellationToken)));
        Assert.Equal("7", Assert.Single((await source.GetHomeAsync(TestContext.Current.CancellationToken)).Items).Id);
        Assert.Equal("?ac=list&custom=keep", urls[0].Query);
        var page = await source.GetItemsAsync("1", 2, null, TestContext.Current.CancellationToken);
        Assert.Equal(2, page.Page);
        Assert.Equal(5, page.PageCount);
        Assert.Contains("ac=detail", urls[^1].Query);
        Assert.Contains("custom=keep", urls[^1].Query);
        Assert.DoesNotContain("ac=list", urls[^1].Query);
        Assert.Contains("pg=2", urls[^1].Query);
        var detail = await source.GetDetailAsync("7", TestContext.Current.CancellationToken);
        Assert.Equal(2, detail.Lines.Count);
        Assert.Equal("剧情", detail.Description);
        var episode = detail.Lines[0].Episodes[0];
        var play = await source.ResolvePlaybackAsync("7", episode.Id, TestContext.Current.CancellationToken);
        Assert.Equal(ResolutionKind.Direct, play.Resolution);
        Assert.Equal(episode.Id, play.EpisodeId);
        Assert.Equal("直连", play.LineId);
        Assert.Equal("https://cdn.example/a.m3u8", play.Uri);
        var web = await source.ResolvePlaybackAsync("7", detail.Lines[1].Episodes[0].Id, TestContext.Current.CancellationToken);
        Assert.Equal(ResolutionKind.Json, web.Resolution);
        Assert.Equal("7", Assert.Single((await source.SearchAsync("中文", 3, TestContext.Current.CancellationToken)).Items).Id);
        Assert.Contains("wd=%E4%B8%AD%E6%96%87", urls[^1].Query);
        Assert.Contains("pg=3", urls[^1].Query);
        await Assert.ThrowsAsync<InvalidDataException>(() => source.ResolvePlaybackAsync("7", "missing", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WebSearchHydratesOnlyMatchingSameOriginIdsAndIsolatesCredentials()
    {
        var requests = new List<(Uri Uri, IReadOnlyDictionary<string, string> Headers)>();
        var site = Site("""{"type":1,"siteUrl":"https://api.example/provide?ac=list","searchMode":"web&https://search.example/"}""")
            with { Header = new Dictionary<string, string> { ["Cookie"] = "api-session", ["Authorization"] = "api-token", ["User-Agent"] = "fixture-ua" } };
        var source = new FtyT4Source(site, (url, headers, _) =>
        {
            requests.Add((new Uri(url), headers));
            return Task.FromResult(url.StartsWith("https://api.example", StringComparison.Ordinal) ? Detail : """
                <a href="/index.php/vod/detail/id/7.html"><b>中文</b>电影</a>
                <a href='/index.php/vod/detail/id/7.html'>中文电影</a>
                <a href="https://other.example/index.php/vod/detail/id/8.html">中文电影</a>
                <a href="/index.php/vod/detail/id/9.html">其他电影</a>
                """);
        });
        var page = await source.SearchAsync("中文", 2, TestContext.Current.CancellationToken);
        Assert.Equal("7", Assert.Single(page.Items).Id);
        Assert.Equal(2, requests.Count);
        Assert.Equal("/index.php/vod/search/page/2/wd/%E4%B8%AD%E6%96%87.html", requests[0].Uri.AbsolutePath);
        Assert.Equal("fixture-ua", Assert.Single(requests[0].Headers).Value);
        Assert.Contains("ac=detail", requests[1].Uri.Query);
        Assert.Contains("ids=7", requests[1].Uri.Query);
        Assert.Equal("api-session", requests[1].Headers["Cookie"]);
        Assert.Equal(3, page.PageCount); // search hydration omitted pagination metadata
    }

    [Fact]
    public async Task ExplicitPlayUrlIsUsedEvenForHlsAndEmptySearchStopsPagination()
    {
        var site = Site("""{"type":1,"siteUrl":"https://api.example/","playUrl":"https://parser.example/?url="}""");
        var source = new FtyT4Source(site, (url, _, _) => Task.FromResult(url.Contains("ids=", StringComparison.Ordinal) ? Detail : "{\"list\":[]}"));
        var request = await source.ResolvePlaybackAsync("7", "https://cdn.example/a.m3u8", TestContext.Current.CancellationToken);
        Assert.Equal(ResolutionKind.Json, request.Resolution);
        Assert.Equal("https://parser.example/?url=", request.ParseEndpoint);
        var empty = await source.SearchAsync("none", 2, TestContext.Current.CancellationToken);
        Assert.Empty(empty.Items);
        Assert.Equal(2, empty.PageCount);
    }

    [Fact]
    public async Task CaptchaMalformedResponsesAndCancellationRemainVisible()
    {
        var site = Site("""{"type":1,"siteUrl":"https://api.example/","searchMode":"web&https://search.example/"}""");
        var challenge = new FtyT4Source(site, (_, _, _) => Task.FromResult("系统安全验证"));
        await Assert.ThrowsAsync<NotSupportedException>(() => challenge.SearchAsync("中文", 1, TestContext.Current.CancellationToken));
        var malformed = new FtyT4Source(Site(), (_, _, _) => Task.FromResult("{\"code\":500}"));
        await Assert.ThrowsAsync<InvalidDataException>(() => malformed.GetHomeAsync(TestContext.Current.CancellationToken));
        var cancelled = new FtyT4Source(Site(), (_, _, ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct).ContinueWith(_ => "{}", ct));
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.GetHomeAsync(timeout.Token));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => malformed.SearchAsync("x", 0, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<NotSupportedException>(() => malformed.GetItemsAsync("1", 1, new Dictionary<string, string> { ["year"] = "2026" }, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("{\"type\":4,\"siteUrl\":\"https://api.example\"}")]
    [InlineData("{\"type\":1,\"siteUrl\":\"file:///tmp/a\"}")]
    [InlineData("{\"type\":1,\"siteUrl\":\"https://user:pass@api.example\"}")]
    [InlineData("invalid!")]
    public void UnsupportedDefinitionsKeepTheJarFallback(string ext)
    {
        using var parsed = JsonDocument.Parse(JsonSerializer.Serialize(ext, Json.TypeInfo<string>()));
        var site = new TvBoxSite { Key = "t4", Name = "T4", Api = "csp_T4Guard", Type = 3, Ext = parsed.RootElement.Clone() };
        Assert.False(NativeSpiders.IsSupported(site));
        Assert.False(NativeSpiders.IsSupported(new TvBoxSite { Key = "t4", Name = "T4", Api = "csp_T4Guard ", Type = 3, Ext = parsed.RootElement.Clone() }));
        var config = new TvBoxConfig { Sites = [site] };
        Assert.Equal(SourceRuntime.Jar, Assert.Single(ConfigLoader.ToSources(config, null, true)).Runtime);
        Assert.Empty(ConfigLoader.ToSources(config));
    }
}
