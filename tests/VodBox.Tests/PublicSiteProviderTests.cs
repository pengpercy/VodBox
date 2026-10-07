using System.Net;
using System.Text;
using VodBox.Core;
using VodBox.Infrastructure;
using Xunit;

namespace VodBox.Tests;

public sealed class PublicSiteProviderTests
{
    private const string TxbList = """({"status":0,"data":{"items":[{"video_id":1556,"name":"粉可爱","image":"https://cdn.example/poster.png","duration_string":"03:51"}]}});""";
    private const string TxbDetail = """<title>粉可爱-兔小贝</title><mip-search-video video-src="https://cdn.example/video.mp4?token=fresh&amp;x=1" id="videoWrap"></mip-search-video>""";
    private const string HuyaRoom = """{"status":200,"data":{"liveStatus":"ON","liveData":{"roomName":"直播标题","screenshot":"https://cdn.example/cover.jpg"},"stream":{"flv":{"multiLine":[{"cdnType":"TX","url":"https://cdn.example/live/stream.flv?wsSecret=old&wsTime=ffffff&fm=cHJlZml4XyQwXyQxXyQyXyQz&ctype=tars_mp&t=102"}]}}}}""";
    private const string DouyuRoom = """<script id="vike_pageContext" type="application/json">{"pageProps":{"room":{"roomInfo":{"roomInfo":{"rid":593392,"roomName":"直播标题","roomSrc":"https://cdn.example/cover.jpg","isLive":1}}}}}</script>""";
    private static SourceDefinition Source(string provider) => new() { Id = "test", Name = "站点", Provider = provider };

    [Fact]
    public async Task TrailerCatalogueParsesPaginationLinesAndStaticMediaWithoutExecutingScript()
    {
        using var http = new HttpClient(new Responses(request => Task.FromResult(request.RequestUri!.AbsolutePath switch
        {
            "/movie/86794" => """<h1 class="movie-name">影片</h1><table class="tlist"><th>预告片</th><a href="/show/196149" class="tlist-bbs-tdtitle">终极预告</a></table>""",
            "/show/196149" => """<script>var videoObject = {container: '#player', video: 'https://cdn.example/trailer.mp4'}; throw 'must not execute';</script>""",
            _ => """<div class="movlist"><ul><li><a href="/movie/86794"><img src="/poster.jpg"><span class="item-title">影片</span></a></li></ul></div><a href="/movlist/____2">下一页</a>"""
        })));
        await using var provider = new TrailerProvider(Source("trailers"), http);
        var list = await provider.GetItemsAsync(null, null, default); Assert.Equal("2", list.NextCursor); Assert.Single(list.Items);
        Assert.Single((await provider.SearchAsync("影片", default)).Items);
        Assert.Equal("终极预告", (await provider.GetDetailAsync("/movie/86794", default)).PlaybackLines[0].Episodes[0].Title);
        var request = await provider.ResolvePlaybackAsync("/movie/86794", "/show/196149", default);
        Assert.Equal("https://cdn.example/trailer.mp4", request.Uri); Assert.Equal(ResolutionKind.Direct, request.ResolutionKind);
        Assert.False(request.Headers.ContainsKey("Referer"));
        await Assert.ThrowsAsync<InvalidDataException>(() => provider.ResolvePlaybackAsync("/movie/86794", "/show/196150", default));
        await Assert.ThrowsAsync<InvalidDataException>(() => provider.GetDetailAsync("/movie/../../etc", default));
    }
    [Fact]
    public async Task TuxiaobeiReadsWrappedListSearchAndFreshPlayback()
    {
        int detailRequests = 0;
        using var http = new HttpClient(new Responses(async request =>
        {
            Assert.Contains("iPhone", request.Headers.UserAgent.ToString());
            string path = request.RequestUri!.AbsolutePath;
            if (path.Contains("mip-data")) return TxbList;
            if (path.Contains("search"))
            {
                Assert.Equal("?key=%E7%B2%89%20%26", request.RequestUri.Query);
                return """<div class="list-con"><div class="items"><div class="pic"><a href="/play/1556"><mip-img src="https://cdn.example/poster.png"></a></div><p class="title">粉可爱</p><span class="time">03:51</span><!--items end--></div>""";
            }
            detailRequests++; await Task.Yield(); return TxbDetail.Replace("fresh", detailRequests.ToString());
        }));
        await using var provider = new TuxiaobeiProvider(Source("tuxiaobei"), http);
        Assert.Equal(4, (await provider.GetCategoriesAsync(default)).Count);
        var list = await provider.GetItemsAsync(null, null, default); Assert.Null(list.NextCursor); Assert.Equal("1556", Assert.Single(list.Items).Id);
        Assert.Equal("粉可爱", Assert.Single((await provider.SearchAsync("粉 &", default)).Items).Title);
        Assert.Equal("粉可爱", (await provider.GetDetailAsync("1556", default)).Item.Title);
        var first = await provider.ResolvePlaybackAsync("1556", "1556", default); var second = await provider.ResolvePlaybackAsync("1556", "1556", default);
        Assert.NotEqual(first.Uri, second.Uri); Assert.Contains("&x=1", second.Uri); Assert.Equal("test", second.SourceId); Assert.False(second.IsLive);
        Assert.Contains("play/1556", second.Headers["Referer"]);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TuxiaobeiFollowsOnlyBoundedSameOriginRedirects(bool foreign)
    {
        using var http = new HttpClient(new Redirects(foreign));
        await using var provider = new TuxiaobeiProvider(Source("tuxiaobei"), http);
        if (foreign) await Assert.ThrowsAsync<InvalidDataException>(() => provider.SearchAsync("粉可爱", default));
        else Assert.Empty((await provider.SearchAsync("粉可爱", default)).Items);
    }
    [Fact]
    public async Task FirstAidReadsEightSectionsSearchesLessonsAndRefreshesMedia()
    {
        int details = 0;
        string directory = string.Concat(Enumerable.Range(0, 8).Select(i => $"<div class=\"jj-title-li\"><li class=\"list-br3\"><a href=\"/jijiu/article/A{i}.html\"><div>课程{i}</div></a></li></div>"));
        using var http = new HttpClient(new Responses(request => Task.FromResult(request.RequestUri!.AbsolutePath == "/jijiu" ? directory : $"<h2 class=\"video-title h1-title\">课程0</h2><video id=\"video\" poster=\"https://cdn.example/poster.png\"><source src=\"https://cdn.example/video.mp4?token={++details}\"></video>")));
        await using var provider = new FirstAidProvider(Source("firstaid"), http);
        Assert.Equal(8, (await provider.GetCategoriesAsync(default)).Count);
        Assert.Equal("/jijiu/article/A7.html", Assert.Single((await provider.GetItemsAsync("7", null, default)).Items).Id);
        Assert.Single((await provider.SearchAsync("课程0", default)).Items);
        var first = await provider.ResolvePlaybackAsync("/jijiu/article/A0.html", "/jijiu/article/A0.html", default);
        var second = await provider.ResolvePlaybackAsync("/jijiu/article/A0.html", "/jijiu/article/A0.html", default);
        Assert.NotEqual(first.Uri, second.Uri); Assert.Equal("课程0", second.Title);
        await Assert.ThrowsAsync<InvalidDataException>(() => provider.GetDetailAsync("https://foreign.example/x", default));
        await Assert.ThrowsAsync<InvalidDataException>(() => provider.GetItemsAsync("8", null, default));
    }
    private sealed class Redirects(bool foreign) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri!.AbsolutePath == "/search/index")
            {
                var response = new HttpResponseMessage(HttpStatusCode.Found) { RequestMessage = request };
                response.Headers.Location = new Uri(foreign ? "https://foreign.example/search" : "/search/result", UriKind.RelativeOrAbsolute);
                return Task.FromResult(response);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent("<div class=\"list-con\"></div>") });
        }
    }
    [Theory]
    [InlineData("../../etc", "1556")]
    [InlineData("1556", "1557")]
    public async Task TuxiaobeiRejectsInvalidIdentityBeforeNetwork(string media, string episode)
    {
        using var http = new HttpClient(new Responses(_ => throw new Exception("unexpected network")));
        await using var provider = new TuxiaobeiProvider(Source("tuxiaobei"), http);
        await Assert.ThrowsAsync<InvalidDataException>(() => provider.ResolvePlaybackAsync(media, episode, default));
    }
    [Fact]
    public async Task TuxiaobeiRejectsMissingPlayerAndApiErrors()
    {
        using var http = new HttpClient(new Responses(_ => Task.FromResult("""{"status":1,"data":{"items":[]}}""")));
        await using var provider = new TuxiaobeiProvider(Source("tuxiaobei"), http);
        await Assert.ThrowsAsync<InvalidDataException>(() => provider.GetItemsAsync(null, null, default));
        await Assert.ThrowsAsync<InvalidDataException>(() => provider.GetDetailAsync("1556", default));
    }
    [Fact]
    public async Task HuyaReadsPaginationSearchAndSignsFreshLiveRequest()
    {
        using var http = new HttpClient(new Responses(request => Task.FromResult(request.RequestUri!.Host switch
        {
            "www.huya.com" => """{"status":200,"data":{"totalPage":2,"datas":[{"profileRoom":"11342412","introduction":"直播标题","screenshot":"https://cdn.example/cover.jpg","nick":"主播"}]}}""",
            "search.cdn.huya.com" => """{"response":{"3":{"docs":[{"room_id":11342412,"gameName":"一起看","game_screenshot":"https://cdn.example/cover.jpg","game_nick":"主播"}]}}}""",
            _ => HuyaRoom
        })));
        await using var provider = new HuyaProvider(Source("huya"), http);
        var list = await provider.GetItemsAsync(null, null, default); Assert.Equal("2", list.NextCursor); Assert.Single(list.Items);
        Assert.Equal("11342412", Assert.Single((await provider.SearchAsync("主播", default)).Items).Id);
        Assert.Single((await provider.GetDetailAsync("11342412", default)).PlaybackLines);
        var request = await provider.ResolvePlaybackAsync("11342412", "0", default);
        Assert.True(request.IsLive); Assert.Equal(ResolutionKind.Direct, request.ResolutionKind);
        Assert.DoesNotContain("wsSecret=old", request.Uri); Assert.DoesNotContain("fm=", request.Uri);
        Assert.Contains("u=0&seqid=", request.Uri); Assert.Contains("ctype=tars_mp", request.Uri); Assert.Contains("t=102", request.Uri);
        Assert.Equal("11342412", request.MediaId);
    }
    [Fact]
    public async Task HuyaRefusesOfflineRooms()
    {
        using var http = new HttpClient(new Responses(_ => Task.FromResult(HuyaRoom.Replace("\"ON\"", "\"OFF\""))));
        await using var provider = new HuyaProvider(Source("huya"), http);
        await Assert.ThrowsAsync<InvalidDataException>(() => provider.GetDetailAsync("11342412", default));
        await Assert.ThrowsAsync<InvalidDataException>(() => provider.ResolvePlaybackAsync("11342412", "0", default));
    }
    [Fact]
    public async Task DouyuParsesNewPageContextAndReturnsExplicitBrowserRequest()
    {
        using var http = new HttpClient(new Responses(async request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                string form = await request.Content!.ReadAsStringAsync(); Assert.Contains("sk=%E6%B5%8B%E8%AF%95", form);
                return """{"error":0,"data":{"list":[{"roomId":593392,"roomName":"直播标题","roomSrc":"https://cdn.example/cover.jpg"}]}}""";
            }
            if (request.RequestUri!.AbsolutePath.StartsWith("/api/")) return """{"code":0,"data":{"list":[{"rid":593392,"roomName":"直播标题","roomSrc":"https://cdn.example/cover.jpg"}]}}""";
            return DouyuRoom;
        }));
        await using var provider = new DouyuProvider(Source("douyu"), http);
        Assert.Equal("2", (await provider.GetItemsAsync("LOL", null, default)).NextCursor);
        Assert.Single((await provider.SearchAsync("测试", default)).Items);
        Assert.Equal("直播标题", (await provider.GetDetailAsync("593392", default)).Item.Title);
        var request = await provider.ResolvePlaybackAsync("593392", "593392", default);
        Assert.Equal(ResolutionKind.Browser, request.ResolutionKind); Assert.True(request.IsLive); Assert.Equal("https://m.douyu.com/593392", request.Uri);
    }
    [Fact]
    public async Task DouyuRefusesWrongRoomAndOfflineRoom()
    {
        foreach (string room in new[] { DouyuRoom.Replace("593392", "593393"), DouyuRoom.Replace("\"isLive\":1", "\"isLive\":0") })
        {
            using var http = new HttpClient(new Responses(_ => Task.FromResult(room)));
            await using var provider = new DouyuProvider(Source("douyu"), http);
            await Assert.ThrowsAsync<InvalidDataException>(() => provider.GetDetailAsync("593392", default));
        }
    }
    [Theory]
    [InlineData("tuxiaobei")]
    [InlineData("huya")]
    [InlineData("douyu")]
    public async Task CancelledOrDisposedProvidersDoNotIssueRequests(string profile)
    {
        using var http = new HttpClient(new Responses(_ => throw new Exception("unexpected network")));
        IContentProvider provider = profile switch { "tuxiaobei" => new TuxiaobeiProvider(Source(profile), http), "huya" => new HuyaProvider(Source(profile), http), _ => new DouyuProvider(Source(profile), http) };
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.GetItemsAsync(null, null, cancellation.Token));
        await provider.DisposeAsync(); await Assert.ThrowsAsync<ObjectDisposedException>(() => provider.GetItemsAsync(null, null, default));
    }
    private sealed class Responses(Func<HttpRequestMessage, Task<string>> response) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            => new(HttpStatusCode.OK) { Content = new StringContent(await response(request), Encoding.UTF8), RequestMessage = request };
    }
}
