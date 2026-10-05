using System.Text.Json;
using VodBox.Core;

namespace VodBox.Infrastructure;

/// <summary>Portable data only: no media, caches, executables or downloaded workers.</summary>
public sealed class BackupService(LibraryStore library, ConfigurationRepository configurations, PreferencesStore preferences)
{
    public const int MaximumBytes = 64 * 1024 * 1024;
    public async Task ExportAsync(string path, AppPreferences currentPreferences, CancellationToken token = default)
    {
        var backup = new PortableBackup { Preferences = currentPreferences, Library = await Task.Run(() => library.ExportSnapshot(token), token).ConfigureAwait(false) };
        foreach (var entry in await configurations.ListAsync(token).ConfigureAwait(false)) backup.Configurations.Add(new(entry, await configurations.LoadAsync(entry.Id, token).ConfigureAwait(false)));
        string json = await Task.Run(() => { Validate(backup); return JsonSerializer.Serialize(backup, VodBoxJson.Default.PortableBackup); }, token).ConfigureAwait(false);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaximumBytes) throw new InvalidDataException("备份超过 64 MiB 限制。");
        await AtomicFile.WriteAsync(path, json, token).ConfigureAwait(false);
    }
    public async Task<PortableBackup> ReadAsync(string path, CancellationToken token = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        if (stream.Length > MaximumBytes) throw new InvalidDataException("备份超过 64 MiB 限制。");
        // The length bound also applies to a file that is growing during the read.
        using var buffer = new MemoryStream(); byte[] bytes = new byte[65536]; int count;
        while ((count = await stream.ReadAsync(bytes, token).ConfigureAwait(false)) > 0)
        { if (buffer.Length + count > MaximumBytes) throw new InvalidDataException("备份超过 64 MiB 限制。"); await buffer.WriteAsync(bytes.AsMemory(0, count), token).ConfigureAwait(false); }
        buffer.Position = 0;
        using var document = await JsonDocument.ParseAsync(buffer, cancellationToken: token).ConfigureAwait(false);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("format", out var format) || format.ValueKind != JsonValueKind.String || format.GetString() != "VodBox.Backup" || !root.TryGetProperty("version", out _)) throw new InvalidDataException("文件不是 VodBox 数据备份。");
        return await Task.Run(() => { var backup = root.Deserialize(VodBoxJson.Default.PortableBackup) ?? throw new InvalidDataException("备份为空。"); Validate(backup); return backup; }, token).ConfigureAwait(false);
    }
    public async Task ImportAsync(PortableBackup backup, CancellationToken token = default)
    {
        await Task.Run(() => Validate(backup), token).ConfigureAwait(false);
        foreach (var entry in backup.Configurations) await configurations.SaveAsync(entry.Configuration, entry.Metadata.Location, token).ConfigureAwait(false);
        await Task.Run(() => library.MergeSnapshot(backup.Library, token), token).ConfigureAwait(false);
        await preferences.SaveAsync(backup.Preferences, token).ConfigureAwait(false);
    }
    private static void Validate(PortableBackup backup)
    {
        if (backup.Format != "VodBox.Backup" || backup.Version != 1) throw new InvalidDataException("不支持此备份版本。");
        if (backup.Preferences is null || backup.Configurations is null || backup.Library is null || backup.Library.History is null || backup.Library.Favorites is null) throw new InvalidDataException("备份缺少必要字段。");
        if (backup.Configurations.Count > 1024 || backup.Library.History.Count > 100000 || backup.Library.Favorites.Count > 100000) throw new InvalidDataException("备份条目过多。");
        if (backup.Preferences.PlaybackEngineMode is not (PlaybackEngineMode.Automatic or PlaybackEngineMode.LibVlc or PlaybackEngineMode.Mpv)) throw new InvalidDataException("备份播放内核偏好无效。");
        if (!double.IsFinite(backup.Preferences.DanmakuFontSize) || !double.IsFinite(backup.Preferences.DanmakuOpacity) || !double.IsFinite(backup.Preferences.DanmakuCoverage) || !double.IsFinite(backup.Preferences.Volume) || !double.IsFinite(backup.Preferences.Rate) || backup.Preferences.Theme is not ("Dark" or "Light" or "System")) throw new InvalidDataException("备份偏好无效。");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in backup.Configurations)
        {
            if (entry is null || entry.Metadata is null || entry.Configuration is null || entry.Metadata.Id != entry.Configuration.Id || !ids.Add(entry.Metadata.Id) || string.IsNullOrWhiteSpace(entry.Metadata.Location)) throw new InvalidDataException("备份配置身份无效。");
            ConfigLoader.Validate(entry.Configuration);
        }
        foreach (var entry in backup.Library.History)
            if (entry is null || InvalidKey(entry.ConfigId, entry.SourceId, entry.MediaId) || entry.EpisodeId is null || entry.Title is null || string.IsNullOrWhiteSpace(entry.Uri) || entry.PositionMs < 0 || !Enum.IsDefined(entry.ResolutionKind)) throw new InvalidDataException("备份历史无效。");
        foreach (var entry in backup.Library.Favorites)
            if (entry is null || InvalidKey(entry.ConfigId, entry.SourceId, entry.MediaId) || entry.Title is null) throw new InvalidDataException("备份收藏无效。");
    }
    private static bool InvalidKey(string config, string source, string media) => string.IsNullOrWhiteSpace(config) || string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(media);
}
