using System.Net;
using System.Text.Json;
using VodBox.Core;
using VodBox.Infrastructure;
using Xunit;

namespace VodBox.Tests;

public sealed class AudioSiteProviderTests
{
    private static SourceDefinition Source(string host = "www.xsmp3.com") => new()
    {
        Id = "audio", Name = "Audio", Provider = "audio-site", Entry = "https://" + host,
        Options = JsonDocument.Parse("{\"protocol\":\"audio-zblog-v1\"}").RootElement.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.Clone())
    };
    private const string List = "<ul id='post_list_box'><li class='site post_list_li'><a href='/gdg-yq/show.html' title='One &amp; Two'><img src='/poster.jpg'></a></li></ul>";
    private const string Detail = "<meta property='og:title' content='Album &amp; Episodes'><meta name='description' content='Description'><meta property='og:image' content='/poster.jpg'><script>const ap = new APlayer({autoplay:0,audio:[{name:\"First, url: \\\"text\\\"\",artist:\"A\",url:\"//audio.example/track?generation=GEN\",cover:\"\"},{name:\"Second\",url:\"https://audio.example/second\"},]});</script>";

    [Theory]
    [InlineData("www.xsmp3.com", "gdg", 6)]
    [InlineData("www.psmp3.com", "ykc", 7)]
    public async Task BothProfilesReadPagesEscapedTracksAndRefreshPlayback(string host, string category, int count)
    {
        var routes = new Routes(); using var http = new HttpClient(routes); await using var provider = new AudioSiteProvider(Source(host), http);
        Assert.Equal(count, (await provider.GetCategoriesAsync(default)).Count);
        var page = await provider.GetItemsAsync(category, null, default);
        Assert.Equal("2", page.NextCursor); Assert.Equal("One & Two", Assert.Single(page.Items).Title);
        Assert.Equal("https://" + host + "/poster.jpg", page.Items[0].Poster);
        Assert.Null((await provider.GetItemsAsync(category, "2", default)).NextCursor);
        var detail = await provider.GetDetailAsync(page.Items[0].Id, default); await provider.GetDetailAsync(page.Items[0].Id, default);
        Assert.Equal(1, routes.Details); Assert.Equal("Album & Episodes", detail.Item.Title);
        Assert.Equal("First, url: \"text\"", detail.PlaybackLines[0].Episodes[0].Title);
        var first = await provider.ResolvePlaybackAsync(page.Items[0].Id, "0", default);
        var second = await provider.ResolvePlaybackAsync(page.Items[0].Id, "0", default);
        Assert.NotEqual(first.Uri, second.Uri); Assert.Equal(3, routes.Details); Assert.Equal(2, first.Headers.Count);
        Assert.Equal("https://" + host + "/gdg-yq/show.html", first.Headers["Referer"]);
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.SearchAsync("A & 中文", default));
    }

    [Theory]
    [InlineData("{name:\"Title\",url:runCode()}")]
    [InlineData("{name:\"Title\",url:\"file:///tmp/music\"}")]
    [InlineData("{name:\"Title\",name:\"Duplicate\",url:\"https://audio.example/music\"}")]
    [InlineData("{name:\"Title\",url:\"https://user:secret@audio.example/music\"}")]
    [InlineData("")]
    public async Task UnsupportedOrExecutableTrackDataNeverBecomesPlayback(string tracks)
    {
        var routes = new Routes { CustomDetail = "<meta property='og:title' content='Album'><script>const ap = new APlayer({audio:[" + tracks + "]});</script>" };
        using var http = new HttpClient(routes); await using var provider = new AudioSiteProvider(Source(), http);
        await Assert.ThrowsAsync<InvalidDataException>(() => provider.GetDetailAsync("gdg-yq/show.html", default));
    }

    [Fact]
    public async Task MissingListCrossOriginLinksAndOversizedHtmlFailExplicitly()
    {
        var routes = new Routes { CustomList = "<html>server error</html>" }; using var http = new HttpClient(routes);
        await using var provider = new AudioSiteProvider(Source(), http);
        await Assert.ThrowsAsync<InvalidDataException>(() => provider.GetItemsAsync("gdg", null, default));
        routes.CustomList = List.Replace("/gdg-yq/show.html", "https://elsewhere.example/show.html", StringComparison.Ordinal);
        await Assert.ThrowsAsync<InvalidDataException>(() => provider.GetItemsAsync("gdg", null, default));
        routes.CustomList = new string('x', 2 * 1024 * 1024 + 1);
        await Assert.ThrowsAsync<InvalidDataException>(() => provider.GetItemsAsync("gdg", null, default));
    }

    [Fact]
    public async Task PageRedirectsAndUnavailableSearchFailExplicitly()
    {
        var routes = new Routes { RedirectAgain = true }; using var http = new HttpClient(routes);
        await using var provider = new AudioSiteProvider(Source(), http);
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.SearchAsync("query", default)); Assert.Equal(0, routes.Requests);
        await Assert.ThrowsAsync<InvalidDataException>(() => provider.GetItemsAsync("gdg", null, default)); Assert.Equal(1, routes.Requests);
    }

    [Fact]
    public async Task FourIoSlotsCancellationAndDisposedChecksArePreserved()
    {
        var routes = new Routes { Wait = true }; using var http = new HttpClient(routes); await using var provider = new AudioSiteProvider(Source(), http);
        using var cancellation = new CancellationTokenSource();
        Task[] tasks = Enumerable.Range(0, 8).Select(_ => provider.GetItemsAsync("gdg", null, cancellation.Token)).ToArray();
        await routes.FourStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(4, routes.Active); cancellation.Cancel();
        foreach (var task in tasks) await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await task);
        Assert.Equal(0, routes.Active); routes.Wait = false; Assert.Single((await provider.GetItemsAsync("gdg", null, default)).Items);
        await provider.DisposeAsync(); await Assert.ThrowsAsync<ObjectDisposedException>(() => provider.GetDetailAsync("gdg-yq/show.html", default));
    }

    [Fact]
    public async Task UnverifiedProfilesPathsAndCursorsAreRejectedBeforeHttp()
    {
        var routes = new Routes(); using var http = new HttpClient(routes); await using var provider = new AudioSiteProvider(Source(), http);
        Assert.Throws<InvalidDataException>(() => new AudioSiteProvider(Source("unknown.example"), http));
        await Assert.ThrowsAsync<InvalidDataException>(() => provider.GetItemsAsync("unknown", null, default));
        await Assert.ThrowsAsync<InvalidDataException>(() => provider.GetItemsAsync("gdg", "1001", default));
        await Assert.ThrowsAsync<InvalidDataException>(() => provider.GetDetailAsync("../private.html", default));
        await Assert.ThrowsAsync<InvalidDataException>(() => provider.ResolvePlaybackAsync("gdg-yq/show.html", "-1", default));
        Assert.Equal(0, routes.Requests);
        Assert.IsType<AudioSiteProvider>(new ProviderFactory(http, "unused", "unused").Create(Source() with { Provider = "csp_XBPQ" }));
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.SearchPageAsync("query", "2", default));
    }

    private sealed class Routes : HttpMessageHandler
    {
        public string? CustomList, CustomDetail;
        public bool RedirectAgain, Wait;
        public int Details, Requests, Active;
        public TaskCompletionSource FourStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Interlocked.Increment(ref Requests); Assert.Single(request.Headers);
            if (Interlocked.Increment(ref Active) == 4) FourStarted.TrySetResult();
            try { if (Wait) await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { Interlocked.Decrement(ref Active); }
            Assert.Equal(HttpMethod.Get, request.Method);
            if (RedirectAgain) return new(HttpStatusCode.Found);
            if (request.RequestUri!.AbsolutePath == "/gdg-yq/show.html")
            {
                string detail = CustomDetail ?? Detail.Replace("GEN", Interlocked.Increment(ref Details).ToString(), StringComparison.Ordinal);
                if (request.RequestUri.Host == "www.psmp3.com") detail = detail.Replace("<meta property='og:title' content='Album &amp; Episodes'>", "<h1><span>Album &amp; Episodes</span></h1>", StringComparison.Ordinal);
                return Html(detail);
            }
            string category = request.RequestUri.AbsolutePath.Split('/')[1];
            return Html(CustomList ?? (request.RequestUri.AbsolutePath.EndsWith("/2.html", StringComparison.Ordinal)
                ? "<ul id='post_list_box'></ul>" : List + "<a class='next' href='/" + category + "/2.html'>Next</a>"));
        }
        private static HttpResponseMessage Html(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text) };
    }
}
