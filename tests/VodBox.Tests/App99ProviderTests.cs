using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VodBox.Core;
using VodBox.Infrastructure;
using Xunit;

namespace VodBox.Tests;

public sealed class App99ProviderTests
{
    private const string Uuid = "01234567-89ab-cdef-0123-456789abcdef";
    private static SourceDefinition Source() => new()
    {
        Id = "app99", Name = "App99", Entry = "https://api.example/app/bn", Provider = "app99",
        Options = JsonDocument.Parse("{\"protocol\":\"bn-v2\",\"uuid\":\"" + Uuid + "\",\"appkey\":\"fixture\",\"name\":\"现场演出\",\"versionName\":\"2.0\",\"buildSignature\":\"fixture-signature\"}")
            .RootElement.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.Clone())
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnonymousEncryptedSpiderPreservesPrefixPagesAndFreshPlayback(bool compressed)
    {
        var routes = new Routes { Compressed = compressed }; using var http = new HttpClient(routes);
        await using var provider = new App99Provider(Source(), http);
        Assert.Equal("电影", Assert.Single(await provider.GetCategoriesAsync(default)).Name);
        var page = await provider.GetItemsFilteredAsync("1", null, new Dictionary<string, string> { ["year"] = "2026" }, default);
        Assert.Equal("2", page.NextCursor); Assert.Equal("7", Assert.Single(page.Items).Id);
        Assert.Equal("https://cdn.example/poster.jpg", page.Items[0].Poster);
        Assert.Null((await provider.GetItemsAsync("1", page.NextCursor, default)).NextCursor);
        Assert.Single((await provider.SearchAsync("C# & 中文", default)).Items);
        var detail = await provider.GetDetailAsync("7", default); Assert.Equal(2, detail.PlaybackLines.Count);
        await provider.GetDetailAsync("7", default); Assert.Equal(1, routes.Details);
        var direct = await provider.ResolvePlaybackAsync("7", "0:0", default);
        Assert.Equal("https://cdn.example/movie.m3u8", direct.Uri); Assert.Single(direct.Headers);
        var first = await provider.ResolvePlaybackAsync("7", "1:0", default);
        var second = await provider.ResolvePlaybackAsync("7", "1:0", default);
        Assert.NotEqual(first.Uri, second.Uri); Assert.Equal(4, routes.Details); Assert.Equal(1, routes.Initializations);
        Assert.DoesNotContain(routes.Paths, x => x.Contains("/app/log", StringComparison.Ordinal));
        await Assert.ThrowsAsync<InvalidDataException>(() => provider.ResolvePlaybackAsync("7", "missing", default));
    }

    [Fact]
    public async Task IndependentOpenSslVectorUsesAsciiUuidAsAes256Key()
    {
        using var http = new HttpClient(new Routes { Raw = "ABEiM0RVZneImaq7zN3u/w9a71lnDqHHD7cn0Btn62LxHdPqdb83MHKv/Vue4VBDDUu9muudbbJ9o0AyS9rzgwhTKPNYk1Ec3ow4Ytuvhw4=" });
        await using var provider = new App99Provider(Source(), http);
        Assert.Empty(await provider.GetCategoriesAsync(default));
    }

    [Theory]
    [InlineData("not base64")]
    [InlineData("AA==")]
    public async Task InvalidCiphertextNeverBecomesEmptySuccess(string raw)
    {
        using var http = new HttpClient(new Routes { Raw = raw });
        await using var provider = new App99Provider(Source(), http);
        await Assert.ThrowsAsync<InvalidDataException>(() => provider.GetCategoriesAsync(default));
    }

    [Fact]
    public async Task DecompressionBombIsBoundedBeforeJsonParsing()
    {
        using var http = new HttpClient(new Routes { Bomb = true });
        await using var provider = new App99Provider(Source(), http);
        await Assert.ThrowsAsync<InvalidDataException>(() => provider.GetCategoriesAsync(default));
    }

    [Fact]
    public async Task RequestsShareFourIoSlotsAndCancellationReleasesThem()
    {
        var routes = new Routes { Wait = true }; using var http = new HttpClient(routes);
        await using var provider = new App99Provider(Source(), http);
        using var cancellation = new CancellationTokenSource();
        Task[] tasks = Enumerable.Range(0, 8).Select(_ => provider.SearchAsync("wait", cancellation.Token)).ToArray();
        await routes.FourStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(4, routes.Active); cancellation.Cancel();
        foreach (var task in tasks) await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await task);
        Assert.Equal(0, routes.Active); routes.Wait = false;
        Assert.Single((await provider.SearchAsync("next", default)).Items);
    }

    [Fact]
    public async Task UnknownProtocolsInvalidIdsAndRedirectsFailExplicitly()
    {
        var source = Source(); var routes = new Routes(); using var http = new HttpClient(routes);
        await using var provider = new App99Provider(source, http);
        await Assert.ThrowsAsync<InvalidDataException>(() => provider.GetDetailAsync("file:///tmp/video", default));
        await Assert.ThrowsAsync<InvalidDataException>(() => provider.SearchPageAsync("query", "1001", default));
        Assert.Empty(routes.Paths);
        Assert.IsType<App99Provider>(new ProviderFactory(http, "unused", "unused").Create(source with { Provider = "csp_App99" }));
        routes.Redirect = true;
        await Assert.ThrowsAsync<InvalidDataException>(() => provider.GetCategoriesAsync(default));
        await provider.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => provider.SearchAsync("query", default));
        source.Options["uuid"] = JsonSerializer.SerializeToElement("bad", VodBoxJson.Default.String);
        Assert.Throws<InvalidDataException>(() => new App99Provider(source));
    }

    private sealed class Routes : HttpMessageHandler
    {
        public bool Compressed, Bomb, Wait, Redirect;
        public string? Raw;
        public int Initializations, Details, Parses, Active;
        public ConcurrentQueue<string> Paths { get; } = new();
        public TaskCompletionSource FourStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Paths.Enqueue(request.RequestUri!.AbsolutePath);
            if (request.RequestUri.Host == "parser.example")
            {
                Assert.Equal(HttpMethod.Get, request.Method); Assert.Single(request.Headers); Assert.Null(request.Content);
                Assert.Equal("opaque & 中文", Uri.UnescapeDataString(request.RequestUri.Query[5..]));
                return Response("{\"result\":{\"playUrl\":\"https://cdn.example/stream?generation=" + Interlocked.Increment(ref Parses) + "\"}}");
            }
            Assert.Equal("api.example", request.RequestUri.Host); Assert.StartsWith("/app/bn/", request.RequestUri.AbsolutePath);
            Assert.Equal(HttpMethod.Post, request.Method); Assert.Equal(Uuid, Assert.Single(request.Headers.GetValues("uuid")));
            string body = await request.Content!.ReadAsStringAsync(token), timestamp = Assert.Single(request.Headers.GetValues("timestamp")), nonce = Assert.Single(request.Headers.GetValues("nonce"));
            Assert.Equal(16, Convert.FromBase64String(nonce).Length);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body + ":" + timestamp + ":" + nonce + "::fixture"))), Assert.Single(request.Headers.GetValues("sign")));
            byte[] wire = Convert.FromBase64String(body); using var aes = Aes.Create(); aes.Key = Encoding.ASCII.GetBytes(Uuid.Replace("-", ""));
            using var json = JsonDocument.Parse(aes.DecryptCbc(wire.AsSpan(16), wire.AsSpan(0, 16)));
            Assert.Equal(timestamp, json.RootElement.GetProperty("timestamp").GetString());
            Assert.Equal(nonce, json.RootElement.GetProperty("nonce").GetString()); Assert.Equal("", json.RootElement.GetProperty("token").GetString());
            if (Redirect) return new(HttpStatusCode.Redirect);
            if (Raw is not null) return Response(Raw);
            string path = request.RequestUri.AbsolutePath, response;
            if (path.EndsWith("/app/systemInit", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref Initializations); Assert.Equal("现场演出", json.RootElement.GetProperty("n").GetString());
                response = "{\"code\":0,\"categorys\":{\"data\":[{\"id\":1,\"name\":\"电影\"}]},\"player\":{\"direct\":{\"name\":\"直接\",\"type\":0},\"json\":{\"name\":\"JSON\",\"type\":1,\"parseUrl\":\"9\"}},\"parser_api\":[{\"id\":9,\"api_url\":\"https://parser.example/?url=\"}]}";
            }
            else if (path.EndsWith("/vod/detail", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref Details); Assert.Equal("7", json.RootElement.GetProperty("id").GetString());
                response = "{\"code\":0,\"data\":{\"id\":7,\"name\":\"Title\",\"play_from\":\"direct$$$json\",\"play_url\":\"First$https://cdn.example/movie.m3u8$$$Second$opaque & 中文\"}}";
            }
            else
            {
                Assert.EndsWith("/vod/search", path); Assert.Equal(21, json.RootElement.GetProperty("limit").GetInt32());
                if (Interlocked.Increment(ref Active) == 4) FourStarted.TrySetResult();
                try { if (Wait) await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                finally { Interlocked.Decrement(ref Active); }
                bool last = json.RootElement.GetProperty("page").ToString() == "2";
                response = "{\"code\":0,\"page_count\":2,\"data\":[" + (last ? "" : "{\"id\":7,\"name\":\"Title\",\"pic\":\"//cdn.example/poster.jpg\"}") + "]}";
            }
            byte[] payload = Encoding.UTF8.GetBytes(response);
            if (Compressed || Bomb)
            {
                using var stream = new MemoryStream(); using (var zipped = new ZLibStream(stream, CompressionLevel.SmallestSize, true)) zipped.Write(Bomb ? new byte[8 * 1024 * 1024 + 1] : payload);
                payload = stream.ToArray();
            }
            byte[] iv = RandomNumberGenerator.GetBytes(16), encrypted = aes.EncryptCbc(payload, iv), result = new byte[16 + encrypted.Length];
            iv.CopyTo(result, 0); encrypted.CopyTo(result, 16); return Response(Convert.ToBase64String(result));
        }
        private static HttpResponseMessage Response(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text) };
    }
}
