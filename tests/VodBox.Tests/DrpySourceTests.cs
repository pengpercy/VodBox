using System.Text.Json;
using VodBox.Core;
using VodBox.Infrastructure;
using VodBox.PluginHost;
using Xunit;

namespace VodBox.Tests;

public sealed class DrpySourceTests
{
    private static SourceInfo Site => new()
    {
        Key = "drpy-fixture", Name = "脚本站点", Runtime = SourceRuntime.QuickJs,
        Api = "https://example.org/drpy2.min.js", Ext = "var rule = {};"
    };

    [Fact]
    public void ParsesDynamicFiltersByCategoryWithoutMixingOtherCategories()
    {
        using var document=System.Text.Json.JsonDocument.Parse("{\"filters\":{\"1\":[{\"key\":\"year\",\"name\":\"年份\",\"init\":\"2026\",\"value\":[{\"n\":\"全部\",\"v\":\"\"},{\"n\":\"2026\",\"v\":\"2026\"}]}]}}");
        var group=Assert.Single(DrpySource.ParseFilters(document.RootElement,"1"));
        Assert.Equal("year",group.Key);Assert.Equal(2,group.Values.Count);Assert.Equal("2026",group.Init);
        Assert.Empty(DrpySource.ParseFilters(document.RootElement,"2"));
    }

    [Fact]
    public async Task HomeVodIsDistinctFromHomeAndMapsMetadata()
    {
        using var source = new DrpySource(Site, () => new FakeRuntime());
        Assert.Equal(new Category("2", "儿歌"), Assert.Single(await source.GetCategoriesAsync(ct: TestContext.Current.CancellationToken)));
        var home = await source.GetHomeAsync(ct: TestContext.Current.CancellationToken);
        Assert.Equal("home", Assert.Single(home.Items).Id);
        Assert.Equal("首页", home.Items[0].Title);
        Assert.Equal("https://cdn.example/poster.jpg", home.Items[0].Poster);
        Assert.Equal("2026", home.Items[0].Year);
        Assert.Equal(1, home.PageCount);
    }

    [Fact]
    public async Task CategoryFiltersAndSearchArgumentsReachRuntimeAndPagingIsLenient()
    {
        using var source = new DrpySource(Site, () => new FakeRuntime());
        var page = await source.GetItemsAsync("2", 3, new Dictionary<string, string> { ["year"] = "2026" }, ct: TestContext.Current.CancellationToken);
        Assert.Equal("filtered", Assert.Single(page.Items).Id);
        Assert.Equal(3, page.Page);
        Assert.Equal(8, page.PageCount);
        Assert.Equal("search", Assert.Single((await source.SearchAsync("C#\t中文", 2, ct: TestContext.Current.CancellationToken)).Items).Id);
    }

    [Fact]
    public async Task DetailKeepsEmptyLineSlotsAndStableResumeIdsWhenSignedUrlsChange()
    {
        using var source = new DrpySource(Site, () => new FakeRuntime());
        var first = await source.GetDetailAsync("7", ct: TestContext.Current.CancellationToken);
        var second = await source.GetDetailAsync("7", ct: TestContext.Current.CancellationToken);
        Assert.Equal("简介\n下一行", first.Description);
        Assert.Equal("导演", first.Director);
        Assert.Equal(2, first.Lines.Count);
        Assert.Equal("线路A", first.Lines[0].Name);
        Assert.Equal("线路B", first.Lines[1].Name);
        Assert.Equal(first.Lines.Select(l => l.Id), second.Lines.Select(l => l.Id));
        Assert.Equal(first.Lines[0].Episodes[0].Id, second.Lines[0].Episodes[0].Id);
        Assert.NotEqual(first.Lines[0].Episodes[0].Id, first.Lines[1].Episodes[0].Id);
        Assert.Null(first.Lines[0].Episodes[0].Uri); // 必须点播时现取
        var play = await source.ResolvePlaybackAsync("7", first.Lines[1].Episodes[0].Id, ct: TestContext.Current.CancellationToken);
        Assert.Equal("https://cdn.example/new.mp4", play.Uri);
        Assert.Equal(ResolutionKind.Direct, play.Resolution);
        Assert.Equal("脚本站点", play.SourceName);
        Assert.Equal("7", play.MediaId);
        Assert.Equal(first.Lines[1].Id, play.LineId);
        Assert.Equal(first.Lines[1].Episodes[0].Id, play.EpisodeId);
        Assert.Equal("https://example.org/", play.Headers["Referer"]);
        Assert.Equal("Fixture", play.Headers["User-Agent"]);
    }

    [Theory]
    [InlineData(0, 0, "https://cdn.example/video", false)]
    [InlineData(1, 0, "https://cdn.example/video.mp4?token=1", false)]
    [InlineData(1, 0, "https://example.org/watch/7", true)]
    [InlineData(0, 1, "https://cdn.example/video.mp4", false)]
    [InlineData(2, 0, "https://example.org/parser", true)]
    [InlineData(0, 0, "", true)]
    [InlineData(0, 0, "javascript:alert(1)", true)]
    public async Task ParseAndJxNeverSilentlyPlayWebPages(int parse, int jx, string url, bool fails)
    {
        using var source = new DrpySource(Site, () => new FakeRuntime { PlayJson = "{\"parse\":" + parse + ",\"jx\":" + jx + ",\"url\":\"" + url + "\"}" });
        var id = (await source.GetDetailAsync("7", ct: TestContext.Current.CancellationToken)).Lines[0].Episodes[0].Id;
        if (fails) await Assert.ThrowsAnyAsync<Exception>(() => source.ResolvePlaybackAsync("7", id, ct: TestContext.Current.CancellationToken));
        else Assert.Equal(jx==1?ResolutionKind.Json:ResolutionKind.Direct, (await source.ResolvePlaybackAsync("7", id, ct: TestContext.Current.CancellationToken)).Resolution);
    }

    [Fact]
    public async Task InvalidEpisodeFailsRatherThanPlayingFirstAndMalformedJsonIsVisible()
    {
        using var source = new DrpySource(Site, () => new FakeRuntime());
        await Assert.ThrowsAsync<InvalidDataException>(() => source.ResolvePlaybackAsync("7", "missing", ct: TestContext.Current.CancellationToken));
        using var invalid = new DrpySource(Site, () => new FakeRuntime { HomeJson = "not-json" });
        await Assert.ThrowsAnyAsync<JsonException>(() => invalid.GetCategoriesAsync(ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SiteCallsSerializeAndQueuedCancellationDoesNotEnterRuntime()
    {
        var runtime = new FakeRuntime { BlockSearch = true };
        using var source = new DrpySource(Site, () => runtime);
        var first = source.SearchAsync("block", 1, ct: TestContext.Current.CancellationToken);
        await runtime.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken: TestContext.Current.CancellationToken);
        using var ct = new CancellationTokenSource();
        var second = source.GetHomeAsync(ct.Token);
        ct.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        Assert.False(first.IsCompleted);
        runtime.Release.TrySetResult();
        await first;
        Assert.Single((await source.GetHomeAsync(ct: TestContext.Current.CancellationToken)).Items);
    }

    [Fact]
    public async Task DisposeCancelsInFlightAndRejectsNewCalls()
    {
        var runtime = new FakeRuntime { BlockSearch = true };
        var source = new DrpySource(Site, () => runtime);
        var pending = source.SearchAsync("block", 1, ct: TestContext.Current.CancellationToken);
        await runtime.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken: TestContext.Current.CancellationToken);
        source.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => source.GetHomeAsync(ct: TestContext.Current.CancellationToken));
        source.Dispose();
        Assert.True(runtime.Disposed);
    }

    [Fact]
    public async Task RegistryRegistersQuickJsLazilyAndKeepsNativeKeyFallback()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, """{"sites":[{"key":"customjs","name":"自定义","type":3,"api":"drpy2.min.js","ext":"var rule={};"},{"key":"dr_兔小贝","name":"儿童","type":3,"api":"drpy2.min.js"}]}""", cancellationToken: TestContext.Current.CancellationToken);
            using var registry = new SourceRegistry();
            await registry.LoadConfigAsync(path, ct: TestContext.Current.CancellationToken);
            Assert.IsType<DrpySource>(registry.Get("customjs"));
            Assert.IsType<TuxiaobeiSource>(registry.Get("dr_兔小贝"));
            var old = registry.Get("customjs")!;
            await registry.LoadConfigAsync(path, ct: TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => old.GetHomeAsync(ct: TestContext.Current.CancellationToken));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task FailedRuntimeIsRetiredAndNextCallReinitializes()
    {
        var failed = new FakeRuntime { HomeVodError = new OperationCanceledException() };
        var instances = new Queue<IDrpyRuntime>([failed, new FakeRuntime()]);
        using var source = new DrpySource(Site, instances.Dequeue);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.GetHomeAsync(ct: TestContext.Current.CancellationToken));
        Assert.True(failed.Disposed);
        Assert.Equal("home", Assert.Single((await source.GetHomeAsync(ct: TestContext.Current.CancellationToken)).Items).Id);
    }

    [Theory]
    [InlineData("\"invalid\"", "0")]
    [InlineData("0", "\"invalid\"")]
    [InlineData("{}", "0")]
    [InlineData("1.5", "0")]
    public async Task MalformedPlaybackFlagsAreNotDefaultedToDirect(string parse, string jx)
    {
        using var source = new DrpySource(Site, () => new FakeRuntime
        {
            PlayJson = "{\"parse\":" + parse + ",\"jx\":" + jx + ",\"url\":\"https://cdn.example/video.mp4\"}"
        });
        var id = (await source.GetDetailAsync("7", ct: TestContext.Current.CancellationToken)).Lines[0].Episodes[0].Id;
        await Assert.ThrowsAsync<InvalidDataException>(() => source.ResolvePlaybackAsync("7", id, ct: TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("{\"\":\"value\"}")]
    [InlineData("{\"X-Test\":{\"nested\":true}}")]
    [InlineData("{\"X-Test\":\"line\\r\\nbreak\"}")]
    [InlineData("{\"Bad Header\":\"value\"}")]
    public async Task InvalidPlaybackHeadersAreRejected(string headers)
    {
        using var source = new DrpySource(Site, () => new FakeRuntime
        {
            PlayJson = "{\"parse\":0,\"url\":\"https://cdn.example/video.mp4\",\"header\":" + headers + "}"
        });
        var id = (await source.GetDetailAsync("7", ct: TestContext.Current.CancellationToken)).Lines[0].Episodes[0].Id;
        await Assert.ThrowsAsync<InvalidDataException>(() => source.ResolvePlaybackAsync("7", id, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CancellationDuringInitDisposesUnpublishedRuntimeAndCanRetry()
    {
        var failed = new FakeRuntime { BlockInit = true };
        var instances = new Queue<IDrpyRuntime>([failed, new FakeRuntime()]);
        using var source = new DrpySource(Site, instances.Dequeue);
        using var ct = new CancellationTokenSource();
        var pending = source.GetHomeAsync(ct.Token);
        await failed.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken: TestContext.Current.CancellationToken);
        ct.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.True(failed.Disposed);
        Assert.Single((await source.GetHomeAsync(ct: TestContext.Current.CancellationToken)).Items);
    }

    [Fact]
    public async Task NullListsAreEmptyButWrongShapesRemainVisible()
    {
        using var source = new DrpySource(Site, () => new FakeRuntime { HomeJson = "{\"class\":null}", HomeVodJson = "{\"list\":null}" });
        Assert.Empty(await source.GetCategoriesAsync(ct: TestContext.Current.CancellationToken));
        Assert.Empty((await source.GetHomeAsync(ct: TestContext.Current.CancellationToken)).Items);
        using var invalid = new DrpySource(Site, () => new FakeRuntime { HomeVodJson = "{\"list\":{}}" });
        await Assert.ThrowsAsync<InvalidDataException>(() => invalid.GetHomeAsync(ct: TestContext.Current.CancellationToken));
    }

    private sealed class FakeRuntime : IDrpyRuntime
    {
        public string HomeJson = """{"class":[{"type_id":2,"type_name":"儿歌"}]}""";
        public string? PlayJson, HomeVodJson;
        public Exception? HomeVodError;
        public bool BlockSearch, BlockInit, Disposed;
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _details;
        public async Task InitAsync(string ext, CancellationToken ct = default)
        {
            if (ext != "var rule = {};") throw new InvalidDataException("wrong ext");
            if (BlockInit)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(ct);
            }
        }
        public Task<string> HomeAsync(CancellationToken ct = default) => Task.FromResult(HomeJson);
        public Task<string> HomeVodAsync(CancellationToken ct = default) => HomeVodError is not null
            ? Task.FromException<string>(HomeVodError)
            : Task.FromResult(HomeVodJson ?? """{"list":[{"vod_id":"home","vod_name":" 首页 ","vod_pic":"https://cdn.example/poster.jpg","vod_year":2026}]}""");
        public Task<string> CategoryAsync(string tid, int page, IReadOnlyDictionary<string, string>? filters, CancellationToken ct = default) =>
            tid == "2" && page == 3 && filters?["year"] == "2026"
                ? Task.FromResult("""{"page":"3","pagecount":"8","list":[{"vod_id":"filtered","vod_name":"筛选"}]}""")
                : throw new InvalidDataException("wrong category arguments");
        public Task<string> DetailAsync(string id, CancellationToken ct = default)
        {
            var token = ++_details;
            return Task.FromResult("{\"list\":[{\"vod_id\":\"7\",\"vod_name\":\"电影\",\"vod_content\":\"<p>简介</p><br>下一行\",\"vod_director\":\"导演\",\"vod_play_from\":\"线路A$$$空线路$$$线路B\",\"vod_play_url\":\"第1集$https://cdn.example/a?token=" + token + "$$$$$$第1集$opaque$token=" + token + "\"}]}");
        }
        public async Task<string> SearchAsync(string query, int page, CancellationToken ct = default)
        {
            if (BlockSearch)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(ct);
            }
            else if (query != "C#\t中文" || page != 2) throw new InvalidDataException("wrong search arguments");
            return """{"list":[{"vod_id":"search","vod_name":"搜索"}]}""";
        }
        public Task<string> PlayAsync(string flag, string id, IReadOnlyList<string> flags, CancellationToken ct = default)
        {
            if (PlayJson is not null) return Task.FromResult(PlayJson);
            if (flag != "线路B" || !id.StartsWith("opaque$token=") || flags.Count != 3) throw new InvalidDataException("wrong play arguments");
            return Task.FromResult("""{"parse":"1","jx":0,"url":"https://cdn.example/new.mp4","header":"{\"Referer\":\"https://example.org/\",\"User-Agent\":\"Fixture\"}"}""");
        }
        public void Dispose() => Disposed = true;
    }
}
