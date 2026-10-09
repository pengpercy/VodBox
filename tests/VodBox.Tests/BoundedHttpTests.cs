using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using VodBox.Infrastructure;
using Xunit;

namespace VodBox.Tests;

public sealed class BoundedHttpTests
{
    [Fact]
    public async Task BoundedDownloadRejectsDeclaredAndChunkedOversizeAndReadsValidResponse()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(options => options.Listen(System.Net.IPAddress.Loopback, 0));
        await using var app = builder.Build();
        app.MapGet("/small", async context => await context.Response.WriteAsync("hello"));
        app.MapGet("/declared", async context =>
        {
            context.Response.ContentLength = 4096;
            await context.Response.Body.WriteAsync(new byte[4096]);
        });
        app.MapGet("/chunked", async context =>
        {
            for (var i = 0; i < 4; i++)
            {
                await context.Response.Body.WriteAsync(new byte[1024]);
                await context.Response.Body.FlushAsync();
            }
        });
        await app.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            using var http = new DefaultHttp();
            var address = app.Urls.Single();
            Assert.Equal("hello", DefaultHttp.Decode(await http.GetBoundedAsync(address + "/small", 100, TestContext.Current.CancellationToken)));
            await Assert.ThrowsAsync<InvalidDataException>(() => http.GetBoundedAsync(address + "/declared", 100, TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<InvalidDataException>(() => http.GetBoundedAsync(address + "/chunked", 100, TestContext.Current.CancellationToken));
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => http.GetBoundedAsync(address + "/small", 100, cancelled.Token));
        }
        finally { await app.StopAsync(TestContext.Current.CancellationToken); }
    }
}
