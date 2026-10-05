namespace VodBox.Core;

public sealed record BackupConfiguration(SavedConfiguration Metadata, VodBoxConfig Configuration);
public sealed record LibrarySnapshot
{
    public List<HistoryEntry> History { get; set; } = [];
    public List<FavoriteEntry> Favorites { get; set; } = [];
}
public sealed record PortableBackup
{
    public string Format { get; set; } = "VodBox.Backup";
    public int Version { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public AppPreferences Preferences { get; set; } = new();
    public List<BackupConfiguration> Configurations { get; set; } = [];
    public LibrarySnapshot Library { get; set; } = new();
}
