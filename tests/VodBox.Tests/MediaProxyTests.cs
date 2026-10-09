using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using VodBox.Infrastructure;
using Xunit;

namespace VodBox.Tests;

public sealed class MediaProxyTests
{
    [Fact]
    public void HlsRewriteCoversRelativeSegmentsKeyAndNestedPlaylist()
    {
        var urls = new List<string>();
        var text = MediaProxy.RewriteHls("#EXTM3U\n#EXT-X-KEY:METHOD=AES-128,URI=\"key.bin\"\n../video/part.ts\nvariant.m3u8", new Uri("https://example.com/live/main.m3u8"), url => { urls.Add(url); return "/proxy/" + urls.Count; });
        Assert.Contains("URI=\"/proxy/1\"", text);
        Assert.Contains("/proxy/2", text);
        Assert.Equal(new[] { "https://example.com/live/key.bin", "https://example.com/video/part.ts", "https://example.com/live/variant.m3u8" }, urls);
    }

    [Fact]
    public async Task ProxyPlaylistAndRewrittenSegmentAreActuallyReadable()
    {
        var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(options => options.Listen(System.Net.IPAddress.Loopback, 0));
        await using var upstream = builder.Build();
        upstream.MapGet("/live/main.m3u8", async context =>
        {
            context.Response.ContentType = "application/vnd.apple.mpegurl";
            await context.Response.WriteAsync("#EXTM3U\n#EXTINF:5,\npart.ts\n#EXT-X-ENDLIST");
        });
        upstream.MapGet("/live/part.ts", async context => await context.Response.WriteAsync("segment-bytes"));
        await upstream.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await using var server = new LocalControlServer();
            await server.StartAsync(_ => Task.CompletedTask, _ => Task.CompletedTask, ct: TestContext.Current.CancellationToken);
            var url = server.RegisterMedia(upstream.Urls.Single() + "/live/main.m3u8");
            using var client = new HttpClient();
            var playlist = await client.GetStringAsync(url, TestContext.Current.CancellationToken);
            var segment = playlist.Split('\n').Single(line => line.StartsWith("/proxy/"));
            Assert.Equal("segment-bytes", await client.GetStringAsync(server.Address + segment, TestContext.Current.CancellationToken));
            await server.DisposeAsync();
            await server.StartAsync(_ => Task.CompletedTask, _ => Task.CompletedTask, ct: TestContext.Current.CancellationToken);
            using var old = await client.GetAsync(server.Address + new Uri(url).AbsolutePath, TestContext.Current.CancellationToken);
            Assert.Equal(System.Net.HttpStatusCode.NotFound, old.StatusCode);
        }
        finally { await upstream.StopAsync(TestContext.Current.CancellationToken); }
    }

    [Fact]
    public async Task CrossOriginPlaylistDoesNotForwardSourceReferer()
    {
        var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(options => options.Listen(System.Net.IPAddress.Loopback, 0));
        await using var foreign = builder.Build();
        foreign.MapGet("/part.ts", async context =>
        {
            Assert.Empty(context.Request.Headers.Referer.ToString());
            Assert.Equal("TVClient", context.Request.Headers.UserAgent.ToString());
            await context.Response.WriteAsync("foreign-segment");
        });
        await foreign.StartAsync(TestContext.Current.CancellationToken);
        var sourceBuilder = WebApplication.CreateSlimBuilder(); sourceBuilder.Logging.ClearProviders();
        sourceBuilder.WebHost.UseKestrel(options => options.Listen(System.Net.IPAddress.Loopback, 0));
        await using var source = sourceBuilder.Build();
        source.MapGet("/main.m3u8", async context =>
        {
            context.Response.ContentType = "application/vnd.apple.mpegurl";
            await context.Response.WriteAsync("#EXTM3U\n#EXTINF:5,\n" + foreign.Urls.Single() + "/part.ts");
        });
        await source.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await using var server = new LocalControlServer();
            await server.StartAsync(_ => Task.CompletedTask, _ => Task.CompletedTask, ct: TestContext.Current.CancellationToken);
            var url = server.RegisterMedia(source.Urls.Single() + "/main.m3u8", new Dictionary<string, string>
            { ["Referer"] = "https://private-source.example/", ["User-Agent"] = "TVClient" });
            using var client = new HttpClient();
            var playlist = await client.GetStringAsync(url, TestContext.Current.CancellationToken);
            var segment = playlist.Split('\n').Single(line => line.StartsWith("/proxy/"));
            Assert.Equal("foreign-segment", await client.GetStringAsync(server.Address + segment, TestContext.Current.CancellationToken));
        }
        finally { await source.StopAsync(TestContext.Current.CancellationToken); await foreign.StopAsync(TestContext.Current.CancellationToken); }
    }

    [Fact]
    public void PlaylistRewriterValidatesInputAndKeepsInlineDataKeys()
    {
        Assert.Throws<InvalidDataException>(() => MediaProxy.RewriteHls("<html>error</html>", new Uri("https://example.com/"), _ => "/proxy/x"));
        var result = MediaProxy.RewriteHls("#EXTM3U\n#EXT-X-KEY:METHOD=AES-128,URI=\"data:text/plain;base64,AAAA\"", new Uri("https://example.com/"), _ => throw new Exception("inline data must not be registered"));
        Assert.Contains("data:text/plain", result);
    }

    [Fact]
    public void DashSegmentListUsesInheritedBaseAndRejectsUnsupportedTemplate()
    {
        var urls = new List<string>();
        const string mpd = "<MPD xmlns='urn:mpeg:dash:schema:mpd:2011'><BaseURL>video/</BaseURL><Period><AdaptationSet><Representation><SegmentList><Initialization sourceURL='init.mp4'/><SegmentURL media='part.m4s'/></SegmentList></Representation></AdaptationSet></Period></MPD>";
        var result = MediaProxy.RewriteDash(mpd, new Uri("https://example.com/root/main.mpd"), url => { urls.Add(url); return "/proxy/" + urls.Count; });
        Assert.Equal(new[] { "https://example.com/root/video/init.mp4", "https://example.com/root/video/part.m4s" }, urls);
        Assert.Contains("/proxy/1", result);
        Assert.DoesNotContain("<BaseURL>", result);
        Assert.Throws<NotSupportedException>(() => MediaProxy.RewriteDash("<MPD><Period><SegmentTemplate media='part-$Number$.m4s'/></Period></MPD>", new Uri("https://example.com/"), _ => "/proxy/x"));
    }

    [Fact]
    public async Task StaticDashSegmentsAreReadableAndUnsupportedManifestReturnsBadGateway()
    {
        var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(options => options.Listen(System.Net.IPAddress.Loopback, 0));
        await using var upstream = builder.Build();
        upstream.MapGet("/main.mpd", async context =>
        {
            context.Response.ContentType = "application/dash+xml";
            await context.Response.WriteAsync("<MPD><Period><AdaptationSet><Representation><SegmentList><SegmentURL media='part.m4s'/></SegmentList></Representation></AdaptationSet></Period></MPD>");
        });
        upstream.MapGet("/unsupported.mpd", async context => await context.Response.WriteAsync("<MPD><SegmentTemplate media='$Number$.m4s'/></MPD>"));
        upstream.MapGet("/part.m4s", async context => await context.Response.WriteAsync("dash-segment"));
        await upstream.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await using var server = new LocalControlServer();
            await server.StartAsync(_ => Task.CompletedTask, _ => Task.CompletedTask, ct: TestContext.Current.CancellationToken);
            using var client = new HttpClient();
            var manifest = await client.GetStringAsync(server.RegisterMedia(upstream.Urls.Single() + "/main.mpd"), TestContext.Current.CancellationToken);
            var document = System.Xml.Linq.XDocument.Parse(manifest);
            var segment = document.Descendants("SegmentURL").Single().Attribute("media")!.Value;
            Assert.Equal("dash-segment", await client.GetStringAsync(server.Address + segment, TestContext.Current.CancellationToken));
            using var bad = await client.GetAsync(server.RegisterMedia(upstream.Urls.Single() + "/unsupported.mpd"), TestContext.Current.CancellationToken);
            Assert.Equal(System.Net.HttpStatusCode.BadGateway, bad.StatusCode);
        }
        finally { await upstream.StopAsync(TestContext.Current.CancellationToken); }
    }

    [Fact]
    public void FiniteDashNumberAndTimeTemplatesExpandIntoRegisteredSegments()
    {
        var urls = new List<string>();
        const string mpd = "<MPD mediaPresentationDuration='PT6S'><Period><AdaptationSet><SegmentTemplate timescale='1' duration='2' startNumber='5' initialization='init-$RepresentationID$.mp4' media='part-$Number%03d$.m4s'/><Representation id='v1'/></AdaptationSet></Period></MPD>";
        var result = MediaProxy.RewriteDash(mpd, new Uri("https://example.com/main.mpd"), url => { urls.Add(url); return "/proxy/" + urls.Count; });
        Assert.Equal(new[] { "https://example.com/init-v1.mp4", "https://example.com/part-005.m4s", "https://example.com/part-006.m4s", "https://example.com/part-007.m4s" }, urls);
        Assert.DoesNotContain("SegmentTemplate", result);
        urls.Clear();
        const string timeline = "<MPD><Period><AdaptationSet><Representation id='a'><SegmentTemplate media='part-$Time$.m4s'><SegmentTimeline><S t='10' d='2' r='2'/></SegmentTimeline></SegmentTemplate></Representation></AdaptationSet></Period></MPD>";
        MediaProxy.RewriteDash(timeline, new Uri("https://example.com/"), url => { urls.Add(url); return "/proxy/" + urls.Count; });
        Assert.Equal(new[] { "https://example.com/part-10.m4s", "https://example.com/part-12.m4s", "https://example.com/part-14.m4s" }, urls);
    }

    [Fact]
    public void RepeatedLivePlaylistRegistrationReusesHandlesButNotDifferentHeaders()
    {
        using var proxy=new MediaProxy();
        var first=proxy.Register("https://example.com/part.ts",new Dictionary<string,string>{["Referer"]="https://source.example/"});
        for(var index=0;index<5000;index++)Assert.Equal(first,proxy.Register("https://example.com/part.ts",new Dictionary<string,string>{["Referer"]="https://source.example/"}));
        Assert.NotEqual(first,proxy.Register("https://example.com/part.ts",new Dictionary<string,string>{["Referer"]="https://other.example/"}));
        proxy.Clear();
        Assert.NotEqual(first,proxy.Register("https://example.com/part.ts",new Dictionary<string,string>{["Referer"]="https://source.example/"}));
    }

    [Fact]
    public async Task RedirectedPlaylistUsesFinalBaseAndRedirectLoopFailsBoundedly()
    {
        var builder=WebApplication.CreateSlimBuilder();builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(options=>options.Listen(System.Net.IPAddress.Loopback,0));
        await using var upstream=builder.Build();
        upstream.MapGet("/start",context=>{context.Response.Redirect("/final/main.m3u8");return Task.CompletedTask;});
        upstream.MapGet("/loop",context=>{context.Response.Redirect("/loop");return Task.CompletedTask;});
        upstream.MapGet("/final/main.m3u8",async context=>{context.Response.ContentType="application/vnd.apple.mpegurl";await context.Response.WriteAsync("#EXTM3U\n#EXTINF:5,\npart.ts");});
        upstream.MapGet("/final/part.ts",async context=>await context.Response.WriteAsync("redirected-segment"));
        await upstream.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await using var server=new LocalControlServer();
            await server.StartAsync(_=>Task.CompletedTask,_=>Task.CompletedTask,ct:TestContext.Current.CancellationToken);
            using var client=new HttpClient();
            var playlist=await client.GetStringAsync(server.RegisterMedia(upstream.Urls.Single()+"/start"),TestContext.Current.CancellationToken);
            var segment=playlist.Split('\n').Single(line=>line.StartsWith("/proxy/"));
            Assert.Equal("redirected-segment",await client.GetStringAsync(server.Address+segment,TestContext.Current.CancellationToken));
            using var loop=await client.GetAsync(server.RegisterMedia(upstream.Urls.Single()+"/loop"),TestContext.Current.CancellationToken);
            Assert.Equal(System.Net.HttpStatusCode.BadGateway,loop.StatusCode);
        }
        finally{await upstream.StopAsync(TestContext.Current.CancellationToken);}
    }

    [Fact]
    public async Task CrossOriginRedirectStripsRefererAndInvalidRangeIsClientError()
    {
        var targetBuilder=WebApplication.CreateSlimBuilder();targetBuilder.Logging.ClearProviders();
        targetBuilder.WebHost.UseKestrel(options=>options.Listen(System.Net.IPAddress.Loopback,0));
        await using var target=targetBuilder.Build();
        target.MapGet("/media",async context=>{Assert.Empty(context.Request.Headers.Referer.ToString());await context.Response.WriteAsync("safe");});
        await target.StartAsync(TestContext.Current.CancellationToken);
        var sourceBuilder=WebApplication.CreateSlimBuilder();sourceBuilder.Logging.ClearProviders();
        sourceBuilder.WebHost.UseKestrel(options=>options.Listen(System.Net.IPAddress.Loopback,0));
        await using var source=sourceBuilder.Build();
        source.MapGet("/redirect",context=>{context.Response.Redirect(target.Urls.Single()+"/media");return Task.CompletedTask;});
        await source.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await using var server=new LocalControlServer();
            await server.StartAsync(_=>Task.CompletedTask,_=>Task.CompletedTask,ct:TestContext.Current.CancellationToken);
            var address=server.RegisterMedia(source.Urls.Single()+"/redirect",new Dictionary<string,string>{["Referer"]="https://private.example/"});
            using var client=new HttpClient();
            Assert.Equal("safe",await client.GetStringAsync(address,TestContext.Current.CancellationToken));
            using var request=new HttpRequestMessage(HttpMethod.Get,address);request.Headers.TryAddWithoutValidation("Range","invalid");
            using var response=await client.SendAsync(request,TestContext.Current.CancellationToken);
            Assert.Equal(System.Net.HttpStatusCode.BadRequest,response.StatusCode);
        }
        finally{await source.StopAsync(TestContext.Current.CancellationToken);await target.StopAsync(TestContext.Current.CancellationToken);}
    }

    [Fact]
    public void DashNegativeRepeatUsesFinitePeriodAndPreservesPresentationOffset()
    {
        var urls=new List<string>();
        const string mpd="<MPD><Period duration='PT6S'><AdaptationSet><Representation id='v'><SegmentTemplate timescale='1' presentationTimeOffset='10' media='part-$Time$.m4s'><SegmentTimeline><S t='10' d='2' r='-1'/></SegmentTimeline></SegmentTemplate></Representation></AdaptationSet></Period></MPD>";
        var rewritten=MediaProxy.RewriteDash(mpd,new Uri("https://example.com/"),url=>{urls.Add(url);return "/proxy/"+urls.Count;});
        Assert.Equal(new[]{"https://example.com/part-10.m4s","https://example.com/part-12.m4s","https://example.com/part-14.m4s"},urls);
        Assert.Contains("presentationTimeOffset=\"10\"",rewritten);
        Assert.DoesNotContain("r=\"-1\"",rewritten);
        Assert.Throws<NotSupportedException>(()=>MediaProxy.RewriteDash(mpd.Replace("duration='PT6S'",""),new Uri("https://example.com/"),_=>"/proxy/x"));
    }

    [Fact]
    public void DynamicDashFiniteTimelinePreservesRefreshMetadataAndRejectsUnboundedGuessing()
    {
        const string manifest="<MPD type='dynamic' minimumUpdatePeriod='PT2S'><Period><AdaptationSet><Representation id='v'><SegmentTemplate timescale='1' media='part-$Time$.m4s'><SegmentTimeline><S t='100' d='2' r='2'/></SegmentTimeline></SegmentTemplate></Representation></AdaptationSet></Period></MPD>";
        var urls=new List<string>();
        var output=MediaProxy.RewriteDash(manifest,new Uri("https://example.com/"),url=>{urls.Add(url);return "/proxy/"+urls.Count;});
        Assert.Equal(new[]{"https://example.com/part-100.m4s","https://example.com/part-102.m4s","https://example.com/part-104.m4s"},urls);
        Assert.Contains("type=\"dynamic\"",output);Assert.Contains("minimumUpdatePeriod=\"PT2S\"",output);
        Assert.Throws<NotSupportedException>(()=>MediaProxy.RewriteDash(manifest.Replace("r='2'","r='-1'"),new Uri("https://example.com/"),_=>"/proxy/x"));
        Assert.Throws<NotSupportedException>(()=>MediaProxy.RewriteDash("<MPD type='dynamic'><Period><AdaptationSet><Representation><SegmentTemplate duration='2' media='$Number$.m4s'/></Representation></AdaptationSet></Period></MPD>",new Uri("https://example.com/"),_=>"/proxy/x"));
    }

    [Fact]
    public async Task DynamicDashRefreshKeepsOldHandlesAndRegistersNewTimelineSegments()
    {
        var requests=0;var builder=WebApplication.CreateSlimBuilder();builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(options=>options.Listen(System.Net.IPAddress.Loopback,0));
        await using var upstream=builder.Build();
        upstream.MapGet("/main.mpd",async context=>
        {
            var start=Interlocked.Increment(ref requests)==1?100:102;
            context.Response.ContentType="application/dash+xml";
            await context.Response.WriteAsync($"<MPD type='dynamic' minimumUpdatePeriod='PT2S'><Period><AdaptationSet><Representation id='v'><SegmentTemplate media='part-$Time$.m4s'><SegmentTimeline><S t='{start}' d='2' r='1'/></SegmentTimeline></SegmentTemplate></Representation></AdaptationSet></Period></MPD>");
        });
        upstream.MapGet("/{name}",async context=>await context.Response.WriteAsync(context.Request.RouteValues["name"]?.ToString()??""));
        await upstream.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await using var server=new LocalControlServer();await server.StartAsync(_=>Task.CompletedTask,_=>Task.CompletedTask,ct:TestContext.Current.CancellationToken);
            using var client=new HttpClient();var url=server.RegisterMedia(upstream.Urls.Single()+"/main.mpd");
            async Task<string[]> Segments()
            {
                var document=System.Xml.Linq.XDocument.Parse(await client.GetStringAsync(url,TestContext.Current.CancellationToken));
                return document.Descendants("SegmentURL").Select(segment=>segment.Attribute("media")!.Value).ToArray();
            }
            var first=await Segments();var second=await Segments();
            Assert.Equal(first[1],second[0]);Assert.NotEqual(first[0],second[1]);
            Assert.Equal("part-100.m4s",await client.GetStringAsync(server.Address+first[0],TestContext.Current.CancellationToken));
            Assert.Equal("part-104.m4s",await client.GetStringAsync(server.Address+second[1],TestContext.Current.CancellationToken));
            Assert.Equal(2,requests);
        }
        finally{await upstream.StopAsync(TestContext.Current.CancellationToken);}
    }

    [Fact]
    public async Task RegisteredProxyPreservesRangeAndRejectsUnknownHandles()
    {
        var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(options => options.Listen(System.Net.IPAddress.Loopback, 0));
        await using var upstream = builder.Build();
        upstream.MapGet("/media", async context =>
        {
            Assert.Equal("bytes=2-4", context.Request.Headers.Range.ToString());
            Assert.Equal("https://source.example/", context.Request.Headers.Referer.ToString());
            context.Response.StatusCode = 206; context.Response.Headers.ContentRange = "bytes 2-4/10";
            await context.Response.WriteAsync("234");
        });
        await upstream.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await using var server = new LocalControlServer();
            await server.StartAsync(_ => Task.CompletedTask, _ => Task.CompletedTask, ct: TestContext.Current.CancellationToken);
            var url = server.RegisterMedia(upstream.Urls.Single() + "/media", new Dictionary<string, string> { ["Referer"] = "https://source.example/" });
            using var client = new HttpClient(); using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(2, 4);
            using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
            Assert.Equal(System.Net.HttpStatusCode.PartialContent, response.StatusCode);
            Assert.Equal("234", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            using var missing = await client.GetAsync(server.Address + "/proxy/not-registered", TestContext.Current.CancellationToken);
            Assert.Equal(System.Net.HttpStatusCode.NotFound, missing.StatusCode);
        }
        finally { await upstream.StopAsync(TestContext.Current.CancellationToken); }
    }
}
