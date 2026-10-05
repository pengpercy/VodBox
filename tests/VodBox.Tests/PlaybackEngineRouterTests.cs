using System.Collections.Concurrent;
using VodBox.Application;
using VodBox.Core;
using Xunit;

namespace VodBox.Tests;

public sealed class PlaybackEngineRouterTests
{
    private static PlaybackRequest Request(string uri = "https://media.example/video") => new() { Uri = uri, Headers = new() { ["User-Agent"] = "Fixture" } };

    [Fact]
    public async Task RendererFailureDuringOpenSurvivesPlayingEventsAndFallsBackOnce()
    {
        var mpv = new Engine(); var vlc = new Engine();
        await using var router = new PlaybackEngineRouter(kind => kind == PlaybackEngineKind.Mpv ? mpv : vlc);
        mpv.OnOpen = () => router.ReportSurfaceFailure(mpv, new NotSupportedException("No OpenGL"));
        await router.OpenAsync(Request(), 7, default);
        Assert.Equal(PlaybackEngineKind.LibVlc, router.ActiveKind); Assert.Equal(PlaybackState.Playing, router.Snapshot.State);
        Assert.Equal(1, mpv.Opens); Assert.Equal(1, vlc.Opens);
        router.ReportSurfaceFailure(mpv, new IOException("Old surface")); Assert.Equal(PlaybackState.Playing, router.Snapshot.State);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RuntimeRendererLossRespectsManualMode(bool automatic)
    {
        var mpv = new Engine(); var vlc = new Engine();
        await using var router = new PlaybackEngineRouter(kind => kind == PlaybackEngineKind.Mpv ? mpv : vlc);
        if (!automatic) await router.ChangeModeAsync(PlaybackEngineMode.Mpv, default);
        await router.OpenAsync(Request(), 8, default); mpv.Emit(PlaybackState.Playing, 3);
        router.ReportSurfaceFailure(mpv, new IOException("GPU context lost"));
        if (automatic)
        {
            await Until(() => router.ActiveKind == PlaybackEngineKind.LibVlc && router.Snapshot.State == PlaybackState.Playing);
            Assert.Equal(3000, vlc.Request!.StartPositionMs);
        }
        else
        {
            Assert.Equal(PlaybackState.Failed, router.Snapshot.State); Assert.Equal(0, vlc.Opens);
            mpv.Emit(PlaybackState.Playing, 4); Assert.Equal(PlaybackState.Failed, router.Snapshot.State);
            await Until(() => mpv.Stops == 1); Assert.Equal(PlaybackState.Failed, router.Snapshot.State);
        }
    }

    [Fact]
    public async Task AutomaticStartupAndRuntimeFailureTryAlternateOnlyOnce()
    {
        var mpv = new Engine { OpenError = new DllNotFoundException("missing mpv") }; var vlc = new Engine();
        await using var router = new PlaybackEngineRouter(kind => kind == PlaybackEngineKind.Mpv ? mpv : vlc);
        var choices = new ConcurrentQueue<ActivePlaybackEngine>(); router.ActiveEngineChanged += (_, choice) => choices.Enqueue(choice);
        await router.OpenAsync(Request(), 1, default);
        Assert.Equal(PlaybackEngineKind.LibVlc, router.ActiveKind); Assert.Equal(PlaybackState.Playing, router.Snapshot.State);
        Assert.Equal(1, mpv.Opens); Assert.Equal(1, vlc.Opens); Assert.Contains(choices, x => x.Reason.Contains("回退", StringComparison.Ordinal));
        vlc.Emit(PlaybackState.Failed, error: "CDN error"); await Task.Delay(150);
        Assert.Equal(PlaybackState.Failed, router.Snapshot.State); Assert.Equal(1, mpv.Opens); Assert.Equal(1, vlc.Opens);
    }

    [Theory]
    [InlineData(PlaybackEngineMode.Mpv)]
    [InlineData(PlaybackEngineMode.LibVlc)]
    public async Task ManualModeNeverCreatesAlternateOnFailure(PlaybackEngineMode mode)
    {
        var engine = new Engine { OpenError = new IOException("failed") }; int created = 0;
        await using var router = new PlaybackEngineRouter(_ => { created++; return engine; });
        await router.ChangeModeAsync(mode, default);
        await Assert.ThrowsAsync<IOException>(() => router.OpenAsync(Request(), 1, default));
        Assert.Equal(1, created); Assert.Equal(PlaybackState.Failed, router.Snapshot.State);
    }

    [Fact]
    public async Task FixingCurrentKindToManualAlsoDisablesFallback()
    {
        var engine = new Engine(); int created = 0;
        await using var router = new PlaybackEngineRouter(_ => { created++; return engine; });
        await router.OpenAsync(Request(), 1, default); await router.ChangeModeAsync(PlaybackEngineMode.Mpv, default);
        Assert.Equal(1, engine.Opens); engine.Emit(PlaybackState.Failed, error: "error"); await Task.Delay(150);
        Assert.Equal(1, created); Assert.Equal(PlaybackState.Failed, router.Snapshot.State);
    }

    [Fact]
    public async Task SwitchingPreservesPositionPauseVolumeRateAndDelays()
    {
        var mpv = new Engine(); var vlc = new Engine { PauseAdvance = .4 }; await using var router = new PlaybackEngineRouter(kind => kind == PlaybackEngineKind.Mpv ? mpv : vlc);
        await router.SetVolumeAsync(.42, default); await router.SetRateAsync(1.75, default);
        await router.SetAudioDelayAsync(120, default); await router.SetSubtitleDelayAsync(-340, default);
        await router.OpenAsync(Request(), 9, default); mpv.Emit(PlaybackState.Playing, 5); await router.PauseAsync(default);
        await router.ChangeModeAsync(PlaybackEngineMode.LibVlc, default); await Until(() => router.Snapshot.State == PlaybackState.Paused && router.Snapshot.Position.TotalSeconds == 5);
        Assert.Equal(5000, vlc.Request!.StartPositionMs); Assert.Equal(9, vlc.Session); Assert.Equal(.42, vlc.Volume); Assert.Equal(1.75, vlc.Rate);
        Assert.Equal(120, vlc.AudioDelay); Assert.Equal(-340, vlc.SubtitleDelay); Assert.Equal(1, mpv.Stops);
        mpv.Emit(PlaybackState.Playing, 99); Assert.Equal(PlaybackState.Paused, router.Snapshot.State); Assert.Equal(TimeSpan.FromSeconds(5), router.Snapshot.Position);
        await router.TakeSnapshotAsync("fixture.png", default); Assert.Equal("fixture.png", vlc.Screenshot);
    }

    [Fact]
    public async Task RuntimeFallbackCopiesRequestAndResumesSameSession()
    {
        var mpv = new Engine(); var vlc = new Engine(); await using var router = new PlaybackEngineRouter(kind => kind == PlaybackEngineKind.Mpv ? mpv : vlc);
        var request = Request(); await router.OpenAsync(request, 3, default); request.Headers["User-Agent"] = "Mutated";
        mpv.Emit(PlaybackState.Failed, 4.5, "failed"); await Until(() => router.ActiveKind == PlaybackEngineKind.LibVlc && router.Snapshot.State == PlaybackState.Playing);
        Assert.Equal(4500, vlc.Request!.StartPositionMs); Assert.Equal("Fixture", vlc.Request.Headers["User-Agent"]); Assert.Equal(3, vlc.Session);
        vlc.Emit(PlaybackState.Failed, 5, "failed again"); await Task.Delay(150); Assert.Equal(1, mpv.Opens); Assert.Equal(1, vlc.Opens);
    }

    [Fact]
    public async Task NetworkFileSelectionDoesNotFallBackToMpv()
    {
        var engine = new Engine { OpenError = new IOException("SMB unavailable") }; var kinds = new List<PlaybackEngineKind>();
        await using var router = new PlaybackEngineRouter(kind => { kinds.Add(kind); return engine; });
        await Assert.ThrowsAsync<IOException>(() => router.OpenAsync(Request("smb://server/share/movie.mp4"), 1, default));
        Assert.Equal(PlaybackEngineKind.LibVlc, Assert.Single(kinds));
    }

    [Fact]
    public async Task CancelledQueuedRequestKeepsPreviousSessionEvents()
    {
        var engine = new Engine(); await using var router = new PlaybackEngineRouter(_ => engine);
        await router.OpenAsync(Request(), 1, default);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => router.OpenAsync(Request(), 2, cancellation.Token));
        engine.Emit(PlaybackState.Playing, 6); Assert.Equal(TimeSpan.FromSeconds(6), router.Snapshot.Position); Assert.Equal(1, engine.Opens);
    }

    [Fact]
    public async Task PendingFailureCannotOverrideNewerMediaRequest()
    {
        var mpv = new Engine(); var vlc = new Engine(); await using var router = new PlaybackEngineRouter(kind => kind == PlaybackEngineKind.Mpv ? mpv : vlc);
        await router.OpenAsync(Request(), 1, default); mpv.BlockVolume = true;
        Task control = router.SetVolumeAsync(.4, default); await mpv.VolumeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task next = router.OpenAsync(Request("https://media.example/next"), 2, default);
        mpv.Emit(PlaybackState.Failed, error: "old failure"); mpv.VolumeRelease.TrySetResult(); await control; await next; await Task.Delay(150);
        Assert.Equal(PlaybackEngineKind.Mpv, router.ActiveKind); Assert.Equal(2, mpv.Session); Assert.Equal(2, mpv.Opens); Assert.Equal(0, vlc.Opens);
        Assert.Equal(PlaybackState.Playing, router.Snapshot.State);
    }

    [Fact]
    public async Task CancellationAfterEngineCreationStopsItWithoutFallback()
    {
        var engine = new Engine { OpenError = new OperationCanceledException() }; int created = 0;
        await using var router = new PlaybackEngineRouter(_ => { created++; return engine; });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => router.OpenAsync(Request(), 1, default));
        Assert.Equal(1, created); Assert.Equal(1, engine.Stops); Assert.Equal(PlaybackState.Idle, router.Snapshot.State); Assert.Null(router.ActiveEngine);
    }

    [Fact]
    public async Task CancelledModeSwitchStopsReplacementWithoutLeavingLoadingSession()
    {
        var mpv = new Engine(); var vlc = new Engine { OpenError = new OperationCanceledException() };
        await using var router = new PlaybackEngineRouter(kind => kind == PlaybackEngineKind.Mpv ? mpv : vlc);
        await router.OpenAsync(Request(), 4, default);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => router.ChangeModeAsync(PlaybackEngineMode.LibVlc, default));
        Assert.Equal(1, mpv.Stops); Assert.Equal(1, vlc.Stops); Assert.Null(router.ActiveEngine);
        Assert.Equal(PlaybackState.Idle, router.Snapshot.State);
        mpv.Emit(PlaybackState.Playing, 9); Assert.Equal(PlaybackState.Idle, router.Snapshot.State);
    }

    [Fact]
    public async Task MissingVlcTracksDoNotFailPlaybackAndNewSubtitleReceivesDelay()
    {
        var engine = new Engine { HasTracks = false, ThrowTracklessDelay = true };
        await using var router = new PlaybackEngineRouter(_ => engine);
        await router.ChangeModeAsync(PlaybackEngineMode.LibVlc, default); await router.SetAudioDelayAsync(120, default); await router.SetSubtitleDelayAsync(-340, default);
        await router.OpenAsync(Request(), 1, default); await Task.Delay(100);
        Assert.Equal(PlaybackState.Playing, router.Snapshot.State); Assert.Equal(0, engine.AudioDelay); Assert.Equal(0, engine.SubtitleDelay);
        await router.SetAudioDelayAsync(240, default); await router.SetSubtitleDelayAsync(-500, default);
        Assert.Equal(PlaybackState.Playing, router.Snapshot.State);
        await router.AddSubtitleAsync("fixture.srt", default); Assert.Equal(-500, engine.SubtitleDelay);
    }

    [Fact]
    public async Task DisposeAttemptsEveryEngineAndIsIdempotentEvenOnError()
    {
        var mpv = new Engine { ThrowDispose = true }; var vlc = new Engine(); var router = new PlaybackEngineRouter(kind => kind == PlaybackEngineKind.Mpv ? mpv : vlc);
        await router.OpenAsync(Request(), 1, default); await router.ChangeModeAsync(PlaybackEngineMode.LibVlc, default);
        await Assert.ThrowsAsync<AggregateException>(async () => await router.DisposeAsync()); Assert.Equal(1, mpv.Disposals); Assert.Equal(1, vlc.Disposals);
        await router.DisposeAsync(); await Assert.ThrowsAsync<ObjectDisposedException>(() => router.PlayAsync(default));
    }

    private static async Task Until(Func<bool> condition)
    { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); while (!condition()) await Task.Delay(20, timeout.Token); }

    private sealed class Engine : IPlaybackEngine, IPlaybackAdvancedControls
    {
        public Exception? OpenError;
        public Action? OnOpen;
        public bool BlockVolume, ThrowDispose, ThrowTracklessDelay;
        public bool HasTracks = true;
        public int Opens, Stops, Disposals, AudioDelay, SubtitleDelay;
        public double Volume, Rate, PauseAdvance;
        public long Session;
        public PlaybackRequest? Request;
        public string? Screenshot;
        public TaskCompletionSource VolumeStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource VolumeRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public PlaybackSnapshot Snapshot { get; private set; } = new(PlaybackState.Idle, TimeSpan.Zero, TimeSpan.Zero, false);
        public event EventHandler<PlaybackEvent>? StateChanged;
        public void Emit(PlaybackState state, double? position = null, string? error = null)
        { Snapshot = new(state, TimeSpan.FromSeconds(position ?? Snapshot.Position.TotalSeconds), TimeSpan.FromSeconds(15), true, error); StateChanged?.Invoke(this, new(Session, Snapshot)); }
        public Task OpenAsync(PlaybackRequest request, long sessionId, CancellationToken token)
        { Opens++; Request = request; Session = sessionId; OnOpen?.Invoke(); if (OpenError is { } error) return Task.FromException(error); Emit(PlaybackState.Playing, request.StartPositionMs / 1000d); return Task.CompletedTask; }
        public Task PlayAsync(CancellationToken token) { Emit(PlaybackState.Playing); return Task.CompletedTask; }
        public Task PauseAsync(CancellationToken token) { Emit(PlaybackState.Paused, Snapshot.Position.TotalSeconds + PauseAdvance); return Task.CompletedTask; }
        public Task StopAsync(CancellationToken token) { Stops++; Emit(PlaybackState.Idle); return Task.CompletedTask; }
        public Task SeekAsync(TimeSpan position, CancellationToken token) { Emit(Snapshot.State, position.TotalSeconds); return Task.CompletedTask; }
        public Task SetRateAsync(double rate, CancellationToken token) { Rate = rate; return Task.CompletedTask; }
        public async Task SetVolumeAsync(double volume, CancellationToken token)
        { if (BlockVolume) { VolumeStarted.TrySetResult(); await VolumeRelease.Task.WaitAsync(token); BlockVolume = false; } Volume = volume; }
        public Task SetAudioDelayAsync(int value, CancellationToken token) { if (!HasTracks && ThrowTracklessDelay) throw new InvalidOperationException("No audio"); AudioDelay = value; return Task.CompletedTask; }
        public Task SetSubtitleDelayAsync(int value, CancellationToken token) { if (!HasTracks && ThrowTracklessDelay) throw new InvalidOperationException("No subtitle"); SubtitleDelay = value; return Task.CompletedTask; }
        public Task TakeSnapshotAsync(string path, CancellationToken token) { Screenshot = path; return Task.CompletedTask; }
        public IReadOnlyList<MediaTrack> GetTracks(TrackKind kind) => HasTracks ? [new("1", "Fixture", kind)] : [];
        public Task SelectTrackAsync(TrackKind kind, string id, CancellationToken token) => Task.CompletedTask;
        public Task AddSubtitleAsync(string path, CancellationToken token) { HasTracks = true; return Task.CompletedTask; }
        public ValueTask DisposeAsync() { Disposals++; if (ThrowDispose) throw new IOException("dispose error"); return ValueTask.CompletedTask; }
    }
}
