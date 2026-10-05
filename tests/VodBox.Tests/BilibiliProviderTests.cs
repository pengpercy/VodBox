using System.Net;
using System.Text.Json;
using VodBox.Core;
using VodBox.Infrastructure;
using Xunit;

namespace VodBox.Tests;

public sealed class BilibiliProviderTests
{
    private const string Bvid = "BV1WSHL66EdZ";
    private static SourceDefinition PlaylistSource(string categories) => new()
    {
        Id = "playlist", Name = "Playlist", Provider = "bilibili",
        Options = new() { ["categories"] = JsonDocument.Parse(categories).RootElement.Clone() }
    };

    [Fact]
    public async Task CuratedPlaylistPreservesOrderPagesAndCachesDetailsWithFourWorkers()
    {
        string ids = string.Join(',', Enumerable.Range(1, 25).Select(x => $"\"av{x}\""));
        var handler = new PlaylistRoutes(); using var http = new HttpClient(handler);
        await using var provider = new BilibiliProvider(PlaylistSource("[{\"id\":\"lessons\",\"name\":\"课程\",\"videos\":[" + ids + "]}]"), http);
        var first = await provider.GetItemsAsync("lessons", null, default);
        Assert.Equal(Enumerable.Range(1, 20).Select(x => $"av{x}"), first.Items.Select(x => x.Id));
        Assert.Equal("2", first.NextCursor); Assert.InRange(handler.MaximumActive, 2, 4);
        var second = await provider.GetItemsAsync("lessons", first.NextCursor, default);
        Assert.Equal(Enumerable.Range(21, 5).Select(x => $"av{x}"), second.Items.Select(x => x.Id));
        Assert.Null(second.NextCursor);
        Assert.Empty((await provider.GetItemsAsync("lessons", "3", default)).Items);
        await provider.GetItemsAsync("lessons", null, default);
        Assert.Equal(25, handler.Requests);
        Assert.Equal("Video 1", first.Items[0].Title);
    }

    [Theory]
    [InlineData("{\"query\":\"x\",\"videos\":[\"av1\"]}")]
    [InlineData("{}")]
    [InlineData("{\"videos\":[]}")]
    [InlineData("{\"videos\":[\"av1\",\"av1\"]}")]
    [InlineData("{\"videos\":[123]}")]
    [InlineData("{\"videos\":[\"https://example.com\"]}")]
    [InlineData("{\"query\":null}")]
    public void InvalidPlaylistConfigurationFailsBeforeNetwork(string fields)
    {
        string body = fields[1..^1];
        string category = "[{\"id\":\"lessons\",\"name\":\"课程\"" + (body.Length == 0 ? "" : "," + body) + "}]";
        Assert.Throws<InvalidDataException>(() => new BilibiliProvider(PlaylistSource(category)));
    }

    [Fact]
    public async Task CuratedPlaylistCancelsWorkersAndDoesNotSilentlySkipInaccessibleVideos()
    {
        var handler = new PlaylistRoutes { Block = true }; using var http = new HttpClient(handler);
        await using var provider = new BilibiliProvider(PlaylistSource("[{\"id\":\"lessons\",\"name\":\"课程\",\"videos\":[\"av1\",\"av2\"]}]"), http);
        using var cancellation = new CancellationTokenSource();
        var page = provider.GetItemsAsync("lessons", null, cancellation.Token);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await page);
        Assert.Equal(0, handler.Active);
        handler.Block = false; handler.Fail = true;
        await Assert.ThrowsAsync<InvalidDataException>(() => provider.GetItemsAsync("lessons", null, default));
        Assert.Equal(0, handler.Active);
        handler.Fail = false;
        Assert.Equal(2, (await provider.GetItemsAsync("lessons", null, default)).Items.Count);
    }

    private sealed class PlaylistRoutes : HttpMessageHandler
    {
        public int Requests, Active, MaximumActive;
        public bool Block, Fail;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.Equal("/x/web-interface/view", request.RequestUri!.AbsolutePath);
            int aid = int.Parse(request.RequestUri.Query.Split('=')[1]);
            Interlocked.Increment(ref Requests);
            int active = Interlocked.Increment(ref Active);
            int maximum;
            do { maximum = Volatile.Read(ref MaximumActive); }
            while (maximum < active && Interlocked.CompareExchange(ref MaximumActive, active, maximum) != maximum);
            Started.TrySetResult();
            try
            {
                await Task.Delay(Block ? Timeout.Infinite : 30 + (aid % 4) * 10, token);
                return Json(Fail ? "{\"code\":-404,\"message\":\"not available\"}" :
                    "{\"code\":0,\"data\":{\"aid\":" + aid + ",\"bvid\":\"" + Bvid + "\",\"title\":\"Video " + aid + "\",\"pages\":[{\"cid\":" + aid + ",\"part\":\"Main\"}]}}");
            }
            finally { Interlocked.Decrement(ref Active); }
        }
    }
    private static SourceDefinition Source() => new()
    {
        Id = "bili", Name = "Bilibili", Provider = "bilibili",
        Options = new()
        {
            ["cookie"] = JsonSerializer.SerializeToElement("SESSDATA=fixture", VodBoxJson.Default.String),
            ["categories"] = JsonDocument.Parse("[{\"id\":\"code\",\"name\":\"Code\",\"query\":\"C# & 中文\"}]").RootElement.Clone()
        }
    };

    [Fact]
    public async Task PublicSpiderPagesSearchDetailsAndFreshPlaybackShareStableIdentity()
    {
        var handler = new Routes(); using var http = new HttpClient(handler);
        await using var provider = new BilibiliProvider(Source(), http);
        Assert.Equal(2, (await provider.GetCategoriesAsync(default)).Count);
        var popular = await provider.GetItemsAsync("popular", null, default);
        Assert.Equal("2", popular.NextCursor); Assert.Equal(Bvid, Assert.Single(popular.Items).Id);
        Assert.Null((await provider.GetItemsAsync("popular", "2", default)).NextCursor);
        var search = await provider.GetItemsAsync("code", null, default);
        Assert.Equal("2", search.NextCursor); Assert.Equal("C# & 中文", Assert.Single(search.Items).Title);
        Assert.Equal("https://i.example.com/poster.jpg", search.Items[0].Poster);
        Assert.Null((await provider.SearchPageAsync("C# & 中文", search.NextCursor, default)).NextCursor);
        var detail = await provider.GetDetailAsync(Bvid, default);
        Assert.Equal("Description & details", detail.Description);
        Assert.Equal(["42000000001", "42000000002"], detail.PlaybackLines[0].Episodes.Select(x => x.Id).ToArray());
        var first = await provider.ResolvePlaybackAsync(Bvid, "42000000002", default);
        var second = await provider.ResolvePlaybackAsync(Bvid, "42000000002", default);
        Assert.Equal("https://cdn.example.com/video.mp4?generation=1", first.Uri);
        Assert.Equal("https://cdn.example.com/video.mp4?generation=2", second.Uri);
        Assert.Equal("https://comment.bilibili.com/42000000002.xml", first.DanmakuUri);
        Assert.Equal("bili", first.SourceId); Assert.Equal(Bvid, first.MediaId); Assert.Equal("42000000002", first.EpisodeId);
        Assert.Equal("https://www.bilibili.com/", first.Headers["Referer"]);
        Assert.False(first.Headers.ContainsKey("Cookie")); Assert.Equal(1, handler.DetailRequests); Assert.Equal(1, handler.NavigationRequests);
        await Assert.ThrowsAsync<InvalidDataException>(() => provider.ResolvePlaybackAsync(Bvid, "42000000003", default));
        Assert.Equal(2, handler.PlayRequests);
    }

    [Theory]
    [InlineData("{\"code\":-412,\"message\":\"request blocked\"}", "request blocked")]
    [InlineData("{\"code\":0,\"data\":{\"durl\":[]}}", "DASH")]
    [InlineData("{\"code\":0,\"data\":{\"durl\":[{\"url\":\"https://a.example/1\"},{\"url\":\"https://a.example/2\"}]}}", "多段")]
    [InlineData("{\"code\":0,\"data\":{\"is_preview\":1,\"durl\":[{\"url\":\"https://a.example/1\"}]}}", "试看")]
    [InlineData("{\"code\":0,\"data\":{\"durl\":[{\"url\":\"file:///tmp/video.mp4\"}]}}", "地址无效")]
    public async Task UnplayableResponsesFailWithoutPlayingOnlyTheFirstSegment(string json, string message)
    {
        using var http = new HttpClient(new Routes { PlaybackJson = json });
        await using var provider = new BilibiliProvider(Source(), http);
        var error = await Assert.ThrowsAnyAsync<Exception>(() => provider.ResolvePlaybackAsync(Bvid, "42000000001", default));
        Assert.Contains(message, error.Message);
    }

    [Fact]
    public async Task InvalidIdsCursorsAndCancelledRequestsNeverReachNetwork()
    {
        var handler = new Routes(); using var http = new HttpClient(handler);
        var provider = new BilibiliProvider(Source(), http);
        await Assert.ThrowsAsync<InvalidDataException>(() => provider.GetDetailAsync("https://other.example/video", default));
        await Assert.ThrowsAsync<InvalidDataException>(() => provider.GetItemsAsync("popular", "1001", default));
        await Assert.ThrowsAsync<InvalidDataException>(() => provider.GetItemsAsync("missing", null, default));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.SearchAsync("query", cancelled.Token));
        Assert.Equal(0, handler.Requests);
        await provider.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => provider.SearchAsync("query", default));
    }

    [Fact]
    public async Task AwaitingIoHonoursCancellationAndReleasesConcurrencySlots()
    {
        var handler = new Waiting(); using var http = new HttpClient(handler);
        await using var provider = new BilibiliProvider(new() { Id = "b", Name = "b" }, http);
        using var cancellation = new CancellationTokenSource();
        Task[] requests = Enumerable.Range(0, 8).Select(_ => provider.SearchAsync("query", cancellation.Token)).ToArray();
        await handler.FourStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(4, handler.Active); cancellation.Cancel();
        foreach (var request in requests) await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await request);
        Assert.Equal(0, handler.Active);
        handler.Wait = false;
        Assert.Empty((await provider.SearchAsync("next", default)).Items);
    }

    [Fact]
    public void KnownSpiderAliasIsExplicitAndOtherJavaNamesRemainUnsupported()
    {
        var factory = new ProviderFactory(new HttpClient(), "unused", "unused");
        Assert.IsType<BilibiliProvider>(factory.Create(new() { Id = "b", Name = "b", Provider = "csp_Bili" }));
        Assert.Throws<NotSupportedException>(() => factory.Create(new() { Id = "b", Name = "b", Provider = "csp_AppGet" }));
        var source = Source() with { Options = new() { ["cookie"] = JsonSerializer.SerializeToElement("bad\r\ncookie", VodBoxJson.Default.String) } };
        Assert.Throws<InvalidDataException>(() => new BilibiliProvider(source));
    }

    private sealed class Waiting : HttpMessageHandler
    {
        private int _active;
        public int Active => Volatile.Read(ref _active);
        public bool Wait = true;
        public TaskCompletionSource FourStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/nav", StringComparison.Ordinal)) return Json(Navigation);
            if (request.RequestUri.AbsolutePath.EndsWith("/spi", StringComparison.Ordinal)) return Json(Visitor);
            if (Interlocked.Increment(ref _active) == 4) FourStarted.TrySetResult();
            try
            {
                if (Wait) await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return new(HttpStatusCode.OK) { Content = new StringContent("{\"code\":0,\"data\":{\"numPages\":1,\"result\":[]}}") };
            }
            finally { Interlocked.Decrement(ref _active); }
        }
    }

    private sealed class Routes : HttpMessageHandler
    {
        public int Requests, DetailRequests, PlayRequests, NavigationRequests;
        public string? PlaybackJson;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests++;
            Assert.Equal("api.bilibili.com", request.RequestUri!.Host); Assert.Equal("https", request.RequestUri.Scheme);
            Assert.Contains("SESSDATA=fixture", Assert.Single(request.Headers.GetValues("Cookie")));
            Assert.Equal("https://www.bilibili.com/", request.Headers.Referrer!.AbsoluteUri);
            string path = request.RequestUri.AbsolutePath, query = request.RequestUri.Query, json;
            if (path.EndsWith("/nav", StringComparison.Ordinal))
            {
                NavigationRequests++; json = Navigation;
            }
            else if (path.EndsWith("/spi", StringComparison.Ordinal)) json = Visitor;
            else if (path.EndsWith("/view", StringComparison.Ordinal))
            {
                DetailRequests++; Assert.Contains("bvid=" + Bvid, query);
                json = "{\"code\":0,\"data\":{\"bvid\":\"" + Bvid + "\",\"title\":\"Title\",\"desc\":\"<p>Description &amp; details</p>\",\"pages\":[{\"cid\":42000000001,\"part\":\"P1\"},{\"cid\":42000000002,\"part\":\"P2\"}]}}";
            }
            else if (path.EndsWith("/playurl", StringComparison.Ordinal))
            {
                PlayRequests++; Assert.Contains("qn=16", query); Assert.Contains("fnval=1", query);
                json = PlaybackJson ?? "{\"code\":0,\"data\":{\"durl\":[{\"url\":\"https://cdn.example.com/video.mp4?generation=" + PlayRequests + "\"}]}}";
            }
            else if (path.EndsWith("/popular", StringComparison.Ordinal))
                json = "{\"code\":0,\"data\":{\"no_more\":" + (query.Contains("pn=2", StringComparison.Ordinal) ? "true" : "false") + ",\"list\":[{\"bvid\":\"" + Bvid + "\",\"title\":\"popular\"}]}}";
            else
            {
                Assert.EndsWith("/wbi/search/type", path); Assert.Contains("keyword=C%23%20%26%20%E4%B8%AD%E6%96%87", query);
                Assert.Contains("buvid3=visitor3", Assert.Single(request.Headers.GetValues("Cookie")));
                string unsigned = query.TrimStart('?').Split('&').Where(x => !x.StartsWith("w_rid=", StringComparison.Ordinal)).Aggregate((a, b) => a + "&" + b);
                string expected = Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(unsigned + "ea1db124af3c7062474693fa704f4ff8")));
                Assert.Contains("w_rid=" + expected, query);
                json = "{\"code\":0,\"data\":{\"numPages\":2,\"result\":[{\"bvid\":\"" + Bvid + "\",\"title\":\"<em class='keyword'>C#</em> &amp; 中文\",\"pic\":\"//i.example.com/poster.jpg\",\"duration\":\"5:00\"}]}}";
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }
    private const string Navigation = "{\"code\":-101,\"data\":{\"wbi_img\":{\"img_url\":\"https://i.example.com/7cd084941338484aae1ad9425b84077c.png\",\"sub_url\":\"https://i.example.com/4932caff0ff746eab6f01bf08b70ac45.png\"}}}";
    private const string Visitor = "{\"code\":0,\"data\":{\"b_3\":\"visitor3\",\"b_4\":\"visitor4+/==\"}}";
    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value) };
}
