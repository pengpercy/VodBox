using System.Runtime.InteropServices;
using VodBox.Core;

namespace VodBox.Playback.Mpv;

/// <summary>libmpv 播放引擎（render API 由渲染控件驱动，本类负责命令/属性/事件）。</summary>
public sealed class MpvEngine : IPlaybackEngine
{
    private IntPtr _ctx;
    private IntPtr _renderCtx;
    private Thread? _eventThread;
    private volatile bool _running;
    private long _sessionId;
    private readonly Lock _sync = new();

    public PlaybackSnapshot Snapshot { get; private set; } = new(PlaybackState.Idle, TimeSpan.Zero, TimeSpan.Zero, false);
    public event EventHandler<PlaybackEvent>? StateChanged;
    public event Action? RenderUpdate;
    public event Action? VideoChanged;

    /// <summary>是否成功加载 libmpv。</summary>
    public bool Available { get; }

    public MpvEngine()
    {
        try
        {
            _ctx = MpvNative.mpv_create();
            if (_ctx == IntPtr.Zero) return;
            SetOption("vo", "libmpv");
            SetOption("hwdec", "auto");
            SetOption("keep-open", "always");
            SetOption("idle", "yes");
            if (MpvNative.mpv_initialize(_ctx) < 0) return;
            Available = true;
        }
        catch (DllNotFoundException)
        {
            Available = false;
        }
    }

    private void EnsureReady()
    {
        if (!Available) throw new InvalidOperationException("libmpv 不可用");
    }

    private void SetOption(string name, string value)
    {
        if (_ctx != IntPtr.Zero) MpvNative.mpv_set_option_string(_ctx, name, value);
    }

    internal void SetProperty(string name, string value)
    {
        EnsureReady();
        lock (_sync) MpvNative.mpv_set_property_string(_ctx, name, value);
    }

    internal string? GetProperty(string name)
    {
        EnsureReady();
        IntPtr value;
        lock (_sync) value = MpvNative.mpv_get_property_string(_ctx, name);
        if (value == IntPtr.Zero) return null;
        var result = Marshal.PtrToStringUTF8(value);
        MpvNative.mpv_free(value);
        return result;
    }

    private void Command(params string[] args)
    {
        EnsureReady();
        var array = new IntPtr[args.Length + 1];
        try
        {
            for (var i = 0; i < args.Length; i++) array[i] = Marshal.StringToHGlobalAnsi(args[i]);
            lock (_sync)
            {
                var error = MpvNative.mpv_command(_ctx, args);
                if (error < 0) throw new InvalidOperationException($"mpv 命令失败 {args[0]}: {error}");
            }
        }
        finally
        {
            foreach (var pointer in array) if (pointer != IntPtr.Zero) Marshal.FreeHGlobal(pointer);
        }
    }

    public Task OpenAsync(PlaybackRequest request, long sessionId, CancellationToken ct = default)
    {
        EnsureReady();
        _sessionId = sessionId;
        foreach (var (key, value) in request.Headers)
        {
            if (key.Equals("referer", StringComparison.OrdinalIgnoreCase)) SetProperty("referrer", value);
            else if (key.Equals("user-agent", StringComparison.OrdinalIgnoreCase)) SetProperty("user-agent", value);
        }
        if (request.StartPositionMs > 0)
            Command("loadfile", request.Uri, "replace", $"start={request.StartPositionMs / 1000.0:0.###}");
        else
            Command("loadfile", request.Uri, "replace");
        Update(PlaybackState.Loading);
        StartEventLoop();
        return Task.CompletedTask;
    }

    public Task PlayAsync(CancellationToken ct = default)
    {
        SetProperty("pause", "no");
        Update(PlaybackState.Playing);
        return Task.CompletedTask;
    }

    public Task PauseAsync(CancellationToken ct = default)
    {
        SetProperty("pause", "yes");
        Update(PlaybackState.Paused);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        Command("stop");
        Update(PlaybackState.Idle);
        return Task.CompletedTask;
    }

    public Task SeekToAsync(TimeSpan position, CancellationToken ct = default)
    {
        Command("seek", $"{position.TotalSeconds:0.###}", "absolute");
        return Task.CompletedTask;
    }

    public Task SeekByAsync(TimeSpan delta, CancellationToken ct = default)
    {
        Command("seek", $"{delta.TotalSeconds:0.###}", "relative");
        return Task.CompletedTask;
    }

    public Task SetRateAsync(double rate, CancellationToken ct = default)
    {
        SetProperty("speed", rate.ToString("0.###"));
        return Task.CompletedTask;
    }

    public Task SetVolumeAsync(int volume, CancellationToken ct = default)
    {
        SetProperty("volume", Math.Clamp(volume, 0, 100).ToString());
        return Task.CompletedTask;
    }

    public IReadOnlyList<MediaTrack> GetTracks(TrackKind kind)
    {
        var prefix = kind switch
        {
            TrackKind.Audio => "audio",
            TrackKind.Subtitle => "sub",
            TrackKind.Video => "video",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        var count = int.TryParse(GetProperty($"{prefix}-track-count"), out var c) ? c : 0;
        var selectedId = int.TryParse(GetProperty($"{prefix}-id"), out var s) ? s : 0;
        var tracks = new List<MediaTrack>();
        for (var i = 1; i <= count; i++)
        {
            var title = GetProperty($"track-list/{i - 1}/title") ?? $"{prefix} {i}";
            tracks.Add(new MediaTrack(i.ToString(), title, kind, i == selectedId));
        }
        return tracks;
    }

    public Task SelectTrackAsync(TrackKind kind, string trackId, CancellationToken ct = default)
    {
        var prefix = kind switch
        {
            TrackKind.Audio => "aid",
            TrackKind.Subtitle => "sid",
            TrackKind.Video => "vid",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        if (trackId == "0") SetProperty(prefix, "no");
        else SetProperty(prefix, trackId);
        return Task.CompletedTask;
    }

    // ---------- 事件循环 ----------

    private void StartEventLoop()
    {
        if (_eventThread is { IsAlive: true }) return;
        _running = true;
        _eventThread = new Thread(EventLoop) { IsBackground = true, Name = "mpv-events" };
        _eventThread.Start();
    }

    private void EventLoop()
    {
        while (_running)
        {
            var eventPointer = MpvNative.mpv_wait_event(_ctx, 1000);
            if (eventPointer == IntPtr.Zero) continue;
            var evt = Marshal.PtrToStructure<MpvNative.MpvEvent>(eventPointer);
            switch (evt.EventId)
            {
                case MpvNative.MpvEventFileLoaded:
                    Update(PlaybackState.Playing);
                    VideoChanged?.Invoke();
                    break;
                case MpvNative.MpvEventEndFile:
                    Update(PlaybackState.Ended);
                    break;
                case MpvNative.MpvEventShutdown:
                    _running = false;
                    Update(PlaybackState.Idle);
                    break;
            }
        }
    }

    private void Update(PlaybackState state, string? error = null)
    {
        var position = TimeSpan.Zero;
        var duration = TimeSpan.Zero;
        try
        {
            if (state is PlaybackState.Playing or PlaybackState.Paused or PlaybackState.Buffering)
            {
                if (double.TryParse(GetProperty("time-pos"), out var pos)) position = TimeSpan.FromSeconds(pos);
                if (double.TryParse(GetProperty("duration"), out var dur)) duration = TimeSpan.FromSeconds(dur);
            }
        }
        catch (InvalidOperationException)
        {
        }
        Snapshot = new PlaybackSnapshot(state, position, duration, state is PlaybackState.Playing or PlaybackState.Paused, error);
        StateChanged?.Invoke(this, new PlaybackEvent(_sessionId, Snapshot));
    }

    /// <summary>当前位置（事件线程外轮询用）。</summary>
    public (TimeSpan Position, TimeSpan Duration) PollPosition()
    {
        try
        {
            var pos = double.TryParse(GetProperty("time-pos"), out var p) ? TimeSpan.FromSeconds(p) : Snapshot.Position;
            var dur = double.TryParse(GetProperty("duration"), out var d) ? TimeSpan.FromSeconds(d) : Snapshot.Duration;
            return (pos, dur);
        }
        catch (InvalidOperationException)
        {
            return (Snapshot.Position, Snapshot.Duration);
        }
    }

    public ValueTask DisposeAsync()
    {
        _running = false;
        try
        {
            if (_ctx != IntPtr.Zero)
            {
                MpvNative.mpv_wakeup(_ctx);
                _eventThread?.Join(500);
                MpvNative.mpv_terminate_destroy(_ctx);
                _ctx = IntPtr.Zero;
            }
        }
        catch (DllNotFoundException)
        {
        }
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }
}
