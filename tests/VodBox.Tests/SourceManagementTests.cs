using VodBox.Infrastructure;
using Xunit;

namespace VodBox.Tests;

public sealed class SourceManagementTests
{
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
            Assert.False(second.CanSearch(second.VisibleSources[0]));
            var results = await second.SearchAllAsync("anything", TestContext.Current.CancellationToken);
            Assert.Empty(results);
        }
        finally { Directory.Delete(directory, true); }
    }
}
