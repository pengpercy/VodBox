using System.Net;
using System.Text.Json;
using VodBox.Core;
using VodBox.Infrastructure;
using Xunit;

namespace VodBox.Tests;

public sealed class AppSxSourceTests
{
    private const string Key = "0123456789abcdef";
    private static SourceInfo Site => new() { Key = "sx", Name = "SX", Api = "csp_AppSxGuard", Runtime = SourceRuntime.NativeSpider, Ext = """{"host":"http://127.0.0.1:4905","key":"0123456789abcdef"}""" };
    private static JsonDocument Fixture(string name) => JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", name)));
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<string>> run) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => new(HttpStatusCode.OK) { Content = new StringContent(await run(request, ct)) };
    }
    private static string Envelope(string text) => "{\"data\":\"" + AppSxSource.Encrypt(text, Key, Key) + "\"}";
    private static Dictionary<string, string> Fields(string text) => text.Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(pair => pair.Split('=', 2)).ToDictionary(pair => WebUtility.UrlDecode(pair[0]), pair => pair.Length > 1 ? WebUtility.UrlDecode(pair[1]) : "");

    [Fact]
    public void CryptoMatchesIndependentNativeHeaderAndWrappedPlaybackVectors()
    {
        Assert.Equal("WJVYPixH44bauOXBO8jLJw==", AppSxSource.Encrypt("1792259822", Key, Key));
        Assert.Equal("ViX0Z41I0gkvvhYrB4WAw3yjEN0oCfDNxHJy86fdv/xbc5P8taBrAYgXEdw7iytR\n", AppSxSource.Encrypt("https://media.example/direct.mp4", Key, Key, wrapped: true));
        Assert.Equal("1792259822", AppSxSource.Decode("WJVYPixH44bauOXBO8jLJw==", Key, Key));
        Assert.Throws<InvalidDataException>(() => AppSxSource.Decode("invalid", Key, Key));
    }

    [Fact]
    public void BothRealExtsUseTheVerifiedBranchAndOtherVersionsRemainUnmapped()
    {
        var config = ConfigLoader.ParseBytes(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "fty-in.bmp")))!;
        var sites = config.Sites.Where(site => site.Api == "csp_AppSxGuard").ToArray();
        Assert.Equal(2, sites.Length);
        Assert.All(sites, site => Assert.True(AppSxSource.Supports(site.Ext!.Value.GetString())));
        foreach (string extra in new[] { "\"session\":1", "\"token\":\"credential\"", "\"ver\":120", "\"path\":\"/other/\"", "\"biz\":\"1\"" })
            Assert.False(AppSxSource.Supports("{\"host\":\"https://api.example\",\"key\":\"0123456789abcdef\"," + extra + "}"));
        Assert.False(AppSxSource.Supports("""{"host":"file:///tmp","key":"0123456789abcdef"}"""));
        Assert.False(AppSxSource.Supports("""{"host":"https://api.example","key":"short"}"""));
    }

    [Fact]
    public async Task CategoriesFiltersHomeListsAndSearchMatchTheOriginalOracle()
    {
        using var responses = Fixture("fty-appsx-responses.json");
        using var oracle = Fixture("fty-appsx-oracle.json");
        var calls = new List<string>();
        using var handler = new Handler(async (request, ct) =>
        {
            string operation = request.RequestUri!.Segments[^1]; calls.Add(operation);
            Assert.StartsWith("/api.php/getappapi.index/", request.RequestUri.AbsolutePath, StringComparison.Ordinal);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("okhttp/3.14.9", request.Headers.UserAgent.ToString());
            Assert.Equal("WJVYPixH44bauOXBO8jLJw==", Assert.Single(request.Headers.GetValues("app-api-verify-sign")));
            Assert.Equal("fixture-device", Assert.Single(request.Headers.GetValues("app-user-device-id")));
            var fields = Fields(await request.Content!.ReadAsStringAsync(ct));
            if (operation == "typeFilterVodList")
            {
                Assert.Equal("1", fields["type_id"]); Assert.Equal("2", fields["page"]);
                Assert.Equal("动作", fields["class"]); Assert.Equal("全部", fields["area"]);
                Assert.Equal(7, fields.Count);
                return Envelope("{\"recommend_list\":[]}");
            }
            if (operation == "searchList")
            {
                Assert.Equal("中文", fields["keywords"]); Assert.Equal("1", fields["page"]); Assert.Equal("0", fields["type_id"]);
            }
            return Envelope(responses.RootElement.GetProperty(operation).GetRawText());
        });
        using var http = new HttpClient(handler);
        var source = new AppSxSource(Site, http, () => 1792259822, "fixture-device");
        Assert.Equal(new Category("1", "电影"), Assert.Single(await source.GetCategoriesAsync(TestContext.Current.CancellationToken)));
        var home = Assert.Single((await source.GetHomeAsync(TestContext.Current.CancellationToken)).Items);
        var expected = oracle.RootElement.GetProperty("results")[0].GetProperty("response").GetProperty("list")[0];
        Assert.Equal(expected.GetProperty("vod_name").GetString(), home.Title);
        Assert.Equal(expected.GetProperty("vod_pic").GetString(), home.Poster);
        var filters = await source.GetFiltersAsync("1", TestContext.Current.CancellationToken);
        Assert.Equal(new[] { "class", "area", "lang", "year", "sort" }, filters.Select(filter => filter.Key));
        var page = await source.GetItemsAsync("1", 2, new Dictionary<string, string> { ["class"] = "动作" }, TestContext.Current.CancellationToken);
        Assert.Empty(page.Items); Assert.Equal(2, page.PageCount);
        Assert.Single((await source.SearchAsync("中文", 1, TestContext.Current.CancellationToken)).Items);
        Assert.Empty((await source.SearchAsync("中文", 2, TestContext.Current.CancellationToken)).Items);
        Assert.Equal(5, calls.Count);
    }

    [Fact]
    public async Task AllNinePlaybackBranchesAndJarEpisodeIdentitiesMatchOriginalPlugin()
    {
        using var responses = Fixture("fty-appsx-responses.json");
        using var oracle = Fixture("fty-appsx-oracle.json");
        int details = 0, parses = 0, external = 0;
        using var handler = new Handler(async (request, ct) =>
        {
            string operation = request.RequestUri!.Segments[^1];
            if (request.RequestUri.AbsolutePath == "/parse")
            {
                external++;
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal("?token=fixture-token&url=https%3A%2F%2Fwatch.example%2F7", request.RequestUri.Query);
                Assert.False(request.Headers.Contains("app-api-verify-sign"));
                Assert.False(request.Headers.Contains("app-user-device-id"));
                Assert.Equal("media-agent", request.Headers.UserAgent.ToString());
                return """{"json":"{\"data\":{\"url\":\"https://media.example/external.mp4\"}}"}""";
            }
            if (operation == "vodDetail") details++;
            if (operation == "vodParse")
            {
                parses++;
                var fields = Fields(await request.Content!.ReadAsStringAsync(ct));
                Assert.EndsWith("\n", fields["url"], StringComparison.Ordinal);
                Assert.Equal("fixture-token", fields["token"]);
                Assert.Equal(4, fields.Count);
            }
            return Envelope(responses.RootElement.GetProperty(operation).GetRawText());
        });
        using var http = new HttpClient(handler);
        var source = new AppSxSource(Site, http, () => 1792259822, "fixture-device");
        var detail = await source.GetDetailAsync("7", TestContext.Current.CancellationToken);
        Assert.Equal(9, detail.Lines.Count);
        var reference = oracle.RootElement.GetProperty("results");
        for (int index = 0; index < 9; index++)
        {
            var line = detail.Lines[index]; var episode = Assert.Single(line.Episodes);
            var original = reference[index + 4]; var request = original.GetProperty("request"); var expected = original.GetProperty("response");
            Assert.Equal(request.GetProperty("flag").GetString() + '\u0001' + request.GetProperty("play").GetString(), episode.Id);
            var play = await source.ResolvePlaybackAsync("7", episode.Id, TestContext.Current.CancellationToken);
            Assert.Equal(expected.GetProperty("url").GetString(), play.Uri);
            Assert.Equal(expected.GetProperty("parse").GetInt32() == 1 ? ResolutionKind.Sniff : ResolutionKind.Direct, play.Resolution);
            Assert.Equal("media-agent", play.Headers["User-Agent"]);
            Assert.Equal("https://watch.example/", play.Headers["Referer"]);
        }
        Assert.Equal(10, details); Assert.Equal(3, parses); Assert.Equal(3, external);
        await Assert.ThrowsAsync<InvalidDataException>(() => source.ResolvePlaybackAsync("7", "missing", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CancellationChallengeAndMalformedResponsesAreVisible()
    {
        using var handler = new Handler(async (_, ct) => { await Task.Delay(Timeout.InfiniteTimeSpan, ct); return "{}"; });
        using var http = new HttpClient(handler);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new AppSxSource(Site, http, () => 1, "device").GetCategoriesAsync(timeout.Token));
        using var invalidHandler = new Handler((_, _) => Task.FromResult("{\"code\":0,\"msg\":\"captcha\"}"));
        using var invalid = new HttpClient(invalidHandler);
        await Assert.ThrowsAsync<InvalidDataException>(() => new AppSxSource(Site, invalid, () => 1, "device").SearchAsync("中文", 1, TestContext.Current.CancellationToken));
    }
}
