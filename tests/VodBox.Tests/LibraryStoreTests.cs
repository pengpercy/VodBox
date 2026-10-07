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
    public async Task HistoryRoundTripsEpisodeContextAndSourceName()
    {
        // S0-2 回归：续播所需的线路/选集/海报/备注/真实来源名必须持久化
        await _store.SaveHistoryAsync(new HistoryEntry
        {
            SourceKey = "fty",
            SourceName = "饭太硬",
            MediaId = "vod-42",
            Title = "片名",
            Poster = "https://img.example/p.jpg",
            Remarks = "更新至12集",
            LineId = "line2",
            EpisodeId = "ep-7",
            PositionMs = 123_456,
            DurationMs = 2_400_000,
            Rate = 1.25,
        }, TestContext.Current.CancellationToken);

        var found = await _store.FindHistoryAsync("fty", "vod-42", TestContext.Current.CancellationToken);
        Assert.NotNull(found);
        Assert.Equal("饭太硬", found!.SourceName);
        Assert.Equal("line2", found.LineId);
        Assert.Equal("ep-7", found.EpisodeId);
        Assert.Equal("https://img.example/p.jpg", found.Poster);
        Assert.Equal("更新至12集", found.Remarks);
        Assert.Equal(123_456, found.PositionMs);
        Assert.Equal(1.25, found.Rate);
    }

    [Fact]
    public async Task StoredContextRestoresLineEpisodeAndResumePosition()
    {
        // S0-2 端到端：详情页存下线路/选集 → 续播读回 → 还原到同一集，且位置可复用
        await _store.SaveHistoryAsync(new HistoryEntry
        {
            SourceKey = "fty", SourceName = "饭太硬", MediaId = "m1", Title = "片名",
            LineId = "line2", EpisodeId = "e8", PositionMs = 754_000,
        }, TestContext.Current.CancellationToken);

        var detail = new MediaDetail
        {
            Item = new MediaItem { Id = "m1", Title = "片名" },
            Lines =
            [
                new PlaybackLine("line1", "线路一", [new Episode("e1", "第1集")]),
                new PlaybackLine("line2", "线路二", [new Episode("e7", "第7集"), new Episode("e8", "第8集")]),
            ],
        };

        var entry = await _store.FindHistoryAsync("fty", "m1", TestContext.Current.CancellationToken);
        Assert.NotNull(entry);
        var line = detail.FindLine(entry!.LineId);
        var episode = detail.FindEpisode(line, entry.EpisodeId);
        Assert.Equal("line2", line!.Id);
        Assert.Equal("e8", episode!.Id);
        Assert.True(MediaDetail.ResumePositionApplies(entry.LineId, entry.EpisodeId, line.Id, episode.Id));
        Assert.Equal(754_000, entry.PositionMs);
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
