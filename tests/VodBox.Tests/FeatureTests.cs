using System.IO.Compression;
using System.Net;
using System.Text;
using VodBox.Application;
using VodBox.Core;
using VodBox.Infrastructure;
using Xunit;

namespace VodBox.Tests;

public sealed class FeatureTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CollectionApiSupportsPagingSearchAndMultiplePlaybackLines(bool xml)
    {
        const string json = """
            {"code":1,"page":1,"pagecount":2,"class":[{"type_id":7,"type_name":"电影"}],"list":[{"vod_id":42,"vod_name":"测试电影","vod_pic":"/poster.jpg","vod_remarks":"完结","vod_content":"简介","vod_play_from":"main$$$backup","vod_play_url":"第1集$https://media.example/a.m3u8#第2集$https://media.example/b.m3u8$$$正片$https://media.example/backup.mp4"}]}
            """;
        const string document = """
            <rss><class><ty id="7">电影</ty></class><list page="1" pagecount="2"><video><id>42</id><name>测试电影</name><pic>/poster.jpg</pic><note>完结</note><des>简介</des><dl><dd flag="main"><![CDATA[第1集$https://media.example/a.m3u8#第2集$https://media.example/b.m3u8]]></dd><dd flag="backup"><![CDATA[正片$https://media.example/backup.mp4]]></dd></dl></video></list></rss>
            """;
        var handler = new Responses(xml ? document : json); using var http = new HttpClient(handler);
        var source = new SourceDefinition { Id = "api", Name = "采集源", Entry = "https://api.example/api.php?key=fixture", Provider = xml ? "maccms-xml" : "maccms-json" };
        await using var provider = new ProviderFactory(http, "unused", "unused").Create(source);
        Assert.Equal("7", Assert.Single(await provider.GetCategoriesAsync(default)).Id);
        Assert.Equal("2", (await provider.GetItemsAsync("7", null, default)).NextCursor);
        await provider.SearchAsync("中文 & x=1", default);
        Assert.Contains("wd=%E4%B8%AD%E6%96%87%20%26%20x%3D1", handler.Requests[^1].Query);
        Assert.Contains("key=fixture", handler.Requests[^1].Query);
        var detail = await provider.GetDetailAsync("42", default);
        Assert.Equal(2, detail.PlaybackLines.Count); Assert.Equal(2, detail.PlaybackLines[0].Episodes.Count);
        var request = await provider.ResolvePlaybackAsync("42", detail.PlaybackLines[1].Episodes[0].Id, default);
        Assert.Equal("https://media.example/backup.mp4", request.Uri); Assert.Equal("api", request.SourceId);
        Assert.Equal("https://api.example/poster.jpg", detail.Item.Poster);
    }

    [Fact]
    public async Task SavedConfigurationRetainsResolvedOriginsAndPreferences()
    {
        string directory = Temporary();
        try
        {
            var repository = new ConfigurationRepository(Path.Combine(directory, "configs"));
            var config = new VodBoxConfig { Id = "../../arbitrary-id", Sources = [new() { Id = "s", Name = "源", Entry = "https://api.example/catalog.json" }] };
            await repository.SaveAsync(config, "https://api.example/config.json");
            Assert.Equal(config.Sources[0].Entry, Assert.Single((await repository.LoadAsync(config.Id)).Sources).Entry);
            Assert.Equal("https://api.example/config.json", Assert.Single(await repository.ListAsync()).Location);
            var preferences = new PreferencesStore(Path.Combine(directory, "preferences.json"));
            var expected = new AppPreferences { LastConfigId = config.Id, Volume = 35, AutoNext = false, SkipIntroSeconds = 15 };
            await preferences.SaveAsync(expected); Assert.Equal(expected, await preferences.LoadAsync());
            await repository.RemoveAsync(config.Id); Assert.Empty(await repository.ListAsync());
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task GzipEpgMatchesDisplayNameAndPreservesTimezone()
    {
        string directory = Temporary();
        try
        {
            string file = Path.Combine(directory, "epg.xml.gz");
            using (var output = File.Create(file)) using (var gzip = new GZipStream(output, CompressionMode.Compress))
                await gzip.WriteAsync(Encoding.UTF8.GetBytes("<tv><channel id='c1'><display-name>CCTV-1</display-name></channel><programme channel='c1' start='20261005120000 +0800' stop='20261005130000 +0800'><title>新闻</title></programme></tv>"));
            using var http = new HttpClient(); var service = new EpgService(http, Path.Combine(directory, "cache"));
            var schedule = await service.LoadAsync(new Uri(file).AbsoluteUri);
            var matches = EpgService.ForChannel(schedule, new("one", "CCTV1", "央视", []), now: new(2026, 10, 5, 4, 30, 0, TimeSpan.Zero));
            Assert.Equal("新闻", Assert.Single(matches).Title); Assert.Equal(4, matches[0].Start.Hour);
            File.Delete(file); Assert.Same(schedule, await service.LoadAsync(new Uri(file).AbsoluteUri));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task AggregateSearchStreamsPartialFailuresAndLimitsConcurrency()
    {
        var factory = new SearchFactory(); var search = new AggregateSearch(factory);
        var sources = Enumerable.Range(0, 10).Select(x => new SourceDefinition { Id = x.ToString(), Name = x.ToString() }).ToArray();
        var results = new List<SearchBatch>();
        await foreach (var batch in search.SearchAsync(sources, "query")) results.Add(batch);
        Assert.Equal(10, results.Count); Assert.Single(results, x => x.Error is not null);
        Assert.InRange(factory.Peak, 1, 4); Assert.Equal(10, factory.Disposed);
    }
    private static string Temporary() { string value = Path.Combine(Path.GetTempPath(), "vodbox-features-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(value); return value; }
    private sealed class Responses(string body) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Requests.Add(request.RequestUri!); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) }); }
    }
    private sealed class SearchFactory : IProviderFactory
    {
        private int _active; public int Peak, Disposed;
        public IContentProvider Create(SourceDefinition source) => new SearchProvider(this, source.Id);
        private sealed class SearchProvider(SearchFactory owner, string id) : IContentProvider
        {
            public string SourceId => id;
            public async Task<MediaPage> SearchAsync(string query, CancellationToken token)
            {
                int active = Interlocked.Increment(ref owner._active); owner.Peak = Math.Max(owner.Peak, active);
                try { await Task.Delay(20, token); if (id == "3") throw new IOException("source unavailable"); return new([new(id, query)]); }
                finally { Interlocked.Decrement(ref owner._active); }
            }
            public ValueTask DisposeAsync() { Interlocked.Increment(ref owner.Disposed); return ValueTask.CompletedTask; }
            public Task<IReadOnlyList<Category>> GetCategoriesAsync(CancellationToken token) => throw new NotSupportedException();
            public Task<MediaPage> GetItemsAsync(string? category, string? cursor, CancellationToken token) => throw new NotSupportedException();
            public Task<MediaDetail> GetDetailAsync(string id, CancellationToken token) => throw new NotSupportedException();
            public Task<PlaybackRequest> ResolvePlaybackAsync(string media, string episode, CancellationToken token) => throw new NotSupportedException();
        }
    }
}
