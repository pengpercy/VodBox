using VodBox.Application;
using VodBox.Core;
using Xunit;

namespace VodBox.Tests;

public sealed class PlaybackCoordinatorTests
{
    [Fact]
    public async Task NewResolutionCancelsOldRequestBeforeItCanOpen()
    {
        var engine = new FakeEngine();
        await using var coordinator = new PlaybackCoordinator(engine, new FakeStore());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = coordinator.PlayAsync(async token =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return new() { Uri = "https://example.com/old" };
        }, "config");
        await entered.Task;
        await coordinator.PlayAsync(_ => Task.FromResult(new PlaybackRequest { Uri = "https://example.com/new" }), "config");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Equal(["https://example.com/new"], engine.Opened);
    }

    [Fact]
    public async Task IncognitoAndLiveDoNotPersistHistory()
    {
        var engine = new FakeEngine(); var store = new FakeStore();
        await using var coordinator = new PlaybackCoordinator(engine, store);
        await coordinator.PlayAsync(_ => Task.FromResult(new PlaybackRequest { Uri = "https://example.com/a" }), "config");
        engine.Snapshot = engine.Snapshot with { Position = TimeSpan.FromSeconds(30) };
        coordinator.Incognito = true;
        await coordinator.SaveProgressAsync();
        Assert.Empty(store.History);
        coordinator.Incognito = false;
        await coordinator.SaveProgressAsync();
        Assert.Equal(30000, Assert.Single(store.History).PositionMs);
        await coordinator.PlayAsync(_ => Task.FromResult(new PlaybackRequest { Uri = "https://example.com/live", IsLive = true }), "config");
        store.History.Clear();
        engine.Snapshot = engine.Snapshot with { Position = TimeSpan.FromMinutes(1) };
        await coordinator.SaveProgressAsync();
        Assert.Empty(store.History);
    }

    private sealed class FakeStore : ILibraryStore
    {
        public List<HistoryEntry> History { get; } = [];
        public Task SaveHistoryAsync(HistoryEntry entry, CancellationToken cancellationToken = default) { History.Add(entry); return Task.CompletedTask; }
        public Task<IReadOnlyList<HistoryEntry>> GetHistoryAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<HistoryEntry>>(History);
        public Task SetFavoriteAsync(FavoriteEntry entry, bool favorite, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<FavoriteEntry>> GetFavoritesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<FavoriteEntry>>([]);
    }

    private sealed class FakeEngine : IPlaybackEngine
    {
        public PlaybackSnapshot Snapshot { get; set; } = new(PlaybackState.Idle, TimeSpan.Zero, TimeSpan.Zero, true);
        public event EventHandler<PlaybackEvent>? StateChanged { add { } remove { } }
        public List<string> Opened { get; } = [];
        public Task OpenAsync(PlaybackRequest request, long sessionId, CancellationToken cancellationToken) { Opened.Add(request.Uri); return Task.CompletedTask; }
        public Task PlayAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task PauseAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SetRateAsync(double rate, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SetVolumeAsync(double volume, CancellationToken cancellationToken) => Task.CompletedTask;
        public IReadOnlyList<MediaTrack> GetTracks(TrackKind kind) => [];
        public Task SelectTrackAsync(TrackKind kind, string trackId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task AddSubtitleAsync(string path, CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
