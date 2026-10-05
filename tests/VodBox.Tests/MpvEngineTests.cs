using System.Collections.Concurrent;
using System.Globalization;
using VodBox.Core;
using VodBox.Playback.Mpv;
using Xunit;

namespace VodBox.Tests;

public sealed class MpvEngineTests
{
    [Fact]
    public async Task VideoSurfaceHandshakePreventsPrematureLoadAndAllowsCancelledRetry()
    {
        var client = new Client(); await using var engine = new MpvEngine(factory: () => client, waitForVideoSurface: true);
        var initialized = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.Initialized += (_, _) => initialized.TrySetResult();
        using var cancellation = new CancellationTokenSource();
        var opening = engine.OpenAsync(new() { Uri = "https://media.example/video" }, 1, cancellation.Token);
        await initialized.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(0, client.Loads); Assert.False(opening.IsCompleted);
        cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening); Assert.Equal(0, client.Loads);
        engine.NotifyVideoSurfaceReady(); await engine.OpenAsync(new() { Uri = "https://media.example/video" }, 2, default);
        Assert.Equal(1, client.Loads); await Until(() => engine.Snapshot.State == PlaybackState.Playing);
    }

    [Fact]
    public async Task VideoSurfaceFailureUnblocksOpenWithoutLoadingMedia()
    {
        var client = new Client(); await using var engine = new MpvEngine(factory: () => client, waitForVideoSurface: true);
        engine.Initialized += (_, _) => engine.NotifyVideoSurfaceFailure(new NotSupportedException("No OpenGL"));
        await Assert.ThrowsAsync<NotSupportedException>(() => engine.OpenAsync(new() { Uri = "https://media.example/video" }, 1, default));
        Assert.Equal(0, client.Loads);
    }
    [Fact]
    public async Task LazyEngineObservesStateTracksAndControlsWithoutLoadingNativeLibrary()
    {
        var client = new Client(); int initialized = 0; await using var engine = new MpvEngine(factory: () => { initialized++; return client; });
        await engine.SetVolumeAsync(.35, default); await engine.SetRateAsync(1.5, default); Assert.Equal(0, initialized);
        var sessions = new ConcurrentQueue<long>(); engine.StateChanged += (_, e) => sessions.Enqueue(e.SessionId);
        await engine.OpenAsync(new() { Uri = "https://media.example/video", StartPositionMs = 1500, Headers = new() { ["User-Agent"] = "Test", ["Referer"] = "https://source.example/" } }, 9, default);
        await Until(() => engine.Snapshot.State == PlaybackState.Playing && engine.Snapshot.Position.TotalSeconds == 1.5);
        Assert.Equal(TimeSpan.FromSeconds(10), engine.Snapshot.Duration); Assert.True(engine.Snapshot.CanSeek); Assert.Equal(1, initialized);
        Assert.Equal(6, client.Observed.Count); Assert.Equal("35", client.Properties["volume"]); Assert.Equal("1.5", client.Properties["speed"]);
        Assert.Equal("中文音轨", Assert.Single(engine.GetTracks(TrackKind.Audio)).Name); Assert.Contains(engine.GetTracks(TrackKind.Subtitle), x => x.Id == "no");
        await engine.PauseAsync(default); await Until(() => engine.Snapshot.State == PlaybackState.Paused);
        await engine.SeekAsync(TimeSpan.FromSeconds(2.5), default); await Until(() => engine.Snapshot.Position.TotalSeconds == 2.5);
        await engine.SetAudioDelayAsync(120, default); await engine.SetSubtitleDelayAsync(-340, default);
        Assert.Equal("0.12", client.Properties["audio-delay"]); Assert.Equal("-0.34", client.Properties["sub-delay"]);
        await engine.SelectTrackAsync(TrackKind.Audio, "1", default); Assert.Equal("1", client.Properties["aid"]);
        await engine.PlayAsync(default); await Until(() => engine.Snapshot.State == PlaybackState.Playing);
        await engine.OpenAsync(new() { Uri = "https://other.example/next" }, 10, default);
        await Until(() => engine.Snapshot.State == PlaybackState.Playing);
        Assert.Equal("", client.Properties["user-agent"]); Assert.Equal("", client.Properties["referrer"]); Assert.Equal(1, initialized);
        Assert.Contains(9L, sessions); Assert.Contains(10L, sessions);
        await engine.StopAsync(default); Assert.Equal(PlaybackState.Idle, engine.Snapshot.State);
    }

    [Fact]
    public async Task EndReasonAndLateUnavailableEventsDoNotTurnFailureIntoSuccess()
    {
        var client = new Client(); await using var engine = new MpvEngine(factory: () => client);
        await engine.OpenAsync(new() { Uri = "https://media.example/video" }, 1, default); await Until(() => engine.Snapshot.State == PlaybackState.Playing);
        client.Events.Enqueue(new(7, 0, 0, EndReason: 4, EndError: -13));
        client.Events.Enqueue(new(22, 0, 3, PropertyName: "pause", PropertyFlag: false));
        client.Events.Enqueue(new(22, 0, 1, PropertyName: "time-pos"));
        await Until(() => engine.Snapshot.State == PlaybackState.Failed); Assert.NotNull(engine.Snapshot.Error);
        await engine.OpenAsync(new() { Uri = "https://media.example/retry" }, 2, default); await Until(() => engine.Snapshot.State == PlaybackState.Playing);
        client.Events.Enqueue(new(7, 0, 0, EndReason: 0)); await Until(() => engine.Snapshot.State == PlaybackState.Ended); Assert.Null(engine.Snapshot.Error);
    }

    [Fact]
    public async Task RejectedHeadersCancellationAndDisposeDoNotInitializeOrLeakClient()
    {
        var client = new Client(); int initialized = 0; await using var engine = new MpvEngine(factory: () => { initialized++; return client; });
        await Assert.ThrowsAsync<NotSupportedException>(() => engine.OpenAsync(new() { Uri = "https://media.example", Headers = new() { ["Cookie"] = "token" } }, 1, default));
        await Assert.ThrowsAsync<InvalidDataException>(() => engine.OpenAsync(new() { Uri = "https://media.example", Headers = new() { ["Referer"] = "bad\nheader" } }, 1, default));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.OpenAsync(new() { Uri = "https://media.example" }, 1, cancellation.Token)); Assert.Equal(0, initialized);
        await engine.OpenAsync(new() { Uri = "https://media.example" }, 2, default); await Until(() => engine.Snapshot.State == PlaybackState.Playing);
        await engine.DisposeAsync(); await engine.DisposeAsync(); Assert.Equal(1, client.Disposals);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => engine.PlayAsync(default)); Assert.Null(engine.Client);
    }

    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }
    private sealed class Client : IMpvClient
    {
        public ConcurrentQueue<MpvEvent> Events { get; } = new();
        public ConcurrentDictionary<string, string> Properties { get; } = new();
        public List<string> Observed { get; } = [];
        public int Disposals, Loads;
        public void Command(params string[] args)
        {
            switch (args[0])
            {
                case "set":
                    Properties[args[1]] = args[2];
                    if (args[1] == "pause") Events.Enqueue(new(22, 0, 3, PropertyName: "pause", PropertyFlag: args[2] == "yes"));
                    break;
                case "loadfile":
                    Interlocked.Increment(ref Loads);
                    Events.Enqueue(new(8, 0, 0)); Events.Enqueue(new(22, 0, 2, PropertyName: "duration", PropertyDouble: 10));
                    Events.Enqueue(new(22, 0, 4, PropertyName: "seekable", PropertyFlag: true));
                    Events.Enqueue(new(22, 0, 1, PropertyName: "time-pos", PropertyDouble: 0)); break;
                case "seek": Events.Enqueue(new(22, 0, 1, PropertyName: "time-pos", PropertyDouble: double.Parse(args[1], CultureInfo.InvariantCulture))); break;
                case "stop": Events.Enqueue(new(7, 0, 0, EndReason: 2)); break;
            }
        }
        public double? GetDouble(string name) => name switch { "track-list/count" => 1, "track-list/0/id" => 1, _ => null };
        public string? GetString(string name) => name switch { "track-list/0/type" => "audio", "track-list/0/title" => "中文音轨", _ => null };
        public void Observe(string property, ulong id, MpvPropertyFormat format) => Observed.Add(property);
        public MpvEvent PollEvent() => Events.TryDequeue(out var item) ? item : new(0, 0, 0);
        public void Dispose() => Interlocked.Increment(ref Disposals);
    }
}
