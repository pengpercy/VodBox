using Xunit;
using System.Net;
using System.Text;
using VodBox.Core;
using VodBox.Infrastructure;

namespace VodBox.Tests;

/// <summary>按 URL 返回预置响应的假 HTTP 处理器，记录请求地址。</summary>
internal sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<string> Requests { get; } = [];
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (Requests) Requests.Add(request.RequestUri!.AbsoluteUri);
        return Task.FromResult(respond(request));
    }

    public static HttpResponseMessage Text(string body, HttpStatusCode status = HttpStatusCode.OK, string type = "application/json") =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, type) };
}

public sealed class SourceConfigParserTests
{
    private const string Config = """
        {
          // LunaTV 允许的注释
          "cache_time": 9200,
          "api_site": {
            "api_1": { "name": "TV-1080资源", "api": "https://api.1080zyku.com/inc/api_mac10.php", "detail": "https://api.1080zyku.com" },
            "api_2": { "name": "量子", "api": "https://cj.lziapi.com/api.php/provide/vod" },
          },
          "custom_category": [ { "name": "华语", "type": "movie", "query": "华语" } ],
          "lives": { "tv": { "name": "电视", "url": "https://example.com/tv.m3u", "ua": "okhttp" } }
        }
        """;

    [Fact]
    public void ParsesLunaConfigWithCommentsAndTrailingCommas()
    {
        var config = SourceConfigParser.Parse(SourceConfigParser.Decode(Config));
        Assert.Equal(9200, config.CacheTime);
        Assert.Equal(2, config.ApiSite!.Count);
        Assert.Equal("https://api.1080zyku.com", config.ApiSite["api_1"].Detail);
        Assert.Equal("华语", Assert.Single(config.CustomCategory!).Query);
        Assert.Equal("okhttp", config.Lives!["tv"].Ua);
    }

    [Fact]
    public void DecodesBase58Subscription()
    {
        var encoded = SourceConfigParser.EncodeBase58(Config);
        Assert.DoesNotContain("{", encoded);
        Assert.Equal(Config.Trim(), SourceConfigParser.Decode(encoded));
    }

    [Fact]
    public void RejectsNonConfigContent()
    {
        Assert.Throws<InvalidDataException>(() => SourceConfigParser.Decode("<html>not found</html>"));
        Assert.Throws<InvalidDataException>(() => SourceConfigParser.Validate(SourceConfigParser.Parse("{}")));
    }
}

public sealed class SourceConfigStoreTests
{
    private static string TempFile() => Path.Combine(Path.GetTempPath(), "vodbox-test-" + Guid.NewGuid().ToString("N"), "source-config.json");

    [Fact]
    public void RefineKeepsUserStateAndMarksRemovedSourcesCustom()
    {
        var path = TempFile();
        var store = new SourceConfigStore(path);
        store.ApplyConfigFile("""{"api_site":{"a":{"name":"A","api":"https://a.example/api"},"b":{"name":"B","api":"https://b.example/api"}}}""");
        store.SetSourceDisabled("a", true);
        store.MoveSource("b", 0);
        store.AddSource("mine", "我的源", "https://mine.example/api", null);

        store.ApplyConfigFile("""{"cache_time":600,"api_site":{"a":{"name":"A2","api":"https://a2.example/api"},"c":{"name":"C","api":"https://c.example/api"}}}""");

        var sources = store.Sources;
        Assert.Equal(["b", "a", "mine", "c"], sources.Select(site => site.Key));
        var a = sources.Single(site => site.Key == "a");
        Assert.Equal(("A2", "https://a2.example/api", true, SourceOrigin.Config), (a.Name, a.Api, a.Disabled, a.From));
        Assert.Equal(SourceOrigin.Custom, sources.Single(site => site.Key == "b").From);
        Assert.Equal(600, store.Settings.CacheTimeSeconds);

        // 落盘后重新加载状态一致。
        var reloaded = new SourceConfigStore(path);
        Assert.Equal(sources, reloaded.Sources);
        Assert.Equal(["b", "mine", "c"], reloaded.EnabledSources.Select(site => site.Key));
    }

    [Fact]
    public void DeletedConfigSourcesStayDeletedAcrossRefreshUntilRestored()
    {
        const string config = """{"api_site":{"a":{"name":"A","api":"https://a.example/api"},"b":{"name":"B","api":"https://b.example/api"}}}""";
        var store = new SourceConfigStore(null);
        store.ApplyConfigFile(config);
        store.AddSource("mine", "我的", "https://mine.example/api", null);
        Assert.Equal(2, store.DeleteSources(["a", "mine", "missing"]));
        Assert.Equal(["b"], store.Sources.Select(site => site.Key));
        Assert.Equal(["a"], store.Data.DeletedSourceKeys); // 只记订阅源

        store.ApplyConfigFile(config); // 刷新订阅不加回
        Assert.Equal(["b"], store.Sources.Select(site => site.Key));

        Assert.Equal(1, store.RestoreDeletedSources());
        Assert.Equal(["b", "a"], store.Sources.Select(site => site.Key));
        Assert.Equal(0, store.DeletedSourceCount);

        Assert.True(store.DeleteSource("a"));
        store.AddSource("a", "手动A", "https://a2.example/api", null); // 手动加回同键会清掉删除记录
        Assert.Empty(store.Data.DeletedSourceKeys);
        Assert.Throws<ArgumentException>(() => store.AddSource("a", "重复", "https://x.example", null));
        Assert.Throws<ArgumentException>(() => store.AddSource("bad", "坏", "ftp://x", null));
    }

    [Fact]
    public void DeletedSourceKeysSurviveReload()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vodbox-deleted-{Guid.NewGuid():N}.json");
        try
        {
            var store = new SourceConfigStore(path);
            store.ApplyConfigFile("""{"api_site":{"a":{"name":"A","api":"https://a.example/api"}}}""");
            store.DeleteSource("a");
            Assert.Equal(["a"], new SourceConfigStore(path).Data.DeletedSourceKeys);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void CategoriesAndLivesMerge()
    {
        var store = new SourceConfigStore(null);
        store.ApplyConfigFile("""{"custom_category":[{"type":"tv","query":"美剧"}],"lives":{"l":{"name":"直播","url":"https://l.example/a.m3u"}}}""");
        var category = Assert.Single(store.EnabledCategories);
        Assert.Equal(("美剧", "tv"), (category.DisplayName, category.Type));
        store.SetLiveDisabled("l", true);
        Assert.Empty(store.EnabledLives);
    }
}

public sealed class CmsParsingTests
{
    [Fact]
    public void KeepsAllPlayableLinesNotOnlyM3u8AndOrdersMediaFirst()
    {
        var lines = CmsClient.ParseLines("ukyun$$$ukm3u8$$$mp4",
            "720P$https://ukzyvod3.example.com/share/ta64D7CMrl$$$第1集$https://cdn.example.com/a/index.m3u8?token=1#第2集$https://cdn.example.com/b/index.m3u8$$$正片$https://cdn.example.com/movie.mp4");
        // share 网页线路被丢弃，带查询串的 m3u8 与 mp4 线路都保留。
        Assert.Equal(["ukm3u8", "mp4"], lines.Select(line => line.Name));
        Assert.Equal(2, lines[0].Episodes.Count);
        Assert.Equal("https://cdn.example.com/a/index.m3u8?token=1", lines[0].Episodes[0].Uri);
    }

    [Fact]
    public void CloudPlayerPagesAreTreatedAsWebPages()
    {
        var lines = CmsClient.ParseLines("gsyun$$$gsm3u8", "全集$https://v.gsuus.com/play/av2QDg5a$$$全集$https://v.gsuus.com/20260101/x/index.m3u8");
        Assert.Equal("gsm3u8", Assert.Single(lines).Name);
    }

    [Fact]
    public void UnknownExtensionLinesAreKeptAfterMediaLines()
    {
        var lines = CmsClient.ParseLines("raw$$$hls", "1$https://cdn.example.com/stream/12345$$$1$https://cdn.example.com/x.m3u8");
        Assert.Equal(["hls", "raw"], lines.Select(line => line.Name));
    }

    [Fact]
    public void DropsNonHttpEpisodesAndNamesUntitledEpisodes()
    {
        var line = Assert.Single(CmsClient.ParseLines("m", "javascript:alert(1)#https://cdn.example.com/1.m3u8#https://cdn.example.com/2.m3u8"));
        Assert.Equal(["第1集", "第2集"], line.Episodes.Select(episode => episode.Title));
    }

    [Fact]
    public void CleansHtmlAndNormalizesYear()
    {
        Assert.Equal("第一段 & 简介\n第二段", CmsClient.CleanHtml("<p>第一段 &amp; 简介</p><div>第二段</div>"));
        Assert.Equal("2023", CmsClient.NormalizeYear("2023年"));
        Assert.Equal("unknown", CmsClient.NormalizeYear(""));
    }
}

public sealed class CmsClientTests
{
    private static readonly ApiSite Site = new() { Key = "s", Name = "测试源", Api = "https://cms.example/api.php/provide/vod" };

    private static string Page(int page, int pageCount, params (string Id, string Name)[] items) =>
        $$"""{"page":"{{page}}","pagecount":{{pageCount}},"list":[{{string.Join(",", items.Select(item => $$"""{"vod_id":{{item.Id}},"vod_name":"{{item.Name}}","vod_year":"2024","type_name":"国产剧","vod_play_from":"m3u8","vod_play_url":"第1集$https://cdn.example/{{item.Id}}/1.m3u8#第2集$https://cdn.example/{{item.Id}}/2.m3u8"}"""))}}]}""";

    [Fact]
    public async Task SearchFetchesAdditionalPagesUpToLimitAndCaches()
    {
        var handler = new FakeHandler(request =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            var page = url.Contains("pg=") ? int.Parse(url[(url.IndexOf("pg=") + 3)..]) : 1;
            return FakeHandler.Text(Page(page, 9, ($"{page}0", "繁花"), ($"{page}1", "繁花 番外")));
        });
        using var client = new CmsClient(() => new SiteSettings { SearchMaxPage = 3 }, handler);
        var results = await client.SearchAsync(Site, "繁花", TestContext.Current.CancellationToken);
        Assert.Equal(6, results.Count);
        Assert.Equal(3, handler.Requests.Count);
        Assert.StartsWith("https://cms.example/api.php/provide/vod?ac=videolist&wd=", handler.Requests[0]);
        Assert.Equal("2024", results[0].Year);
        Assert.Equal(2, results[0].EpisodeCount);

        await client.SearchAsync(Site, "繁花", TestContext.Current.CancellationToken);
        Assert.Equal(3, handler.Requests.Count); // 全部命中缓存
    }

    [Fact]
    public async Task ForbiddenIsNegativelyCached()
    {
        var handler = new FakeHandler(_ => FakeHandler.Text("", HttpStatusCode.Forbidden));
        using var client = new CmsClient(() => new SiteSettings(), handler);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.SearchAsync(Site, "x", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.SearchAsync(Site, "x", TestContext.Current.CancellationToken));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task DetailFallsBackToHtmlPageWhenJsonFails()
    {
        var site = Site with { Detail = "https://cms.example" };
        var handler = new FakeHandler(request => request.RequestUri!.AbsolutePath.EndsWith(".html")
            ? FakeHandler.Text("<h1>繁花</h1><div class=\"sketch\">简介<br/>内容</div><img src=\"https://img.example/p.jpg\"><span>2023</span>" +
                               "<li>$https://cdn.example/20240101/1_abc/index.m3u8</li><li>$https://cdn.example/20240101/2_def/index.m3u8(备用)</li>", type: "text/html")
            : FakeHandler.Text("<html>error</html>", type: "text/html"));
        using var client = new CmsClient(() => new SiteSettings(), handler);
        var detail = await client.GetDetailAsync(site, "42", TestContext.Current.CancellationToken);
        Assert.Equal("繁花", detail.Title);
        Assert.Equal("https://img.example/p.jpg", detail.Poster);
        Assert.Equal("2023", detail.Year);
        Assert.Equal(["https://cdn.example/20240101/1_abc/index.m3u8", "https://cdn.example/20240101/2_def/index.m3u8"], Assert.Single(detail.Lines).Episodes.Select(episode => episode.Uri));
        Assert.Contains(handler.Requests, url => url == "https://cms.example/index.php/vod/detail/id/42.html");
    }

    [Fact]
    public async Task CatalogStreamsPerSourceBatchesAndFiltersYellowContent()
    {
        var store = new SourceConfigStore(null);
        store.ApplyConfigFile("""{"api_site":{"ok":{"name":"好源","api":"https://ok.example/api"},"bad":{"name":"坏源","api":"https://bad.example/api"},"off":{"name":"禁用","api":"https://off.example/api"}}}""");
        store.SetSourceDisabled("off", true);
        var handler = new FakeHandler(request => request.RequestUri!.Host switch
        {
            "ok.example" => FakeHandler.Text("""{"pagecount":1,"list":[{"vod_id":1,"vod_name":"繁花","type_name":"国产剧","vod_play_url":"1$https://c/1.m3u8#2$https://c/2.m3u8"},{"vod_id":2,"vod_name":"繁花","type_name":"伦理片","vod_play_url":"1$https://c/x.m3u8"}]}"""),
            _ => FakeHandler.Text("oops", HttpStatusCode.InternalServerError),
        });
        using var catalog = new VodCatalog(store, new CmsClient(() => store.Settings, handler));
        var batches = new List<SourceSearchBatch>();
        await foreach (var batch in catalog.SearchStreamAsync("繁花", TestContext.Current.CancellationToken)) batches.Add(batch);
        Assert.Equal(2, batches.Count);
        Assert.Equal("1", Assert.Single(batches.Single(batch => batch.Site.Key == "ok").Results).Id);
        Assert.Equal("HTTP 500", batches.Single(batch => batch.Site.Key == "bad").Error);
        Assert.DoesNotContain(handler.Requests, url => url.Contains("off.example"));
    }
}

public sealed class SearchAggregationTests
{
    private static VodResult Result(string source, string title, string year, int episodes) => new()
    {
        Id = source + title, Title = title, SourceKey = source, SourceName = source, Year = year,
        Lines = [new PlaybackLine("l", "l", Enumerable.Range(1, episodes).Select(i => new Episode($"{i}", $"{i}", $"https://c/{i}.m3u8")).ToArray())],
        DoubanId = source == "c" ? "999" : "123",
    };

    [Fact]
    public void GroupsByTitleYearAndKindInOrder()
    {
        var groups = SearchAggregation.Group([Result("a", "繁花", "2023", 30), Result("b", "繁 花", "2023", 30), Result("c", "繁花", "2023", 1), Result("d", "繁花", "2024", 30)]);
        Assert.Equal([2, 1, 1], groups.Select(group => group.Count));
        Assert.Equal("123", SearchAggregation.MajorityDoubanId(groups[0]));
        var card = SearchAggregation.ToCard(groups[0]);
        Assert.Equal(("2 个源", 2, "tv"), (card.SourceName, card.SourceCount, card.Kind));
    }

    [Fact]
    public void MatchesUsesTitleYearAndKindWhenKnown()
    {
        var series = Result("a", "繁花", "2023", 30);
        Assert.True(SearchAggregation.Matches(series, "繁 花", "2023", "tv"));
        Assert.True(SearchAggregation.Matches(series, "繁花", null, null));
        Assert.False(SearchAggregation.Matches(series, "繁花", "2024", null));
        Assert.False(SearchAggregation.Matches(series, "繁花", "2023", "movie"));
        Assert.False(SearchAggregation.Matches(series, "繁花番外", null, null));
    }
}
