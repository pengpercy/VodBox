using VodBox.Application;
using VodBox.Core;
using Xunit;

namespace VodBox.Tests;

public sealed class PlaylistTests
{
    [Fact]
    public async Task NextEpisodeRetainsPlayingSourceAndStopsAtPlaylistBoundary()
    {
        var engine = new Engine(); await using var coordinator = new PlaybackCoordinator(engine, new Store());
        var factory = new Factory(); var playlist = new PlaylistSession(coordinator, factory);
        var source = new SourceDefinition { Id = "original", Name = "源" };
        var line = new PlaybackLine("main", "主线路", [new("one", "第一集"), new("two", "第二集")]);
        var detail = new MediaDetail(new("movie", "影片"), "", [line]);
        await playlist.PlayAsync(source, detail, line, line.Episodes[0], "config", 15000);
        Assert.True(playlist.HasNext); Assert.False(playlist.HasPrevious);
        Assert.True(await playlist.MoveAsync(1, 12000));
        Assert.Equal("https://media.example/original/two", engine.Requests[^1].Uri);
        Assert.Equal(12000, engine.Requests[^1].StartPositionMs);
        Assert.False(await playlist.MoveAsync(1)); Assert.True(playlist.HasPrevious);
        await coordinator.StopAsync(); Assert.False(await playlist.MoveAsync(-1));
        Assert.Equal(2, factory.Disposed);
    }
    private sealed class Factory : IProviderFactory
    {
        public int Disposed;
        public IContentProvider Create(SourceDefinition source) => new Provider(this, source.Id);
        private sealed class Provider(Factory factory, string id) : IContentProvider
        {
            public string SourceId => id;
            public Task<PlaybackRequest> ResolvePlaybackAsync(string media, string episode, CancellationToken token) => Task.FromResult(new PlaybackRequest { Uri = $"https://media.example/{id}/{episode}" });
            public ValueTask DisposeAsync() { factory.Disposed++; return ValueTask.CompletedTask; }
            public Task<IReadOnlyList<Category>> GetCategoriesAsync(CancellationToken token) => throw new NotSupportedException();
            public Task<MediaPage> GetItemsAsync(string? category, string? cursor, CancellationToken token) => throw new NotSupportedException();
            public Task<MediaPage> SearchAsync(string query, CancellationToken token) => throw new NotSupportedException();
            public Task<MediaDetail> GetDetailAsync(string media, CancellationToken token) => throw new NotSupportedException();
        }
    }
    private sealed class Store : ILibraryStore
    {
        public Task SaveHistoryAsync(HistoryEntry entry, CancellationToken token = default) => Task.CompletedTask;
        public Task<IReadOnlyList<HistoryEntry>> GetHistoryAsync(CancellationToken token = default) => Task.FromResult<IReadOnlyList<HistoryEntry>>([]);
        public Task SetFavoriteAsync(FavoriteEntry entry, bool favorite, CancellationToken token = default) => Task.CompletedTask;
        public Task<IReadOnlyList<FavoriteEntry>> GetFavoritesAsync(CancellationToken token = default) => Task.FromResult<IReadOnlyList<FavoriteEntry>>([]);
    }
    private sealed class Engine : IPlaybackEngine
    {
        public List<PlaybackRequest> Requests { get; } = [];
        public PlaybackSnapshot Snapshot => new(PlaybackState.Idle, TimeSpan.Zero, TimeSpan.Zero, true);
        public event EventHandler<PlaybackEvent>? StateChanged { add { } remove { } }
        public Task OpenAsync(PlaybackRequest request, long sessionId, CancellationToken token) { Requests.Add(request); return Task.CompletedTask; }
        public Task PlayAsync(CancellationToken token) => Task.CompletedTask;
        public Task PauseAsync(CancellationToken token) => Task.CompletedTask;
        public Task StopAsync(CancellationToken token) => Task.CompletedTask;
        public Task SeekAsync(TimeSpan position, CancellationToken token) => Task.CompletedTask;
        public Task SetRateAsync(double rate, CancellationToken token) => Task.CompletedTask;
        public Task SetVolumeAsync(double volume, CancellationToken token) => Task.CompletedTask;
        public IReadOnlyList<MediaTrack> GetTracks(TrackKind kind) => [];
        public Task SelectTrackAsync(TrackKind kind, string id, CancellationToken token) => Task.CompletedTask;
        public Task AddSubtitleAsync(string path, CancellationToken token) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
