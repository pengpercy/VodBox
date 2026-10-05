using System.Text.Json;
using VodBox.Core;
using VodBox.Infrastructure;
using Xunit;

namespace VodBox.Tests;

public sealed class BackupTests
{
    [Fact]
    public async Task PortableBackupRoundTripsAllHistoryAndMergesNewerRecords()
    {
        string root = Path.Combine(Path.GetTempPath(), "vodbox-backup-" + Guid.NewGuid().ToString("N"));
        try
        {
            var source = new LibraryStore(Path.Combine(root, "source.db"));
            var configs = new ConfigurationRepository(Path.Combine(root, "source-config"));
            var preferences = new PreferencesStore(Path.Combine(root, "source-preferences.json"));
            await configs.SaveAsync(new() { Id = "fixture" }, "https://example.com/config.json");
            var now = DateTimeOffset.UtcNow;
            for (int i = 0; i < 205; i++) await source.SaveHistoryAsync(new("fixture", "local", "movie" + i, "", "Movie", "https://example.com/media.mp4", i, now));
            await source.SetFavoriteAsync(new("fixture", "local", "favorite", "Favorite"), true);
            var service = new BackupService(source, configs, preferences);
            string file = Path.Combine(root, "portable.json");
            await service.ExportAsync(file, new() { Theme = "Light", Volume = 42 });
            var backup = await service.ReadAsync(file); Assert.Equal(205, backup.Library.History.Count);
            var target = new LibraryStore(Path.Combine(root, "target.db"));
            await target.SaveHistoryAsync(new("fixture", "local", "movie0", "", "Newer", "https://example.com/new.mp4", 999, now.AddDays(1).ToOffset(TimeSpan.FromHours(8))));
            await target.SetFavoriteAsync(new("other", "source", "existing", "Existing"), true);
            var importedConfigs = new ConfigurationRepository(Path.Combine(root, "target-config"));
            var importedPreferences = new PreferencesStore(Path.Combine(root, "target-preferences.json"));
            await new BackupService(target, importedConfigs, importedPreferences).ImportAsync(backup);
            var snapshot = target.ExportSnapshot(); Assert.Equal(205, snapshot.History.Count); Assert.Equal(2, snapshot.Favorites.Count);
            Assert.Equal(999, snapshot.History.Single(x => x.MediaId == "movie0").PositionMs);
            Assert.Equal("fixture", (await importedConfigs.LoadAsync("fixture")).Id);
            Assert.Equal("Light", (await importedPreferences.LoadAsync()).Theme);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Fact]
    public async Task InvalidBackupIsRejectedBeforeAnyConfigurationWrite()
    {
        string root = Path.Combine(Path.GetTempPath(), "vodbox-backup-invalid-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LibraryStore(Path.Combine(root, "library.db")); var configs = new ConfigurationRepository(Path.Combine(root, "configs"));
            var service = new BackupService(store, configs, new(Path.Combine(root, "preferences.json")));
            var invalid = new PortableBackup { Configurations = [new(new("wrong", "fixture.json", DateTimeOffset.UtcNow), new() { Id = "right" })] };
            await Assert.ThrowsAsync<InvalidDataException>(() => service.ImportAsync(invalid)); Assert.Empty(await configs.ListAsync());
            string file = Path.Combine(root, "ordinary.json"); await File.WriteAllTextAsync(file, "{}");
            await Assert.ThrowsAsync<InvalidDataException>(() => service.ReadAsync(file));
            invalid = new() { Version = 999 }; await File.WriteAllTextAsync(file, JsonSerializer.Serialize(invalid, VodBoxJson.Default.PortableBackup));
            await Assert.ThrowsAsync<InvalidDataException>(() => service.ReadAsync(file));
            await File.WriteAllTextAsync(file, "{\"format\":\"VodBox.Backup\",\"version\":1,\"library\":null}");
            await Assert.ThrowsAsync<InvalidDataException>(() => service.ReadAsync(file));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Fact]
    public async Task OversizedBackupIsRejectedWithoutReadingItsContents()
    {
        string root = Path.Combine(Path.GetTempPath(), "vodbox-backup-size-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LibraryStore(Path.Combine(root, "library.db"));
            var service = new BackupService(store, new(Path.Combine(root, "configs")), new(Path.Combine(root, "preferences.json")));
            string file = Path.Combine(root, "oversized.json");
            using (var stream = File.Create(file)) stream.SetLength(BackupService.MaximumBytes + 1L);
            await Assert.ThrowsAsync<InvalidDataException>(() => service.ReadAsync(file));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Fact]
    public void FailedMergeRollsBackEarlierInsertedRecords()
    {
        string root = Path.Combine(Path.GetTempPath(), "vodbox-backup-rollback-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LibraryStore(Path.Combine(root, "library.db"));
            Assert.Throws<InvalidOperationException>(() => store.MergeSnapshot(new()
            {
                Favorites = [new("c", "s", "valid", "Valid"), new("c", "s", "invalid", null!)]
            }));
            Assert.Empty(store.ExportSnapshot().Favorites);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Fact]
    public async Task CancelledMergeLeavesDatabaseUnchanged()
    {
        string root = Path.Combine(Path.GetTempPath(), "vodbox-backup-cancel-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LibraryStore(Path.Combine(root, "library.db"));
            await store.SetFavoriteAsync(new("config", "source", "existing", "Existing"), true);
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            Assert.Throws<OperationCanceledException>(() => store.MergeSnapshot(new() { Favorites = [new("config", "source", "new", "New")] }, cancellation.Token));
            Assert.Single(store.ExportSnapshot().Favorites);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
