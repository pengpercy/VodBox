using VodBox.Core;
using VodBox.Infrastructure;
using Xunit;

namespace VodBox.Tests;

/// <summary>
/// type4（<c>site.type == 4</c>）独立 HTTP 协议适配的离线契约：play/flag 回调、多地址、playUrl、
/// parse/jx、站点 header/extend 与响应 header/UA 都要被保留，而不是被吞成单个地址或丢头。
/// </summary>
public sealed class Type4SourceTests
{
    private const string Home =
        "{\"class\":[{\"type_id\":\"1\",\"type_name\":\"电影\"}],\"list\":[{\"vod_id\":\"m1\",\"vod_name\":\"片子\"}],\"page\":1,\"pagecount\":3}";

    private const string Detail =
        "{\"list\":[{\"vod_id\":\"m1\",\"vod_name\":\"片子\",\"vod_play_from\":\"高清$$$备用\",\"vod_play_url\":\"第1集$tokA#第2集$tokB$$$第1集$tokC\"}]}";

    private static SourceInfo Site(IReadOnlyDictionary<string, string>? header = null) => new()
    {
        Key = "t4",
        Name = "Type4站",
        Runtime = SourceRuntime.Type4,
        Api = "https://api.example/jx?k=1",
        Ext = "{\"v\":1}",
        Header = header,
    };

    private static Dictionary<string, string> SiteHeader() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Cookie"] = "sid=abc",
        ["Referer"] = "https://site.example/",
    };

    private static bool IsDetail(string url) => url.Contains("ac=detail", StringComparison.Ordinal);
    private static bool IsPlay(string url) => url.Contains("play=", StringComparison.Ordinal);

    private static async Task<string> FirstEpisodeIdAsync(Type4Source source) =>
        (await source.GetDetailAsync("m1", TestContext.Current.CancellationToken)).Lines[0].Episodes[0].Id;

    [Fact]
    public async Task PlayCallbackCarriesSiteHeaderExtendFlagAndMultiAddress()
    {
        var calls = new List<(string Url, IReadOnlyDictionary<string, string> Headers)>();
        var source = new Type4Source(Site(SiteHeader()), (url, headers, _) =>
        {
            calls.Add((url, headers));
            if (IsPlay(url)) return Task.FromResult(
                "{\"url\":[\"高清\",\"https://media.example/a.m3u8\",\"备用\",\"https://media.example/b.m3u8\"]," +
                "\"header\":{\"Referer\":\"https://source.example/\",\"Cookie\":\"up=2\"},\"UA\":\"TVClient\"}");
            if (IsDetail(url)) return Task.FromResult(Detail);
            return Task.FromResult(Home);
        });

        var categories = await source.GetCategoriesAsync(TestContext.Current.CancellationToken);
        Assert.Equal("1", Assert.Single(categories).Id);
        Assert.Equal("m1", Assert.Single((await source.GetHomeAsync(TestContext.Current.CancellationToken)).Items).Id);

        var detail = await source.GetDetailAsync("m1", TestContext.Current.CancellationToken);
        Assert.Equal(2, detail.Lines.Count);
        Assert.Equal(2, detail.Lines[0].Episodes.Count);

        var request = await source.ResolvePlaybackAsync("m1", detail.Lines[0].Episodes[0].Id, TestContext.Current.CancellationToken);

        // 多地址完整保留，首选地址仍是其中之一。
        Assert.Equal("https://media.example/a.m3u8", request.Uri);
        Assert.Equal(2, request.Choices.Count);
        Assert.Equal("高清", request.Choices[0].Name);
        Assert.Equal("https://media.example/b.m3u8", request.Choices[1].Uri);
        Assert.Equal("备用", request.Choices[1].Name);
        Assert.Equal(ResolutionKind.Direct, request.Resolution);
        // 响应 header 覆盖站点 header，UA 归一到 User-Agent。
        Assert.Equal("up=2", request.Headers["Cookie"]);
        Assert.Equal("https://source.example/", request.Headers["Referer"]);
        Assert.Equal("TVClient", request.Headers["User-Agent"]);

        // 每次调用都带站点 header 与 extend，且保留接口原有查询串。
        Assert.All(calls, call =>
        {
            Assert.Equal("sid=abc", call.Headers["Cookie"]);
            Assert.Equal("https://site.example/", call.Headers["Referer"]);
            Assert.Contains("k=1", call.Url, StringComparison.Ordinal);
            Assert.Contains("extend=", call.Url, StringComparison.Ordinal);
        });
        // 播放回调带 play/flag 两个参数。
        var play = calls.Single(call => IsPlay(call.Url));
        Assert.Contains("play=tokA", play.Url, StringComparison.Ordinal);
        Assert.Contains("flag=", play.Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlayUrlAndParseJxBecomeParseEndpointAndPreservedFlag()
    {
        var source = new Type4Source(Site(), (url, _, _) => Task.FromResult(IsDetail(url) ? Detail :
            "{\"url\":\"https://site.example/player?id=1\",\"playUrl\":\"https://jx.example/?url=\",\"parse\":1,\"jx\":1}"));

        var request = await source.ResolvePlaybackAsync("m1", await FirstEpisodeIdAsync(source), TestContext.Current.CancellationToken);

        Assert.Equal("https://site.example/player?id=1", request.Uri);
        Assert.Equal(ResolutionKind.Json, request.Resolution);
        Assert.Equal("https://jx.example/?url=", request.ParseEndpoint);
        Assert.Equal("1", request.Jx);
    }

    [Fact]
    public async Task PlayUrlWithoutDirectUrlBecomesParseTarget()
    {
        var source = new Type4Source(Site(), (url, _, _) => Task.FromResult(IsDetail(url) ? Detail :
            "{\"playUrl\":\"https://jx.example/?url=\"}"));

        var request = await source.ResolvePlaybackAsync("m1", await FirstEpisodeIdAsync(source), TestContext.Current.CancellationToken);

        // 没有 direct url 时回退到原始选集地址并要求解析，而不是伪造直连。
        Assert.Equal("tokA", request.Uri);
        Assert.Equal(ResolutionKind.Json, request.Resolution);
        Assert.Equal("https://jx.example/?url=", request.ParseEndpoint);
    }

    [Fact]
    public async Task ProviderErrorSurfacesReasonInsteadOfInvalidAddress()
    {
        var source = new Type4Source(Site(), (url, _, _) => Task.FromResult(IsDetail(url) ? Detail : "{\"url\":\"\",\"errMsg\":\"需要登录\"}"));

        var error = await Assert.ThrowsAsync<NotSupportedException>(
            async () => await source.ResolvePlaybackAsync("m1", await FirstEpisodeIdAsync(source), TestContext.Current.CancellationToken));
        Assert.Contains("需要登录", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NonHttpAddressIsRejected()
    {
        var source = new Type4Source(Site(), (url, _, _) => Task.FromResult(IsDetail(url) ? Detail : "{\"url\":\"file:///etc/passwd\"}"));

        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await source.ResolvePlaybackAsync("m1", await FirstEpisodeIdAsync(source), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UnknownEpisodeIsRejectedAfterRefresh()
    {
        var source = new Type4Source(Site(), (_, _, _) => Task.FromResult(Detail));

        await Assert.ThrowsAsync<InvalidDataException>(
            () => source.ResolvePlaybackAsync("m1", "gone", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FiltersAreReadFromHomeResponse()
    {
        var source = new Type4Source(Site(), (_, _, _) => Task.FromResult(
            "{\"filters\":{\"1\":[{\"key\":\"area\",\"name\":\"地区\",\"value\":[{\"n\":\"大陆\",\"v\":\"大陆\"}]}]}}"));

        var group = Assert.Single(await source.GetFiltersAsync("1", TestContext.Current.CancellationToken));
        Assert.Equal("area", group.Key);
        Assert.Equal("大陆", Assert.Single(group.Values).Value);
    }

    [Fact]
    public async Task ObjectHeaderInStringFormIsParsedAndInjectedIntoMediaHeaders()
    {
        var source = new Type4Source(Site(), (url, _, _) => Task.FromResult(IsDetail(url) ? Detail :
            "{\"url\":\"https://media.example/a.m3u8\",\"header\":\"{\\\"Referer\\\":\\\"https://source.example/\\\"}\"}"));

        var request = await source.ResolvePlaybackAsync("m1", await FirstEpisodeIdAsync(source), TestContext.Current.CancellationToken);
        Assert.Equal("https://source.example/", request.Headers["Referer"]);
    }
}
