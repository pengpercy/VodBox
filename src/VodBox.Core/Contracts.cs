namespace VodBox.Core;

/// <summary>内容源提供器：所有站点（MacCMS/脚本/本地）的统一访问契约。</summary>
public interface IContentSource
{
    string Key { get; }
    string Name { get; }
    Task<IReadOnlyList<Category>> GetCategoriesAsync(CancellationToken ct = default);
    Task<MediaPage> GetHomeAsync(CancellationToken ct = default);
    Task<MediaPage> GetItemsAsync(string categoryId, int page, IReadOnlyDictionary<string, string>? filters, CancellationToken ct = default);
    Task<MediaDetail> GetDetailAsync(string mediaId, CancellationToken ct = default);
    Task<MediaPage> SearchAsync(string query, int page, CancellationToken ct = default);
}

/// <summary>播放地址解析器（对应 TVBox Parse 的 type 1 JSON 接口）。</summary>
public interface IPlayResolver
{
    string Name { get; set; }
    Task<PlaybackRequest?> ResolveAsync(PlaybackRequest request, CancellationToken ct = default);
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
    IReadOnlyList<MediaTrack> GetTracks(TrackKind kind);
    Task SelectTrackAsync(TrackKind kind, string trackId, CancellationToken ct = default);
}

/// <summary>本地库存储（历史/收藏/配置订阅/偏好）。</summary>
public interface ILibraryStore
{
    Task SaveHistoryAsync(HistoryEntry entry, CancellationToken ct = default);
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

/// <summary>
/// 需要即时解析播放地址的内容源（对应 TVBox 的 csp_ 爬虫类型站点）。
/// 这类站点的媒体地址带签名/时效（如哔哩哔哩 CDN），不能预置在 <see cref="Episode.Uri"/>，
/// 必须在用户点播时现取。不实现本接口的源（如 MacCMS）走 <see cref="Episode.Uri"/> 直连。
/// </summary>
public interface IResolvingContentSource : IContentSource
{
    /// <summary>解析某线路某一集的最终播放请求（含请求头、弹幕地址、续播位置）。</summary>
    Task<PlaybackRequest> ResolvePlaybackAsync(string mediaId, string episodeId, CancellationToken ct = default);
}
