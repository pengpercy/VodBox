namespace VodBox.Core;

public sealed record AppPreferences
{
    public string? LastConfigId { get; set; }
    public string? LastSourceId { get; set; }
    public double Volume { get; set; } = 80;
    public double Rate { get; set; } = 1;
    public bool AutoNext { get; set; } = true;
    public bool ResumePlayback { get; set; } = true;
    public int SkipIntroSeconds { get; set; }
    public int SkipOutroSeconds { get; set; }
    public string Theme { get; set; } = "Dark";
    public int AudioDelayMs { get; set; }
    public int SubtitleDelayMs { get; set; }
    public string? LastLiveConfigId { get; set; }
    public string? LastLiveSourceId { get; set; }
    public string? LastLiveChannelId { get; set; }
    public bool AutoLiveFallback { get; set; } = true;
    public bool ResumeLiveOnStartup { get; set; }
    public bool DanmakuEnabled { get; set; } = true;
    public double DanmakuFontSize { get; set; } = 24;
    public double DanmakuOpacity { get; set; } = .85;
    public double DanmakuCoverage { get; set; } = .5;
    public int DanmakuDelayMs { get; set; }
    public bool LargeScreen { get; set; }
}

public sealed record SavedConfiguration(string Id, string Location, DateTimeOffset ImportedAt);
public sealed record SearchHit(SourceDefinition Source, MediaItem Item)
{
    public string Title => Item.Title;
    public string SourceName => Source.Name;
}
public sealed record SearchBatch(SourceDefinition Source, IReadOnlyList<SearchHit> Hits, string? Error = null);
