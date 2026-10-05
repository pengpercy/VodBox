using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VodBox.Core;
using VodBox.Infrastructure;
using Xunit;

namespace VodBox.Tests;

public sealed class AppGetProviderTests
{
    private const string Key = "0123456789abcdef";
    private static SourceDefinition Source(bool discovery = false) => new()
    {
        Id = "app", Name = "AppGet", Entry = discovery ? "https://discovery.example/entry.txt" : "https://api.example/",
        Provider = "appget", Options = new()
        {
            ["protocol"] = JsonSerializer.SerializeToElement("v119", VodBoxJson.Default.String),
            ["key"] = JsonSerializer.SerializeToElement(Key, VodBoxJson.Default.String),
            ["discovery"] = JsonDocument.Parse(discovery ? "true" : "false").RootElement.Clone()
        }
    };

    [Fact]
    public async Task ExplicitQijiV122UsesJsonVersionAndItsOwnEndpoints()
    {
        var source = Source();
        source.Options["protocol"] = JsonSerializer.SerializeToElement("qiji-v122", VodBoxJson.Default.String);
        source.Options["iv"] = JsonSerializer.SerializeToElement("fedcba9876543210", VodBoxJson.Default.String);
        var routes = new QijiRoutes(); using var http = new HttpClient(routes);
        await using var provider = new AppGetProvider(source, http);
        Assert.Equal("6", Assert.Single(await provider.GetCategoriesAsync(default)).Id);
        Assert.Single((await provider.SearchAsync("中文 & C#", default)).Items);
        Assert.Single((await provider.GetItemsAsync("6", null, default)).Items);
        var request = await provider.ResolvePlaybackAsync("7", "0:0", default);
        Assert.Equal("https://cdn.example/stream?id=7", request.Uri);
        Assert.Equal(["initV122", "searchList4", "typeFilterVodList", "vodDetail2"], routes.Actions);
    }

    [Fact]
    public async Task QijiSearchVerificationRequirementStopsBeforeSearchRequest()
    {
        var source = Source(); source.Options["protocol"] = JsonSerializer.SerializeToElement("qiji-v122", VodBoxJson.Default.String);
        source.Options["iv"] = JsonSerializer.SerializeToElement("fedcba9876543210", VodBoxJson.Default.String);
        var routes = new QijiRoutes { Verification = true }; using var http = new HttpClient(routes);
        await using var provider = new AppGetProvider(source, http);
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.SearchAsync("query", default));
        Assert.Equal(["initV122"], routes.Actions);
    }

    private sealed class QijiRoutes : HttpMessageHandler
    {
        public bool Verification;
        public List<string> Actions { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.Equal("api.example", request.RequestUri!.Host);
            Assert.StartsWith("/api.php/qijiappapi.index/", request.RequestUri.AbsolutePath);
            Assert.Equal(HttpMethod.Post, request.Method); Assert.Single(request.Headers);
            Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
            using var payload = JsonDocument.Parse(await request.Content.ReadAsStringAsync(token));
            Assert.Equal("305", payload.RootElement.GetProperty("version").GetString());
            string action = request.RequestUri.Segments.Last(); Actions.Add(action);
            string card = "{\"vod_id\":7,\"vod_name\":\"Fixture\"}";
            string response = action switch
            {
                "initV122" => "{\"type_list\":[{\"type_id\":6,\"type_name\":\"电影\"}],\"banner_list\":[],\"config\":{\"system_search_verify_status\":" + (Verification ? "1" : "0") + "}}",
                "searchList4" => "{\"search_list\":[" + card + "]}",
                "typeFilterVodList" => "{\"recommend_list\":[" + card + "]}",
                "vodDetail2" => "{\"vod\":" + card + ",\"vod_play_list\":[{\"player_info\":{\"show\":\"直接\",\"parse_type\":\"0\"},\"urls\":[{\"name\":\"Main\",\"url\":\"https://cdn.example/stream?id=7\"}]}]}",
                _ => throw new InvalidOperationException(action)
            };
            using var aes = Aes.Create(); aes.Key = Encoding.UTF8.GetBytes(Key);
            string encrypted = Convert.ToBase64String(aes.EncryptCbc(Encoding.UTF8.GetBytes(response), Encoding.UTF8.GetBytes("fedcba9876543210")));
            return Json("{\"code\":1,\"data\":\"" + encrypted + "\"}");
        }
    }

    [Fact]
    public async Task IndependentOpenSslCiphertextFixtureDecryptsWithProtocolIvAndPadding()
    {
        using var http = new HttpClient(new Routes
        {
            Envelope = "{\"code\":1,\"data\":\"hcqZKaf9IBcXZi7eb6hTvcdRZE130b1HVgnIfBNtcldoJtr8p7ZH3fK6/WUhpIjT\"}"
        });
        await using var provider = new AppGetProvider(Source(), http);
        Assert.Empty(await provider.GetCategoriesAsync(default));
    }

    [Fact]
    public async Task EncryptedSpiderMapsHomePagingFiltersSearchDetailAndFreshParser()
    {
        var routes = new Routes(); using var http = new HttpClient(routes);
        await using var provider = new AppGetProvider(Source(), http);
        Assert.Equal("番剧", Assert.Single(await provider.GetCategoriesAsync(default)).Name);
        Assert.Equal("7", Assert.Single((await provider.GetItemsAsync(null, null, default)).Items).Id);
        Assert.Equal(1, routes.HomeRequests);
        var first = await provider.GetItemsFilteredAsync("6", null, new Dictionary<string, string> { ["year"] = "2026" }, default);
        Assert.Equal("2", first.NextCursor);
        Assert.Empty((await provider.GetItemsAsync("6", "2", default)).Items);
        Assert.Null((await provider.SearchPageAsync("动画 & 科技", "2", default)).NextCursor);
        var detail = await provider.GetDetailAsync("7", default);
        Assert.Equal(2, detail.PlaybackLines.Count);
        Assert.Equal("7", detail.Item.Id);
        var direct = await provider.ResolvePlaybackAsync("7", "0:0", default);
        Assert.Equal("https://cdn.example/movie.m3u8", direct.Uri);
        Assert.Equal("Fixture Player", direct.Headers["User-Agent"]);
        Assert.Single(direct.Headers);
        var parsed = await provider.ResolvePlaybackAsync("7", "1:0", default);
        var refreshed = await provider.ResolvePlaybackAsync("7", "1:0", default);
        Assert.NotEqual(parsed.Uri, refreshed.Uri);
        Assert.Equal(4, routes.DetailRequests); Assert.Equal(2, routes.ParseRequests);
        await Assert.ThrowsAsync<InvalidDataException>(() => provider.ResolvePlaybackAsync("7", "missing", default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExternalJsonParserUsesDeclaredModeWithoutLeakingApiHeaders(bool prepared)
    {
        var routes = new Routes { External = true, Prepared = prepared }; using var http = new HttpClient(routes);
        await using var provider = new AppGetProvider(Source(), http);
        var first = await provider.ResolvePlaybackAsync("7", "1:0", default);
        var second = await provider.ResolvePlaybackAsync("7", "1:0", default);
        Assert.Equal("https://cdn.example/stream?generation=1", first.Uri);
        Assert.Equal("https://cdn.example/stream?generation=2", second.Uri);
        Assert.Equal(2, routes.ExternalRequests); Assert.Equal(0, routes.ParseRequests);
        Assert.Single(first.Headers);
    }

    [Theory]
    [InlineData("{\"url\":\"file:///private/tmp/movie.mp4\"}")]
    [InlineData("{\"data\":{\"url\":\"javascript:alert(1)\"}}")]
    [InlineData("{\"data\":{}}")]
    public async Task ExternalParserInvalidResultFailsBeforePlayback(string response)
    {
        using var http = new HttpClient(new Routes { External = true, ExternalResponse = response });
        await using var provider = new AppGetProvider(Source(), http);
        await Assert.ThrowsAsync<InvalidDataException>(() => provider.ResolvePlaybackAsync("7", "1:0", default));
    }

    [Fact]
    public async Task ExternalParserResponseLimitFailsWithoutAllocatingAnUnboundedDocument()
    {
        using var http = new HttpClient(new Routes { External = true, ExternalResponse = new string('x', 1024 * 1024 + 1) });
        await using var provider = new AppGetProvider(Source(), http);
        await Assert.ThrowsAsync<InvalidDataException>(() => provider.ResolvePlaybackAsync("7", "1:0", default));
    }

    [Fact]
    public async Task ExternalParserCancellationReleasesSharedConcurrencySlots()
    {
        var routes = new Routes { External = true, ExternalWait = true }; using var http = new HttpClient(routes);
        await using var provider = new AppGetProvider(Source(), http);
        using var cancellation = new CancellationTokenSource();
        Task[] requests = Enumerable.Range(0, 8).Select(_ => provider.ResolvePlaybackAsync("7", "1:0", cancellation.Token)).ToArray();
        await routes.FourStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(4, routes.Active); cancellation.Cancel();
        foreach (var task in requests) await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await task);
        Assert.Equal(0, routes.Active);
        routes.ExternalWait = false;
        Assert.NotNull(await provider.ResolvePlaybackAsync("7", "1:0", default));
    }

    [Fact]
    public async Task DiscoveryOnlyReceivesUserAgentAndApiRejectsRedirects()
    {
        var routes = new Routes(); using var http = new HttpClient(routes);
        await using var provider = new AppGetProvider(Source(true), http);
        await provider.GetCategoriesAsync(default);
        Assert.Equal(1, routes.Discoveries);
        routes.Redirect = true;
        await Assert.ThrowsAsync<InvalidDataException>(() => provider.SearchAsync("query", default));
    }

    [Theory]
    [InlineData("{\"code\":0,\"data\":[]}")]
    [InlineData("{\"code\":1,\"data\":\"invalid base64\"}")]
    [InlineData("{\"code\":1,\"data\":\"AA==\"}")]
    public async Task InvalidEnvelopeOrEncryptionFailsWithoutReturningEmptySuccess(string json)
    {
        using var http = new HttpClient(new Routes { Envelope = json });
        await using var provider = new AppGetProvider(Source(), http);
        await Assert.ThrowsAsync<InvalidDataException>(() => provider.GetCategoriesAsync(default));
    }

    [Fact]
    public async Task CancellationReleasesAllFourIoSlots()
    {
        var routes = new Routes { Wait = true }; using var http = new HttpClient(routes);
        await using var provider = new AppGetProvider(Source(), http);
        using var cancellation = new CancellationTokenSource();
        Task[] tasks = Enumerable.Range(0, 8).Select(_ => provider.SearchAsync("query", cancellation.Token)).ToArray();
        await routes.FourStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(4, routes.Active); cancellation.Cancel();
        foreach (var task in tasks) await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await task);
        Assert.Equal(0, routes.Active);
        routes.Wait = false;
        Assert.Single((await provider.SearchAsync("query", default)).Items);
    }

    [Fact]
    public async Task InvalidInputAndDisposedProviderNeverReachNetwork()
    {
        var routes = new Routes(); using var http = new HttpClient(routes);
        var provider = new AppGetProvider(Source(), http);
        await Assert.ThrowsAsync<InvalidDataException>(() => provider.GetDetailAsync("https://other.example", default));
        await Assert.ThrowsAsync<InvalidDataException>(() => provider.GetItemsAsync("6", "1001", default));
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.GetItemsFilteredAsync("6", null, new Dictionary<string, string> { ["unknown"] = "x" }, default));
        await provider.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => provider.SearchAsync("query", default));
        Assert.Equal(0, routes.Requests);
        Assert.IsType<AppGetProvider>(new ProviderFactory(http, "unused", "unused").Create(Source() with { Provider = "csp_AppGet" }));
        var wrong = Source(); wrong.Options["protocol"] = JsonSerializer.SerializeToElement("v122", VodBoxJson.Default.String);
        Assert.Throws<NotSupportedException>(() => new AppGetProvider(wrong));
    }

    private sealed class Routes : HttpMessageHandler
    {
        public int HomeRequests, DetailRequests, ParseRequests, Discoveries, Requests, Active, ExternalRequests;
        public bool Wait, Redirect, External, Prepared, ExternalWait;
        public string? Envelope, ExternalResponse;
        public TaskCompletionSource FourStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Interlocked.Increment(ref Requests);
            if (request.RequestUri!.Host == "parser.example")
            {
                ExternalRequests++;
                Assert.Equal(HttpMethod.Get, request.Method); Assert.Single(request.Headers);
                Assert.False(request.Headers.Contains("app-api-verify-sign"));
                Assert.False(request.Headers.Contains("app-user-device-id"));
                Assert.Null(request.Content);
                if (Interlocked.Increment(ref Active) == 4) FourStarted.TrySetResult();
                try { if (ExternalWait) await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                finally { Interlocked.Decrement(ref Active); }
                return Json(ExternalResponse ?? "{\"data\":{\"url\":\"https://cdn.example/stream?generation=" + ExternalRequests + "\"}}");
            }
            if (request.RequestUri!.Host == "discovery.example")
            {
                Discoveries++; Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Single(request.Headers); Assert.False(request.Headers.Contains("app-api-verify-sign"));
                return Json("https://api.example");
            }
            Assert.Equal("api.example", request.RequestUri.Host); Assert.Equal(HttpMethod.Post, request.Method);
            Assert.StartsWith("/api.php/getappapi.index/", request.RequestUri.AbsolutePath);
            Assert.Equal(Assert.Single(request.Headers.GetValues("app-api-verify-time")), Decrypt(Assert.Single(request.Headers.GetValues("app-api-verify-sign"))));
            Assert.False(request.Headers.Contains("Cookie"));
            Assert.Equal("application/x-www-form-urlencoded", request.Content!.Headers.ContentType!.MediaType);
            if (Redirect) return new(HttpStatusCode.Redirect);
            if (Envelope is not null) return Json(Envelope);
            var form = (await request.Content.ReadAsStringAsync(token)).Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Split('=', 2)).ToDictionary(x => Decode(x[0]), x => Decode(x[1]));
            string action = request.RequestUri.Segments.Last(), data;
            if (action == "initV119")
            {
                HomeRequests++; data = "{\"type_list\":[{\"type_id\":0,\"type_name\":\"全部\"},{\"type_id\":6,\"type_name\":\"番剧\",\"recommend_list\":[" + Card + "]}],\"banner_list\":[" + Card + "]}";
            }
            else if (action == "vodDetail")
            {
                DetailRequests++; Assert.Equal("7", form["vod_id"]);
                data = "{\"vod\":" + Card + ",\"vod_play_list\":[{\"player_info\":{\"show\":\"直接\",\"user_agent\":\"Fixture Player\"},\"urls\":[{\"name\":\"第一集\",\"url\":\"https://cdn.example/movie.m3u8\"}]},{\"player_info\":{\"show\":\"解析\",\"parse\":\"https://parser.example/?url=\",\"player_parse_type\":\"1\"},\"urls\":[{\"name\":\"第二集\",\"url\":\"opaque token & 中文\",\"token\":\"fixture\"}]}]}";
            }
            else if (action == "vodParse")
            {
                ParseRequests++; Assert.Equal("opaque token & 中文", Decrypt(form["url"]));
                Assert.False(form["url"].Contains('%'));
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
                else { Assert.Equal("searchList", action); data = "{\"pagecount\":2,\"search_list\":[" + Card + "]}"; }
            }
            if (External && action == "vodDetail")
            {
                data = data.Replace("\"player_parse_type\":\"1\"", "\"player_parse_type\":\"2\"", StringComparison.Ordinal);
                if (Prepared)
                {
                    data = data.Replace("\"parse\":\"https://parser.example/?url=\"", "\"parse\":\"\"", StringComparison.Ordinal)
                        .Replace("\"url\":\"opaque token & 中文\"", "\"url\":\"opaque token & 中文\",\"parse_api_url\":\"https://parser.example/?ticket=fixture\"", StringComparison.Ordinal);
                }
            }
            return Json("{\"code\":1,\"data\":\"" + Encrypt(data) + "\"}");
        }
        private static string Decode(string value) => Uri.UnescapeDataString(value.Replace('+', ' '));
        private const string Card = "{\"vod_id\":7,\"vod_name\":\"Title &amp; more\",\"vod_pic\":\"//cdn.example/poster.jpg\"}";
    }
    private static string Encrypt(string text) { using var aes = Aes.Create(); aes.Key = Encoding.UTF8.GetBytes(Key); return Convert.ToBase64String(aes.EncryptCbc(Encoding.UTF8.GetBytes(text), aes.Key)); }
    private static string Decrypt(string text) { using var aes = Aes.Create(); aes.Key = Encoding.UTF8.GetBytes(Key); return Encoding.UTF8.GetString(aes.DecryptCbc(Convert.FromBase64String(text), aes.Key)); }
    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text) };
}
