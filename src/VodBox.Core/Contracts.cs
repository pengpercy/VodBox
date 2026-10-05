namespace VodBox.Core;

public interface IProviderFactory
{
    IContentProvider Create(SourceDefinition source);
}

public interface IPlaybackResolver
{
    Task<PlaybackRequest> ResolveAsync(PlaybackRequest request, CancellationToken cancellationToken);
}

public interface IContentProvider : IAsyncDisposable
{
    string SourceId { get; }
    Task<IReadOnlyList<Category>> GetCategoriesAsync(CancellationToken cancellationToken);
    Task<MediaPage> GetItemsAsync(string? categoryId, string? cursor, CancellationToken cancellationToken);
    Task<MediaPage> GetItemsFilteredAsync(string? categoryId, string? cursor, IReadOnlyDictionary<string, string> filters, CancellationToken cancellationToken) =>
        filters.Count == 0 ? GetItemsAsync(categoryId, cursor, cancellationToken) : throw new NotSupportedException("此内容源尚未实现筛选。");
    Task<MediaPage> SearchAsync(string query, CancellationToken cancellationToken);
    Task<MediaPage> SearchPageAsync(string query, string? cursor, CancellationToken cancellationToken) =>
        cursor is null ? SearchAsync(query, cancellationToken) : Task.FromResult(new MediaPage([]));
    Task<MediaDetail> GetDetailAsync(string mediaId, CancellationToken cancellationToken);
    Task<PlaybackRequest> ResolvePlaybackAsync(string mediaId, string episodeId, CancellationToken cancellationToken);
}

public interface IPlaybackEngine : IAsyncDisposable
{
    PlaybackSnapshot Snapshot { get; }
    event EventHandler<PlaybackEvent>? StateChanged;
    Task OpenAsync(PlaybackRequest request, long sessionId, CancellationToken cancellationToken);
    Task PlayAsync(CancellationToken cancellationToken);
    Task PauseAsync(CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
    Task SeekAsync(TimeSpan position, CancellationToken cancellationToken);
    Task SetRateAsync(double rate, CancellationToken cancellationToken);
    Task SetVolumeAsync(double volume, CancellationToken cancellationToken);
    IReadOnlyList<MediaTrack> GetTracks(TrackKind kind);
    Task SelectTrackAsync(TrackKind kind, string trackId, CancellationToken cancellationToken);
    Task AddSubtitleAsync(string path, CancellationToken cancellationToken);
}

public interface IPlaybackAdvancedControls
{
    Task SetAudioDelayAsync(int milliseconds, CancellationToken cancellationToken);
    Task SetSubtitleDelayAsync(int milliseconds, CancellationToken cancellationToken);
    Task TakeSnapshotAsync(string path, CancellationToken cancellationToken);
}

public interface ILibraryStore
{
    Task SaveHistoryAsync(HistoryEntry entry, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<HistoryEntry>> GetHistoryAsync(CancellationToken cancellationToken = default);
    Task DeleteHistoryAsync(HistoryEntry? entry, CancellationToken cancellationToken = default);
    Task SetFavoriteAsync(FavoriteEntry entry, bool favorite, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<FavoriteEntry>> GetFavoritesAsync(CancellationToken cancellationToken = default);
}
