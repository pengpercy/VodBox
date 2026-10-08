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

/// <summary>
/// csp_App99 离线回归：真实宝盒 JSON ext 形态、AES-256（ASCII UUID 作密钥）+
/// SHA256 签名 + zlib/去填充双响应、解析器头隔离、并发槽位/取消、炸弹防护。
/// </summary>
public sealed class App99SourceTests
{
    private const string Uuid = "01234567-89ab-cdef-0123-456789abcdef";

    /// <summary>真实宝盒 ext 形态（双星99 实测字段）。</summary>
    private static SourceInfo Source() => new()
    {
        Key = "app99",
        Name = "App99",
        Runtime = SourceRuntime.NativeSpider,
        Api = "csp_App99",
        Ext = "{\"host\":\"https://api.example/app/bn\",\"appkey\":\"fixture\",\"name\":\"现场演出\",\"versionName\":\"2.0\",\"buildSignature\":\"fixture-signature\",\"uuid\":\"" + Uuid + "\"}"
    };

    // ---------- ext 解析 ----------

    [Fact]
    public void ExtJson真实形态解析出所有参数()
    {
        var cfg = App99Source.ParseExt("""{"host":"http://103.217.190.91:19987/app/bn","appkey":"24d625a8a29b4700a1a294c6f3b29e2c","versionName":"3.5.8","name":"半日闲","package":"com.yf.lelian","buildNumber":"2001","buildSignature":"A40DA80A59D170CAA950CF15C18C454D47A39B26989D8B640ECD745BA71BF5DC"}""");
        Assert.Equal("http://103.217.190.91:19987/app/bn", cfg.Host);
        Assert.Equal("24d625a8a29b4700a1a294c6f3b29e2c", cfg.AppKey);
        Assert.Equal("半日闲", cfg.Name);
        Assert.Equal("3.5.8", cfg.VersionName);
        Assert.Equal("A40DA80A59D170CAA950CF15C18C454D47A39B26989D8B640ECD745BA71BF5DC", cfg.BuildSignature);
        Assert.Null(cfg.Uuid); // 未下发 → 实例内随机
    }

    [Fact]
    public void Ext非Json或缺失关键字段报明确错误()
    {
        Assert.Throws<NotSupportedException>(() => App99Source.ParseExt("https://a.example|key"));
        Assert.Throws<NotSupportedException>(() => App99Source.ParseExt(""));
        Assert.Throws<InvalidDataException>(() => App99Source.ParseExt("""{"host":"https://a.example"}"""));
        Assert.Throws<InvalidDataException>(() => App99Source.ParseExt("""{"appkey":"k"}"""));
        Assert.Throws<NotSupportedException>(() => App99Source.ParseExt("""{"host":"https://a.example","appkey":"k","protocol":"v3"}"""));
    }

    [Fact]
    public void 非法uuid构造即失败()
    {
        var bad = Source();
        var ext = bad.Ext!.Replace(Uuid, "bad");
        Assert.Throws<InvalidDataException>(() => new App99Source(bad with { Ext = ext }, new HttpClient(new DummyHandler())));
    }

    private sealed class DummyHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult<HttpResponseMessage>(new(HttpStatusCode.OK) { Content = new StringContent("{}") });
    }

    // ---------- 协议 ----------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 匿名加密协议保留分页与新鲜播放(bool compressed)
    {
        var routes = new Routes { Compressed = compressed };
        using var http = new HttpClient(routes);
        using var source = new App99Source(Source(), http);
        Assert.Equal("电影", Assert.Single(await source.GetCategoriesAsync(default)).Name);
        var page = await source.GetItemsAsync("1", 1, new Dictionary<string, string> { ["year"] = "2026" }, default);
        Assert.Equal(2, page.PageCount);
        Assert.Equal("7", Assert.Single(page.Items).Id);
        Assert.Equal("https://cdn.example/poster.jpg", page.Items[0].Poster);
        var next = await source.GetItemsAsync("1", 2, null, default);
        Assert.Equal(2, next.PageCount); // 空页停在当前页
        Assert.Single((await source.SearchAsync("C# & 中文", 1, default)).Items);
        var detail = await source.GetDetailAsync("7", default);
        Assert.Equal(2, detail.Lines.Count);
        await source.GetDetailAsync("7", default);
        Assert.Equal(1, routes.Details); // 详情缓存生效
        var direct = await source.ResolvePlaybackAsync("7", "0:0", default);
        Assert.Equal("https://cdn.example/movie.m3u8", direct.Uri);
        Assert.Single(direct.Headers);
        var first = await source.ResolvePlaybackAsync("7", "1:0", default);
        var second = await source.ResolvePlaybackAsync("7", "1:0", default);
        Assert.NotEqual(first.Uri, second.Uri);
        Assert.Equal(4, routes.Details); // 老契约对齐：缓存1 + 断言1 + 解析刷新2
        Assert.Equal(1, routes.Initializations);
        Assert.DoesNotContain(routes.Paths, x => x.Contains("/app/log", StringComparison.Ordinal));
        await Assert.ThrowsAsync<InvalidDataException>(() => source.ResolvePlaybackAsync("7", "missing", default));
    }

    [Fact]
    public async Task 独立OpenSSL向量以ASCIIUuid为AES256密钥()
    {
        using var http = new HttpClient(new Routes
        {
            Raw = "ABEiM0RVZneImaq7zN3u/w9a71lnDqHHD7cn0Btn62LxHdPqdb83MHKv/Vue4VBDDUu9muudbbJ9o0AyS9rzgwhTKPNYk1Ec3ow4Ytuvhw4="
        });
        using var source = new App99Source(Source(), http);
        Assert.Empty(await source.GetCategoriesAsync(default));
    }

    [Theory]
    [InlineData("not base64")]
    [InlineData("AA==")]
    public async Task 非法密文不会变成空成功(string raw)
    {
        using var http = new HttpClient(new Routes { Raw = raw });
        using var source = new App99Source(Source(), http);
        await Assert.ThrowsAsync<InvalidDataException>(() => source.GetCategoriesAsync(default));
    }

    [Fact]
    public async Task 解压炸弹在JSON解析前被截断()
    {
        using var http = new HttpClient(new Routes { Bomb = true });
        using var source = new App99Source(Source(), http);
        await Assert.ThrowsAsync<InvalidDataException>(() => source.GetCategoriesAsync(default));
    }

    [Fact]
    public async Task 四路IO槽位与取消释放()
    {
        var routes = new Routes { Wait = true };
        using var http = new HttpClient(routes);
        using var source = new App99Source(Source(), http);
        using var cancellation = new CancellationTokenSource();
        var tasks = Enumerable.Range(0, 8).Select(_ => source.SearchAsync("wait", 1, cancellation.Token)).ToArray();
        await routes.FourStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(4, routes.Active);
        cancellation.Cancel();
        foreach (var task in tasks) await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await task);
        Assert.Equal(0, routes.Active);
        routes.Wait = false;
        Assert.Single((await source.SearchAsync("next", 1, default)).Items);
    }

    [Fact]
    public async Task 非法标识页码重定向与释放后均显式失败()
    {
        var routes = new Routes();
        using var http = new HttpClient(routes);
        var source = new App99Source(Source(), http);
        await Assert.ThrowsAsync<InvalidDataException>(() => source.GetDetailAsync("file:///tmp/video", default));
        await Assert.ThrowsAsync<InvalidDataException>(() => source.SearchAsync("query", 1001, default));
        Assert.Empty(routes.Paths);
        Assert.IsType<App99Source>(NativeSpiders.Create(Source()));
        routes.Redirect = true;
        await Assert.ThrowsAsync<InvalidDataException>(() => source.GetCategoriesAsync(default));
        source.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => source.SearchAsync("query", 1, default));
    }

    [Fact]
    public void NativeSpiders登记App99入口()
    {
        Assert.True(NativeSpiders.IsSupported("csp_App99"));
        Assert.Contains("csp_App99", NativeSpiders.Registered);
    }

    // ---------- 假件 ----------

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
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Single(request.Headers); // 只有 UA
                Assert.Null(request.Content);
                Assert.Equal("opaque & 中文", Uri.UnescapeDataString(request.RequestUri.Query[5..]));
                return Response("{\"result\":{\"playUrl\":\"https://cdn.example/stream?generation=" + Interlocked.Increment(ref Parses) + "\"}}");
            }
            Assert.Equal("api.example", request.RequestUri.Host);
            Assert.StartsWith("/app/bn/", request.RequestUri.AbsolutePath);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(Uuid, Assert.Single(request.Headers.GetValues("uuid")));
            string body = await request.Content!.ReadAsStringAsync(token);
            string timestamp = Assert.Single(request.Headers.GetValues("timestamp"));
            string nonce = Assert.Single(request.Headers.GetValues("nonce"));
            Assert.Equal(16, Convert.FromBase64String(nonce).Length);
            // 签名：SHA256(body:timestamp:nonce::appkey)
            Assert.Equal(
                Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body + ":" + timestamp + ":" + nonce + "::fixture"))),
                Assert.Single(request.Headers.GetValues("sign")));
            byte[] wire = Convert.FromBase64String(body);
            using var aes = Aes.Create();
            aes.Key = Encoding.ASCII.GetBytes(Uuid.Replace("-", ""));
            using var json = JsonDocument.Parse(aes.DecryptCbc(wire.AsSpan(16), wire.AsSpan(0, 16)));
            Assert.Equal(timestamp, json.RootElement.GetProperty("timestamp").GetString());
            Assert.Equal(nonce, json.RootElement.GetProperty("nonce").GetString());
            Assert.Equal("", json.RootElement.GetProperty("token").GetString());
            if (Redirect) return new(HttpStatusCode.Redirect);
            if (Raw is not null) return Response(Raw);
            string path = request.RequestUri.AbsolutePath, response;
            if (path.EndsWith("/app/systemInit", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref Initializations);
                Assert.Equal("现场演出", json.RootElement.GetProperty("n").GetString());
                response = "{\"code\":0,\"categorys\":{\"data\":[{\"id\":1,\"name\":\"电影\"}]},\"player\":{\"direct\":{\"name\":\"直接\",\"type\":0},\"json\":{\"name\":\"JSON\",\"type\":1,\"parseUrl\":\"9\"}},\"parser_api\":[{\"id\":9,\"api_url\":\"https://parser.example/?url=\"}]}";
            }
            else if (path.EndsWith("/vod/detail", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref Details);
                Assert.Equal("7", json.RootElement.GetProperty("id").GetString());
                response = "{\"code\":0,\"data\":{\"id\":7,\"name\":\"Title\",\"play_from\":\"direct$$$json\",\"play_url\":\"First$https://cdn.example/movie.m3u8$$$Second$opaque & 中文\"}}";
            }
            else
            {
                Assert.EndsWith("/vod/search", path);
                Assert.Equal(21, json.RootElement.GetProperty("limit").GetInt32());
                if (Interlocked.Increment(ref Active) == 4) FourStarted.TrySetResult();
                try { if (Wait) await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                finally { Interlocked.Decrement(ref Active); }
                bool last = json.RootElement.GetProperty("page").ToString() == "2";
                response = "{\"code\":0,\"page_count\":2,\"data\":[" + (last ? "" : "{\"id\":7,\"name\":\"Title\",\"pic\":\"//cdn.example/poster.jpg\"}") + "]}";
            }
            byte[] payload = Encoding.UTF8.GetBytes(response);
            if (Compressed || Bomb)
            {
                using var stream = new MemoryStream();
                using (var zipped = new ZLibStream(stream, CompressionLevel.SmallestSize, true))
                    zipped.Write(Bomb ? new byte[8 * 1024 * 1024 + 1] : payload);
                payload = stream.ToArray();
            }
            byte[] iv = RandomNumberGenerator.GetBytes(16);
            byte[] encrypted = aes.EncryptCbc(payload, iv);
            var result = new byte[16 + encrypted.Length];
            iv.CopyTo(result, 0);
            encrypted.CopyTo(result, 16);
            return Response(Convert.ToBase64String(result));
        }

        private static HttpResponseMessage Response(string text) =>
            new(HttpStatusCode.OK) { Content = new StringContent(text) };
    }
}
