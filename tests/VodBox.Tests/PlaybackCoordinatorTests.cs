using VodBox.Core;
using Xunit;

namespace VodBox.Tests;

public sealed class PlaybackCoordinatorTests
{
    [Fact]
    public async Task LateResolverCannotReplaceNewPlayback()
    {
        var engine = new FakeEngine();
        using var coordinator = new PlaybackCoordinator(engine);
        var late = new TaskCompletionSource<PlaybackRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var old = coordinator.OpenAsync(_ => late.Task, ct: TestContext.Current.CancellationToken);
        await coordinator.OpenAsync(new PlaybackRequest { Uri = "https://example/new.mp4" }, ct: TestContext.Current.CancellationToken);
        late.SetResult(new PlaybackRequest { Uri = "https://example/old.mp4" });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => old);
        Assert.Equal(["https://example/new.mp4"], engine.Opened);
        Assert.Equal(2, coordinator.SessionId);
    }

    [Fact]
    public async Task CloseCancelsResolverEvenWhenResolverIgnoresToken()
    {
        var engine = new FakeEngine();
        using var coordinator = new PlaybackCoordinator(engine);
        var late = new TaskCompletionSource<PlaybackRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var open = coordinator.OpenAsync(_ => late.Task, ct: TestContext.Current.CancellationToken);
        await coordinator.CloseAsync(ct: TestContext.Current.CancellationToken);
        late.SetResult(new PlaybackRequest { Uri = "https://example/old.mp4" });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => open);
        Assert.Empty(engine.Opened);
        Assert.Equal(1, engine.Stops);
    }

    [Fact]
    public async Task NativeTransitionsAreSerialized()
    {
        var engine = new FakeEngine { HoldFirst = true };
        using var coordinator = new PlaybackCoordinator(engine);
        var first = coordinator.OpenAsync(new PlaybackRequest { Uri = "https://example/first.mp4" }, ct: TestContext.Current.CancellationToken);
        await engine.Started.Task;
        var second = coordinator.OpenAsync(new PlaybackRequest { Uri = "https://example/second.mp4" }, ct: TestContext.Current.CancellationToken);
        engine.Release.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await second;
        Assert.Equal(1, engine.MaxActive);
        Assert.Equal("https://example/second.mp4", engine.Opened.Last());
    }

    private sealed class FakeEngine : IPlaybackEngine
    {
        public List<string> Opened { get; } = [];
        public int Stops, MaxActive, Active;
        public bool HoldFirst;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public PlaybackSnapshot Snapshot => new(PlaybackState.Idle, TimeSpan.Zero, TimeSpan.Zero, false);
        public event EventHandler<PlaybackEvent>? StateChanged { add { } remove { } }
        public async Task OpenAsync(PlaybackRequest request, long sessionId, CancellationToken ct = default)
        {
            MaxActive = Math.Max(MaxActive, ++Active);
            try
            {
                Opened.Add(request.Uri);
                if (HoldFirst && Opened.Count == 1) { Started.SetResult(); await Release.Task; }
            }
            finally { --Active; }
        }
        public Task StopAsync(CancellationToken ct = default) { Stops++; return Task.CompletedTask; }
        public Task PlayAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task PauseAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SeekToAsync(TimeSpan position, CancellationToken ct = default) => Task.CompletedTask;
        public Task SeekByAsync(TimeSpan delta, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetRateAsync(double rate, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetVolumeAsync(int volume, CancellationToken ct = default) => Task.CompletedTask;
        public IReadOnlyList<MediaTrack> GetTracks(TrackKind kind) => [];
        public Task SelectTrackAsync(TrackKind kind, string id, CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
