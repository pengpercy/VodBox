namespace VodBox.Core;

/// <summary>内容源提供器：豆瓣浏览等分类/分页内容的统一访问契约。</summary>
public interface IContentSource
{
    string Key { get; }
    string Name { get; }
    Task<IReadOnlyList<Category>> GetCategoriesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<FilterGroup>> GetFiltersAsync(string categoryId,CancellationToken ct=default)=>Task.FromResult<IReadOnlyList<FilterGroup>>([]);
    Task<MediaPage> GetHomeAsync(CancellationToken ct = default);
    Task<MediaPage> GetItemsAsync(string categoryId, int page, IReadOnlyDictionary<string, string>? filters, CancellationToken ct = default);
    Task<MediaDetail> GetDetailAsync(string mediaId, CancellationToken ct = default);
    Task<MediaPage> SearchAsync(string query, int page, CancellationToken ct = default);
}

/// <summary>播放引擎抽象（libmpv 实现；留出后续内核扩展位）。</summary>
public interface IPlaybackEngine : IAsyncDisposable
{
    PlaybackSnapshot Snapshot { get; }
    event EventHandler<PlaybackEvent>? StateChanged;
    Task OpenAsync(PlaybackRequest request, long sessionId, CancellationToken ct = default);
    Task PlayAsync(CancellationToken ct = default);
    Task PauseAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
    Task SeekToAsync(TimeSpan position, CancellationToken ct = default);
    Task SeekByAsync(TimeSpan delta, CancellationToken ct = default);
    Task SetRateAsync(double rate, CancellationToken ct = default);
    Task SetVolumeAsync(int volume, CancellationToken ct = default);
    /// <summary>null 恢复媒体原始比例，其余为宽/高比值。</summary>
    Task SetAspectRatioAsync(double? ratio, CancellationToken ct = default) =>
        Task.FromException(new NotSupportedException("当前引擎不支持画面比例切换。"));
    /// <summary>加载本地外挂字幕；会话编号防止文件选择期间切集串入新媒体。</summary>
    Task LoadSubtitleAsync(string path, long sessionId, CancellationToken ct = default) =>
        Task.FromException(new NotSupportedException("当前引擎不支持外挂字幕。"));
    Task SetSubtitleStyleAsync(double delaySeconds, int fontSize, CancellationToken ct = default) =>
        Task.FromException(new NotSupportedException("当前引擎不支持字幕样式。"));
    IReadOnlyList<MediaTrack> GetTracks(TrackKind kind);
    Task SelectTrackAsync(TrackKind kind, string trackId, CancellationToken ct = default);
    /// <summary>统计信息面板的一次采样；媒体未加载或引擎不支持时返回 null。</summary>
    Task<PlaybackStats?> GetStatsAsync(CancellationToken ct = default) => Task.FromResult<PlaybackStats?>(null);
    /// <summary>把当前画面（不含字幕与界面）保存为图片文件。</summary>
    Task SaveScreenshotAsync(string path, CancellationToken ct = default) =>
        Task.FromException(new NotSupportedException("当前引擎不支持截图。"));
}

/// <summary>本地库存储（历史/收藏/配置订阅/偏好）。</summary>
public interface ILibraryStore
{
    Task SaveHistoryAsync(HistoryEntry entry, CancellationToken ct = default);
    Task MarkEpisodeWatchedAsync(string sourceKey, string mediaId, string lineId, string episodeId, CancellationToken ct = default) => Task.CompletedTask;
    Task<IReadOnlySet<string>> GetWatchedEpisodesAsync(string sourceKey, string mediaId, string lineId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlySet<string>>(new HashSet<string>());
    Task<IReadOnlyList<HistoryEntry>> GetHistoryAsync(int limit = 200, CancellationToken ct = default);
    Task DeleteHistoryAsync(string sourceKey, string mediaId, CancellationToken ct = default);
    Task ClearHistoryAsync(CancellationToken ct = default);
    Task SetFavoriteAsync(FavoriteEntry entry, bool favorite, CancellationToken ct = default);
    Task<IReadOnlyList<FavoriteEntry>> GetFavoritesAsync(FavoriteKind kind, CancellationToken ct = default);
    Task<HistoryEntry?> FindHistoryAsync(string sourceKey, string mediaId, CancellationToken ct = default);
    Task<bool> IsFavoriteAsync(string sourceKey, string mediaId, CancellationToken ct = default);
}

/// <summary>配置订阅记录（Config 表等价物）。</summary>
public sealed record ConfigSubscription
{
    public long Id { get; init; }
    public required string Url { get; init; }
    public required string Name { get; init; }
    public ConfigKind Kind { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    public bool Active { get; init; }
}

public interface IConfigStore
{
    Task<IReadOnlyList<ConfigSubscription>> ListAsync(ConfigKind kind, CancellationToken ct = default);
    Task AddAsync(ConfigSubscription subscription, CancellationToken ct = default);
    Task RemoveAsync(long id, CancellationToken ct = default);
    Task SetActiveAsync(ConfigKind kind, long id, CancellationToken ct = default);
}

/// <summary>键值偏好（对应 Android SharedPreferences）。</summary>
public interface IPreferences
{
    string GetString(string key, string fallback = "");
    int GetInt(string key, int fallback = 0);
    bool GetBool(string key, bool fallback = false);
    double GetDouble(string key, double fallback = 0);
    void Set(string key, string value);
    void Set(string key, int value);
    void Set(string key, bool value);
    void Set(string key, double value);
}

