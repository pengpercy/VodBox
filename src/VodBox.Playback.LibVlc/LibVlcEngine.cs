using LibVLCSharp.Shared;
using VodBox.Core;
using MediaTrack = VodBox.Core.MediaTrack;
using VlcCore = LibVLCSharp.Shared.Core;

namespace VodBox.Playback.LibVlc;

public sealed class LibVlcEngine(bool headless = false, bool rebuildPluginCache = false, bool waitForVideoSurface = false) : IPlaybackEngine, IPlaybackAdvancedControls
{
    private readonly TaskCompletionSource _videoSurfaceReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void NotifyVideoSurfaceReady() => _videoSurfaceReady.TrySetResult();
    private readonly SemaphoreSlim _commands = new(1, 1);
    private LibVLC? _lib;
    private MediaPlayer? _player;
    private Media? _media;
    private long _session;
    private long _startPosition;
    private int _pauseRequested;
    private int _volumeRequested = 80;
    private PlaybackSnapshot _snapshot = new(PlaybackState.Idle, TimeSpan.Zero, TimeSpan.Zero, false);
    public PlaybackSnapshot Snapshot => Volatile.Read(ref _snapshot);
    public MediaPlayer? Player => _player;
    public event EventHandler<PlaybackEvent>? StateChanged;
    public event EventHandler? Initialized;

    public void Initialize()
    {
        if (_player is not null) return;
        var configured = Environment.GetEnvironmentVariable("VODBOX_VLC_PATH");
        var bundled = AppLayout.VlcDirectory;
        var installed = "/Applications/VLC.app/Contents/MacOS/lib";
        string? path = configured ?? (Directory.Exists(bundled) ? Directory.Exists(Path.Combine(bundled, "lib")) ? Path.Combine(bundled, "lib") : bundled : OperatingSystem.IsMacOS() && Directory.Exists(installed) ? installed : null);
        string plugins = Path.Combine(bundled, "plugins");
        if (!Directory.Exists(plugins) && path is not null)
        {
            string adjacent = Path.Combine(path, "plugins");
            plugins = Directory.Exists(adjacent) ? adjacent : Path.GetFullPath(Path.Combine(path, "..", "plugins"));
        }
        if (Directory.Exists(plugins)) NativeEnvironment.SetPluginPath(plugins);
        // LibVLCSharp's Linux loader uses LD_LIBRARY_PATH, set by the launcher before process startup.
        VlcCore.Initialize(OperatingSystem.IsLinux() ? null : path);
        List<string> options = ["--no-video-title-show", "--no-osd"];
        if (headless) options.AddRange(["--aout=dummy", "--vout=dummy"]);
        if (rebuildPluginCache) options.Add("--reset-plugins-cache");
        _lib = new LibVLC(options.ToArray());
        _player = new MediaPlayer(_lib);
        _player.Opening += (_, _) => Update(PlaybackState.Loading);
        _player.Playing += (_, _) =>
        {
            Update(PlaybackState.Playing);
            // Native callbacks must not synchronously seek or stop the player.
            if (Interlocked.Exchange(ref _startPosition, 0) is var position && position > 0)
                _ = SeekAsync(TimeSpan.FromMilliseconds(position), CancellationToken.None);
        };
        _player.Paused += (_, _) => Update(PlaybackState.Paused);
        _player.Stopped += (_, _) => Update(PlaybackState.Idle);
        _player.EndReached += (_, _) => Update(PlaybackState.Ended);
        _player.EncounteredError += (_, _) => Update(PlaybackState.Failed, "LibVLC 无法播放此媒体，请检查地址、请求头或解码支持。");
        _player.TimeChanged += (_, e) => Update(position: TimeSpan.FromMilliseconds(e.Time));
        _player.LengthChanged += (_, e) => Update(duration: TimeSpan.FromMilliseconds(Math.Max(0, e.Length)));
        _player.SeekableChanged += (_, e) => Update(canSeek: e.Seekable != 0);
        // Playing can precede creation of the audio output, which otherwise loses an early mute.
        _player.AudioDevice += (_, _) => _ = RestoreVolumeAsync();
        _player.Buffering += (_, e) =>
        {
            // Seek buffering callbacks may arrive after Paused; preserve the requested pause.
            if (Volatile.Read(ref _pauseRequested) != 0) return;
            if (e.Cache < 100) Update(PlaybackState.Buffering);
            else if (_player.IsPlaying) Update(PlaybackState.Playing);
        };
        Initialized?.Invoke(this, EventArgs.Empty);
    }
    private async Task RestoreVolumeAsync()
    {
        try { await CommandAsync(() => { if (_player is not null) _player.Volume = Volatile.Read(ref _volumeRequested); }, CancellationToken.None); }
        catch (ObjectDisposedException) { }
    }
    private void Update(PlaybackState? state = null, string? error = null, TimeSpan? position = null, TimeSpan? duration = null, bool? canSeek = null)
    {
        PlaybackSnapshot old, next;
        do { old = Snapshot; next = old with { State = state ?? old.State, Position = position ?? old.Position,
            Duration = duration ?? old.Duration, CanSeek = canSeek ?? old.CanSeek, Error = error }; }
        while (Interlocked.CompareExchange(ref _snapshot, next, old) != old);
        StateChanged?.Invoke(this, new(_session, next));
    }
    private async Task CommandAsync(Action action, CancellationToken token)
    {
        await _commands.WaitAsync(token);
        try { token.ThrowIfCancellationRequested(); await Task.Run(action, CancellationToken.None); }
        finally { _commands.Release(); }
    }
    public async Task OpenAsync(PlaybackRequest request, long sessionId, CancellationToken token)
    {
        if (waitForVideoSurface)
        {
            await CommandAsync(Initialize, token).ConfigureAwait(false);
            await _videoSurfaceReady.Task.WaitAsync(TimeSpan.FromSeconds(8), token).ConfigureAwait(false);
        }
        await CommandAsync(() =>
    {
        Initialize();
        _player!.Stop(); _player.Media = null; _media?.Dispose();
        _session = sessionId;
        Volatile.Write(ref _pauseRequested, 0);
        Volatile.Write(ref _snapshot, new(PlaybackState.Loading, TimeSpan.Zero, TimeSpan.Zero, false));
        _media = new Media(_lib!, new Uri(request.Uri));
        foreach (var (key, value) in request.Headers)
        {
            if (value.Contains('\r') || value.Contains('\n')) throw new InvalidDataException("请求头含换行。");
            switch (key.ToLowerInvariant())
            {
                case "user-agent": _media.AddOption(":http-user-agent=" + value); break;
                case "referer": _media.AddOption(":http-referrer=" + value); break;
                default: throw new NotSupportedException($"请求头 {key} 需要流代理支持，当前不会静默忽略。");
            }
        }
        foreach (var subtitle in request.Subtitles) _media.AddSlave(MediaSlaveType.Subtitle, 1, subtitle.Uri);
        Interlocked.Exchange(ref _startPosition, request.StartPositionMs);
        if (!_player.Play(_media)) throw new InvalidOperationException("LibVLC 启动播放失败。");
        }, token).ConfigureAwait(false);
    }
    public Task PlayAsync(CancellationToken token) => CommandAsync(() => { Volatile.Write(ref _pauseRequested, 0); _player?.Play(); }, token);
    public async Task PauseAsync(CancellationToken token)
    {
        if (waitForVideoSurface && !headless)
        {
            // During startup, pausing before outputs exist can leave a black frame and no audio device.
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            while (System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds < 1)
            {
                bool ready = false;
                await CommandAsync(() => ready = _player is null || !_player.IsPlaying ||
                    ((_player.AudioTrack < 0 || _player.Volume >= 0) && (_player.VideoTrack < 0 || _player.VoutCount > 0)), token).ConfigureAwait(false);
                if (ready) break;
                await Task.Delay(20, token).ConfigureAwait(false);
            }
        }
        await CommandAsync(() => { Volatile.Write(ref _pauseRequested, 1); _player?.SetPause(true); }, token).ConfigureAwait(false);
    }
    public Task StopAsync(CancellationToken token) => CommandAsync(() => { _player?.Stop(); Volatile.Write(ref _pauseRequested, 0); Interlocked.Exchange(ref _startPosition, 0); }, token);
    public Task SeekAsync(TimeSpan position, CancellationToken token) => CommandAsync(() =>
    {
        if (_player is not null && _player.IsSeekable) _player.Time = (long)Math.Clamp(position.TotalMilliseconds, 0, Math.Max(0, _player.Length));
    }, token);
    public Task SetRateAsync(double rate, CancellationToken token) => CommandAsync(() => _player?.SetRate((float)Math.Clamp(rate, .25, 4)), token);
    public Task SetVolumeAsync(double volume, CancellationToken token) => CommandAsync(() =>
    {
        Volatile.Write(ref _volumeRequested, (int)Math.Clamp(double.IsFinite(volume) ? volume * 100 : 80, 0, 100));
        if (_player is not null) _player.Volume = Volatile.Read(ref _volumeRequested);
    }, token);
    public IReadOnlyList<MediaTrack> GetTracks(TrackKind kind) => _player is null ? [] :
        (kind == TrackKind.Audio ? _player.AudioTrackDescription : _player.SpuDescription).Select(x => new MediaTrack(x.Id.ToString(), x.Name, kind)).ToList();
    public Task SelectTrackAsync(TrackKind kind, string trackId, CancellationToken token) => CommandAsync(() =>
    {
        if (_player is null) return;
        if (kind == TrackKind.Audio) _player.SetAudioTrack(int.Parse(trackId)); else _player.SetSpu(int.Parse(trackId));
    }, token);
    public Task AddSubtitleAsync(string path, CancellationToken token) => CommandAsync(() => _player?.AddSlave(MediaSlaveType.Subtitle, new Uri(Path.GetFullPath(path)).AbsoluteUri, true), token);
    public Task SetAudioDelayAsync(int milliseconds, CancellationToken token) => CommandAsync(() =>
    { if (_player is not null && !_player.SetAudioDelay(milliseconds * 1000L)) throw new InvalidOperationException("当前音轨无法设置延迟。"); }, token);
    public Task SetSubtitleDelayAsync(int milliseconds, CancellationToken token) => CommandAsync(() =>
    { if (_player is not null && !_player.SetSpuDelay(milliseconds * 1000L)) throw new InvalidOperationException("当前字幕无法设置延迟。"); }, token);
    public async Task TakeSnapshotAsync(string path, CancellationToken token)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        MediaPlayer? player = null;
        EventHandler<MediaPlayerSnapshotTakenEventArgs> handler = (_, e) =>
        { if (string.Equals(e.Filename, path, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) completion.TrySetResult(); };
        try
        {
            await CommandAsync(() =>
            {
                player = _player ?? throw new InvalidOperationException("请先播放视频。");
                player.SnapshotTaken += handler;
                if (!player.TakeSnapshot(0, path, 0, 0)) throw new InvalidOperationException("当前视频无法截图。");
            }, token);
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(8), token);
            if (!File.Exists(path) || new FileInfo(path).Length == 0) throw new IOException("截图文件没有写入。");
        }
        finally
        {
            try { await CommandAsync(() => { if (player is not null && ReferenceEquals(player, _player)) player.SnapshotTaken -= handler; }, CancellationToken.None); }
            catch (ObjectDisposedException) { }
        }
    }
    public async ValueTask DisposeAsync()
    {
        await CommandAsync(() => { _player?.Stop(); _player?.Dispose(); _media?.Dispose(); _lib?.Dispose(); _player = null; }, CancellationToken.None);
        _commands.Dispose();
    }
}
