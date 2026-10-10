using System.Net;
using VodBox.Core;
using VodBox.Infrastructure;
using Xunit;

namespace VodBox.Tests;

public sealed class AudioSiteSourceTests
{
    private static SourceInfo Site(string host = "www.xsmp3.com") => new()
    {
        Key = "audio", Name = "相声", Api = "csp_XBPQ", Runtime = SourceRuntime.NativeSpider,
        Ext = "{\"主页url\":\"https://" + host + "/\"}"
    };

    [Fact]
    public async Task CategoriesCardsDetailAndFreshPlaybackWork()
    {
        using var http = new HttpClient(new Handler());
        using var source = new AudioSiteSource(Site(), http);
        Assert.Equal("gdg", (await source.GetCategoriesAsync(TestContext.Current.CancellationToken))[0].Id);
        var page = await source.GetHomeAsync(TestContext.Current.CancellationToken);
        var item = Assert.Single(page.Items);
        Assert.Equal("gdg-yq/show.html", item.Id);
        var detail = await source.GetDetailAsync(item.Id, TestContext.Current.CancellationToken);
        Assert.Equal("专辑 & 标题", detail.Item.Title);
        var episode = Assert.Single(Assert.Single(detail.Lines).Episodes);
        var request = await source.ResolvePlaybackAsync(item.Id, episode.Id, TestContext.Current.CancellationToken);
        Assert.Equal("audio", request.SourceKey);
        Assert.Equal("audio", request.LineId);
        Assert.Contains("token=2", request.Uri);
        Assert.Equal("https://www.xsmp3.com/gdg-yq/show.html", request.Headers["Referer"]);
    }

    [Fact]
    public void ImportOnlyEnablesVerifiedAudioRulesNotArbitraryXbpq()
    {
        var config = ConfigLoader.Parse("""{"sites":[{"key":"audio","name":"相声","api":"csp_XBPQ","ext":{"主页url":"https://www.xsmp3.com"}},{"key":"unknown","name":"未知","api":"csp_XBPQ","ext":{"主页url":"https://example.invalid"}}]}""")!;
        var info = Assert.Single(ConfigLoader.ToSources(config));
        Assert.Equal("audio", info.Key);
        using var source = Assert.IsType<AudioSiteSource>(NativeSpiders.Create(info));
        Assert.False(NativeSpiders.IsSupported("csp_XBPQ"));
    }

    [Fact]
    public void UnknownHostsAreNotTreatedAsGenericXbpq()
    {
        Assert.Throws<InvalidDataException>(() => new AudioSiteSource(Site("example.invalid")));
    }

    [Fact]
    public async Task TraversalAndUnsupportedSearchAreExplicitlyRejected()
    {
        using var source = new AudioSiteSource(Site());
        await Assert.ThrowsAsync<InvalidDataException>(() => source.GetDetailAsync("../secret", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<NotSupportedException>(() => source.SearchAsync("测试", 1, TestContext.Current.CancellationToken));
    }

    private sealed class Handler : HttpMessageHandler
    {
        private int _details;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string html;
            if (request.RequestUri!.AbsolutePath.EndsWith("show.html", StringComparison.Ordinal))
                html = "<meta property='og:title' content='专辑 &amp; 标题'><script>const ap = new APlayer({audio:[{name:\"第一集\",url:\"https://cdn.example.invalid/a.mp3?token=" + ++_details + "\"}]});</script>";
            else
                html = "<ul id='post_list_box'><li class='www_xsmp3_com post_list_li'><a href='/gdg-yq/show.html' title='专辑'><img src='/cover.jpg'></a></li></ul>";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(html) });
        }
    }
}
