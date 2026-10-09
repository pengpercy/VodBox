using System.Text.Json;
using VodBox.Core;

namespace VodBox.Infrastructure;

public sealed class LibraryBackupService(LibraryStore store)
{
    public async Task<string> ExportAsync(CancellationToken ct = default)
    {
        var backup = new LibraryBackup
        {
            Watched = (await store.ExportWatchedAsync(ct)).ToList(),
            History = (await store.GetHistoryAsync(100000, ct)).ToList(),
            Favorites = (await store.GetFavoritesAsync(FavoriteKind.Vod, ct)).Concat(await store.GetFavoritesAsync(FavoriteKind.Live, ct)).ToList(),
            Subscriptions = (await store.ListAsync(ConfigKind.Vod, ct)).Concat(await store.ListAsync(ConfigKind.Live, ct)).ToList(),
            Preferences = await store.ExportPreferencesAsync(ct),
        };
        return JsonSerializer.Serialize(backup, Json.TypeInfo<LibraryBackup>());
    }

    public async Task ImportMergeAsync(string json, CancellationToken ct = default)
    {
        if (json.Length > 16 * 1024 * 1024) throw new InvalidDataException("备份超过大小限制。");
        using var original=JsonDocument.Parse(json);
        if(original.RootElement.ValueKind!=JsonValueKind.Object)throw new InvalidDataException("备份根结构无效。");
        LibraryBackup backup;
        try
        {
            backup=JsonSerializer.Deserialize(json,Json.TypeInfo<LibraryBackup>())??throw new InvalidDataException("备份为空。");
        }
        catch(JsonException error){throw new InvalidDataException("备份字段格式无效。",error);}
        // 旧v1备份没有watched字段。缺失补空值，显式null仍拒绝。
        if(!original.RootElement.EnumerateObject().Any(property=>property.Name.Equals("watched",StringComparison.OrdinalIgnoreCase)))
            backup=backup with{Watched=[]};
        if (backup.Version != 1 || backup.Watched is null || backup.Watched.Count>100000 || backup.History is null || backup.Favorites is null || backup.Subscriptions is null || backup.Preferences is null ||
            backup.History.Count > 100000 || backup.Favorites.Count > 100000 || backup.Subscriptions.Count > 1000 || backup.Preferences.Count > 10000)
            throw new InvalidDataException("备份版本或记录数量无效。");
        if (backup.Watched.Any(item=>item is null||string.IsNullOrWhiteSpace(item.EpisodeId)) || backup.History.Any(item => item is null || string.IsNullOrEmpty(item.MediaId) || string.IsNullOrEmpty(item.SourceKey)) ||
            backup.Favorites.Any(item => item is null || string.IsNullOrEmpty(item.MediaId) || !Enum.IsDefined(item.Kind)) ||
            backup.Subscriptions.Any(item => item is null || string.IsNullOrWhiteSpace(item.Url) || !Enum.IsDefined(item.Kind)))
            throw new InvalidDataException("备份包含无效记录。");
        if (backup.History.Any(entry => entry.PositionMs < 0 || entry.DurationMs < 0 || !double.IsFinite(entry.Rate) || entry.Rate <= 0) ||
            backup.Preferences.Any(entry => string.IsNullOrWhiteSpace(entry.Key) || entry.Value is null))
            throw new InvalidDataException("备份进度、倍速或偏好无效。");
        if(backup.Preferences.TryGetValue("live.epg-xml",out var programmeXml)&&programmeXml.Length>0)
            _=XmlTvParser.Parse(programmeXml); // 全部校验后才进入数据库事务。
        await store.ImportBackupAtomicAsync(backup, ct);
    }
}
