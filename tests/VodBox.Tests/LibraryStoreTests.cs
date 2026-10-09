using VodBox.Core;
using VodBox.Infrastructure;
using Xunit;

namespace VodBox.Tests;

public sealed class LibraryStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"vodbox-test-{Guid.NewGuid():N}");
    private readonly LibraryStore _store;

    public LibraryStoreTests()
    {
        _store = new LibraryStore(Path.Combine(_directory, "library.db"));
    }

    [Fact]
    public async Task ActiveSubscriptionCannotSelectAnotherKindOrClearOnInvalidId()
    {
        await _store.AddAsync(new ConfigSubscription { Url = "vod", Name = "点播", Kind = ConfigKind.Vod }, ct: TestContext.Current.CancellationToken);
        await _store.AddAsync(new ConfigSubscription { Url = "live", Name = "直播", Kind = ConfigKind.Live }, ct: TestContext.Current.CancellationToken);
        var vod = Assert.Single(await _store.ListAsync(ConfigKind.Vod, ct: TestContext.Current.CancellationToken));
        var live = Assert.Single(await _store.ListAsync(ConfigKind.Live, ct: TestContext.Current.CancellationToken));
        await _store.SetActiveAsync(ConfigKind.Vod, vod.Id, ct: TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _store.SetActiveAsync(ConfigKind.Vod, live.Id, ct: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _store.SetActiveAsync(ConfigKind.Vod, -1, ct: TestContext.Current.CancellationToken));
        Assert.True(Assert.Single(await _store.ListAsync(ConfigKind.Vod, ct: TestContext.Current.CancellationToken)).Active);
        Assert.False(Assert.Single(await _store.ListAsync(ConfigKind.Live, ct: TestContext.Current.CancellationToken)).Active);
    }

    [Fact]
    public async Task LogicalBackupRoundTripsHistoryPreferencesAndRejectsInvalidVersion()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.SaveHistoryAsync(new HistoryEntry { SourceKey = "s", SourceName = "站", MediaId = "m", Title = "片" }, ct);
        _store.Set("player.volume", 35);
        var backup = await new LibraryBackupService(_store).ExportAsync(ct);
        using var restored = new LibraryStore(Path.Combine(_directory, "restored.db"));
        var service = new LibraryBackupService(restored);
        await service.ImportMergeAsync(backup, ct);
        Assert.Equal("片", Assert.Single(await restored.GetHistoryAsync(ct: ct)).Title);
        Assert.Equal(35, restored.GetInt("player.volume"));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ImportMergeAsync("{\"version\":2}", ct));
        Assert.Single(await restored.GetHistoryAsync(ct: ct));
    }

    [Fact]
    public async Task BackupFailureRollsBackAllEarlierWritesAndKeepsNewerHistory()
    {
        var ct = TestContext.Current.CancellationToken;
        var target = Path.Combine(_directory, "atomic.db");
        using var store = new LibraryStore(target);
        // Pooling keeps a file handle alive after Dispose, which Windows rejects as a cross-process lock.
        var builder = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = target, Pooling = false };
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(builder.ToString()))
        {
            connection.Open();
            using var trigger = connection.CreateCommand();
            trigger.CommandText = "CREATE TRIGGER reject_restore BEFORE INSERT ON prefs WHEN NEW.key='reject' BEGIN SELECT RAISE(ABORT,'test restore failure'); END";
            trigger.ExecuteNonQuery();
        }
        var backup = new LibraryBackup
        {
            History = [new HistoryEntry { SourceKey="s",SourceName="站",MediaId="new",Title="新记录" }],
            Preferences = new() { ["safe"]="value", ["reject"]="value" },
        };
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => store.ImportBackupAtomicAsync(backup,ct));
        Assert.Empty(await store.GetHistoryAsync(ct:ct));
        Assert.Equal("missing",store.GetString("safe","missing"));
        var latest = new HistoryEntry { SourceKey="s",SourceName="站",MediaId="m",Title="最近",UpdatedAt=DateTimeOffset.Now };
        await store.SaveHistoryAsync(latest,ct);
        await store.ImportBackupAtomicAsync(new LibraryBackup { History=[latest with { Title="旧备份",UpdatedAt=latest.UpdatedAt.AddDays(-1) }] },ct);
        Assert.Equal("最近",Assert.Single(await store.GetHistoryAsync(ct:ct)).Title);
    }

    [Fact]
    public async Task WatchedEpisodesArePersistedAndIsolatedBySourceMediaAndLine()
    {
        var ct=TestContext.Current.CancellationToken;
        await _store.MarkEpisodeWatchedAsync("s","m","line1","ep1",ct);
        await _store.MarkEpisodeWatchedAsync("s","m","line1","ep1",ct);
        Assert.Contains("ep1",await _store.GetWatchedEpisodesAsync("s","m","line1",ct));
        Assert.Empty(await _store.GetWatchedEpisodesAsync("s","m","line2",ct));
        Assert.Empty(await _store.GetWatchedEpisodesAsync("other","m","line1",ct));
    }

    [Fact]
    public async Task WatchedRecordsRoundTripBackupAndClearWithHistory()
    {
        var ct=TestContext.Current.CancellationToken;
        await _store.MarkEpisodeWatchedAsync("s","m","line","ep1",ct);
        var backup=await new LibraryBackupService(_store).ExportAsync(ct);
        using var restored=new LibraryStore(Path.Combine(_directory,"watched-restore.db"));
        await new LibraryBackupService(restored).ImportMergeAsync(backup,ct);
        Assert.Contains("ep1",await restored.GetWatchedEpisodesAsync("s","m","line",ct));
        await restored.ClearHistoryAsync(ct);
        Assert.Empty(await restored.GetWatchedEpisodesAsync("s","m","line",ct));
    }

    [Fact]
    public async Task DeletingMediaHistoryAlsoDeletesOnlyItsWatchedEpisodes()
    {
        var ct=TestContext.Current.CancellationToken;
        await _store.MarkEpisodeWatchedAsync("s","one","l","e",ct);
        await _store.MarkEpisodeWatchedAsync("s","two","l","e",ct);
        await _store.DeleteHistoryAsync("s","one",ct);
        Assert.Empty(await _store.GetWatchedEpisodesAsync("s","one","l",ct));
        Assert.Contains("e",await _store.GetWatchedEpisodesAsync("s","two","l",ct));
    }

    [Fact]
    public async Task BackupRestoresActiveSubscriptionConsistentlyWithCurrentUrl()
    {
        var ct=TestContext.Current.CancellationToken;
        await _store.ImportBackupAtomicAsync(new LibraryBackup
        {
            Subscriptions=[new ConfigSubscription{Kind=ConfigKind.Vod,Url="https://example.com/one",Name="一",Active=true},new ConfigSubscription{Kind=ConfigKind.Vod,Url="https://example.com/two",Name="二"}],
            Preferences=new(){["config_vod"]="https://example.com/two"},
        },ct);
        var selected=Assert.Single(await _store.ListAsync(ConfigKind.Vod,ct), entry=>entry.Active);
        Assert.Equal("https://example.com/two",selected.Url);
        Assert.Equal(selected.Url,_store.GetString("config_vod"));
    }

    [Fact]
    public async Task LegacyVersionOneBackupWithoutNewFieldsStillImports()
    {
        var json="{\"version\":1,\"history\":[],\"favorites\":[],\"subscriptions\":[],\"preferences\":{\"player.volume\":\"50\"}}";
        var parsed=System.Text.Json.JsonSerializer.Deserialize(json,Json.TypeInfo<LibraryBackup>())!;
        Assert.Equal(1,parsed.Version);
        Assert.NotNull(parsed.History);Assert.NotNull(parsed.Preferences);
        await new LibraryBackupService(_store).ImportMergeAsync(json,TestContext.Current.CancellationToken);
        Assert.Equal(50,_store.GetInt("player.volume"));Assert.Empty(await _store.ExportWatchedAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ExplicitNullWatchedListAndMalformedBackupRootRemainRejected()
    {
        var service=new LibraryBackupService(_store);var ct=TestContext.Current.CancellationToken;
        await Assert.ThrowsAsync<InvalidDataException>(()=>service.ImportMergeAsync("{\"version\":1,\"watched\":null,\"history\":[],\"favorites\":[],\"subscriptions\":[],\"preferences\":{}}",ct));
        await Assert.ThrowsAsync<InvalidDataException>(()=>service.ImportMergeAsync("[]",ct));
        Assert.Empty(await _store.GetHistoryAsync(ct:ct));Assert.Empty(await _store.ExportWatchedAsync(ct));
    }

    [Fact]
    public async Task ProgrammeBackupValidatesBeforeTransactionAndRestoresWithPreferences()
    {
        var ct=TestContext.Current.CancellationToken;
        var start=new DateTimeOffset(2026,10,9,9,0,0,TimeSpan.FromHours(8));
        var xml=ProgrammeStore.Serialize([new Programme("c1","节目",start,start.AddHours(1))]);
        _store.Set("live.epg-xml",xml);
        var backup=await new LibraryBackupService(_store).ExportAsync(ct);
        using var restored=new LibraryStore(Path.Combine(_directory,"epg-backup.db"));var service=new LibraryBackupService(restored);
        await service.ImportMergeAsync(backup,ct);Assert.Equal("节目",Assert.Single(XmlTvParser.Parse(restored.GetString("live.epg-xml"))).Title);
        var corrupt=new LibraryBackup{Preferences=new(){["live.epg-xml"]="<html/>",["test-change"]="bad"}};
        await Assert.ThrowsAsync<InvalidDataException>(()=>service.ImportMergeAsync(System.Text.Json.JsonSerializer.Serialize(corrupt,Json.TypeInfo<LibraryBackup>()),ct));
        Assert.Equal("",restored.GetString("test-change"));Assert.Equal(xml,restored.GetString("live.epg-xml"));
    }

    [Fact]
    public void DecimalPreferencesUseInvariantFormatAndRejectNonFiniteValues()
    {
        var original=System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture=System.Globalization.CultureInfo.GetCultureInfo("fr-FR");
            _store.Set("rate",1.5);Assert.Equal("1.5",_store.GetString("rate"));Assert.Equal(1.5,_store.GetDouble("rate"));
            _store.Set("legacy","1,5");Assert.Equal(1.5,_store.GetDouble("legacy"));
            System.Globalization.CultureInfo.CurrentCulture=System.Globalization.CultureInfo.GetCultureInfo("en-US");
            Assert.Equal(1.5,_store.GetDouble("rate"));Assert.Equal(2,_store.GetDouble("legacy",2));
            Assert.Throws<ArgumentOutOfRangeException>(()=>_store.Set("bad",double.NaN));
            _store.Set("bad","Infinity");Assert.Equal(1,_store.GetDouble("bad",1));
        }
        finally{System.Globalization.CultureInfo.CurrentCulture=original;}
    }

    [Fact]
    public async Task HistoryUpsertsBySourceAndMedia()
    {
        var first = MakeHistory("src1", "m1", position: 1000);
        await _store.SaveHistoryAsync(first, ct: TestContext.Current.CancellationToken);
        await _store.SaveHistoryAsync(MakeHistory("src1", "m1", position: 5000), ct: TestContext.Current.CancellationToken);
        await _store.SaveHistoryAsync(MakeHistory("src1", "m2", position: 100), ct: TestContext.Current.CancellationToken);

        var history = await _store.GetHistoryAsync(ct: TestContext.Current.CancellationToken);
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
        await _store.SaveHistoryAsync(MakeHistory("s", "m", position: 42), ct: TestContext.Current.CancellationToken);
        var found = await _store.FindHistoryAsync("s", "m", ct: TestContext.Current.CancellationToken);
        Assert.NotNull(found);
        Assert.Equal(42, found!.PositionMs);
        Assert.Null(await _store.FindHistoryAsync("s", "missing", ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FavoriteToggleAndQuery()
    {
        var entry = new FavoriteEntry
        {
            Kind = FavoriteKind.Vod,
            SourceKey = "s", SourceName = "站点", MediaId = "m", Title = "片名",
        };
        await _store.SetFavoriteAsync(entry, true, ct: TestContext.Current.CancellationToken);
        Assert.True(await _store.IsFavoriteAsync("s", "m", ct: TestContext.Current.CancellationToken));
        var favorites = await _store.GetFavoritesAsync(FavoriteKind.Vod, ct: TestContext.Current.CancellationToken);
        Assert.Single(favorites);

        await _store.SetFavoriteAsync(entry, false, ct: TestContext.Current.CancellationToken);
        Assert.False(await _store.IsFavoriteAsync("s", "m", ct: TestContext.Current.CancellationToken));
        Assert.Empty(await _store.GetFavoritesAsync(FavoriteKind.Vod, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeleteAndClearHistory()
    {
        await _store.SaveHistoryAsync(MakeHistory("s", "a"), ct: TestContext.Current.CancellationToken);
        await _store.SaveHistoryAsync(MakeHistory("s", "b"), ct: TestContext.Current.CancellationToken);
        await _store.DeleteHistoryAsync("s", "a", ct: TestContext.Current.CancellationToken);
        Assert.Single(await _store.GetHistoryAsync(ct: TestContext.Current.CancellationToken));
        await _store.ClearHistoryAsync(ct: TestContext.Current.CancellationToken);
        Assert.Empty(await _store.GetHistoryAsync(ct: TestContext.Current.CancellationToken));
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

        _store.Dispose();
        using var reopened = new LibraryStore(Path.Combine(_directory, "library.db"));
        Assert.Equal("dark", reopened.GetString("theme"));
        Assert.Equal(66, reopened.GetInt("volume"));
        Assert.True(reopened.GetBool("incognito"));
        Assert.Equal(1.5, reopened.GetDouble("rate"));
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

    public void Dispose()
    {
        _store.Dispose();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
