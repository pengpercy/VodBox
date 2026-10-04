using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using VodBox.Core;
using VodBox.Infrastructure;
using Xunit;

namespace VodBox.Tests;

public sealed class InfrastructureTests
{
    private static string Root => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));

    [Fact]
    public async Task ConfigResolvesRelativeEntriesAndCatalogPlayback()
    {
        using var http = new HttpClient();
        var config = await new ConfigLoader(http).LoadAsync(Path.Combine(Root, "examples/vodbox.json"));
        Assert.Equal(4, config.Sources.Count);
        Assert.All(config.Sources, source => Assert.True(new Uri(source.Entry!).IsFile));
        await using var provider = new CatalogProvider(config.Sources[0], http);
        Assert.Single(await provider.GetCategoriesAsync(default));
        Assert.Single((await provider.SearchAsync("本地", default)).Items);
        Assert.Empty((await provider.SearchAsync("不存在", default)).Items);
        var request = await provider.ResolvePlaybackAsync("sample", "main", default);
        Assert.Equal("catalog", request.SourceId);
        Assert.Equal(Path.Combine(Root, "examples", "sample.mp4"), new Uri(request.Uri).LocalPath);
    }

    [Fact]
    public async Task RejectsUnsupportedSchema()
    {
        string file = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(file, "{\"schemaVersion\":2}");
            using var http = new HttpClient();
            await Assert.ThrowsAsync<InvalidDataException>(() => new ConfigLoader(http).LoadAsync(file));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task SqliteSeparatesConfigurationsAndUpsertsProgress()
    {
        string directory = Path.Combine(Path.GetTempPath(), "vodbox-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LibraryStore(Path.Combine(directory, "library.db"));
            var entry = new HistoryEntry("a", "s", "m", "e", "title", "https://example.com/a", 100, DateTimeOffset.UtcNow);
            await store.SaveHistoryAsync(entry);
            await store.SaveHistoryAsync(entry with { PositionMs = 200 });
            await store.SaveHistoryAsync(entry with { ConfigId = "b" });
            var history = await store.GetHistoryAsync();
            Assert.Equal(2, history.Count);
            Assert.Equal(200, history.Single(x => x.ConfigId == "a").PositionMs);
            var favorite = new FavoriteEntry("a", "s", "m", "title");
            await store.SetFavoriteAsync(favorite, true);
            await store.SetFavoriteAsync(favorite, true);
            Assert.Single(await store.GetFavoritesAsync());
            await store.SetFavoriteAsync(favorite, false);
            Assert.Empty(await store.GetFavoritesAsync());
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
    }

    [Fact]
    public void LiveParserPreservesMirrorsAndExplicitEpgTimezone()
    {
        var channels = LiveParser.Parse("新闻,#genre#\n频道,https://example.com/a\n频道,https://example.com/b");
        Assert.Equal(2, Assert.Single(channels).Uris.Count);
        var m3u = LiveParser.Parse("#EXTM3U\n#EXTINF:-1 http-user-agent=\"AptvPlayer-UA\",CCTV1\n#EXTVLCOPT:http-referrer=https://example.com/\nhttps://example.com/live\n#EXTINF:-1,Other\nhttps://example.com/other");
        Assert.Equal("AptvPlayer-UA", m3u[0].Headers!["User-Agent"]);
        Assert.Equal("https://example.com/", m3u[0].Headers!["Referer"]);
        Assert.Empty(m3u[1].Headers!);
        using var xml = new MemoryStream(Encoding.UTF8.GetBytes("<tv><programme channel='c' start='20261005120000 +0800' stop='20261005130000 +0800'><title>节目</title></programme></tv>"));
        var programme = Assert.Single(LiveParser.ParseEpg(xml));
        Assert.Equal(4, programme.Start.Hour);
        Assert.Equal("节目", programme.Title);
        using var invalid = new MemoryStream(Encoding.UTF8.GetBytes("<!DOCTYPE tv [<!ENTITY a 'external'>]><tv/>"));
        Assert.Throws<System.Xml.XmlException>(() => LiveParser.ParseEpg(invalid));
    }

    [Theory]
    [InlineData(ProviderRuntime.Python, "demo.py")]
    [InlineData(ProviderRuntime.Node, "demo.mjs")]
    public async Task ScriptWorkersRoundTripAllProviderMethods(ProviderRuntime runtime, string file)
    {
        var source = new SourceDefinition { Id = "worker", Name = "Test", Runtime = runtime,
            Entry = new Uri(Path.Combine(Root, "examples", file)).AbsoluteUri,
            Options = new() { ["mediaUri"] = JsonSerializer.SerializeToElement("https://example.com/video.mp4", VodBoxJson.Default.String) } };
        await using var provider = new ScriptProvider(source, "unused", Root);
        Assert.Single(await provider.GetCategoriesAsync(default));
        Assert.Single((await provider.GetItemsAsync(null, null, default)).Items);
        Assert.Empty((await provider.SearchAsync("missing", default)).Items);
        Assert.Single((await provider.GetDetailAsync("sample", default)).PlaybackLines);
        var request = await provider.ResolvePlaybackAsync("sample", "main", default);
        Assert.Equal("worker", request.SourceId);
        Assert.Equal("https://example.com/video.mp4", request.Uri);
    }
}
