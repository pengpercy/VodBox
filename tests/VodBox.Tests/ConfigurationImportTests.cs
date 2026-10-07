using System.Net;
using System.Text;
using VodBox.Infrastructure;
using Xunit;

namespace VodBox.Tests;

public sealed class ConfigurationImportTests
{
    private const string Subscription = """
        { // subscription comments
          "sites":[
            {"key":"bili","name":"哔哩合集","type":3,"api":"csp_BiliGuard","ext":{"json":"/categories.json"}},
            {"key":"api","name":"采集站","type":1,"api":"/api.php"},
            {"key":"unknown","name":"尚未适配的站点","type":3,"api":"csp_UnknownGuard"}
          ],
          "lives":[{"name":"直播","url":"/live.m3u","ua":"bingcha/1.1"}],
        }
        """;
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WrappedOrPlainSubscriptionMapsOnlySupportedEntries(bool wrapped)
    {
        var bytes = Encoding.UTF8.GetBytes(Subscription);
        if (wrapped) bytes = new byte[] { 0xff, 0xd8, 0xff, 0xd9 }.Concat(Encoding.ASCII.GetBytes("image-tag**" + Convert.ToBase64String(bytes))).ToArray();
        using var http = new HttpClient(new Responses(bytes));
        var config = await new ConfigLoader(http).LoadAsync(" http://example.com/tv\u00a0");
        Assert.Equal(2, config.Sources.Count); Assert.Single(config.ImportWarnings);
        Assert.Contains("csp_UnknownGuard", config.ImportWarnings[0]);
        var bili = config.Sources[0]; Assert.Equal("bilibili", bili.Provider);
        Assert.Single(bili.Options["categories"].EnumerateArray());
        Assert.Equal("纪录片", bili.Options["categories"][0].GetProperty("query").GetString());
        await using var provider = new BilibiliProvider(bili, http);
        Assert.Equal(2, (await provider.GetCategoriesAsync(default)).Count);
        Assert.Equal("http://example.com/api.php", config.Sources[1].Entry);
        Assert.Equal("bingcha/1.1", Assert.Single(config.LiveSources).UserAgent);
        Assert.StartsWith("subscription-", config.Id);
    }
    [Fact]
    public async Task LargeBilibiliMusicCategorySubscriptionIsNotDropped()
    {
        string categories = "{\"class\":[" + string.Join(',', Enumerable.Range(0, 129).Select(i => $"{{\"type_id\":\"MV{i}\",\"type_name\":\"音乐{i}\"}}")) + "]}";
        const string subscription = """{"sites":[{"key":"music","name":"明星MV","api":"csp_BiliGuard","ext":{"json":"https://example.com/categories"}}]}""";
        using var http = new HttpClient(new CategoryResponses(subscription, categories));
        var config = await new ConfigLoader(http).LoadAsync("https://example.com/tv");
        Assert.Empty(config.ImportWarnings);
        await using var provider = new BilibiliProvider(Assert.Single(config.Sources), http);
        Assert.Equal(130, (await provider.GetCategoriesAsync(default)).Count);
    }
    private sealed class CategoryResponses(string subscription, string categories) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(request.RequestUri!.AbsolutePath == "/categories" ? categories : subscription) });
    }
    [Fact]
    public async Task KnownPublicRulesUseNativeAdaptersWithoutDownloadingJavascript()
    {
        const string subscription = """
            {"sites":[
              {"key":"txb","name":"兔小贝","type":3,"api":"https://proxy.example/drpy2.min.js","ext":"https://proxy.example/https://raw.githubusercontent.com/fantaiying7/EXT/refs/heads/main/%E5%85%94%E5%B0%8F%E8%B4%9D.js"},
              {"key":"huya","name":"虎牙","type":3,"api":"https://proxy.example/drpy2.min.js","ext":"https://raw.githubusercontent.com/fantaiying7/EXT/refs/heads/main/虎牙.js"},
              {"key":"douyu","name":"斗鱼","type":3,"api":"https://proxy.example/drpy2.min.js","ext":"https://raw.githubusercontent.com/fantaiying7/EXT/refs/heads/main/斗鱼直播.js"},
              {"key":"other","name":"其他规则","type":3,"api":"https://proxy.example/drpy2.min.js","ext":"https://example.com/兔小贝.js"}
            ]}
            """;
        using var http = new HttpClient(new Responses(Encoding.UTF8.GetBytes(subscription)));
        var config = await new ConfigLoader(http).LoadAsync("https://example.com/tv");
        Assert.Equal(new[] { "tuxiaobei", "huya", "douyu" }, config.Sources.Select(x => x.Provider));
        Assert.All(config.Sources, x => Assert.Equal(VodBox.Core.ProviderRuntime.Csharp, x.Runtime));
        Assert.Single(config.ImportWarnings); Assert.Contains("其他规则", config.ImportWarnings[0]);
        var factory = new ProviderFactory(http, "missing-host", "missing-assets");
        foreach (var source in config.Sources) await factory.Create(source).DisposeAsync();
    }
    [Fact]
    public async Task HtmlAndUnrecognizedJsonDoNotSilentlyReplaceSourcesWithEmptyConfiguration()
    {
        foreach (string content in new[] { "<html>not a config</html>", "{}" })
        {
            using var http = new HttpClient(new Responses(Encoding.UTF8.GetBytes(content)));
            await Assert.ThrowsAsync<InvalidDataException>(() => new ConfigLoader(http).LoadAsync("http://example.com/tv"));
        }
    }
    private sealed class Responses(byte[] bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            byte[] data = request.RequestUri!.AbsolutePath == "/categories.json"
                ? Encoding.UTF8.GetBytes("""{"class":[{"type_id":"peizhi","type_name":"登录配置"},{"type_id":"纪录片","type_name":"纪录片"}]}""") : bytes;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) });
        }
    }
}
