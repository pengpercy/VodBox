using System.Net;
using VodBox.Infrastructure;
using Xunit;

namespace VodBox.Tests;

public sealed class LocalControlServerTests
{
    [Fact]
    public async Task UploadRejectsTraversalAndOversizeWithoutLeavingPartialFiles()
    {
        var directory=Path.Combine(AppContext.BaseDirectory,"uploads-"+Guid.NewGuid().ToString("N"));
        try
        {
            await using var server=new LocalControlServer(directory,32);
            await server.StartAsync(_=>Task.CompletedTask,_=>Task.CompletedTask,ct:TestContext.Current.CancellationToken);
            using var client=new HttpClient{BaseAddress=new Uri(server.Address!)};client.DefaultRequestHeaders.Add("X-VodBox-Control","1");
            using var valid=await client.PostAsync("/upload?name=clip.mp4",new ByteArrayContent([1,2,3]),TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK,valid.StatusCode);
            Assert.Equal(new byte[]{1,2,3},await File.ReadAllBytesAsync(Assert.Single(Directory.GetFiles(directory)),TestContext.Current.CancellationToken));
            using var bad=await client.PostAsync("/upload?name=..%2Fsecret.mp4",new ByteArrayContent([1]),TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.BadRequest,bad.StatusCode);
            using var large=await client.PostAsync("/upload?name=clip.mp4",new ByteArrayContent(new byte[33]),TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge,large.StatusCode);
            Assert.Single(Directory.GetFiles(directory));
        }
        finally{if(Directory.Exists(directory))Directory.Delete(directory,true);}
    }

    [Fact]
    public async Task ConcurrentUploadsUseSeparateNamesAndRespectInboxItemLimit()
    {
        var directory=Path.Combine(AppContext.BaseDirectory,"upload-race-"+Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            for(var index=0;index<99;index++)await File.WriteAllBytesAsync(Path.Combine(directory,index+".mp4"),[0],TestContext.Current.CancellationToken);
            await using var server=new LocalControlServer(directory,32);
            await server.StartAsync(_=>Task.CompletedTask,_=>Task.CompletedTask,ct:TestContext.Current.CancellationToken);
            using var client=new HttpClient{BaseAddress=new Uri(server.Address!)};client.DefaultRequestHeaders.Add("X-VodBox-Control","1");
            var responses=await Task.WhenAll(Enumerable.Range(0,4).Select(_=>client.PostAsync("/upload?name=clip.mp4",new ByteArrayContent([1,2]),TestContext.Current.CancellationToken)));
            try
            {
                Assert.Single(responses, response=>response.StatusCode==HttpStatusCode.OK);
                Assert.Equal(3,responses.Count(response=>response.StatusCode==HttpStatusCode.TooManyRequests));
                Assert.Equal(100,Directory.GetFiles(directory).Length);
            }
            finally{foreach(var response in responses)response.Dispose();}
        }
        finally{if(Directory.Exists(directory))Directory.Delete(directory,true);}
    }

    [Fact]
    public async Task InboxCleanupOnlyRemovesServiceGeneratedNames()
    {
        var directory=Path.Combine(AppContext.BaseDirectory,"inbox-clean-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(directory,Guid.NewGuid().ToString("N")+".mp4"),[1],TestContext.Current.CancellationToken);
            var userFile=Path.Combine(directory,"my-video.mp4");await File.WriteAllBytesAsync(userFile,[2],TestContext.Current.CancellationToken);
            await using var server=new LocalControlServer(directory);
            await server.ClearInboxAsync(TestContext.Current.CancellationToken);
            Assert.Equal(userFile,Assert.Single(Directory.GetFiles(directory)));
        }
        finally{Directory.Delete(directory,true);}
    }

    [Fact]
    public async Task ChunkedOversizeUploadIsRejectedAndPartialFileIsRemoved()
    {
        var directory=Path.Combine(AppContext.BaseDirectory,"chunked-upload-"+Guid.NewGuid().ToString("N"));
        try
        {
            await using var server=new LocalControlServer(directory,32);
            await server.StartAsync(_=>Task.CompletedTask,_=>Task.CompletedTask,ct:TestContext.Current.CancellationToken);
            using var client=new HttpClient{BaseAddress=new Uri(server.Address!)};client.DefaultRequestHeaders.Add("X-VodBox-Control","1");
            using var response=await client.PostAsync("/upload?name=clip.mp4",new ChunkedContent(),TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge,response.StatusCode);
            Assert.Empty(Directory.GetFiles(directory));
        }
        finally{if(Directory.Exists(directory))Directory.Delete(directory,true);}
    }

    private sealed class ChunkedContent:HttpContent
    {
        protected override bool TryComputeLength(out long length){length=0;return false;}
        protected override async Task SerializeToStreamAsync(Stream stream,System.Net.TransportContext? context)
        {
            await stream.WriteAsync(new byte[64]);
        }
    }

    [Fact]
    public async Task LanPairingAuthorizesControlAndStopsInvalidatesSessions()
    {
        var actions=new List<string>();await using var server=new LocalControlServer();
        await server.StartAsync(command=>{actions.Add(command);return Task.CompletedTask;},_=>Task.CompletedTask,ct:TestContext.Current.CancellationToken,allowLan:true);
        using var handler=new HttpClientHandler{CookieContainer=new CookieContainer()};
        using var client=new HttpClient(handler){BaseAddress=new Uri(server.Address!)};client.DefaultRequestHeaders.Add("X-VodBox-Control","1");
        using(var denied=await client.PostAsync("/action?command=toggle",null,TestContext.Current.CancellationToken))Assert.Equal(HttpStatusCode.Unauthorized,denied.StatusCode);
        using(var bad=await client.PostAsync("/pair?code=wrong",null,TestContext.Current.CancellationToken))Assert.Equal(HttpStatusCode.Forbidden,bad.StatusCode);
        using(var paired=await client.PostAsync("/pair?code="+server.PairingCode,null,TestContext.Current.CancellationToken))Assert.Equal(HttpStatusCode.NoContent,paired.StatusCode);
        using(var control=await client.PostAsync("/action?command=toggle",null,TestContext.Current.CancellationToken))Assert.Equal(HttpStatusCode.NoContent,control.StatusCode);
        Assert.Equal("toggle",Assert.Single(actions));
        var oldCookie=handler.CookieContainer.GetCookieHeader(new Uri(server.Address!));
        await server.DisposeAsync();Assert.Null(server.PairingCode);
        await server.StartAsync(_=>Task.CompletedTask,_=>Task.CompletedTask,ct:TestContext.Current.CancellationToken,allowLan:true);
        using var stale=new HttpClient();using var request=new HttpRequestMessage(HttpMethod.Post,server.Address+"/action?command=toggle");
        request.Headers.Add("Cookie",oldCookie);request.Headers.Add("X-VodBox-Control","1");
        using var rejected=await stale.SendAsync(request,TestContext.Current.CancellationToken);Assert.Equal(HttpStatusCode.Unauthorized,rejected.StatusCode);
    }

    [Fact]
    public async Task PairingAttemptsAreRateLimitedAndCrossOriginDenied()
    {
        await using var server=new LocalControlServer();await server.StartAsync(_=>Task.CompletedTask,_=>Task.CompletedTask,ct:TestContext.Current.CancellationToken,allowLan:true);
        using var client=new HttpClient();client.DefaultRequestHeaders.Add("X-VodBox-Control","1");
        for(var index=0;index<5;index++){using var bad=await client.PostAsync(server.Address+"/pair?code=invalid",null,TestContext.Current.CancellationToken);Assert.Equal(HttpStatusCode.Forbidden,bad.StatusCode);}
        using var limited=await client.PostAsync(server.Address+"/pair?code="+server.PairingCode,null,TestContext.Current.CancellationToken);Assert.Equal(HttpStatusCode.TooManyRequests,limited.StatusCode);
        using var request=new HttpRequestMessage(HttpMethod.Post,server.Address+"/pair?code="+server.PairingCode);request.Headers.Add("Origin","https://untrusted.example");
        using var cross=await client.SendAsync(request,TestContext.Current.CancellationToken);Assert.Equal(HttpStatusCode.Forbidden,cross.StatusCode);
    }

    [Fact]
    public async Task UnpairImmediatelyRevokesControlCookie()
    {
        await using var server=new LocalControlServer();await server.StartAsync(_=>Task.CompletedTask,_=>Task.CompletedTask,ct:TestContext.Current.CancellationToken,allowLan:true);
        using var handler=new HttpClientHandler{CookieContainer=new CookieContainer()};using var client=new HttpClient(handler){BaseAddress=new Uri(server.Address!)};
        client.DefaultRequestHeaders.Add("X-VodBox-Control","1");
        using(var paired=await client.PostAsync("/pair?code="+server.PairingCode,null,TestContext.Current.CancellationToken))Assert.Equal(HttpStatusCode.NoContent,paired.StatusCode);
        using(var unpaired=await client.PostAsync("/unpair",null,TestContext.Current.CancellationToken))Assert.Equal(HttpStatusCode.NoContent,unpaired.StatusCode);
        using var control=await client.PostAsync("/action?command=toggle",null,TestContext.Current.CancellationToken);Assert.Equal(HttpStatusCode.Unauthorized,control.StatusCode);
    }

    [Fact]
    public async Task AdvertisedUploadLimitAcceptsMediaAboveDefaultKestrelThirtyMegabyteCap()
    {
        var directory=Path.Combine(AppContext.BaseDirectory,"upload-limit-"+Guid.NewGuid().ToString("N"));
        try
        {
            const int length=31*1024*1024;
            await using var server=new LocalControlServer(directory);
            await server.StartAsync(_=>Task.CompletedTask,_=>Task.CompletedTask,ct:TestContext.Current.CancellationToken);
            using var client=new HttpClient{BaseAddress=new Uri(server.Address!)};client.DefaultRequestHeaders.Add("X-VodBox-Control","1");
            using var response=await client.PostAsync("/upload?name=large.mp4",new ByteArrayContent(new byte[length]),TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK,response.StatusCode);
            Assert.Equal(length,new FileInfo(Assert.Single(Directory.GetFiles(directory))).Length);
        }
        finally{if(Directory.Exists(directory))Directory.Delete(directory,true);}
    }

    [Fact]
    public async Task FailedLanStartDoesNotLeavePairingCodeOrClaimEnabledState()
    {
        var listener=new System.Net.Sockets.TcpListener(System.Net.IPAddress.Any,0);listener.Start();
        var port=((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            await using var server=new LocalControlServer();
            await Assert.ThrowsAnyAsync<Exception>(()=>server.StartAsync(_=>Task.CompletedTask,_=>Task.CompletedTask,port,TestContext.Current.CancellationToken,true));
            Assert.Null(server.Address);Assert.Null(server.PairingCode);Assert.False(server.LanEnabled);Assert.Empty(server.LanAddresses);
        }
        finally{listener.Stop();}
    }

    [Fact]
    public async Task LanInterfaceRequiresPairingForRegisteredMedia()
    {
        await using var server=new LocalControlServer();
        await server.StartAsync(_=>Task.CompletedTask,_=>Task.CompletedTask,ct:TestContext.Current.CancellationToken,allowLan:true);
        var lan=server.LanAddresses.FirstOrDefault();
        if(lan is null)return; // 无LAN接口环境不能冒充跨接口验收。
        var local=server.RegisterMedia("https://media.example/video.mp4");
        using var handler=new HttpClientHandler{UseProxy=false};using var client=new HttpClient(handler);
        using var denied=await client.GetAsync(lan+new Uri(local).AbsolutePath,TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized,denied.StatusCode);
        using var hostRequest=new HttpRequestMessage(HttpMethod.Get,server.Address+"/");hostRequest.Headers.Host="attacker.example";
        using var invalidHost=await client.SendAsync(hostRequest,TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden,invalidHost.StatusCode);
    }

    [Fact]
    public async Task AdjustEndpointValidatesNumericLimitsAndNeverForwardsUnknownCommands()
    {
        var values=new List<(string,double)>();await using var server=new LocalControlServer();
        await server.StartAsync(_=>Task.CompletedTask,_=>Task.CompletedTask,ct:TestContext.Current.CancellationToken,adjust:(command,value)=>{values.Add((command,value));return Task.CompletedTask;});
        using var client=new HttpClient{BaseAddress=new Uri(server.Address!)};client.DefaultRequestHeaders.Add("X-VodBox-Control","1");
        using(var good=await client.PostAsync("/adjust?command=rate&value=1.5",null,TestContext.Current.CancellationToken))Assert.Equal(HttpStatusCode.NoContent,good.StatusCode);
        Assert.Equal(("rate",1.5),Assert.Single(values));
        foreach(var query in new[]{"volume&value=101","rate&value=NaN","seek&value=-1","native-command&value=1"})
        {using var response=await client.PostAsync("/adjust?command="+query,null,TestContext.Current.CancellationToken);Assert.Equal(HttpStatusCode.BadRequest,response.StatusCode);}
        Assert.Single(values);
    }

    [Fact]
    public async Task ConfigPushOnlyAcceptsHttpAddressAndCallsConfirmationHandler()
    {
        var proposed=new List<string>();await using var server=new LocalControlServer();
        await server.StartAsync(_=>Task.CompletedTask,_=>Task.CompletedTask,ct:TestContext.Current.CancellationToken,configure:url=>{proposed.Add(url);return Task.CompletedTask;});
        using var client=new HttpClient{BaseAddress=new Uri(server.Address!)};client.DefaultRequestHeaders.Add("X-VodBox-Control","1");
        using(var valid=await client.PostAsync("/config?url=https%3A%2F%2Fconfig.example%2Ftv.json",null,TestContext.Current.CancellationToken))Assert.Equal(HttpStatusCode.NoContent,valid.StatusCode);
        Assert.Equal("https://config.example/tv.json",Assert.Single(proposed));
        using var bad=await client.PostAsync("/config?url=file%3A%2F%2F%2Fsecret",null,TestContext.Current.CancellationToken);Assert.Equal(HttpStatusCode.BadRequest,bad.StatusCode);Assert.Single(proposed);
    }

    [Fact]
    public async Task StatusEndpointReturnsOnlyPublicPlaybackSnapshotAndRequiresLanPairing()
    {
        await using var server=new LocalControlServer();
        await server.StartAsync(_=>Task.CompletedTask,_=>Task.CompletedTask,ct:TestContext.Current.CancellationToken,allowLan:true,
            status:()=>Task.FromResult(new VodBox.Core.RemotePlaybackStatus("片名","Playing",12,90,50,1.5)));
        using var handler=new HttpClientHandler{CookieContainer=new CookieContainer()};using var client=new HttpClient(handler){BaseAddress=new Uri(server.Address!)};
        using(var denied=await client.GetAsync("/status",TestContext.Current.CancellationToken))Assert.Equal(HttpStatusCode.Unauthorized,denied.StatusCode);
        client.DefaultRequestHeaders.Add("X-VodBox-Control","1");using var paired=await client.PostAsync("/pair?code="+server.PairingCode,null,TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent,paired.StatusCode);
        var text=await client.GetStringAsync("/status",TestContext.Current.CancellationToken);using var document=System.Text.Json.JsonDocument.Parse(text);
        Assert.Equal("片名",document.RootElement.GetProperty("title").GetString());Assert.Equal(12,document.RootElement.GetProperty("positionSeconds").GetDouble());
        Assert.False(document.RootElement.TryGetProperty("uri",out _));Assert.False(document.RootElement.TryGetProperty("headers",out _));Assert.False(document.RootElement.TryGetProperty("pairingCode",out _));
    }

    [Fact]
    public async Task LoopbackServerRoutesCommandsRejectsCrossOriginAndFilePush()
    {
        var actions = new List<string>(); var media = new List<string>();
        await using var server = new LocalControlServer();
        await server.StartAsync(command => { actions.Add(command); return Task.CompletedTask; }, url => { media.Add(url); return Task.CompletedTask; }, ct: TestContext.Current.CancellationToken);
        using var client = new HttpClient { BaseAddress = new Uri(server.Address!) };
        client.DefaultRequestHeaders.Add("X-VodBox-Control", "1");
        Assert.Contains("本地遥控", await client.GetStringAsync("/", TestContext.Current.CancellationToken));
        using var action = await client.PostAsync("/action?command=toggle", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, action.StatusCode);
        Assert.Equal("toggle", Assert.Single(actions));
        using var bad = await client.PostAsync("/media?url=file%3A%2F%2F%2Fsecret", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Empty(media);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/action?command=stop");
        request.Headers.Add("Origin", "https://evil.example");
        using var denied = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Single(actions);
        client.DefaultRequestHeaders.Remove("X-VodBox-Control");
        using var formAttempt = await client.PostAsync("/action?command=stop", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, formAttempt.StatusCode);
        Assert.Single(actions);
        await server.DisposeAsync();
        Assert.Null(server.Address);
    }
}
