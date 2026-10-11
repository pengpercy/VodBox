using System.Text.Json;
using VodBox.Core;
using VodBox.Infrastructure;
using VodBox.PluginHost.JarPlugin;
using Xunit;

namespace VodBox.Tests;

/// <summary>
/// JAR 插件宿主的离线契约测试：协议序列化、origin/jar/md5 解析、错误分类、生命周期（假通道，不启动 JVM）、
/// 以及 home/category/search/detail/play 的适配。所有用例都不触网、不启进程。
/// </summary>
public sealed class JarPluginHostTests
{
    private const string Origin = "https://sub.example.invalid/config/a.json";

    // ---------- 协议 ----------

    [Fact]
    public void LaunchRequestRoundTripsThroughSourceGeneratedContext()
    {
        var request = new JarPluginLaunchRequest
        {
            Jar = "/tmp/plugin.jar", Cache = "/tmp/cache", Profile = "/tmp/profile",
            Api = "csp_AppRJ", Key = "rj", Ext = "{\"url\":\"https://x\"}",
            Spider = "com.github.catvod.spider.AppRJ",
            GlobalSpider = "https://sub.example.invalid/spider.jar;md5;abc",
            Origin = Origin, Site = "{\"key\":\"rj\"}",
        };
        var text = JarPluginJson.Serialize(request);
        var restored = JsonSerializer.Deserialize(text, JarPluginJsonContext.Default.JarPluginLaunchRequest);
        Assert.NotNull(restored);
        Assert.Equal("csp_AppRJ", restored!.Api);
        Assert.Equal("com.github.catvod.spider.AppRJ", restored.Spider);
        Assert.Equal("https://sub.example.invalid/spider.jar;md5;abc", restored.GlobalSpider);
    }

    [Fact]
    public void CommandCarriesIdAndFlatStringParameters()
    {
        var command = new JarPluginCommand { Id = "r1-abc", Params = new Dictionary<string, string> { ["ids"] = "100" } };
        var text = JarPluginJson.Serialize(command);
        Assert.Contains("\"ids\":\"100\"", text);
    }

    [Theory]
    [InlineData("r1-ab", true)]
    [InlineData("", false)]
    [InlineData("bad/id", false)]
    [InlineData("bad id", false)]
    public void IdentifierValidationRejectsPathUnsafeValues(string id, bool expected) =>
        Assert.Equal(expected, JarPluginProtocol.IsValidIdentifier(id));

    // ---------- origin / jar / md5 ----------

    [Fact]
    public void RelativeJarResolvesAgainstOriginAndKeepsDeclaredMd5()
    {
        var reference = JarReference.Parse("./spider.jar;md5;deadbeef", Origin);
        Assert.NotNull(reference);
        Assert.Equal("https://sub.example.invalid/config/spider.jar", reference!.Url);
        Assert.Equal("deadbeef", reference.DeclaredMd5);
        Assert.True(reference.HasDeclaredMd5);
        // 保留声明不等于已校验——此处没有任何校验动作，只断言原样保留。
        Assert.Equal("./spider.jar;md5;deadbeef", reference.Raw);
    }

    [Fact]
    public void AbsoluteJarIsReturnedUnchanged()
    {
        var reference = JarReference.Parse("https://cdn.example.invalid/p.jar", Origin);
        Assert.Equal("https://cdn.example.invalid/p.jar", reference!.Url);
        Assert.Null(reference.DeclaredMd5);
    }

    [Fact]
    public void EmptyJarSpecificationYieldsNull()
    {
        Assert.Null(JarReference.Parse("  ", Origin));
        Assert.Null(JarReference.Parse(null, Origin));
    }

    [Fact]
    public void LayoutPrefersSiteJarAndDerivesSpiderClass()
    {
        var info = new SourceInfo
        {
            Key = "rj", Name = "RJ", Runtime = SourceRuntime.Jar, Api = "csp_AppRJ",
            Spider = "https://sub.example.invalid/spider.jar", Jar = "./site.jar",
            Origin = Origin, Ext = "./rule.json",
        };
        var layout = JarPluginSourceLayout.From(info);
        Assert.Equal("com.github.catvod.spider.AppRJ", layout.SpiderClass);
        Assert.Equal("https://sub.example.invalid/config/site.jar", layout.EffectiveJar!.Url);
        Assert.Equal("https://sub.example.invalid/config/rule.json", layout.Ext);
    }

    [Fact]
    public void LayoutFallsBackToGlobalSpiderWhenSiteJarIsAbsent()
    {
        var info = new SourceInfo
        {
            Key = "rj", Name = "RJ", Runtime = SourceRuntime.Jar, Api = "csp_AppRJ",
            Spider = "https://sub.example.invalid/spider.jar", Origin = Origin,
        };
        var layout = JarPluginSourceLayout.From(info);
        Assert.Equal("https://sub.example.invalid/spider.jar", layout.EffectiveJar!.Url);
    }

    [Fact]
    public void NonRelativeExtIsPreservedVerbatim()
    {
        var info = new SourceInfo
        {
            Key = "rj", Name = "RJ", Runtime = SourceRuntime.Jar, Api = "csp_AppRJ",
            Spider = "https://sub.example.invalid/spider.jar", Origin = Origin,
            Ext = "{\"url\":\"https://x\"}",
        };
        Assert.Equal("{\"url\":\"https://x\"}", JarPluginSourceLayout.From(info).Ext);
    }

    // ---------- 错误分类 ----------

    [Theory]
    [InlineData(JarPluginErrors.UnsupportedDex, JarPluginFailureKind.Unsupported)]
    [InlineData(JarPluginErrors.UnsupportedAndroid, JarPluginFailureKind.Unsupported)]
    [InlineData(JarPluginErrors.UnsupportedNativeLibrary, JarPluginFailureKind.Unsupported)]
    [InlineData(JarPluginErrors.PluginClassMissing, JarPluginFailureKind.Unsupported)]
    [InlineData(JarPluginErrors.ClassConversion, JarPluginFailureKind.Unsupported)]
    [InlineData(JarPluginErrors.SourceHttp, JarPluginFailureKind.Temporary)]
    [InlineData(JarPluginErrors.SourceNetwork, JarPluginFailureKind.Temporary)]
    [InlineData(JarPluginErrors.HostTimeout, JarPluginFailureKind.Temporary)]
    [InlineData(JarPluginErrors.HostUnavailable, JarPluginFailureKind.Protocol)]
    public void ErrorCodesMapToKinds(string code, JarPluginFailureKind expected) =>
        Assert.Equal(expected, JarPluginErrors.Classify(code).Kind);

    [Fact]
    public void SuccessfulEnvelopeIsNotAFailure()
    {
        Assert.Null(JarPluginErrors.Classify(new JarPluginEnvelope { Result = "{}" }));
    }

    [Fact]
    public void UnknownErrorCodeIsSurfacedNotSwallowed()
    {
        var failure = JarPluginErrors.Classify(new JarPluginEnvelope { ErrorCode = "mystery", Error = "原始信息" });
        Assert.NotNull(failure);
        Assert.Equal("mystery", failure!.Code);
        Assert.Contains("原始信息", failure.Message);
    }

    // ---------- CatVod 载荷映射 ----------

    [Fact]
    public void CategoriesAndFiltersAreParsedFromHome()
    {
        const string home = """
            {"class":[{"type_id":"1","type_name":"电影"}],
             "filters":{"1":[{"key":"area","name":"地区","value":[{"n":"全部","v":""},{"n":"大陆","v":"1"}]}]}}
            """;
        var categories = CatVodCatalog.ParseCategories(home);
        Assert.Equal(new Category("1", "电影"), Assert.Single(categories));
        var group = Assert.Single(CatVodCatalog.ParseFilters(home, "1"));
        Assert.Equal("area", group.Key);
        Assert.Equal(2, group.Values.Count);
        Assert.Equal("1", group.Values[1].Value);
    }

    [Fact]
    public void PageParsingHandlesStringNumbersAndDerivesCounts()
    {
        const string body = """
            {"page":"3","pagecount":"9","list":[{"vod_id":7,"vod_name":"示例","vod_pic":"http://p/1.jpg","vod_remarks":"HD"}]}
            """;
        var page = CatVodCatalog.ParsePage(body, 1);
        Assert.Equal(3, page.Page);
        Assert.Equal(9, page.PageCount);
        var item = Assert.Single(page.Items);
        Assert.Equal("7", item.Id);
        Assert.Equal("示例", item.Title);
    }

    [Fact]
    public void DetailSplitsFlagsAndEncodesEpisodeIdentity()
    {
        const string detail = """
            {"list":[{"vod_id":"100","vod_name":"示例","vod_content":"<p>简介</p>",
              "vod_play_from":"线路A$$$线路B","vod_play_url":"第1集$http://m/1.m3u8#第2集$http://m/2.m3u8$$$HD$http://m/hd.m3u8"}]}
            """;
        var parsed = CatVodCatalog.ParseDetail(detail, "100");
        Assert.Equal("简介", parsed.Description);
        Assert.Equal(2, parsed.Lines.Count);
        var first = parsed.Lines[0];
        Assert.Equal("线路A", first.Id);
        Assert.Equal(2, first.Episodes.Count);
        Assert.Null(first.Episodes[0].Uri);
        var (flag, url) = CatVodCatalog.DecodeEpisodeId(first.Episodes[0].Id);
        Assert.Equal("线路A", flag);
        Assert.Equal("http://m/1.m3u8", url);
    }

    [Fact]
    public void PlaybackPreservesParseJxAndHeaders()
    {
        const string play = """
            {"parse":1,"playUrl":"http://jx/api?url=","url":"http://m/1.m3u8","jx":"1",
             "header":"{\"User-Agent\":\"catvod\",\"Referer\":\"http://r/\"}"}
            """;
        var request = CatVodCatalog.ParsePlayback(play, "http://m/1.m3u8", "示例", "rj", "RJ", "100", "线路A", "ep", null, null);
        Assert.Equal("http://m/1.m3u8", request.Uri);
        Assert.Equal(ResolutionKind.Json, request.Resolution);
        Assert.Equal("http://jx/api?url=", request.ParseEndpoint);
        Assert.Equal("1", request.Jx);
        Assert.Equal("catvod", request.Headers["User-Agent"]);
        Assert.Equal("http://r/", request.Headers["referer"]);
    }

    [Fact]
    public void DirectPlaybackWithoutParseStaysDirect()
    {
        var request = CatVodCatalog.ParsePlayback("""{"parse":0,"url":"http://m/x.mp4"}""", "", "t", "rj", "RJ", "1", "f", "e", null, null);
        Assert.Equal(ResolutionKind.Direct, request.Resolution);
        Assert.Null(request.ParseEndpoint);
        Assert.Equal("http://m/x.mp4", request.Uri);
    }

    // ---------- 生命周期（假通道） ----------

    private static JarPluginHostSettings Settings(TimeSpan? request = null) => new()
    {
        JavaExecutable = "/nonexistent/java",
        HostJarPath = "/nonexistent/host.jar",
        WorkRoot = Path.Combine(Path.GetTempPath(), "vodbox-jarhost-tests"),
        RequestTimeout = request ?? TimeSpan.FromSeconds(2),
    };

    private static JarPluginSourceLayout Layout(string key = "rj") => JarPluginSourceLayout.From(new SourceInfo
    {
        Key = key, Name = key, Runtime = SourceRuntime.Jar, Api = "csp_AppRJ",
        Spider = "https://sub.example.invalid/spider.jar", Origin = Origin,
    });

    private sealed class FakeChannel : IJarPluginHostChannel
    {
        public JarPluginEnvelope Ready { get; init; } = new() { Ready = true };
        public Func<IReadOnlyDictionary<string, string>, CancellationToken, Task<JarPluginEnvelope>> OnSend { get; set; } =
            (_, _) => Task.FromResult(new JarPluginEnvelope { Result = "{}" });
        public bool Disposed { get; private set; }
        public bool IsRunning { get; private set; }
        public Task<JarPluginEnvelope> StartAsync(CancellationToken ct) { IsRunning = true; return Task.FromResult(Ready); }
        public Task<JarPluginEnvelope> SendAsync(IReadOnlyDictionary<string, string> parameters, CancellationToken ct) => OnSend(parameters, ct);
        public ValueTask DisposeAsync() { Disposed = true; IsRunning = false; return ValueTask.CompletedTask; }
    }

    private sealed class FakeFactory(FakeChannel channel) : IJarPluginHostChannelFactory
    {
        public List<JarPluginSessionDescriptor> Created { get; } = [];
        public IJarPluginHostChannel Create(JarPluginSessionDescriptor descriptor)
        {
            Created.Add(descriptor);
            return channel;
        }
    }

    private sealed class FakeArtifacts : IJarPluginArtifactStore
    {
        public Task<string> AcquireAsync(JarReference jar, CancellationToken ct) => Task.FromResult("/tmp/plugin.jar");
    }

    [Fact]
    public async Task RequestReturnsChannelEnvelope()
    {
        var channel = new FakeChannel { OnSend = (_, _) => Task.FromResult(new JarPluginEnvelope { Result = "{\"ok\":1}" }) };
        var session = new JarPluginSession(new JarPluginSessionDescriptor("rj", Layout(), "/tmp/plugin.jar", Path.GetTempPath()), new FakeFactory(channel), Settings());
        var envelope = await session.RequestAsync(new Dictionary<string, string>(), TestContext.Current.CancellationToken);
        Assert.Equal("{\"ok\":1}", envelope.Result);
        Assert.False(channel.Disposed);
    }

    [Fact]
    public async Task RequestTimeoutKillsSession()
    {
        var channel = new FakeChannel
        {
            OnSend = async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return new JarPluginEnvelope(); },
        };
        var session = new JarPluginSession(new JarPluginSessionDescriptor("rj", Layout(), "/tmp/plugin.jar", Path.GetTempPath()), new FakeFactory(channel), Settings(TimeSpan.FromMilliseconds(60)));
        var error = await Assert.ThrowsAsync<JarPluginException>(() => session.RequestAsync(new Dictionary<string, string>(), TestContext.Current.CancellationToken));
        Assert.Equal(JarPluginErrors.HostTimeout, error.Failure.Code);
        Assert.True(error.Failure.Retryable);
        Assert.True(channel.Disposed);
        Assert.False(session.IsRunning);
    }

    [Fact]
    public async Task StartupFailureIsClassifiedAndKillsSession()
    {
        var channel = new FakeChannel { Ready = new JarPluginEnvelope { ErrorCode = JarPluginErrors.UnsupportedDex, Error = "dex" } };
        var session = new JarPluginSession(new JarPluginSessionDescriptor("rj", Layout(), "/tmp/plugin.jar", Path.GetTempPath()), new FakeFactory(channel), Settings());
        var error = await Assert.ThrowsAsync<JarPluginException>(() => session.RequestAsync(new Dictionary<string, string>(), TestContext.Current.CancellationToken));
        Assert.Equal(JarPluginErrors.UnsupportedDex, error.Failure.Code);
        Assert.Equal(JarPluginFailureKind.Unsupported, error.Failure.Kind);
        Assert.True(channel.Disposed);
    }

    [Fact]
    public async Task RuntimeIsolatesSessionsPerSourceKey()
    {
        var factory = new FakeFactory(new FakeChannel());
        var runtime = new JarPluginRuntime(factory, Settings(), new FakeArtifacts());
        await runtime.RequestAsync(Layout("a"), new Dictionary<string, string>(), TestContext.Current.CancellationToken);
        await runtime.RequestAsync(Layout("b"), new Dictionary<string, string>(), TestContext.Current.CancellationToken);
        await runtime.RequestAsync(Layout("a"), new Dictionary<string, string>(), TestContext.Current.CancellationToken);
        Assert.Equal(2, factory.Created.Count);
        Assert.Contains(factory.Created, descriptor => descriptor.SourceKey == "a");
        Assert.Contains(factory.Created, descriptor => descriptor.SourceKey == "b");
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task RuntimeWithoutJarReferenceFailsExplicitly()
    {
        var runtime = new JarPluginRuntime(new FakeFactory(new FakeChannel()), Settings(), new FakeArtifacts());
        var layout = JarPluginSourceLayout.From(new SourceInfo { Key = "x", Name = "x", Runtime = SourceRuntime.Jar, Api = "csp_AppRJ" });
        var error = await Assert.ThrowsAsync<JarPluginException>(() => runtime.RequestAsync(layout, new Dictionary<string, string>(), TestContext.Current.CancellationToken));
        Assert.Equal(JarPluginErrors.PluginClassMissing, error.Failure.Code);
        await runtime.DisposeAsync();
    }

    // ---------- 适配器映射（假通道，不启动宿主） ----------

    private static JarPluginContentSource Source(FakeChannel channel) =>
        new(
            new SourceInfo { Key = "rj", Name = "RJ", Runtime = SourceRuntime.Jar, Api = "csp_AppRJ", Spider = "https://sub.example.invalid/spider.jar", Origin = Origin },
            Layout(),
            new JarPluginRuntime(new FakeFactory(channel), Settings(), new FakeArtifacts()));

    private static FakeChannel CatalogChannel(List<IReadOnlyDictionary<string, string>> recorded) => new()
    {
        OnSend = (parameters, _) =>
        {
            recorded.Add(parameters);
            if (parameters.ContainsKey("ids"))
                return Task.FromResult(new JarPluginEnvelope { Result = """{"list":[{"vod_id":"100","vod_name":"示例","vod_play_from":"线路A","vod_play_url":"第1集$http://m/1.m3u8"}]}""" });
            if (parameters.ContainsKey("play"))
                return Task.FromResult(new JarPluginEnvelope { Result = """{"parse":1,"playUrl":"http://jx/?u=","url":"http://m/1.m3u8","jx":"1","header":"{\"User-Agent\":\"catvod\"}"}""" });
            if (parameters.ContainsKey("wd"))
                return Task.FromResult(new JarPluginEnvelope { Result = """{"list":[{"vod_id":"200","vod_name":"搜索项"}]}""" });
            if (parameters.ContainsKey("t"))
                return Task.FromResult(new JarPluginEnvelope { Result = """{"page":"2","pagecount":"5","list":[{"vod_id":"101","vod_name":"分类项"}]}""" });
            return Task.FromResult(new JarPluginEnvelope { Result = """{"class":[{"type_id":"1","type_name":"电影"}],"list":[{"vod_id":"100","vod_name":"首页项"}]}""" });
        },
    };

    [Fact]
    public async Task AdapterMapsHomeCategorySearchDetailAndPlayback()
    {
        var recorded = new List<IReadOnlyDictionary<string, string>>();
        var channel = CatalogChannel(recorded);
        using var source = Source(channel);

        Assert.Equal("电影", Assert.Single(await source.GetCategoriesAsync(TestContext.Current.CancellationToken)).Name);
        Assert.Equal("首页项", Assert.Single((await source.GetHomeAsync(TestContext.Current.CancellationToken)).Items).Title);

        var page = await source.GetItemsAsync("1", 2, new Dictionary<string, string> { ["area"] = "1" }, TestContext.Current.CancellationToken);
        Assert.Equal("分类项", Assert.Single(page.Items).Title);
        Assert.Equal("2", recorded[^1]["pg"]);
        Assert.Equal("1", recorded[^1]["t"]);

        Assert.Equal("搜索项", Assert.Single((await source.SearchAsync("词", 1, TestContext.Current.CancellationToken)).Items).Title);
        Assert.Equal("词", recorded[^1]["wd"]);

        var detail = await source.GetDetailAsync("100", TestContext.Current.CancellationToken);
        var episode = Assert.Single(Assert.Single(detail.Lines).Episodes);
        var request = await source.ResolvePlaybackAsync("100", episode.Id, TestContext.Current.CancellationToken);
        Assert.Equal("http://m/1.m3u8", request.Uri);
        Assert.Equal("http://jx/?u=", request.ParseEndpoint);
        Assert.Equal("1", request.Jx);
        Assert.Equal("catvod", request.Headers["User-Agent"]);
        Assert.Equal("线路A", request.LineId);
        Assert.Equal("rj", request.SourceKey);
    }

    [Fact]
    public async Task AdapterPreservesMultiAddressCredentialHeadersAndParseFlags()
    {
        var channel = new FakeChannel
        {
            OnSend = (parameters, _) =>
            {
                if (parameters.ContainsKey("ids"))
                    return Task.FromResult(new JarPluginEnvelope { Result = """{"list":[{"vod_id":"100","vod_name":"示例","vod_play_from":"线路A","vod_play_url":"第1集$http://m/1.m3u8"}]}""" });
                if (parameters.ContainsKey("play"))
                    return Task.FromResult(new JarPluginEnvelope { Result = """{"parse":1,"jx":"1","playUrl":"http://jx/?u=","url":["高清","http://m/1.m3u8","备用","http://m/2.m3u8"],"header":{"User-Agent":"catvod","Cookie":"sid=abc","Authorization":"Bearer t"}}""" });
                return Task.FromResult(new JarPluginEnvelope { Result = "{}" });
            },
        };
        using var source = Source(channel);
        var detail = await source.GetDetailAsync("100", TestContext.Current.CancellationToken);
        var episode = Assert.Single(Assert.Single(detail.Lines).Episodes);
        var request = await source.ResolvePlaybackAsync("100", episode.Id, TestContext.Current.CancellationToken);

        // 多地址完整保留，首选地址仍是其中之一。
        Assert.Equal("http://m/1.m3u8", request.Uri);
        Assert.Equal(2, request.Choices.Count);
        Assert.Equal("高清", request.Choices[0].Name);
        Assert.Equal("备用", request.Choices[1].Name);
        Assert.Equal("http://m/2.m3u8", request.Choices[1].Uri);
        // parse/jx/playUrl 与 Cookie/Authorization 等播放头都保留（交由受控媒体代理转发）。
        Assert.Equal(ResolutionKind.Json, request.Resolution);
        Assert.Equal("http://jx/?u=", request.ParseEndpoint);
        Assert.Equal("1", request.Jx);
        Assert.Equal("catvod", request.Headers["User-Agent"]);
        Assert.Equal("sid=abc", request.Headers["Cookie"]);
        Assert.Equal("Bearer t", request.Headers["Authorization"]);
    }

    [Fact]
    public async Task AdapterSurfacesHostFailureInsteadOfEmptyResult()
    {
        var channel = new FakeChannel { OnSend = (_, _) => Task.FromResult(new JarPluginEnvelope { ErrorCode = JarPluginErrors.SourceHttp, Status = 403, Host = "cdn" }) };
        using var source = Source(channel);
        var error = await Assert.ThrowsAsync<JarPluginException>(() => source.GetHomeAsync(TestContext.Current.CancellationToken));
        Assert.Equal(JarPluginErrors.SourceHttp, error.Failure.Code);
    }

    // ---------- 配置提升（默认关闭，显式开启才转 Jar 宿主） ----------

    [Fact]
    public void JarSourcesArePromotedOnlyWhenExplicitlyEnabled()
    {
        var config = new TvBoxConfig
        {
            Spider = "https://sub.example.invalid/spider.jar;md5;abc",
            Sites =
            [
                // 无 C# 原生实现的 csp_ 入口：显式开启后应转 Jar 宿主。
                new TvBoxSite { Key = "unknown", Name = "未知Java", Api = "csp_UnknownJar", Type = 3 },
                // 已有原生实现的 csp_ 入口：仍按 NativeSpider 提升，不被 Jar 覆盖。
                new TvBoxSite { Key = "bili", Name = "Bili", Api = "csp_BiliGuard", Type = 3 },
            ],
        };

        // 默认：未实现的 csp_ 被过滤（既有行为不变）。
        var filtered = ConfigLoader.ToSources(config);
        Assert.DoesNotContain(filtered, source => source.Runtime == SourceRuntime.Jar);

        var promoted = ConfigLoader.ToSources(config, Origin, includeJarPlugins: true);
        var jar = promoted.Single(source => source.Runtime == SourceRuntime.Jar);
        Assert.Equal("unknown", jar.Key);
        Assert.Equal("https://sub.example.invalid/spider.jar;md5;abc", jar.Spider);
        Assert.Equal(Origin, jar.Origin);
        Assert.NotNull(jar.SiteJson);
        Assert.Contains(promoted, source => source.Runtime == SourceRuntime.NativeSpider);
    }
}
