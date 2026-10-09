using Microsoft.Data.Sqlite;
using VodBox.Core;
using VodBox.Infrastructure;
using Xunit;

namespace VodBox.Tests;

public sealed class LibraryStoreMigrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"vodbox-migration-{Guid.NewGuid():N}");
    private string DatabasePath => Path.Combine(_directory, "library.db");

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task LegacySchemaMigratesBeforeFirstReadAndPreservesDataAcrossReopen(int version)
    {
        CreateLegacyDatabase(version);
        using (var store = new LibraryStore(DatabasePath))
        {
            var history = await store.GetHistoryAsync(ct: TestContext.Current.CancellationToken);
            Assert.Equal(2, history.Count);
            var latest = history[0];
            Assert.Equal("source", latest.SourceKey);
            Assert.Equal("media", latest.MediaId);
            Assert.Equal("latest", latest.Title);
            Assert.Equal("episode-2", latest.EpisodeId);
            Assert.Equal(4567, latest.PositionMs);
            Assert.Equal(DateTimeOffset.Parse("2025-01-02T03:04:05.678+08:00"), latest.UpdatedAt);
            Assert.Equal(version == 3 ? "Source name" : "source", latest.SourceName);
            Assert.Equal(version == 3 ? "https://example.invalid/poster" : null, latest.Poster);
            Assert.Equal("", latest.LineId);
            Assert.Equal(1.0, latest.Rate);

            var favorites = await store.GetFavoritesAsync(FavoriteKind.Vod, TestContext.Current.CancellationToken);
            var favorite = Assert.Single(favorites);
            Assert.Equal("favorite", favorite.MediaId);
            Assert.Equal("Saved title", favorite.Title);
            Assert.Equal(version == 3 ? "Source name" : "source", favorite.SourceName);
            Assert.True(await store.IsFavoriteAsync("source", "favorite", TestContext.Current.CancellationToken));
            store.Set("theme", "dark");
            await store.AddAsync(new ConfigSubscription { Url = "https://example.invalid/config", Name = "Fixture" }, TestContext.Current.CancellationToken);
            var subscription = Assert.Single(await store.ListAsync(ConfigKind.Vod, TestContext.Current.CancellationToken));
            await store.SetActiveAsync(ConfigKind.Vod, subscription.Id, TestContext.Current.CancellationToken);
        }

        using (var database = OpenDatabase())
        {
            Assert.Equal(5L, Scalar(database, "PRAGMA user_version"));
            Assert.Equal(3L, Scalar(database, "SELECT COUNT(*) FROM history_legacy"));
            Assert.Equal(2L, Scalar(database, "SELECT COUNT(*) FROM favorites_legacy"));
            Assert.Equal("https://example.invalid/play", Scalar(database, "SELECT uri FROM history_legacy WHERE title='latest'"));
            if (version >= 2)
                Assert.Equal("resolver", Scalar(database, "SELECT resolver FROM history_legacy WHERE title='latest'"));
        }

        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var store = new LibraryStore(DatabasePath);
            Assert.Equal(2, (await store.GetHistoryAsync(ct: TestContext.Current.CancellationToken)).Count);
            Assert.Single(await store.GetFavoritesAsync(FavoriteKind.Vod, TestContext.Current.CancellationToken));
            Assert.Equal("dark", store.GetString("theme"));
            Assert.True(Assert.Single(await store.ListAsync(ConfigKind.Vod, TestContext.Current.CancellationToken)).Active);
        }

        using (var store = new LibraryStore(DatabasePath))
        {
            await store.SaveHistoryAsync(new HistoryEntry
            {
                SourceKey = "source", SourceName = "Current source", MediaId = "media", Title = "Updated",
                PositionMs = 9999,
            }, TestContext.Current.CancellationToken);
            Assert.Equal(9999, (await store.FindHistoryAsync("source", "media", TestContext.Current.CancellationToken))!.PositionMs);
            await store.ClearHistoryAsync(TestContext.Current.CancellationToken);
            await store.SetFavoriteAsync(new FavoriteEntry
            {
                SourceKey = "source", SourceName = "Current source", MediaId = "favorite", Title = "Saved title",
            }, false, TestContext.Current.CancellationToken);
        }
        using (var store = new LibraryStore(DatabasePath))
        {
            Assert.Empty(await store.GetHistoryAsync(ct: TestContext.Current.CancellationToken));
            Assert.Empty(await store.GetFavoritesAsync(FavoriteKind.Vod, TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task UnversionedCurrentDatabaseKeepsAllFourTablesAndMarksVersion()
    {
        using (var store = new LibraryStore(DatabasePath))
        {
            await store.SaveHistoryAsync(new HistoryEntry
            {
                SourceKey = "s", SourceName = "Source", MediaId = "m", Title = "Title", PositionMs = 123,
            }, TestContext.Current.CancellationToken);
            await store.SetFavoriteAsync(new FavoriteEntry
            {
                SourceKey = "s", SourceName = "Source", MediaId = "m", Title = "Title",
            }, true, TestContext.Current.CancellationToken);
            await store.AddAsync(new ConfigSubscription { Url = "https://example.invalid/config", Name = "Fixture" }, TestContext.Current.CancellationToken);
            store.Set("volume", 42);
        }
        using (var database = OpenDatabase()) Execute(database, "PRAGMA user_version=0");
        using (var store = new LibraryStore(DatabasePath))
        {
            Assert.Equal(123, Assert.Single(await store.GetHistoryAsync(ct: TestContext.Current.CancellationToken)).PositionMs);
            Assert.Single(await store.GetFavoritesAsync(FavoriteKind.Vod, TestContext.Current.CancellationToken));
            Assert.Single(await store.ListAsync(ConfigKind.Vod, TestContext.Current.CancellationToken));
            Assert.Equal(42, store.GetInt("volume"));
        }
        using var reopened = OpenDatabase();
        Assert.Equal(5L, Scalar(reopened, "PRAGMA user_version"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task EmptyLegacySchemaIsReadyBeforeAnyConfigurationIsLoaded(int version)
    {
        CreateLegacyDatabase(version);
        using (var database = OpenDatabase()) Execute(database, "DELETE FROM history; DELETE FROM favorites;");
        using var store = new LibraryStore(DatabasePath);
        Assert.Empty(await store.GetHistoryAsync(ct: TestContext.Current.CancellationToken));
        Assert.Empty(await store.GetFavoritesAsync(FavoriteKind.Vod, TestContext.Current.CancellationToken));
        Assert.Empty(await store.ListAsync(ConfigKind.Vod, TestContext.Current.CancellationToken));
        Assert.Equal("default", store.GetString("theme", "default"));
    }

    [Fact]
    public void UnknownSchemaIsRejectedAndNewTablesAreRolledBack()
    {
        CreateLegacyDatabase(3);
        using (var database = OpenDatabase()) Execute(database, "ALTER TABLE history RENAME COLUMN updated TO unsupported_timestamp;");
        Assert.Throws<InvalidDataException>(() => new LibraryStore(DatabasePath));
        using var reopened = OpenDatabase();
        Assert.Equal(3L, Scalar(reopened, "PRAGMA user_version"));
        Assert.Equal(3L, Scalar(reopened, "SELECT COUNT(*) FROM history"));
        Assert.Equal(0L, Scalar(reopened, "SELECT COUNT(*) FROM sqlite_master WHERE name='history_legacy'"));
    }

    [Fact]
    public void FailedMigrationRollsBackSchemaDataAndVersion()
    {
        CreateLegacyDatabase(3);
        using (var database = OpenDatabase())
            Execute(database, "UPDATE history SET updated='invalid timestamp' WHERE title='latest'");

        Assert.Throws<InvalidDataException>(() => new LibraryStore(DatabasePath));
        using (var database = OpenDatabase())
        {
            Assert.Equal(3L, Scalar(database, "PRAGMA user_version"));
            Assert.Equal(3L, Scalar(database, "SELECT COUNT(*) FROM history"));
            Assert.Equal(2L, Scalar(database, "SELECT COUNT(*) FROM favorites"));
            Assert.Equal(0L, Scalar(database, "SELECT COUNT(*) FROM sqlite_master WHERE name IN ('history_legacy','favorites_legacy','config','prefs','favorite')"));
            Execute(database, "UPDATE history SET updated='2025-01-02T03:04:05.678+08:00' WHERE title='latest'");
        }
        using var recovered = new LibraryStore(DatabasePath);
    }

    [Theory]
    [InlineData("history")]
    [InlineData("favorite")]
    [InlineData("config")]
    [InlineData("prefs")]
    public void CurrentSchemaWithoutPrimaryKeyIsRejectedBeforeVersionCommit(string table)
    {
        using (var store = new LibraryStore(DatabasePath)) store.Set("theme", "dark");
        using (var database = OpenDatabase())
        {
            Execute(database, $"ALTER TABLE {table} RENAME TO fixture_original; CREATE TABLE {table} AS SELECT * FROM fixture_original; DROP TABLE fixture_original; PRAGMA user_version=0;");
        }
        Assert.Throws<InvalidDataException>(() => new LibraryStore(DatabasePath));
        using var reopened = OpenDatabase();
        Assert.Equal(0L, Scalar(reopened, "PRAGMA user_version"));
        Assert.Equal("dark", Scalar(reopened, "SELECT value FROM prefs WHERE key='theme'"));
    }

    [Fact]
    public void FutureSchemaIsRejectedWithoutChangingDatabase()
    {
        CreateLegacyDatabase(3);
        using (var database = OpenDatabase()) Execute(database, "PRAGMA user_version=99");
        Assert.Throws<InvalidDataException>(() => new LibraryStore(DatabasePath));
        using var reopened = OpenDatabase();
        Assert.Equal(99L, Scalar(reopened, "PRAGMA user_version"));
        Assert.Equal(3L, Scalar(reopened, "SELECT COUNT(*) FROM history"));
    }

    private void CreateLegacyDatabase(int version)
    {
        Directory.CreateDirectory(_directory);
        using var database = OpenDatabase();
        // Historical schemas: 94f08a6 (v1), ed2cc55 (v2), cbe77ac (v3).
        Execute(database, """
            CREATE TABLE history(config TEXT, source TEXT, media TEXT, episode TEXT,
                title TEXT NOT NULL, uri TEXT NOT NULL, position INTEGER NOT NULL, updated TEXT NOT NULL,
                PRIMARY KEY(config,source,media,episode));
            CREATE TABLE favorites(config TEXT, source TEXT, media TEXT, title TEXT NOT NULL,
                PRIMARY KEY(config,source,media));
            INSERT INTO history VALUES('config-a','source','media','episode-1','older','https://example.invalid/old',1234,'2025-01-02T04:00:00.000+09:00');
            INSERT INTO history VALUES('config-b','source','media','episode-2','latest','https://example.invalid/play',4567,'2025-01-02T03:04:05.678+08:00');
            INSERT INTO history VALUES('config-a','other','other-media','episode-1','other','https://example.invalid/other',89,'2024-01-01T00:00:00.000+00:00');
            INSERT INTO favorites VALUES('config-a','source','favorite','Saved title');
            INSERT INTO favorites VALUES('config-b','source','favorite','Saved title');
            """);
        if (version >= 2)
            Execute(database, """
                ALTER TABLE history ADD COLUMN resolution INTEGER NOT NULL DEFAULT 0;
                ALTER TABLE history ADD COLUMN resolver TEXT;
                UPDATE history SET resolution=1080, resolver='resolver';
                """);
        if (version >= 3)
            Execute(database, """
                ALTER TABLE history ADD COLUMN poster TEXT;
                ALTER TABLE history ADD COLUMN source_name TEXT;
                ALTER TABLE favorites ADD COLUMN poster TEXT;
                ALTER TABLE favorites ADD COLUMN source_name TEXT;
                UPDATE history SET poster='https://example.invalid/poster', source_name='Source name';
                UPDATE favorites SET poster='https://example.invalid/poster', source_name='Source name';
                """);
        Execute(database, $"PRAGMA user_version={version}; PRAGMA journal_mode=WAL;");
    }

    private SqliteConnection OpenDatabase()
    {
        var database = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath, Pooling = false,
        }.ToString());
        database.Open();
        return database;
    }

    private static object? Scalar(SqliteConnection database, string sql)
    {
        using var command = database.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static void Execute(SqliteConnection database, string sql)
    {
        using var command = database.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
