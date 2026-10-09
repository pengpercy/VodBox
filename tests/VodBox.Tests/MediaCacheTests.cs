using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using VodBox.Infrastructure;
using Xunit;

namespace VodBox.Tests;

public sealed class MediaCacheTests
{
    [Fact]
    public async Task RegisteredCacheEndpointServesBytesAndRejectsUnknownHandle()
    {
        var directory=Path.Combine(AppContext.BaseDirectory,"cache-server-"+Guid.NewGuid().ToString("N"));var calls=0;
        var builder=WebApplication.CreateSlimBuilder();builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(options=>options.Listen(System.Net.IPAddress.Loopback,0));
        await using var app=builder.Build();
        app.MapGet("/bytes",async context=>{Interlocked.Increment(ref calls);await context.Response.WriteAsync("cached");});
        await app.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await using var server=new LocalControlServer(Path.Combine(directory,"inbox"));
            await server.StartAsync(_=>Task.CompletedTask,_=>Task.CompletedTask,ct:TestContext.Current.CancellationToken);
            var address=server.RegisterCachedMedia(app.Urls.Single()+"/bytes");using var client=new HttpClient();
            Assert.Equal("cached",await client.GetStringAsync(address,TestContext.Current.CancellationToken));
            Assert.Equal("cached",await client.GetStringAsync(address,TestContext.Current.CancellationToken));Assert.Equal(1,calls);
            using var unknown=await client.GetAsync(server.Address+"/cache/unknown",TestContext.Current.CancellationToken);
            Assert.Equal(System.Net.HttpStatusCode.NotFound,unknown.StatusCode);
        }
        finally{await app.StopAsync(TestContext.Current.CancellationToken);if(Directory.Exists(directory))Directory.Delete(directory,true);}
    }

    [Fact]
    public async Task CacheCoalescesRepeatedDownloadsAndLeavesNoOversizeByproduct()
    {
        var directory=Path.Combine(AppContext.BaseDirectory,"cache-"+Guid.NewGuid().ToString("N"));
        var calls=0;
        var builder=WebApplication.CreateSlimBuilder();builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(options=>options.Listen(System.Net.IPAddress.Loopback,0));
        await using var app=builder.Build();
        app.MapGet("/small",async context=>{Interlocked.Increment(ref calls);await context.Response.WriteAsync("media-bytes");});
        app.MapGet("/large",async context=>await context.Response.Body.WriteAsync(new byte[100]));
        await app.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            var cache=new MediaCache(directory,32);
            var url=app.Urls.Single()+"/small";
            var paths=await Task.WhenAll(Enumerable.Range(0,8).Select(_=>cache.GetAsync(url,new Dictionary<string,string>(),TestContext.Current.CancellationToken)));
            Assert.Equal(1,calls);Assert.Single(paths.Distinct());
            Assert.Equal("media-bytes",await File.ReadAllTextAsync(paths[0],TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<InvalidDataException>(()=>cache.GetAsync(app.Urls.Single()+"/large",new Dictionary<string,string>(),TestContext.Current.CancellationToken));
            Assert.Single(Directory.GetFiles(directory));
        }
        finally{await app.StopAsync(TestContext.Current.CancellationToken);if(Directory.Exists(directory))Directory.Delete(directory,true);}
    }
}
