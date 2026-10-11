using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using VodBox.Core;
using VodBox.Infrastructure;
using VodBox.PluginHost.JarPlugin;
using Xunit;

namespace VodBox.Tests;

/// <summary>
/// 播放协议数据闭环（离线，不触外网、不启 JVM）：JAR/CatVod 与 JSON 解析出的合法播放头
/// （UA/Cookie/Authorization/Referer…）、parse/jx/playUrl 与多地址必须被完整保留，并交给现有受控媒体代理
/// 转发——绝不在数据链上丢弃，也不靠放宽引擎的拒绝条件来「凑合能播」。
/// </summary>
public sealed class PlaybackHeaderChainTests
{
    [Fact]
    public void JarPlayerContentPreservesMultiAddressHeadersAndParseFlags()
    {
        const string play = """
            {"parse":1,"jx":"2","playUrl":"https://jx.example/?url=",
             "url":["高清","https://cdn.example/a.m3u8","备用","https://cdn.example/b.m3u8"],
             "header":{"User-Agent":"catvod","Referer":"https://source.example/","Cookie":"sid=abc","Authorization":"Bearer t"}}
            """;
        var request = CatVodCatalog.ParsePlayback(play, "https://cdn.example/a.m3u8", "示例", "rj", "RJ", "100", "线路A", "ep", null, null);

        // 多地址完整保留，首选地址仍是其中之一。
        Assert.Equal("https://cdn.example/a.m3u8", request.Uri);
        Assert.Equal(2, request.Choices.Count);
        Assert.Equal("高清", request.Choices[0].Name);
        Assert.Equal("https://cdn.example/b.m3u8", request.Choices[1].Uri);
        Assert.Equal("备用", request.Choices[1].Name);
        // parse/jx/playUrl 与全部合法播放头都保留（不是只留 UA/Referer）。
        Assert.Equal(ResolutionKind.Json, request.Resolution);
        Assert.Equal("https://jx.example/?url=", request.ParseEndpoint);
        Assert.Equal("2", request.Jx);
        Assert.Equal("catvod", request.Headers["User-Agent"]);
        Assert.Equal("https://source.example/", request.Headers["Referer"]);
        Assert.Equal("sid=abc", request.Headers["Cookie"]);
        Assert.Equal("Bearer t", request.Headers["Authorization"]);
    }

    [Fact]
    public void JarPlayerContentKeepsCookieAuthorizationFromJsonStringHeader()
    {
        const string play = """{"parse":0,"url":"https://cdn.example/x.mp4","header":"{\"Cookie\":\"sid=1\",\"Authorization\":\"Bearer z\",\"X-Token\":\"v\"}"}""";
        var request = CatVodCatalog.ParsePlayback(play, "", "t", "rj", "RJ", "1", "f", "e", null, null);

        Assert.Equal(ResolutionKind.Direct, request.Resolution);
        Assert.Null(request.ParseEndpoint);
        Assert.Equal("sid=1", request.Headers["Cookie"]);
        Assert.Equal("Bearer z", request.Headers["Authorization"]);
        Assert.Equal("v", request.Headers["X-Token"]);
    }

    [Fact]
    public void JarPlayerContentSingleStringUrlKeepsPrimaryAddress()
    {
        var request = CatVodCatalog.ParsePlayback(
            """{"url":"https://cdn.example/only.mp4"}""", "", "t", "rj", "RJ", "1", "线路A", "e", null, null);
        Assert.Equal("https://cdn.example/only.mp4", request.Uri);
        Assert.Single(request.Choices);
        Assert.Equal("https://cdn.example/only.mp4", request.Choices[0].Uri);
    }

    [Theory]
    [InlineData("""{"url":"file:///etc/passwd"}""")]
    [InlineData("""{"url":"https://user:pass@cdn.example/x.mp4"}""")]
    [InlineData("""{"header":{"Bad Header":"x"},"url":"https://cdn.example/x.mp4"}""")]
    [InlineData("""{"header":{"X-Test":"line\r\nbreak"},"url":"https://cdn.example/x.mp4"}""")]
    public void JarPlayerContentRejectsIllegalAddressOrHeader(string play) =>
        Assert.Throws<InvalidDataException>(() =>
            CatVodCatalog.ParsePlayback(play, "https://episode.example/x", "t", "rj", "RJ", "1", "f", "e", null, null));

    [Fact]
    public async Task JsonParserPreservesCookieAuthorizationFromParseResponse()
    {
        var resolver = new JsonPlayResolver(new TvBoxParse { Name = "解析", Type = 1, Url = "https://parse.example/?url=" },
            (_, _, _) => Task.FromResult("""{"url":"https://media.example/stream.m3u8","header":{"User-Agent":"TV","Cookie":"sid=abc","Authorization":"Bearer t","X-Play":"1"}}"""));
        var result = await resolver.ResolveAsync(
            new PlaybackRequest { Uri = "https://video.example/?id=1", Resolution = ResolutionKind.Json }, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("sid=abc", result!.Headers["Cookie"]);
        Assert.Equal("Bearer t", result.Headers["Authorization"]);
        Assert.Equal("TV", result.Headers["User-Agent"]);
        Assert.Equal("1", result.Headers["X-Play"]);
        Assert.Equal(ResolutionKind.Direct, result.Resolution);
    }

    [Fact]
    public async Task JsonParserRejectsIllegalResponseHeaderName()
    {
        var resolver = new JsonPlayResolver(new TvBoxParse { Name = "解析", Type = 1, Url = "https://parse.example/?url=" },
            (_, _, _) => Task.FromResult("""{"url":"https://media.example/a","header":{"Bad Header":"x"}}"""));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            resolver.ResolveAsync(new PlaybackRequest { Uri = "https://x.example" }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task JsonParserKeepsMultiAddressInsteadOfDroppingThem()
    {
        var resolver = new JsonPlayResolver(new TvBoxParse { Name = "解析", Type = 1, Url = "https://parse.example/?url=" },
            (_, _, _) => Task.FromResult("""{"url":["https://media.example/a.m3u8","javascript:bad","https://media.example/b.m3u8"]}"""));
        var result = await resolver.ResolveAsync(
            new PlaybackRequest { Uri = "https://video.example/?id=1", Resolution = ResolutionKind.Json }, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("https://media.example/a.m3u8", result!.Uri);
        Assert.Equal(2, result.Choices.Count);
        Assert.Equal("https://media.example/b.m3u8", result.Choices[1].Uri);
    }

    [Fact]
    public async Task ResolvedCredentialHeadersReachUpstreamThroughControlledProxy()
    {
        await using var upstream = Upstream();
        upstream.MapGet("/media", async context =>
        {
            Assert.Equal("sid=abc", context.Request.Headers.Cookie.ToString());
            Assert.Equal("Bearer t", context.Request.Headers.Authorization.ToString());
            Assert.Equal("TV", context.Request.Headers.UserAgent.ToString());
            Assert.Equal("https://source.example/", context.Request.Headers.Referer.ToString());
            await context.Response.WriteAsync("ok");
        });
        await upstream.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            // 1) 解析出的播放请求：Cookie/Authorization 完整保留（引擎直传不了的头）。
            var play = """{"parse":0,"url":"UPSTREAM/media","header":{"User-Agent":"TV","Referer":"https://source.example/","Cookie":"sid=abc","Authorization":"Bearer t"}}"""
                .Replace("UPSTREAM", upstream.Urls.Single(), StringComparison.Ordinal);
            var request = CatVodCatalog.ParsePlayback(play, "", "t", "rj", "RJ", "1", "f", "e", null, null);
            Assert.Equal("sid=abc", request.Headers["Cookie"]);
            Assert.Equal("Bearer t", request.Headers["Authorization"]);

            // 2) 接现有受控媒体代理：引擎拿到的地址变为本机代理地址，头交给代理带上游。
            await using var server = new LocalControlServer();
            var url = await server.RegisterPlaybackMediaAsync(request.Uri, request.Headers, TestContext.Current.CancellationToken);
            Assert.StartsWith("http://127.0.0.1:", url, StringComparison.Ordinal);
            Assert.Contains("/proxy/", url, StringComparison.Ordinal);

            using var client = new HttpClient();
            Assert.Equal("ok", await client.GetStringAsync(url, TestContext.Current.CancellationToken));
        }
        finally { await upstream.StopAsync(TestContext.Current.CancellationToken); }
    }

    private static WebApplication Upstream()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(options => options.Listen(System.Net.IPAddress.Loopback, 0));
        return builder.Build();
    }
}
