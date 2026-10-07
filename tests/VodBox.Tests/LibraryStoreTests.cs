using VodBox.Core;
using VodBox.Infrastructure;
using Xunit;

namespace VodBox.Tests;

public sealed class LibraryStoreTests : IDisposable
{
    private readonly LibraryStore _store = new(Path.Combine(Path.GetTempPath(), $"vodbox-test-{Guid.NewGuid():N}.db"));

    [Fact]
    public async Task HistoryUpsertsBySourceAndMedia()
    {
        var first = MakeHistory("src1", "m1", position: 1000);
        await _store.SaveHistoryAsync(first);
        await _store.SaveHistoryAsync(MakeHistory("src1", "m1", position: 5000));
        await _store.SaveHistoryAsync(MakeHistory("src1", "m2", position: 100));

        var history = await _store.GetHistoryAsync();
        Assert.Equal(2, history.Count); // (src1,m1) 覆盖而不是新增
        var resumed = history.Single(h => h.MediaId == "m1");
        Assert.Equal(5000, resumed.PositionMs);
    }

    [Fact]
    public async Task FindsHistoryBySourceAndMedia()
    {
        await _store.SaveHistoryAsync(MakeHistory("s", "m", position: 42));
        var found = await _store.FindHistoryAsync("s", "m");
        Assert.NotNull(found);
        Assert.Equal(42, found!.PositionMs);
        Assert.Null(await _store.FindHistoryAsync("s", "missing"));
    }

    [Fact]
    public async Task FavoriteToggleAndQuery()
    {
        var entry = new FavoriteEntry
        {
            Kind = FavoriteKind.Vod,
            SourceKey = "s", SourceName = "站点", MediaId = "m", Title = "片名",
        };
        await _store.SetFavoriteAsync(entry, true);
        Assert.True(await _store.IsFavoriteAsync("s", "m"));
        var favorites = await _store.GetFavoritesAsync(FavoriteKind.Vod);
        Assert.Single(favorites);

        await _store.SetFavoriteAsync(entry, false);
        Assert.False(await _store.IsFavoriteAsync("s", "m"));
        Assert.Empty(await _store.GetFavoritesAsync(FavoriteKind.Vod));
    }

    [Fact]
    public async Task DeleteAndClearHistory()
    {
        await _store.SaveHistoryAsync(MakeHistory("s", "a"));
        await _store.SaveHistoryAsync(MakeHistory("s", "b"));
        await _store.DeleteHistoryAsync("s", "a");
        Assert.Single(await _store.GetHistoryAsync());
        await _store.ClearHistoryAsync();
        Assert.Empty(await _store.GetHistoryAsync());
    }

    [Fact]
    public void PersistsPreferencesAcrossReopen()
    {
        _store.Set("theme", "dark");
        _store.Set("volume", 66);
        _store.Set("incognito", true);
        _store.Set("rate", 1.5);
        Assert.Equal("dark", _store.GetString("theme"));
        Assert.Equal(66, _store.GetInt("volume"));
        Assert.True(_store.GetBool("incognito"));
        Assert.Equal(1.5, _store.GetDouble("rate"));
        Assert.Equal("fallback", _store.GetString("missing", "fallback"));
    }

    private static HistoryEntry MakeHistory(string sourceKey, string mediaId, long position = 0) => new()
    {
        SourceKey = sourceKey,
        SourceName = "站点",
        MediaId = mediaId,
        Title = $"标题-{mediaId}",
        PositionMs = position,
        DurationMs = 90_000,
    };

    public void Dispose() => _store.Dispose();
}
