using System.Collections.Concurrent;
using System.Globalization;
using VodBox.Core;
using VodBox.Playback.Mpv;
using Xunit;

namespace VodBox.Tests;

/// <summary>MpvEngine 行为测试（假 IMpvClient，不加载真实 libmpv）。</summary>
public sealed class MpvEngineTests
{
    [Fact]
    public void GuiUsesNativeAudioAutoselectionInsteadOfInvalidAutoDriver()
    {
        var gui=MpvEngine.DefaultOptions(false);
        Assert.Equal("libmpv",gui["vo"]);Assert.False(gui.ContainsKey("ao"));
        var headless=MpvEngine.DefaultOptions(true);
        Assert.Equal("null",headless["ao"]);Assert.Equal("null",headless["vo"]);
    }

    [Fact]
    public async Task SubtitleStyleSetsNativePropertiesAndRejectsInvalidValues()
    {
        var client = new Client();
        await using var engine = new MpvEngine(factory: () => client);
        await engine.OpenAsync(new PlaybackRequest { Uri = "https://media.example/video" }, 1, TestContext.Current.CancellationToken);
        await engine.SetSubtitleStyleAsync(-.5, 52, TestContext.Current.CancellationToken);
        Assert.Equal("-0.5", client.Properties["sub-delay"]);
        Assert.Equal("52", client.Properties["sub-font-size"]);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => engine.SetSubtitleStyleAsync(double.NaN, 40));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => engine.SetSubtitleStyleAsync(0, 500));
    }

    [Fact]
    public async Task EngineReportsVideoDisplayAspectAndClearsItWhenNewMediaOpens()
    {
        var client=new Client{DisplayAspect=9d/16};await using var engine=new MpvEngine(factory:()=>client);
        await engine.OpenAsync(new PlaybackRequest{Uri="https://media.example/portrait"},1,TestContext.Current.CancellationToken);
        await Until(()=>engine.Snapshot.VideoAspectRatio is not null);Assert.Equal(9d/16,engine.Snapshot.VideoAspectRatio);
        client.DisplayAspect=null;await engine.OpenAsync(new PlaybackRequest{Uri="https://media.example/audio"},2,TestContext.Current.CancellationToken);
        Assert.Null(engine.Snapshot.VideoAspectRatio);
        client.DisplayAspect=double.NaN;await Task.Delay(150,TestContext.Current.CancellationToken);Assert.Null(engine.Snapshot.VideoAspectRatio);
    }

    [Fact]
    public async Task AspectRatioIsAppliedOnOpenRestoredAndValidated()
    {
        var client = new Client();
        await using var engine = new MpvEngine(factory: () => client);
        await engine.SetAspectRatioAsync(16d / 9);
        await engine.OpenAsync(new PlaybackRequest { Uri = "https://media.example/video" }, 1);
        Assert.Equal(16d / 9, double.Parse(client.Properties["video-aspect-override"], CultureInfo.InvariantCulture), 12);
        await engine.SetAspectRatioAsync(null);
        Assert.Equal("-1", client.Properties["video-aspect-override"]);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => engine.SetAspectRatioAsync(double.NaN));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => engine.SetAspectRatioAsync(0));
    }

    [Fact]
    public async Task VideoSurfaceHandshakePreventsPrematureLoadAndAllowsCancelledRetry()
    {
        var client = new Client(); await using var engine = new MpvEngine(factory: () => client, waitForVideoSurface: true);
        var initialized = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.Initialized += (_, _) => initialized.TrySetResult();
        using var cancellation = new CancellationTokenSource();
        var opening = engine.OpenAsync(new PlaybackRequest { Uri = "https://media.example/video" }, 1, cancellation.Token);
        await initialized.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(0, client.Loads); Assert.False(opening.IsCompleted);
        cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening); Assert.Equal(0, client.Loads);
        engine.NotifyVideoSurfaceReady(); await engine.OpenAsync(new PlaybackRequest { Uri = "https://media.example/video" }, 2, default);
        Assert.Equal(1, client.Loads); await Until(() => engine.Snapshot.State == PlaybackState.Playing);
    }

    [Fact]
    public async Task VideoSurfaceFailureUnblocksOpenWithoutLoadingMedia()
    {
        var client = new Client(); await using var engine = new MpvEngine(factory: () => client, waitForVideoSurface: true);
        engine.Initialized += (_, _) => engine.NotifyVideoSurfaceFailure(new NotSupportedException("No OpenGL"));
        await Assert.ThrowsAsync<NotSupportedException>(() => engine.OpenAsync(new PlaybackRequest { Uri = "https://media.example/video" }, 1, default));
        Assert.Equal(0, client.Loads);
    }

    [Fact]
    public async Task LazyEngineObservesStateTracksAndControlsWithoutLoadingNativeLibrary()
    {
        var client = new Client(); int initialized = 0; await using var engine = new MpvEngine(factory: () => { initialized++; return client; });
        await engine.SetVolumeAsync(35, default); await engine.SetRateAsync(1.5, default); Assert.Equal(0, initialized);
        var sessions = new ConcurrentQueue<long>(); engine.StateChanged += (_, e) => sessions.Enqueue(e.SessionId);
        await engine.OpenAsync(new PlaybackRequest { Uri = "https://media.example/video", StartPositionMs = 1500, Headers = new() { ["User-Agent"] = "Test", ["Referer"] = "https://source.example/" } }, 9, default);
        await Until(() => engine.Snapshot.State == PlaybackState.Playing && engine.Snapshot.Position.TotalSeconds == 1.5);
        Assert.Equal(TimeSpan.FromSeconds(10), engine.Snapshot.Duration); Assert.True(engine.Snapshot.CanSeek); Assert.Equal(1, initialized);
        Assert.Equal(6, client.Observed.Count); Assert.Equal("35", client.Properties["volume"]); Assert.Equal("1.5", client.Properties["speed"]);
        Assert.Equal("中文音轨", Assert.Single(engine.GetTracks(TrackKind.Audio)).Name);
        Assert.Equal("视频轨", Assert.Single(engine.GetTracks(TrackKind.Video)).Name);
        Assert.Contains(engine.GetTracks(TrackKind.Subtitle), x => x.Id == "no");
        await engine.PauseAsync(default); await Until(() => engine.Snapshot.State == PlaybackState.Paused);
        await engine.SeekToAsync(TimeSpan.FromSeconds(2.5), default); await Until(() => engine.Snapshot.Position.TotalSeconds == 2.5);
        Assert.Equal("absolute+exact", client.SeekFlags[^1]);
        await engine.SeekByAsync(TimeSpan.FromSeconds(5), default); await Until(() => engine.Snapshot.Position.TotalSeconds == 7.5);
        Assert.Equal("relative", client.SeekFlags[^1]);
        await engine.SelectTrackAsync(TrackKind.Audio, "1", default); Assert.Equal("1", client.Properties["aid"]);
        await engine.SelectTrackAsync(TrackKind.Video, "2", default); Assert.Equal("2", client.Properties["vid"]);
        await engine.PlayAsync(default); await Until(() => engine.Snapshot.State == PlaybackState.Playing);
        await engine.OpenAsync(new PlaybackRequest { Uri = "https://other.example/next" }, 10, default);
        await Until(() => engine.Snapshot.State == PlaybackState.Playing);
        Assert.Equal("", client.Properties["user-agent"]); Assert.Equal("", client.Properties["referrer"]); Assert.Equal(1, initialized);
        Assert.Contains(9L, sessions); Assert.Contains(10L, sessions);
        await engine.StopAsync(default); Assert.Equal(PlaybackState.Idle, engine.Snapshot.State);
    }

    [Fact]
    public async Task EndReasonAndLateUnavailableEventsDoNotTurnFailureIntoSuccess()
    {
        var client = new Client(); await using var engine = new MpvEngine(factory: () => client);
        await engine.OpenAsync(new PlaybackRequest { Uri = "https://media.example/video" }, 1, default); await Until(() => engine.Snapshot.State == PlaybackState.Playing);
        client.Events.Enqueue(new MpvEvent(7, 0, 0, EndReason: 4, EndError: -13));
        client.Events.Enqueue(new MpvEvent(22, 0, 3, PropertyName: "pause", PropertyFlag: false));
        client.Events.Enqueue(new MpvEvent(22, 0, 1, PropertyName: "time-pos"));
        await Until(() => engine.Snapshot.State == PlaybackState.Failed); Assert.NotNull(engine.Snapshot.Error);
        await engine.OpenAsync(new PlaybackRequest { Uri = "https://media.example/retry" }, 2, default); await Until(() => engine.Snapshot.State == PlaybackState.Playing);
        client.Events.Enqueue(new MpvEvent(7, 0, 0, EndReason: 0)); await Until(() => engine.Snapshot.State == PlaybackState.Ended); Assert.Null(engine.Snapshot.Error);
    }

    [Fact]
    public async Task RejectedHeadersCancellationAndDisposeDoNotInitializeOrLeakClient()
    {
        var client = new Client(); int initialized = 0; await using var engine = new MpvEngine(factory: () => { initialized++; return client; });
        await Assert.ThrowsAsync<NotSupportedException>(() => engine.OpenAsync(new PlaybackRequest { Uri = "https://media.example", Headers = new() { ["Cookie"] = "token" } }, 1, default));
        await Assert.ThrowsAsync<InvalidDataException>(() => engine.OpenAsync(new PlaybackRequest { Uri = "https://media.example", Headers = new() { ["Referer"] = "bad\nheader" } }, 1, default));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.OpenAsync(new PlaybackRequest { Uri = "https://media.example" }, 1, cancellation.Token)); Assert.Equal(0, initialized);
        await engine.OpenAsync(new PlaybackRequest { Uri = "https://media.example" }, 2, default); await Until(() => engine.Snapshot.State == PlaybackState.Playing);
        await engine.DisposeAsync(); await engine.DisposeAsync(); Assert.Equal(1, client.Disposals);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => engine.PlayAsync(default)); Assert.Null(engine.Client);
    }

    [Fact]
    public async Task GuiModeReportsUnavailableUntilSurfaceInstallTriggersInitialization()
    {
        var client = new Client(); int initialized = 0;
        await using var engine = new MpvEngine(factory: () => { initialized++; return client; }, waitForVideoSurface: true);
        Assert.False(engine.Available); // 探测可能失败，必须在渲染面安装后再判定
        var opening = engine.OpenAsync(new PlaybackRequest { Uri = "https://media.example/video" }, 1, default);
        await Until(() => initialized == 1);
        Assert.True(engine.Available);
        engine.NotifyVideoSurfaceReady(); await opening;
        await Until(() => engine.Snapshot.State == PlaybackState.Playing);
    }

    [Fact]
    public async Task LocalSubtitleUsesSelectedTrackAndRejectsStaleSession()
    {
        var path = Path.Combine(AppContext.BaseDirectory, $"字幕-{Guid.NewGuid():N}.srt");
        await File.WriteAllTextAsync(path, "1\n00:00:00,000 --> 00:00:01,000\n测试字幕\n");
        try
        {
            var client = new Client();
            await using var engine = new MpvEngine(factory: () => client);
            await engine.OpenAsync(new PlaybackRequest { Uri = "https://media.example/video" }, 11);
            await Until(() => engine.Snapshot.State == PlaybackState.Playing);
            await engine.LoadSubtitleAsync(path, 10);
            Assert.Empty(client.Subtitles);
            await engine.LoadSubtitleAsync(path, 11);
            Assert.Equal(new[] { path, "select" }, Assert.Single(client.Subtitles));
            await Assert.ThrowsAsync<FileNotFoundException>(() => engine.LoadSubtitleAsync(path + ".missing", 11));
            await Assert.ThrowsAsync<FileNotFoundException>(() => engine.LoadSubtitleAsync("relative.srt", 11));
            await engine.StopAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(() => engine.LoadSubtitleAsync(path, 11));
        }
        finally { File.Delete(path); }
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
        public List<string> SeekFlags { get; } = [];
        public int Disposals, Loads;
        public double? DisplayAspect {get;set;}
        public List<string[]> Subtitles { get; } = [];
        public void Command(params string[] args)
        {
            switch (args[0])
            {
                case "sub-add": Subtitles.Add(args.Skip(1).ToArray()); break;
                case "set":
                    Properties[args[1]] = args[2];
                    if (args[1] == "pause") Events.Enqueue(new MpvEvent(22, 0, 3, PropertyName: "pause", PropertyFlag: args[2] == "yes"));
                    break;
                case "loadfile":
                    Interlocked.Increment(ref Loads);
                    Events.Enqueue(new MpvEvent(8, 0, 0)); Events.Enqueue(new MpvEvent(22, 0, 2, PropertyName: "duration", PropertyDouble: 10));
                    Events.Enqueue(new MpvEvent(22, 0, 4, PropertyName: "seekable", PropertyFlag: true));
                    Events.Enqueue(new MpvEvent(22, 0, 1, PropertyName: "time-pos", PropertyDouble: 0)); break;
                case "seek":
                    var seconds = double.Parse(args[1], CultureInfo.InvariantCulture);
                    SeekFlags.Add(args[2]);
                    if (args[2] == "relative")
                    {
                        _position += seconds;
                        Events.Enqueue(new MpvEvent(22, 0, 1, PropertyName: "time-pos", PropertyDouble: _position));
                    }
                    else
                    {
                        _position = seconds;
                        Events.Enqueue(new MpvEvent(22, 0, 1, PropertyName: "time-pos", PropertyDouble: seconds));
                    }
                    break;
                case "stop": Events.Enqueue(new MpvEvent(7, 0, 0, EndReason: 2)); break;
            }
        }

        private double _position;
        public double? GetDouble(string name) => name switch
        {
            "video-out-params/aspect" => DisplayAspect,
            "track-list/count" => 2,
            "track-list/0/id" => 1, "track-list/1/id" => 2,
            _ => null
        };
        public string? GetString(string name) => name switch
        {
            "track-list/0/type" => "audio", "track-list/0/title" => "中文音轨",
            "track-list/1/type" => "video", "track-list/1/title" => "视频轨",
            "aid" => "1", "vid" => "2", "sid" => "no",
            _ => null
        };
        public void Observe(string property, ulong id, MpvPropertyFormat format) => Observed.Add(property);
        public MpvEvent PollEvent() => Events.TryDequeue(out var item) ? item : new MpvEvent(0, 0, 0);
        public void Dispose() => Interlocked.Increment(ref Disposals);
    }
}
