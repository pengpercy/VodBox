using VodBox.Infrastructure;

namespace VodBox.Desktop;

public partial class MainViewModel
{
    private readonly SemaphoreSlim _backupGate = new(1, 1);
    public Task ExportBackupAsync(string path) => RunAsync(async () =>
    {
        if (_store is not LibraryStore store) return;
        await _backupGate.WaitAsync(_lifetime.Token);
        try
        {
            await _coordinator.SaveProgressAsync();
            await new BackupService(store, _configurations, _preferencesStore).ExportAsync(path, CapturePreferences(), _lifetime.Token);
            Status = "备份已保存：" + path;
        }
        finally { _backupGate.Release(); }
    });
    public Task ImportBackupAsync(string path) => RunAsync(async () =>
    {
        if (_store is not LibraryStore store) return;
        await _backupGate.WaitAsync(_lifetime.Token);
        try
        {
            var service = new BackupService(store, _configurations, _preferencesStore);
            var backup = await service.ReadAsync(path, _lifetime.Token);
            await _coordinator.SaveProgressAsync();
            string recovery = Path.Combine(AppPaths.DataDirectory, "backups", $"before-import-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.vodbox-backup.json");
            await service.ExportAsync(recovery, CapturePreferences(), _lifetime.Token);
            Status = "正在导入；导入前备份：" + recovery;
            _preferencesSave?.Cancel(); await _preferencesSaveTask;
            _initialized = false; _browse?.Cancel(); _queryCancellation?.Cancel(); _aggregateCancellation?.Cancel(); _detailCancellation?.Cancel();
            _playingChannel = null; _playlist.Clear(); await _coordinator.StopAsync();
            await service.ImportAsync(backup, _lifetime.Token);
            await InitializeAsync();
            History.Clear(); foreach (var item in await _store.GetHistoryAsync(_lifetime.Token)) History.Add(item);
            Favorites.Clear(); foreach (var item in await _store.GetFavoritesAsync(_lifetime.Token)) Favorites.Add(item);
            Status = "备份已合并，偏好已恢复。导入前备份：" + recovery;
        }
        finally { _initialized = true; _backupGate.Release(); }
    });
}
