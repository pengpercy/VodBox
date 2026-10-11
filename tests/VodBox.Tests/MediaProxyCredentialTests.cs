using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using VodBox.Infrastructure;
using Xunit;

namespace VodBox.Tests;

/// <summary>
/// 受控媒体代理的凭据闭环：Cookie/Authorization 等媒体头完整透传到源站，但绝不跟随跨源重定向或
/// 清单子资源跳到别的源站（防泄漏），逐跳/帧长度头一律丢弃。
/// </summary>
public sealed class MediaProxyCredentialTests
{
    private static WebApplication Upstream()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(options => options.Listen(System.Net.IPAddress.Loopback, 0));
        return builder.Build();
    }

    [Fact]
    public async Task PlaybackProxyForwardsCookieAuthorizationAndUserAgentSameOrigin()
    {
        await using var upstream = Upstream();
        upstream.MapGet("/media", async context =>
        {
            Assert.Equal("sid=abc", context.Request.Headers.Cookie.ToString());
            Assert.Equal("Bearer t", context.Request.Headers.Authorization.ToString());
            Assert.Equal("https://source.example/", context.Request.Headers.Referer.ToString());
            Assert.Equal("TVClient", context.Request.Headers.UserAgent.ToString());
            await context.Response.WriteAsync("ok");
        });
        await upstream.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await using var server = new LocalControlServer();
            var url = await server.RegisterPlaybackMediaAsync(upstream.Urls.Single() + "/media",
                new Dictionary<string, string>
                {
                    ["Cookie"] = "sid=abc",
                    ["Authorization"] = "Bearer t",
                    ["Referer"] = "https://source.example/",
                    ["User-Agent"] = "TVClient",
                }, TestContext.Current.CancellationToken);
            using var client = new HttpClient();
            Assert.Equal("ok", await client.GetStringAsync(url, TestContext.Current.CancellationToken));
        }
        finally { await upstream.StopAsync(TestContext.Current.CancellationToken); }
    }

    [Fact]
    public async Task CrossOriginRedirectStripsCredentialsButKeepsUserAgent()
    {
        await using var foreign = Upstream();
        foreign.MapGet("/media", async context =>
        {
            Assert.Empty(context.Request.Headers.Cookie.ToString());
            Assert.Empty(context.Request.Headers.Authorization.ToString());
            Assert.Empty(context.Request.Headers.Referer.ToString());
            Assert.Equal("TVClient", context.Request.Headers.UserAgent.ToString());
            await context.Response.WriteAsync("foreign");
        });
        await foreign.StartAsync(TestContext.Current.CancellationToken);
        await using var source = Upstream();
        source.MapGet("/redirect", context => { context.Response.Redirect(foreign.Urls.Single() + "/media"); return Task.CompletedTask; });
        await source.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await using var server = new LocalControlServer();
            var url = await server.RegisterPlaybackMediaAsync(source.Urls.Single() + "/redirect",
                new Dictionary<string, string>
                {
                    ["Cookie"] = "sid=abc",
                    ["Authorization"] = "Bearer t",
                    ["Referer"] = "https://private.example/",
                    ["User-Agent"] = "TVClient",
                }, TestContext.Current.CancellationToken);
            using var client = new HttpClient();
            Assert.Equal("foreign", await client.GetStringAsync(url, TestContext.Current.CancellationToken));
        }
        finally
        {
            await source.StopAsync(TestContext.Current.CancellationToken);
            await foreign.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task CrossOriginPlaylistChildKeepsOnlyUserAgent()
    {
        await using var foreign = Upstream();
        foreign.MapGet("/part.ts", async context =>
        {
            Assert.Empty(context.Request.Headers.Cookie.ToString());
            Assert.Empty(context.Request.Headers.Authorization.ToString());
            Assert.Empty(context.Request.Headers.Referer.ToString());
            Assert.Equal("TVClient", context.Request.Headers.UserAgent.ToString());
            await context.Response.WriteAsync("foreign-segment");
        });
        await foreign.StartAsync(TestContext.Current.CancellationToken);
        await using var source = Upstream();
        source.MapGet("/main.m3u8", async context =>
        {
            context.Response.ContentType = "application/vnd.apple.mpegurl";
            await context.Response.WriteAsync("#EXTM3U\n#EXTINF:5,\n" + foreign.Urls.Single() + "/part.ts");
        });
        await source.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await using var server = new LocalControlServer();
            var url = await server.RegisterPlaybackMediaAsync(source.Urls.Single() + "/main.m3u8",
                new Dictionary<string, string>
                {
                    ["Cookie"] = "sid=abc",
                    ["Authorization"] = "Bearer t",
                    ["Referer"] = "https://private.example/",
                    ["User-Agent"] = "TVClient",
                }, TestContext.Current.CancellationToken);
            using var client = new HttpClient();
            var playlist = await client.GetStringAsync(url, TestContext.Current.CancellationToken);
            var segment = playlist.Split('\n').Single(line => line.StartsWith("/proxy/", StringComparison.Ordinal));
            // 仅媒体代理模式：遥控服务器未启动，地址来自注册返回的基地址。
            var origin = new Uri(url).GetLeftPart(UriPartial.Authority);
            Assert.Equal("foreign-segment", await client.GetStringAsync(origin + segment, TestContext.Current.CancellationToken));
        }
        finally
        {
            await source.StopAsync(TestContext.Current.CancellationToken);
            await foreign.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task FramingAndHopByHopHeadersAreDroppedNotForwarded()
    {
        await using var upstream = Upstream();
        upstream.MapGet("/media", async context =>
        {
            Assert.Empty(context.Request.Headers.Range.ToString());
            Assert.Equal("sid=abc", context.Request.Headers.Cookie.ToString());
            await context.Response.WriteAsync("ok");
        });
        await upstream.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await using var server = new LocalControlServer();
            var url = await server.RegisterPlaybackMediaAsync(upstream.Urls.Single() + "/media",
                new Dictionary<string, string>
                {
                    ["Cookie"] = "sid=abc",
                    ["Host"] = "evil.example",
                    ["Range"] = "bytes=0-1",
                    ["Content-Length"] = "999",
                    ["TE"] = "trailers",
                    ["Connection"] = "close",
                }, TestContext.Current.CancellationToken);
            using var client = new HttpClient();
            Assert.Equal("ok", await client.GetStringAsync(url, TestContext.Current.CancellationToken));
        }
        finally { await upstream.StopAsync(TestContext.Current.CancellationToken); }
    }
}
