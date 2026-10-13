using VodBox.Infrastructure;
using Xunit;

namespace VodBox.Tests;

public sealed class SourceManagementTests
{
    [Fact]
    public async Task FtySupportedSitesRemainManuallySelectableDespiteChangeableZero()
    {
        using var registry = new SourceRegistry(async (_, ct) =>
            ConfigLoader.ParseBytes(await File.ReadAllBytesAsync(
                Path.Combine(AppContext.BaseDirectory, "fixtures", "fty-in.bmp"), ct))
            ?? throw new InvalidDataException("饭太硬测试配置解析失败"));
        await registry.LoadConfigAsync("https://example.com/fty", TestContext.Current.CancellationToken);
        Assert.Equal(13, registry.SelectableSources.Count);
        Assert.All(registry.SelectableSources, site =>
        {
            Assert.Equal(VodBox.Core.SourceRuntime.NativeSpider, site.Runtime);
            Assert.NotNull(registry.Get(site.Key));
        });
        // changeable=0 的 12 个站点仍可手动选择；只有配置里 changeable=1 的比特进入换源列表。
        Assert.Equal(12, registry.SelectableSources.Count(site => !site.Changeable));
        Assert.Equal("比特", Assert.Single(registry.ChangeableSources).Key);
        Assert.Equal(35, registry.ImportWarnings.Count);
        Assert.Equal("YGP", registry.Default()!.Key);
    }

    [Fact]
    public async Task HiddenSearchAndOrderPreferencesSurviveRegistryRecreation()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "sites-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "config.json");
        try
        {
            await File.WriteAllTextAsync(path, "{\"sites\":[{\"key\":\"a\",\"name\":\"A\",\"api\":\"https://example.com/a\",\"type\":1},{\"key\":\"b\",\"name\":\"B\",\"api\":\"https://example.com/b\",\"type\":1}]}", TestContext.Current.CancellationToken);
            using var store = new LibraryStore(Path.Combine(directory, "library.db"));
            using var first = new SourceRegistry(store);
            await first.LoadConfigAsync(path, TestContext.Current.CancellationToken);
            first.SetHidden("a", true);
            first.SetSearchable("b", false);
            first.SetStarred("b", true);
            first.SetChangeable("b",false);
            Assert.Equal("b", first.Default()!.Key);
            using var second = new SourceRegistry(store);
            await second.LoadConfigAsync(path, TestContext.Current.CancellationToken);
            Assert.Equal("b", second.Sources[0].Key);
            Assert.Single(second.VisibleSources);
            Assert.Empty(second.ChangeableSources);
            Assert.Equal("b", Assert.Single(second.SelectableSources).Key);
            Assert.False(second.CanSearch(second.VisibleSources[0]));
            var results = await second.SearchAllAsync("anything", TestContext.Current.CancellationToken);
            Assert.Empty(results);
        }
        finally { Directory.Delete(directory, true); }
    }
}
