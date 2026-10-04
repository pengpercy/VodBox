using LibVLCSharp.Shared;
using VodBox.Core;
using MediaTrack = VodBox.Core.MediaTrack;
using VlcCore = LibVLCSharp.Shared.Core;

namespace VodBox.Playback.LibVlc;

public sealed class LibVlcEngine(bool headless = false, bool rebuildPluginCache = false) : IPlaybackEngine
{
    private readonly SemaphoreSlim _commands = new(1, 1);
    private LibVLC? _lib;
    private MediaPlayer? _player;
    private Media? _media;
    private long _session;
    private long _startPosition;
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
        if (Directory.Exists(Path.Combine(bundled, "plugins")))
            NativeEnvironment.SetPluginPath(Path.Combine(bundled, "plugins"));
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
        _player.Buffering += (_, e) => { if (e.Cache < 100) Update(PlaybackState.Buffering); else if (_player.IsPlaying) Update(PlaybackState.Playing); };
        Initialized?.Invoke(this, EventArgs.Empty);
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
    public Task OpenAsync(PlaybackRequest request, long sessionId, CancellationToken token) => CommandAsync(() =>
    {
        Initialize();
        _player!.Stop(); _player.Media = null; _media?.Dispose();
        _session = sessionId;
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
    }, token);
    public Task PlayAsync(CancellationToken token) => CommandAsync(() => _player?.Play(), token);
    public Task PauseAsync(CancellationToken token) => CommandAsync(() => _player?.SetPause(true), token);
    public Task StopAsync(CancellationToken token) => CommandAsync(() => { _player?.Stop(); Interlocked.Exchange(ref _startPosition, 0); }, token);
    public Task SeekAsync(TimeSpan position, CancellationToken token) => CommandAsync(() =>
    {
        if (_player is not null && _player.IsSeekable) _player.Time = (long)Math.Clamp(position.TotalMilliseconds, 0, Math.Max(0, _player.Length));
    }, token);
    public Task SetRateAsync(double rate, CancellationToken token) => CommandAsync(() => _player?.SetRate((float)Math.Clamp(rate, .25, 4)), token);
    public Task SetVolumeAsync(double volume, CancellationToken token) => CommandAsync(() => { if (_player is not null) _player.Volume = (int)Math.Clamp(volume * 100, 0, 100); }, token);
    public IReadOnlyList<MediaTrack> GetTracks(TrackKind kind) => _player is null ? [] :
        (kind == TrackKind.Audio ? _player.AudioTrackDescription : _player.SpuDescription).Select(x => new MediaTrack(x.Id.ToString(), x.Name, kind)).ToList();
    public Task SelectTrackAsync(TrackKind kind, string trackId, CancellationToken token) => CommandAsync(() =>
    {
        if (_player is null) return;
        if (kind == TrackKind.Audio) _player.SetAudioTrack(int.Parse(trackId)); else _player.SetSpu(int.Parse(trackId));
    }, token);
    public Task AddSubtitleAsync(string path, CancellationToken token) => CommandAsync(() => _player?.AddSlave(MediaSlaveType.Subtitle, new Uri(Path.GetFullPath(path)).AbsoluteUri, true), token);
    public async ValueTask DisposeAsync()
    {
        await CommandAsync(() => { _player?.Stop(); _player?.Dispose(); _media?.Dispose(); _lib?.Dispose(); _player = null; }, CancellationToken.None);
        _commands.Dispose();
    }
}
