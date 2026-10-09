namespace VodBox.Core;

/// <summary>可移植逻辑备份；导入采用合并，不删除现有记录。</summary>
public sealed record LibraryBackup
{
    [System.Text.Json.Serialization.JsonConstructor]
    public LibraryBackup() { }
    public int Version { get; init; } = 1;
    public List<WatchedEpisode> Watched { get; init; } = [];
    public List<HistoryEntry> History { get; init; } = [];
    public List<FavoriteEntry> Favorites { get; init; } = [];
    public List<ConfigSubscription> Subscriptions { get; init; } = [];
    public Dictionary<string, string> Preferences { get; init; } = [];
}

public sealed record WatchedEpisode(string SourceKey,string MediaId,string LineId,string EpisodeId,DateTimeOffset WatchedAt);
