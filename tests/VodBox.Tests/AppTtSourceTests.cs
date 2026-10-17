using System.Net;
using System.Text.Json;
using VodBox.Core;
using VodBox.Infrastructure;
using Xunit;

namespace VodBox.Tests;

public sealed class AppTtSourceTests
{
    private static SourceInfo Site => new() { Key = "tt", Name = "热播", Api = "csp_AppTTGuard", Ext = "https://api.example", Runtime = SourceRuntime.NativeSpider };
    private const string Vod = """{"vod_id":7,"vod_name":"中文电影","vod_pic":"","vod_pic_thumb":"https://cdn.example/cover.jpg","vod_content":"&lt;p&gt;剧情&lt;/p&gt;"}""";
    private const string Detail = """
        {"data":{"vod_id":7,"vod_name":"中文电影","vod_content":"&lt;p&gt;剧情&lt;/p&gt;","vod_play_list":[
        {"flag":"direct","urls":[{"name":"第一集","url":"https://media.example/direct.mp4"}]},
        {"flag":"json","parse_urls":[" ","null","https://parser.example/?url="],"urls":[{"name":"正片","url":"https://web.example/watch/7"}]}]}}
        """;
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<string>> run) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            new(HttpStatusCode.OK) { Content = new StringContent(await run(request, ct)) };
    }

    [Fact]
    public void RealEncryptedExtIsDecodedAndUnsupportedShapesRemainUnmapped()
    {
        var config = ConfigLoader.ParseBytes(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "fty-in.bmp")))!;
        var site = Assert.Single(config.Sites, s => s.Api == "csp_AppTTGuard");
        Assert.Equal("http://v.rbotv.cn/", AppTtSource.Endpoint(site.Ext!.Value.GetString()).AbsoluteUri);
        Assert.True(AppTtSource.Supports(site.Ext.Value.GetString()));
        foreach (string ext in new[] { "", "invalid", "file:///tmp", "https://user:pass@example/", "https://api.example/subpath", "https://api.example/?q=1" })
            Assert.False(AppTtSource.Supports(ext));
    }

    [Fact]
    public async Task SignedMultipartHomeCategoryAndSinglePageSearchUseTheAppTtContract()
    {
        var paths = new List<string>();
        using var handler = new Handler(async (request, ct) =>
        {
            string path = request.RequestUri!.AbsolutePath;
            paths.Add(path);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(AppTtSource.UserAgent, request.Headers.UserAgent.ToString());
            var multipart = Assert.IsType<MultipartFormDataContent>(request.Content);
            var fields = new Dictionary<string, string>();
            foreach (var part in multipart) fields[part.Headers.ContentDisposition!.Name!.Trim('"')] = await part.ReadAsStringAsync(ct);
            Assert.Equal("1000", fields["timestamp"]);
            Assert.Equal("fd567853aea2bf33a2deb8c4d000b284", fields["sign"]);
            switch (path)
            {
                case "/v3/type/top_type":
                    Assert.Equal("", fields[""]);
                    return """{"data":{"list":[{"type_id":1,"type_name":"电影"}]}}""";
                case "/v3/type/tj_vod":
                    Assert.Equal("", fields[""]);
                    return "{\"data\":{\"type_vod\":[{\"vod\":[" + Vod + "]}]}}";
                case "/v3/home/type_search":
                    Assert.Equal("12", fields["limit"]);
                    Assert.Equal("1", fields["type_id"]);
                    return fields["page"] == "3" ? "{\"data\":{\"list\":[]}}" : "{\"data\":{\"list\":[" + Vod + "]}}";
                case "/v3/home/search":
                    Assert.Equal("中文", fields["keyword"]);
                    Assert.DoesNotContain("page", fields.Keys);
                    Assert.DoesNotContain("limit", fields.Keys);
                    return "{\"data\":{\"list\":[" + Vod + ",{\"vod_id\":8,\"vod_name\":\"其他\"}]}}";
                default: throw new InvalidOperationException(path);
            }
        });
        using var http = new HttpClient(handler);
        var source = new AppTtSource(Site, http, () => 1000);
        Assert.Equal(new Category("1", "电影"), Assert.Single(await source.GetCategoriesAsync(TestContext.Current.CancellationToken)));
        Assert.Equal("https://cdn.example/cover.jpg", Assert.Single((await source.GetHomeAsync(TestContext.Current.CancellationToken)).Items).Poster);
        Assert.Equal(3, (await source.GetItemsAsync("1", 2, null, TestContext.Current.CancellationToken)).PageCount);
        Assert.Empty((await source.GetItemsAsync("1", 3, null, TestContext.Current.CancellationToken)).Items);
        Assert.Single((await source.SearchAsync("中文", 1, TestContext.Current.CancellationToken)).Items);
        Assert.Empty((await source.SearchAsync("中文", 2, TestContext.Current.CancellationToken)).Items);
        Assert.Equal(5, paths.Count);
    }

    [Fact]
    public async Task PlaybackRefreshPreservesJarIdentityAndParserHeadersWithoutRjSigningSuffix()
    {
        int details = 0;
        using var handler = new Handler((request, _) =>
        {
            if (request.Method == HttpMethod.Post) { details++; return Task.FromResult(Detail); }
            Assert.Equal("https://parser.example/?url=https://web.example/watch/7", request.RequestUri!.AbsoluteUri);
            return Task.FromResult("""{"data":{"url":"https://media.example/parsed.mp4","header":"{\"Referer\":\"https://web.example/\"}","UA":"media-agent"}}""");
        });
        using var http = new HttpClient(handler);
        var source = new AppTtSource(Site, http, () => 1000);
        var detail = await source.GetDetailAsync("7", TestContext.Current.CancellationToken);
        Assert.Equal("剧情", detail.Description);
        Assert.Equal("免费分享 仅供测试\u0001https://media.example/direct.mp4", detail.Lines[0].Episodes[0].Id);
        Assert.Equal("direct", detail.Lines[0].Name);
        var direct = await source.ResolvePlaybackAsync("7", detail.Lines[0].Episodes[0].Id, TestContext.Current.CancellationToken);
        Assert.Equal("免费分享 仅供测试", direct.LineId);
        Assert.Equal(ResolutionKind.Direct, direct.Resolution);
        var parsed = await source.ResolvePlaybackAsync("7", detail.Lines[1].Episodes[0].Id, TestContext.Current.CancellationToken);
        Assert.Equal("https://media.example/parsed.mp4", parsed.Uri);
        Assert.Equal("https://web.example/", parsed.Headers["Referer"]);
        Assert.Equal("media-agent", parsed.Headers["User-Agent"]);
        Assert.Equal(3, details);
        await Assert.ThrowsAsync<InvalidDataException>(() => source.ResolvePlaybackAsync("7", "missing", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"data\":[]}")]
    [InlineData("{\"data\":{\"list\":null}}")]
    public async Task MalformedResponsesAreExplicit(string text)
    {
        using var handler = new Handler((_, _) => Task.FromResult(text));
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => new AppTtSource(Site, http, () => 1).GetCategoriesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CancellationAndUnsupportedFiltersDoNotBecomeEmptySuccess()
    {
        using var handler = new Handler(async (_, ct) => { await Task.Delay(Timeout.InfiniteTimeSpan, ct); return "{}"; });
        using var http = new HttpClient(handler);
        var source = new AppTtSource(Site, http, () => 1);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.GetCategoriesAsync(timeout.Token));
        await Assert.ThrowsAsync<NotSupportedException>(() => source.GetItemsAsync("1", 1, new Dictionary<string, string> { ["year"] = "2026" }, TestContext.Current.CancellationToken));
    }
}
