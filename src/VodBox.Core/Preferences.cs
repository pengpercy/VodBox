namespace VodBox.Core;

public sealed record AppPreferences
{
    public string? LastConfigId { get; init; }
    public string? LastSourceId { get; init; }
    public double Volume { get; init; } = 80;
    public double Rate { get; init; } = 1;
    public bool AutoNext { get; init; } = true;
    public bool ResumePlayback { get; init; } = true;
    public int SkipIntroSeconds { get; init; }
    public int SkipOutroSeconds { get; init; }
    public string Theme { get; init; } = "Dark";
    public bool LargeScreen { get; init; }
}

public sealed record SavedConfiguration(string Id, string Location, DateTimeOffset ImportedAt);
public sealed record SearchHit(SourceDefinition Source, MediaItem Item)
{
    public string Title => Item.Title;
    public string SourceName => Source.Name;
}
public sealed record SearchBatch(SourceDefinition Source, IReadOnlyList<SearchHit> Hits, string? Error = null);
