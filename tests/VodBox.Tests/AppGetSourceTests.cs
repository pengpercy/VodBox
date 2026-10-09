using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VodBox.Core;
using VodBox.Infrastructure;
using Xunit;

namespace VodBox.Tests;

/// <summary>
/// csp_AppGet 离线回归（假 HttpMessageHandler）：
/// 覆盖 ext 管道/JSON 解析、v119 form 签名与 qiji-v122 JSON 双分支、加密封包、
/// 外部解析器头隔离、并发槽位、取消与重定向拒绝。
/// </summary>
public sealed class AppGetSourceTests
{
    private const string Key = "0123456789abcdef";

    /// <summary>真实宝盒配置形态（2026-10-08 实测）：url|key|version|ua。</summary>
    private static SourceInfo Source(string ext, bool discovery = false) => new()
    {
        Key = "app",
        Name = "AppGet",
        Runtime = SourceRuntime.NativeSpider,
        Api = "csp_AppGet",
        Ext = ext
    };

    private static SourceInfo Root() => Source("https://api.example|0123456789abcdef");

    // ---------- ext 解析 ----------

    [Theory]
    // 真实宝盒 4 条目（版本段选择协议分支）
    [InlineData("https://www.gugu3.com|nKfZ8KX6JTNWRzTD|V119|okhttp/3.10", false, "/api.php/getappapi", "210")]
    [InlineData("https://yun-1316442804.cos.ap-guangzhou.myqcloud.com/603.txt|FTgP4Gq8zPiqbt7M|V122|okhttp/3.10.0", true, "/api.php/qijiappapi", "305")]
    // 两字段变体（宝盒「一碗」「蔬菜」）默认 v119
    [InlineData("https://app.95112475.xyz|5a9w6x58dsq6z3a6", false, "/api.php/getappapi", "210")]
    public void Ext管道形态解析出协议分支与发现模式(string ext, bool jsonV122, string apiPath, string version)
    {
        var cfg = AppGetSource.ReadExt(ext);
        Assert.Equal(jsonV122, cfg.JsonV122);
        Assert.Equal(apiPath, cfg.ApiPath);
        Assert.Equal(version, cfg.Version);
        // 非根地址（.txt）自动开启发现模式；根地址不开启
        Assert.Equal(ext.Contains(".txt"), cfg.Discovery);
    }

    [Fact]
    public void Ext管道缺失段数或字段报明确错误()
    {
        Assert.Throws<InvalidDataException>(() => AppGetSource.ReadExt("https://a.example"));
        Assert.Throws<InvalidDataException>(() => AppGetSource.ReadExt("|key"));
        Assert.Throws<InvalidDataException>(() => AppGetSource.ReadExt("https://a.example|"));
        Assert.Throws<NotSupportedException>(() => AppGetSource.ReadExt("   "));
    }

    [Fact]
    public void ExtJson形态用getType选分支并支持显式协议()
    {
        var qiji = AppGetSource.ReadExt("""{"host":"https://api.example","key":"0123456789abcdef","get_type":"2"}""");
        Assert.True(qiji.JsonV122);
        Assert.Equal("/api.php/qijiappapi", qiji.ApiPath);
        // get_type 任意真值（含 "1"/"2"）→ qiji 分支；缺省/假值 → v119
        var viaGet = AppGetSource.ReadExt("""{"host":"https://api.example","key":"0123456789abcdef","get_type":"1"}""");
        Assert.True(viaGet.JsonV122);
        var v119 = AppGetSource.ReadExt("""{"host":"https://api.example","key":"0123456789abcdef"}""");
        Assert.False(v119.JsonV122);
        var explicitCfg = AppGetSource.ReadExt("""{"host":"https://api.example","key":"0123456789abcdef","protocol":"v119","path":"/api.php/custom","version":"9","userAgent":"UA/1"}""");
        Assert.False(explicitCfg.JsonV122);
        Assert.Equal("/api.php/custom", explicitCfg.ApiPath);
        Assert.Equal("9", explicitCfg.Version);
        Assert.Equal("UA/1", explicitCfg.UserAgent);
        Assert.Throws<NotSupportedException>(() => AppGetSource.ReadExt("""{"host":"https://a.example","key":"0123456789abcdef","protocol":"v122"}"""));
    }

    [Fact]
    public void 无效密钥长度构造即失败()
    {
        Assert.Throws<InvalidDataException>(() => new AppGetSource(Source("https://api.example|shortkey")));
    }

    // ---------- 协议：v119 form 签名 + qiji-v122 JSON ----------

    [Fact]
    public async Task V119签名时间戳与解密首页映射分类推荐()
    {
        var routes = new Routes();
        using var http = new HttpClient(routes);
        using var source = new AppGetSource(Root(), http);
        Assert.Equal("番剧", Assert.Single(await source.GetCategoriesAsync(TestContext.Current.CancellationToken)).Name);
        var home = await source.GetHomeAsync(TestContext.Current.CancellationToken);
        Assert.Equal("7", Assert.Single(home.Items).Id);
        Assert.Equal(1, routes.HomeRequests); // 首页复用，不重复 init
    }

    [Fact]
    public async Task V119分类分页筛选与空页终止分页()
    {
        var routes = new Routes();
        using var http = new HttpClient(routes);
        using var source = new AppGetSource(Root(), http);
        var first = await source.GetItemsAsync("6", 1, new Dictionary<string, string> { ["year"] = "2026" }, TestContext.Current.CancellationToken);
        Assert.Single(first.Items);
        Assert.Equal(2, first.PageCount); // PageCount=page+1 表示有下一页
        var second = await source.GetItemsAsync("6", 2, null, TestContext.Current.CancellationToken);
        Assert.Empty(second.Items);
        Assert.Equal(2, second.PageCount);
    }

    [Fact]
    public async Task V119搜索分页带页数上限判定()
    {
        var routes = new Routes();
        using var http = new HttpClient(routes);
        using var source = new AppGetSource(Root(), http);
        var page = await source.SearchAsync("动画 & 科技", 1, TestContext.Current.CancellationToken);
        Assert.Single(page.Items);
        Assert.Equal(2, page.PageCount);
        var last = await source.SearchAsync("动画 & 科技", 2, TestContext.Current.CancellationToken);
        Assert.Equal(2, last.PageCount);
    }

    [Fact]
    public async Task 详情多线路直接播放带播放器UA()
    {
        var routes = new Routes();
        using var http = new HttpClient(routes);
        using var source = new AppGetSource(Root(), http);
        var detail = await source.GetDetailAsync("7", TestContext.Current.CancellationToken);
        Assert.Equal(2, detail.Lines.Count);
        Assert.Equal("7", detail.Item.Id);
        var direct = await source.ResolvePlaybackAsync("7", "0:0", TestContext.Current.CancellationToken);
        Assert.Equal("https://cdn.example/movie.m3u8", direct.Uri);
        Assert.Equal("Fixture Player", direct.Headers["User-Agent"]);
        Assert.Single(direct.Headers);
        Assert.Equal(ResolutionKind.Direct, direct.Resolution);
    }

    [Fact]
    public async Task 站内解析每次刷新详情拿到新地址()
    {
        var routes = new Routes();
        using var http = new HttpClient(routes);
        using var source = new AppGetSource(Root(), http);
        var first = await source.ResolvePlaybackAsync("7", "1:0", TestContext.Current.CancellationToken);
        var second = await source.ResolvePlaybackAsync("7", "1:0", TestContext.Current.CancellationToken);
        Assert.NotEqual(first.Uri, second.Uri);
        Assert.Equal(2, routes.DetailRequests); // 两次解析各刷一次详情（无前置 GetDetail）
        Assert.Equal(2, routes.ParseRequests);
        await Assert.ThrowsAsync<InvalidDataException>(() => source.ResolvePlaybackAsync("7", "missing", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 外部JSON解析器不带API签名头且走parseApiUrl(bool prepared)
    {
        var routes = new Routes { External = true, Prepared = prepared };
        using var http = new HttpClient(routes);
        using var source = new AppGetSource(Root(), http);
        var first = await source.ResolvePlaybackAsync("7", "1:0", TestContext.Current.CancellationToken);
        var second = await source.ResolvePlaybackAsync("7", "1:0", TestContext.Current.CancellationToken);
        Assert.Equal("https://cdn.example/stream?generation=1", first.Uri);
        Assert.Equal("https://cdn.example/stream?generation=2", second.Uri);
        Assert.Equal(2, routes.ExternalRequests);
        Assert.Equal(0, routes.ParseRequests);
        Assert.Single(first.Headers);
    }

    [Fact]
    public async Task QijiV122用JSON版本号与独立端点()
    {
        var routes = new QijiRoutes();
        using var http = new HttpClient(routes);
        using var source = new AppGetSource(Source("https://api.example|0123456789abcdef|V122|okhttp/3.10"), http);
        Assert.Equal("6", Assert.Single(await source.GetCategoriesAsync(TestContext.Current.CancellationToken)).Id);
        Assert.Single((await source.SearchAsync("中文 & C#", 1, TestContext.Current.CancellationToken)).Items);
        Assert.Single((await source.GetItemsAsync("6", 1, null, TestContext.Current.CancellationToken)).Items);
        var request = await source.ResolvePlaybackAsync("7", "0:0", TestContext.Current.CancellationToken);
        Assert.Equal("https://cdn.example/stream?id=7", request.Uri);
        Assert.Equal(["initV122", "searchList4", "typeFilterVodList", "vodDetail2"], routes.Actions);
    }

    [Fact]
    public async Task Qiji搜索需验证码时在请求前停止()
    {
        var routes = new QijiRoutes { Verification = true };
        using var http = new HttpClient(routes);
        using var source = new AppGetSource(Source("https://api.example|0123456789abcdef|V122"), http);
        await Assert.ThrowsAsync<NotSupportedException>(() => source.SearchAsync("query", 1, TestContext.Current.CancellationToken));
        Assert.Equal(["initV122"], routes.Actions);
    }

    // ---------- 安全边界 ----------

    [Theory]
    [InlineData("""{"code":0,"data":[]}""")]
    [InlineData("""{"code":1,"data":"invalid base64"}""")]
    [InlineData("""{"code":1,"data":"AA=="}""")]
    public async Task 非法信封或加密失败不返回空成功(string json)
    {
        using var http = new HttpClient(new Routes { Envelope = json });
        using var source = new AppGetSource(Root(), http);
        await Assert.ThrowsAsync<InvalidDataException>(() => source.GetCategoriesAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("""{"url":"file:///etc/passwd"}""")]
    [InlineData("""{"data":{"url":"javascript:alert(1)"}}""")]
    [InlineData("""{"data":{}}""")]
    public async Task 外部解析器拒绝非HTTP结果(string response)
    {
        using var http = new HttpClient(new Routes { External = true, ExternalResponse = response });
        using var source = new AppGetSource(Root(), http);
        await Assert.ThrowsAsync<InvalidDataException>(() => source.ResolvePlaybackAsync("7", "1:0", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task 外部解析响应超限直接失败()
    {
        using var http = new HttpClient(new Routes { External = true, ExternalResponse = new string('x', 1024 * 1024 + 1) });
        using var source = new AppGetSource(Root(), http);
        await Assert.ThrowsAsync<InvalidDataException>(() => source.ResolvePlaybackAsync("7", "1:0", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task 发现模式只发UA且API拒绝重定向()
    {
        var routes = new Routes();
        using var http = new HttpClient(routes);
        using var source = new AppGetSource(Source("https://discovery.example/entry.txt|0123456789abcdef"), http);
        await source.GetCategoriesAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, routes.Discoveries);
        routes.Redirect = true;
        await Assert.ThrowsAsync<InvalidDataException>(() => source.SearchAsync("query", 1, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task 取消释放全部四个IO槽位()
    {
        var routes = new Routes { Wait = true };
        using var http = new HttpClient(routes);
        using var source = new AppGetSource(Root(), http);
        using var cancellation = new CancellationTokenSource();
        var tasks = Enumerable.Range(0, 8).Select(_ => source.SearchAsync("query", 1, cancellation.Token)).ToArray();
        await routes.FourStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(4, routes.Active);
        cancellation.Cancel();
        foreach (var task in tasks) await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await task);
        Assert.Equal(0, routes.Active);
    }

    [Fact]
    public async Task 非法输入与已释放实例不触网()
    {
        var routes = new Routes();
        using var http = new HttpClient(routes);
        var source = new AppGetSource(Root(), http);
        await Assert.ThrowsAsync<InvalidDataException>(() => source.GetDetailAsync("https://other.example", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidDataException>(() => source.GetItemsAsync("6", 1001, null, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<NotSupportedException>(
            () => source.GetItemsAsync("6", 1, new Dictionary<string, string> { ["unknown"] = "x" }, TestContext.Current.CancellationToken));
        source.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => source.SearchAsync("query", 1, TestContext.Current.CancellationToken));
        Assert.Equal(0, routes.Requests);
        // 注册表路由
        var created = NativeSpiders.Create(Root());
        Assert.IsType<AppGetSource>(created);
    }

    [Fact]
    public void NativeSpiders登记AppGet入口()
    {
        Assert.True(NativeSpiders.IsSupported("csp_AppGet"));
        Assert.Contains("csp_AppGet", NativeSpiders.Registered);
    }

    // ---------- 假件 ----------

    private sealed class Routes : HttpMessageHandler
    {
        public int HomeRequests, DetailRequests, ParseRequests, Discoveries, Requests, Active, ExternalRequests;
        public bool Wait, Redirect, External, Prepared;
        public string? Envelope, ExternalResponse;
        public TaskCompletionSource FourStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Interlocked.Increment(ref Requests);
            var uri = request.RequestUri!;
            if (uri.Host == "parser.example")
            {
                ExternalRequests++;
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Single(request.Headers); // 只有 UA：无签名头、无设备标识
                Assert.False(request.Headers.Contains("app-api-verify-sign"));
                Assert.False(request.Headers.Contains("app-user-device-id"));
                Assert.Null(request.Content);
                if (Interlocked.Increment(ref Active) == 4) FourStarted.TrySetResult();
                Interlocked.Decrement(ref Active);
                return Json(ExternalResponse ?? "{\"data\":{\"url\":\"https://cdn.example/stream?generation=" + ExternalRequests + "\"}}");
            }
            if (uri.Host == "discovery.example")
            {
                Discoveries++;
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Single(request.Headers);
                Assert.False(request.Headers.Contains("app-api-verify-sign"));
                return Json("https://api.example");
            }
            Assert.Equal("api.example", uri.Host);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.StartsWith("/api.php/getappapi.index/", uri.AbsolutePath);
            Assert.Equal("application/x-www-form-urlencoded", request.Content!.Headers.ContentType!.MediaType);
            // 签名头：verify-time 的密文必须等于 verify-sign
            Assert.Equal(Assert.Single(request.Headers.GetValues("app-api-verify-time")),
                Decrypt(Assert.Single(request.Headers.GetValues("app-api-verify-sign"))));
            Assert.False(request.Headers.Contains("Cookie"));
            if (Redirect) return new(HttpStatusCode.Redirect);
            if (Envelope is not null) return Json(Envelope);
            var form = (await request.Content.ReadAsStringAsync(token)).Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Split('=', 2)).ToDictionary(x => Decode(x[0]), x => Decode(x[1]));
            string action = uri.Segments.Last();
            string data;
            if (action == "initV119")
            {
                HomeRequests++;
                data = "{\"type_list\":[{\"type_id\":0,\"type_name\":\"全部\"},{\"type_id\":6,\"type_name\":\"番剧\",\"recommend_list\":[" + Card + "]}],\"banner_list\":[" + Card + "]}";
            }
            else if (action == "vodDetail")
            {
                DetailRequests++;
                Assert.Equal("7", form["vod_id"]);
                data = "{\"vod\":" + Card + ",\"vod_play_list\":[{\"player_info\":{\"show\":\"直接\",\"user_agent\":\"Fixture Player\"},\"urls\":[{\"name\":\"第一集\",\"url\":\"https://cdn.example/movie.m3u8\"}]},{\"player_info\":{\"show\":\"解析\",\"parse\":\"https://parser.example/?url=\",\"player_parse_type\":\"1\"},\"urls\":[{\"name\":\"第二集\",\"url\":\"opaque token & 中文\",\"token\":\"fixture\"}]}]}";
            }
            else if (action == "vodParse")
            {
                ParseRequests++;
                Assert.Equal("opaque token & 中文", Decrypt(form["url"]));
                Assert.False(form["url"].Contains('%')); // 单次表单编码，密文不预编码
                Assert.Equal("1", form["player_parse_type"]);
                Assert.Equal("fixture", form["token"]);
                data = "{\"json\":\"{\\\"url\\\":\\\"https://cdn.example/movie.mp4?generation=" + ParseRequests + "\\\"}\"}";
            }
            else
            {
                if (Interlocked.Increment(ref Active) == 4) FourStarted.TrySetResult();
                try { if (Wait) await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                finally { Interlocked.Decrement(ref Active); }
                if (action == "typeFilterVodList")
                    data = "{\"recommend_list\":[" + (form["page"] == "2" ? "" : Card) + "]}";
                else
                {
                    Assert.Equal("searchList", action);
                    data = "{\"pagecount\":2,\"search_list\":[" + Card + "]}";
                }
            }
            if (External && action == "vodDetail")
            {
                data = data.Replace("\"player_parse_type\":\"1\"", "\"player_parse_type\":\"2\"", StringComparison.Ordinal);
                if (Prepared)
                {
                    data = data.Replace("\"parse\":\"https://parser.example/?url=\"", "\"parse\":\"\"", StringComparison.Ordinal)
                        .Replace("\"url\":\"opaque token & 中文\"",
                            "\"url\":\"opaque token & 中文\",\"parse_api_url\":\"https://parser.example/?ticket=fixture\"",
                            StringComparison.Ordinal);
                }
            }
            return Json("{\"code\":1,\"data\":\"" + Encrypt(data) + "\"}");
        }

        private static string Decode(string value) => Uri.UnescapeDataString(value.Replace('+', ' '));
        private const string Card = "{\"vod_id\":7,\"vod_name\":\"Title &amp; more\",\"vod_pic\":\"//cdn.example/poster.jpg\"}";
    }

    private sealed class QijiRoutes : HttpMessageHandler
    {
        public bool Verification;
        public List<string> Actions { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.Equal("api.example", request.RequestUri!.Host);
            Assert.StartsWith("/api.php/qijiappapi.index/", request.RequestUri.AbsolutePath);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Single(request.Headers); // JSON 分支不带签名头
            Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
            using var payload = JsonDocument.Parse(await request.Content.ReadAsStringAsync(token));
            Assert.Equal("305", payload.RootElement.GetProperty("version").GetString());
            string action = request.RequestUri.Segments.Last();
            Actions.Add(action);
            const string card = "{\"vod_id\":7,\"vod_name\":\"Fixture\"}";
            string response = action switch
            {
                "initV122" => "{\"type_list\":[{\"type_id\":6,\"type_name\":\"电影\"}],\"banner_list\":[],\"config\":{\"system_search_verify_status\":" + (Verification ? "1" : "0") + "}}",
                "searchList4" => "{\"search_list\":[" + card + "]}",
                "typeFilterVodList" => "{\"recommend_list\":[" + card + "]}",
                "vodDetail2" => "{\"vod\":" + card + ",\"vod_play_list\":[{\"player_info\":{\"show\":\"直接\",\"parse_type\":\"0\"},\"urls\":[{\"name\":\"Main\",\"url\":\"https://cdn.example/stream?id=7\"}]}]}",
                _ => throw new InvalidOperationException(action)
            };
            using var aes = Aes.Create();
            aes.Key = Encoding.UTF8.GetBytes(Key);
            string encrypted = Convert.ToBase64String(aes.EncryptCbc(
                Encoding.UTF8.GetBytes(response), Encoding.UTF8.GetBytes("0123456789abcdef")));
            return Json("{\"code\":1,\"data\":\"" + encrypted + "\"}");
        }
    }

    private static string Encrypt(string text)
    {
        using var aes = Aes.Create();
        aes.Key = Encoding.UTF8.GetBytes(Key);
        return Convert.ToBase64String(aes.EncryptCbc(Encoding.UTF8.GetBytes(text), aes.Key));
    }

    private static string Decrypt(string text)
    {
        using var aes = Aes.Create();
        aes.Key = Encoding.UTF8.GetBytes(Key);
        return Encoding.UTF8.GetString(aes.DecryptCbc(Convert.FromBase64String(text), aes.Key));
    }

    private static HttpResponseMessage Json(string text) =>
        new(HttpStatusCode.OK) { Content = new StringContent(text) };
}
