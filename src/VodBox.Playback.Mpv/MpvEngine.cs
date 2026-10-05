using System.Globalization;
using VodBox.Core;

namespace VodBox.Playback.Mpv;

/// <summary>Lazy native engine with serialized commands and observed state, suitable for the shared coordinator.</summary>
public sealed class MpvEngine : IPlaybackEngine
{
    private readonly Func<IMpvClient> _factory;
    private readonly SemaphoreSlim _commands = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private IMpvClient? _client;
    private Task? _pump;
    private int _disposed;
    private long _session, _startPosition;
    private bool _loaded, _paused, _buffering;
    private double _volume = .8, _rate = 1;
    private int _audioDelay, _subtitleDelay;
    private IReadOnlyList<MediaTrack> _tracks = [];
    private IReadOnlyList<SubtitleSource> _subtitles = [];
    private PlaybackSnapshot _snapshot = new(PlaybackState.Idle, TimeSpan.Zero, TimeSpan.Zero, false);
    public PlaybackSnapshot Snapshot => Volatile.Read(ref _snapshot);
    public IMpvClient? Client => Volatile.Read(ref _client);
    public event EventHandler? Initialized;
    public event EventHandler<PlaybackEvent>? StateChanged;

    public MpvEngine(bool headless = false, Func<IMpvClient>? factory = null)
    {
        _factory = factory ?? (() => new MpvClient(new Dictionary<string, string>
        {
            ["vo"] = headless ? "null" : "libmpv", ["ao"] = headless ? "null" : "auto",
            ["hwdec"] = headless ? "no" : "auto-safe"
        }));
    }
    private void Initialize()
    {
        if (_client is not null) return;
        var client = _factory();
        try
        {
            client.Observe("time-pos", 1, MpvPropertyFormat.Double);
            client.Observe("duration", 2, MpvPropertyFormat.Double);
            client.Observe("pause", 3, MpvPropertyFormat.Flag);
            client.Observe("seekable", 4, MpvPropertyFormat.Flag);
            client.Observe("paused-for-cache", 5, MpvPropertyFormat.Flag);
            client.Observe("track-list", 6, MpvPropertyFormat.None);
            _client = client; Initialized?.Invoke(this, EventArgs.Empty);
            _pump = Task.Run(PumpAsync);
        }
        catch { _client = null; client.Dispose(); throw; }
    }
    private async Task ExecuteAsync(Action action, CancellationToken token)
    {
        Check(); await _commands.WaitAsync(token).ConfigureAwait(false);
        try { Check(); token.ThrowIfCancellationRequested(); await Task.Run(action, CancellationToken.None).ConfigureAwait(false); }
        finally { _commands.Release(); }
    }
    public Task OpenAsync(PlaybackRequest request, long sessionId, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Uri.TryCreate(request.Uri, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "file" or "rtsp" or "rtmp" or "udp" or "rtp"))
            throw new InvalidDataException("mpv 需要明确的媒体 URI。");
        foreach (var header in request.Headers)
        {
            if (header.Value.IndexOfAny(['\r', '\n', '\0']) >= 0) throw new InvalidDataException("媒体请求头含控制字符。");
            if (!header.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase) && !header.Key.Equals("Referer", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException($"请求头 {header.Key} 需要通过媒体代理传递。");
        }
        return ExecuteAsync(() =>
        {
            Initialize(); var client = _client!;
            client.Command("stop"); for (int i = 0; i < 256 && client.PollEvent().Id != 0; i++) { }
            _session = sessionId; _loaded = _paused = _buffering = false; _startPosition = Math.Max(0, request.StartPositionMs);
            _subtitles = request.Subtitles.ToArray(); Volatile.Write(ref _tracks, []);
            SetSnapshot(new(PlaybackState.Loading, TimeSpan.Zero, TimeSpan.Zero, false));
            string agent = "", referer = "";
            foreach (var header in request.Headers)
                if (header.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase)) agent = header.Value; else referer = header.Value;
            // Reset both per-open options, so headers cannot leak from a previous source.
            client.Command("set", "user-agent", agent); client.Command("set", "referrer", referer);
            client.Command("set", "pause", "no"); client.Command("set", "volume", Number(_volume * 100)); client.Command("set", "speed", Number(_rate));
            client.Command("set", "audio-delay", Number(_audioDelay / 1000d)); client.Command("set", "sub-delay", Number(_subtitleDelay / 1000d));
            try { client.Command("loadfile", uri.AbsoluteUri, "replace"); }
            catch (Exception error) { SetSnapshot(Snapshot with { State = PlaybackState.Failed, Error = error.Message }); throw; }
        }, token);
    }
    public Task PlayAsync(CancellationToken token) => ExecuteAsync(() => _client?.Command("set", "pause", "no"), token);
    public Task PauseAsync(CancellationToken token) => ExecuteAsync(() => _client?.Command("set", "pause", "yes"), token);
    public Task StopAsync(CancellationToken token) => ExecuteAsync(() =>
    {
        _client?.Command("stop"); _loaded = false; _startPosition = 0; _subtitles = []; Volatile.Write(ref _tracks, []);
        SetSnapshot(new(PlaybackState.Idle, TimeSpan.Zero, TimeSpan.Zero, false));
    }, token);
    public Task SeekAsync(TimeSpan position, CancellationToken token) => ExecuteAsync(() =>
    {
        if (_loaded && Snapshot.CanSeek) _client!.Command("seek", Number(Math.Clamp(position.TotalSeconds, 0,
            Snapshot.Duration > TimeSpan.Zero ? Snapshot.Duration.TotalSeconds : double.MaxValue)), "absolute+exact");
    }, token);
    public Task SetRateAsync(double rate, CancellationToken token) => ExecuteAsync(() =>
    { _rate = Math.Clamp(double.IsFinite(rate) ? rate : 1, .25, 4); _client?.Command("set", "speed", Number(_rate)); }, token);
    public Task SetVolumeAsync(double volume, CancellationToken token) => ExecuteAsync(() =>
    { _volume = Math.Clamp(double.IsFinite(volume) ? volume : .8, 0, 1); _client?.Command("set", "volume", Number(_volume * 100)); }, token);
    public Task SetAudioDelayAsync(int milliseconds, CancellationToken token) => ExecuteAsync(() =>
    { _audioDelay = Math.Clamp(milliseconds, -10000, 10000); _client?.Command("set", "audio-delay", Number(_audioDelay / 1000d)); }, token);
    public Task SetSubtitleDelayAsync(int milliseconds, CancellationToken token) => ExecuteAsync(() =>
    { _subtitleDelay = Math.Clamp(milliseconds, -10000, 10000); _client?.Command("set", "sub-delay", Number(_subtitleDelay / 1000d)); }, token);
    public IReadOnlyList<MediaTrack> GetTracks(TrackKind kind) => Volatile.Read(ref _tracks).Where(x => x.Kind == kind).ToArray();
    public Task SelectTrackAsync(TrackKind kind, string trackId, CancellationToken token)
    {
        if (trackId != "no" && (!long.TryParse(trackId, NumberStyles.None, CultureInfo.InvariantCulture, out long id) || id <= 0))
            throw new InvalidDataException("mpv 轨道编号无效。");
        if (kind is not (TrackKind.Audio or TrackKind.Subtitle)) throw new ArgumentOutOfRangeException(nameof(kind));
        return ExecuteAsync(() => _client?.Command("set", kind == TrackKind.Audio ? "aid" : "sid", trackId), token);
    }
    public Task AddSubtitleAsync(string path, CancellationToken token) => ExecuteAsync(() =>
    { if (_client is null || !_loaded) throw new InvalidOperationException("请先打开媒体。"); _client.Command("sub-add", Path.GetFullPath(path), "select"); }, token);
    public Task TakeSnapshotAsync(string path, CancellationToken token) => ExecuteAsync(() =>
    { if (_client is null || !_loaded) throw new InvalidOperationException("请先打开视频。"); _client.Command("screenshot-to-file", Path.GetFullPath(path), "video"); }, token);

    private async Task PumpAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
            while (await timer.WaitForNextTickAsync(_shutdown.Token).ConfigureAwait(false))
            {
                await _commands.WaitAsync(_shutdown.Token).ConfigureAwait(false);
                try
                {
                    var before = Snapshot; var snapshot = before;
                    for (int i = 0; i < 256; i++)
                    {
                        var item = _client!.PollEvent(); if (item.Id == 0) break;
                        snapshot = Process(item, snapshot);
                    }
                    if (snapshot != before) SetSnapshot(snapshot);
                }
                catch (Exception error) { SetSnapshot(Snapshot with { State = PlaybackState.Failed, Error = error.Message }); }
                finally { _commands.Release(); }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
    }
    private PlaybackSnapshot Process(MpvEvent item, PlaybackSnapshot snapshot)
    {
        if (item.Id == 8)
        {
            _loaded = true; RefreshTracks();
            if (_startPosition > 0) { _client!.Command("seek", Number(_startPosition / 1000d), "absolute+exact"); _startPosition = 0; }
            foreach (var subtitle in _subtitles) _client!.Command("sub-add", subtitle.Uri, "auto", subtitle.Name, subtitle.Language ?? "");
            _subtitles = [];
            return snapshot with { State = CurrentState(), Error = null };
        }
        if (item.Id == 7)
        {
            if (item.EndReason == 2 && !_loaded && snapshot.State == PlaybackState.Loading) return snapshot;
            _loaded = false;
            return snapshot with { State = item.EndReason == 0 ? PlaybackState.Ended : item.EndReason == 4 ? PlaybackState.Failed : PlaybackState.Idle,
                Error = item.EndReason == 4 ? $"mpv 无法播放此媒体（错误 {item.EndError}）。" : null };
        }
        if (item.Id != 22) return snapshot;
        switch (item.PropertyName)
        {
            case "time-pos" when item.PropertyDouble is { } position && double.IsFinite(position): return snapshot with { Position = Seconds(position) };
            case "duration" when item.PropertyDouble is { } duration && double.IsFinite(duration): return snapshot with { Duration = Seconds(duration) };
            case "seekable" when item.PropertyFlag is { } seekable: return snapshot with { CanSeek = seekable };
            case "pause" when item.PropertyFlag is { } paused: _paused = paused; break;
            case "paused-for-cache" when item.PropertyFlag is { } buffering: _buffering = buffering; break;
            case "track-list": if (_loaded) RefreshTracks(); break;
        }
        return _loaded ? snapshot with { State = CurrentState() } : snapshot;
    }
    private PlaybackState CurrentState() => _paused ? PlaybackState.Paused : _buffering ? PlaybackState.Buffering : PlaybackState.Playing;
    private void RefreshTracks()
    {
        int count = (int)Math.Clamp(_client!.GetDouble("track-list/count") ?? 0, 0, 256); var tracks = new List<MediaTrack>();
        for (int i = 0; i < count; i++)
        {
            string prefix = $"track-list/{i}/"; string? type = _client.GetString(prefix + "type");
            if (type is not ("audio" or "sub")) continue;
            long id = (long)(_client.GetDouble(prefix + "id") ?? 0); if (id <= 0) continue;
            var kind = type == "audio" ? TrackKind.Audio : TrackKind.Subtitle;
            tracks.Add(new(id.ToString(CultureInfo.InvariantCulture), _client.GetString(prefix + "title") ?? _client.GetString(prefix + "lang") ?? $"{type} {id}", kind));
        }
        tracks.Add(new("no", "关闭字幕", TrackKind.Subtitle)); Volatile.Write(ref _tracks, tracks.ToArray());
    }
    private void SetSnapshot(PlaybackSnapshot snapshot) { Volatile.Write(ref _snapshot, snapshot); StateChanged?.Invoke(this, new(_session, snapshot)); }
    private static TimeSpan Seconds(double value) => TimeSpan.FromSeconds(Math.Clamp(value, 0, TimeSpan.MaxValue.TotalSeconds / 2));
    private static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    private void Check() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _shutdown.Cancel(); await _commands.WaitAsync().ConfigureAwait(false);
        try { if (_client is { } client) await Task.Run(client.Dispose).ConfigureAwait(false); _client = null; }
        finally { _commands.Release(); }
        if (_pump is not null) await _pump.ConfigureAwait(false);
        _shutdown.Dispose();
    }
}
