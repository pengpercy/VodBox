using VodBox.Core;

namespace VodBox.Application;

public sealed record ActivePlaybackEngine(PlaybackEngineKind Kind, IPlaybackEngine Engine, string Reason);

/// <summary>One serialized playback session, two lazy engines and at most one automatic fallback.</summary>
public sealed class PlaybackEngineRouter(Func<PlaybackEngineKind, IPlaybackEngine> factory) : IPlaybackEngine, IPlaybackAdvancedControls
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<PlaybackEngineKind, (IPlaybackEngine Engine, EventHandler<PlaybackEvent> Handler)> _engines = [];
    private readonly object _backgroundLock = new();
    private readonly HashSet<Task> _background = [];
    private Binding? _binding;
    private PlaybackSnapshot _snapshot = new(PlaybackState.Idle, TimeSpan.Zero, TimeSpan.Zero, false);
    private int _disposed, _mode;
    private long _version;
    private double _volume = .8, _rate = 1;
    private int _audioDelay, _subtitleDelay;
    private bool _paused;
    public PlaybackEngineMode Mode => (PlaybackEngineMode)Volatile.Read(ref _mode);
    public PlaybackEngineKind? ActiveKind => Volatile.Read(ref _binding)?.Kind;
    public IPlaybackEngine? ActiveEngine => Volatile.Read(ref _binding)?.Engine;
    public PlaybackSnapshot Snapshot => Volatile.Read(ref _snapshot);
    public event EventHandler<PlaybackEvent>? StateChanged;
    public event EventHandler<ActivePlaybackEngine>? ActiveEngineChanged;
    private sealed class Binding(IPlaybackEngine engine, PlaybackEngineKind kind, PlaybackRequest request, long session, long version, PlaybackEngineMode mode, bool pause)
    {
        public readonly IPlaybackEngine Engine = engine;
        public readonly PlaybackEngineKind Kind = kind;
        public readonly PlaybackRequest Request = request;
        public readonly long Session = session, Version = version;
        public readonly PlaybackEngineMode Mode = mode;
        public int Opening = 1, FallbackUsed, ReadySettings, RestorePause = pause ? 1 : 0;
        public Exception? SurfaceError;
    }
    private bool Current(Binding binding) => ReferenceEquals(Volatile.Read(ref _binding), binding) && binding.Version == Volatile.Read(ref _version) && Volatile.Read(ref _disposed) == 0;
    private void Check() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    private void Publish(long session, PlaybackSnapshot snapshot) { Volatile.Write(ref _snapshot, snapshot); StateChanged?.Invoke(this, new(session, snapshot)); }
    private static PlaybackRequest Copy(PlaybackRequest request, long? position = null) => request with
    { Headers = new(request.Headers, StringComparer.OrdinalIgnoreCase), Subtitles = request.Subtitles.ToList(), StartPositionMs = position ?? request.StartPositionMs };

    public async Task OpenAsync(PlaybackRequest request, long sessionId, CancellationToken token)
    {
        Check(); ArgumentNullException.ThrowIfNull(request);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            Check(); token.ThrowIfCancellationRequested(); long version = Interlocked.Increment(ref _version);
            _paused = false;
            var copy = Copy(request); var mode = Mode; var selected = PlaybackEnginePolicy.Select(mode, copy.Uri);
            await OpenWithFallbackLockedAsync(copy, sessionId, version, mode, selected, false, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            var binding = Interlocked.Exchange(ref _binding, null);
            if (binding is not null) await binding.Engine.StopAsync(CancellationToken.None).ConfigureAwait(false);
            Publish(sessionId, new(PlaybackState.Idle, TimeSpan.Zero, TimeSpan.Zero, false)); throw;
        }
        finally { _gate.Release(); }
    }
    private async Task OpenWithFallbackLockedAsync(PlaybackRequest request, long session, long version, PlaybackEngineMode mode, PlaybackEngineSelection selected, bool paused, CancellationToken token)
    {
        try
        {
            var binding = await OpenSelectedLockedAsync(request, session, version, mode, selected, paused, false, token).ConfigureAwait(false);
            if (Snapshot.State == PlaybackState.Failed)
            {
                if (PlaybackEnginePolicy.Fallback(mode, selected.Kind, request.Uri) is { } alternative && Interlocked.CompareExchange(ref binding.FallbackUsed, 1, 0) == 0)
                    await OpenSelectedLockedAsync(request, session, version, mode, new(alternative, "启动失败，自动回退一次：" + Snapshot.Error), paused, true, token).ConfigureAwait(false);
                else throw new InvalidOperationException(Snapshot.Error ?? "播放内核启动失败。");
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            var binding = Volatile.Read(ref _binding);
            if (binding?.FallbackUsed == 1 || PlaybackEnginePolicy.Fallback(mode, selected.Kind, request.Uri) is not { } alternative)
            {
                if (binding?.SurfaceError is not null) await binding.Engine.StopAsync(CancellationToken.None).ConfigureAwait(false);
                Publish(session, Snapshot with { State = PlaybackState.Failed, Error = error.Message }); throw;
            }
            if (binding is not null) Interlocked.Exchange(ref binding.FallbackUsed, 1);
            try { await OpenSelectedLockedAsync(request, session, version, mode, new(alternative, "内核启动失败，自动回退一次：" + error.Message), paused, true, token).ConfigureAwait(false); }
            catch (Exception fallbackError) when (fallbackError is not OperationCanceledException)
            { Publish(session, Snapshot with { State = PlaybackState.Failed, Error = fallbackError.Message }); throw new AggregateException("两个播放内核均启动失败。", error, fallbackError); }
        }
    }
    private async Task<Binding> OpenSelectedLockedAsync(PlaybackRequest request, long session, long version, PlaybackEngineMode mode, PlaybackEngineSelection selected, bool paused, bool fallback, CancellationToken token)
    {
        var previous = Interlocked.Exchange(ref _binding, null);
        if (previous is not null) await previous.Engine.StopAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested(); if (version != _version) throw new OperationCanceledException("播放请求已替换。");
        if (!_engines.TryGetValue(selected.Kind, out var cached))
        {
            var engine = factory(selected.Kind); EventHandler<PlaybackEvent> handler = (_, args) => OnState(engine, args);
            engine.StateChanged += handler; cached = (engine, handler); _engines.Add(selected.Kind, cached);
        }
        var binding = new Binding(cached.Engine, selected.Kind, request, session, version, mode, paused) { FallbackUsed = fallback ? 1 : 0 };
        Volatile.Write(ref _binding, binding); Publish(session, new(PlaybackState.Loading, TimeSpan.FromMilliseconds(request.StartPositionMs), TimeSpan.Zero, false));
        ActiveEngineChanged?.Invoke(this, new(selected.Kind, cached.Engine, selected.Reason));
        try
        {
            await cached.Engine.OpenAsync(request, session, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            await ApplySettingsAsync(cached.Engine, selected.Kind, token).ConfigureAwait(false);
            if (Volatile.Read(ref binding.SurfaceError) is { } surfaceError)
                Publish(session, Snapshot with { State = PlaybackState.Failed, Error = "视频表面初始化失败：" + surfaceError.Message });
        }
        finally { Volatile.Write(ref binding.Opening, 0); }
        return binding;
    }
    private void OnState(IPlaybackEngine engine, PlaybackEvent args)
    {
        var binding = Volatile.Read(ref _binding);
        if (binding is null || !ReferenceEquals(engine, binding.Engine) || args.SessionId != binding.Session || !Current(binding)) return;
        if (Volatile.Read(ref binding.SurfaceError) is { } surfaceError)
            args = args with { Snapshot = args.Snapshot with { State = PlaybackState.Failed, Error = "视频表面初始化失败：" + surfaceError.Message } };
        Publish(args.SessionId, args.Snapshot);
        if (args.Snapshot.State == PlaybackState.Playing && Interlocked.Exchange(ref binding.ReadySettings, 1) == 0)
            Background(binding, async () =>
            {
                await _gate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
                try
                {
                    if (!Current(binding)) return;
                    await ApplySettingsAsync(binding.Engine, binding.Kind, _lifetime.Token).ConfigureAwait(false);
                    if (Interlocked.Exchange(ref binding.RestorePause, 0) == 1 && _paused)
                    {
                        await binding.Engine.PauseAsync(_lifetime.Token).ConfigureAwait(false);
                        if (!binding.Request.IsLive && binding.Request.StartPositionMs > 0)
                            await binding.Engine.SeekAsync(TimeSpan.FromMilliseconds(binding.Request.StartPositionMs), _lifetime.Token).ConfigureAwait(false);
                    }
                }
                finally { _gate.Release(); }
            });
        if (args.Snapshot.State == PlaybackState.Failed && Volatile.Read(ref binding.Opening) == 0 &&
            PlaybackEnginePolicy.Fallback(Mode, binding.Kind, binding.Request.Uri) is { } alternative && Interlocked.CompareExchange(ref binding.FallbackUsed, 1, 0) == 0)
            Background(binding, () => RecoverAsync(binding, alternative, args.Snapshot));
    }
    private async Task RecoverAsync(Binding binding, PlaybackEngineKind alternative, PlaybackSnapshot failed)
    {
        await _gate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        try
        {
            if (!Current(binding) || Mode != PlaybackEngineMode.Automatic) return;
            var request = Copy(binding.Request, binding.Request.IsLive ? 0 : Math.Max(binding.Request.StartPositionMs, (long)failed.Position.TotalMilliseconds));
            await OpenSelectedLockedAsync(request, binding.Session, binding.Version, Mode,
                new(alternative, "播放失败，自动回退一次：" + failed.Error), _paused, true, _lifetime.Token).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }
    public void ReportSurfaceFailure(IPlaybackEngine engine, Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        var binding = Volatile.Read(ref _binding);
        if (binding is null || !ReferenceEquals(binding.Engine, engine) || !Current(binding)) return;
        Volatile.Write(ref binding.SurfaceError, error);
        var failed = Snapshot with { State = PlaybackState.Failed, Error = "视频表面初始化失败：" + error.Message };
        OnState(engine, new(binding.Session, failed));
        // During OpenAsync the stored error is checked before the startup fallback decision.
        if (Volatile.Read(ref binding.Opening) == 0 && PlaybackEnginePolicy.Fallback(Mode, binding.Kind, binding.Request.Uri) is null)
            Background(binding, async () =>
            {
                await _gate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
                try { if (Current(binding)) await binding.Engine.StopAsync(_lifetime.Token).ConfigureAwait(false); }
                finally { _gate.Release(); }
            });
    }
    private async Task ApplySettingsAsync(IPlaybackEngine engine, PlaybackEngineKind kind, CancellationToken token)
    {
        await engine.SetVolumeAsync(_volume, token).ConfigureAwait(false); await engine.SetRateAsync(_rate, token).ConfigureAwait(false);
        if (engine is IPlaybackAdvancedControls advanced)
        {
            // VLC cannot apply delays to an absent track. Keep preferences for the next suitable media.
            if (kind != PlaybackEngineKind.LibVlc || engine.GetTracks(TrackKind.Audio).Any(x => x.Id is not ("-1" or "no")))
                await advanced.SetAudioDelayAsync(_audioDelay, token).ConfigureAwait(false);
            if (kind != PlaybackEngineKind.LibVlc || engine.GetTracks(TrackKind.Subtitle).Any(x => x.Id is not ("-1" or "no")))
                await advanced.SetSubtitleDelayAsync(_subtitleDelay, token).ConfigureAwait(false);
        }
    }
    private void Background(Binding origin, Func<Task> operation)
    {
        var task = Task.Run(async () =>
        {
            try { await operation().ConfigureAwait(false); }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested || origin.Version != Volatile.Read(ref _version)) { }
            catch (Exception error) { if (Volatile.Read(ref _binding) is { } binding && binding.Version == origin.Version && Current(binding)) Publish(binding.Session, Snapshot with { State = PlaybackState.Failed, Error = error.Message }); }
        });
        lock (_backgroundLock) _background.Add(task);
        _ = task.ContinueWith(done => { lock (_backgroundLock) _background.Remove(done); }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
    public async Task ChangeModeAsync(PlaybackEngineMode mode, CancellationToken token)
    {
        if (mode is not (PlaybackEngineMode.Automatic or PlaybackEngineMode.LibVlc or PlaybackEngineMode.Mpv)) throw new ArgumentOutOfRangeException(nameof(mode));
        Check(); await _gate.WaitAsync(token).ConfigureAwait(false);
        long? switchingSession = null;
        try
        {
            Check(); token.ThrowIfCancellationRequested(); var binding = Volatile.Read(ref _binding); var selected = binding is null ? null : PlaybackEnginePolicy.Select(mode, binding.Request.Uri);
            if (mode == Mode) return; Volatile.Write(ref _mode, (int)mode);
            if (binding is null || selected!.Kind == binding.Kind) return;
            long version = Interlocked.Increment(ref _version); var request = Copy(binding.Request, binding.Request.IsLive ? 0 : (long)Snapshot.Position.TotalMilliseconds);
            _paused = _paused || Snapshot.State == PlaybackState.Paused;
            switchingSession = binding.Session;
            await OpenWithFallbackLockedAsync(request, binding.Session, version, mode, selected, _paused, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (switchingSession is not null)
        {
            var binding = Interlocked.Exchange(ref _binding, null);
            if (binding is not null) await binding.Engine.StopAsync(CancellationToken.None).ConfigureAwait(false);
            Publish(switchingSession.Value, new(PlaybackState.Idle, TimeSpan.Zero, TimeSpan.Zero, false)); throw;
        }
        finally { _gate.Release(); }
    }
    private async Task ControlAsync(Func<IPlaybackEngine, Task> operation, CancellationToken token, Action? settings = null)
    {
        Check(); await _gate.WaitAsync(token).ConfigureAwait(false);
        try { Check(); token.ThrowIfCancellationRequested(); settings?.Invoke(); if (_binding is { } binding) await operation(binding.Engine).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }
    public Task PlayAsync(CancellationToken token) => ControlAsync(engine => engine.PlayAsync(token), token, () => _paused = false);
    public Task PauseAsync(CancellationToken token) => ControlAsync(engine => engine.PauseAsync(token), token, () => _paused = true);
    public Task SeekAsync(TimeSpan position, CancellationToken token) => ControlAsync(engine => engine.SeekAsync(position, token), token);
    public Task SetRateAsync(double rate, CancellationToken token) => ControlAsync(engine => engine.SetRateAsync(_rate, token), token, () => _rate = Math.Clamp(double.IsFinite(rate) ? rate : 1, .25, 4));
    public Task SetVolumeAsync(double volume, CancellationToken token) => ControlAsync(engine => engine.SetVolumeAsync(_volume, token), token, () => _volume = Math.Clamp(double.IsFinite(volume) ? volume : .8, 0, 1));
    private static IPlaybackAdvancedControls Advanced(IPlaybackEngine engine) => engine as IPlaybackAdvancedControls ?? throw new NotSupportedException("当前内核不支持此控制。");
    private bool CanApplyDelay(IPlaybackEngine engine, TrackKind kind) => ActiveKind != PlaybackEngineKind.LibVlc || engine.GetTracks(kind).Any(x => x.Id is not ("-1" or "no"));
    public Task SetAudioDelayAsync(int milliseconds, CancellationToken token) => ControlAsync(engine => CanApplyDelay(engine, TrackKind.Audio) ? Advanced(engine).SetAudioDelayAsync(_audioDelay, token) : Task.CompletedTask, token, () => _audioDelay = Math.Clamp(milliseconds, -10000, 10000));
    public Task SetSubtitleDelayAsync(int milliseconds, CancellationToken token) => ControlAsync(engine => CanApplyDelay(engine, TrackKind.Subtitle) ? Advanced(engine).SetSubtitleDelayAsync(_subtitleDelay, token) : Task.CompletedTask, token, () => _subtitleDelay = Math.Clamp(milliseconds, -10000, 10000));
    public Task TakeSnapshotAsync(string path, CancellationToken token) => ControlAsync(engine => Advanced(engine).TakeSnapshotAsync(path, token), token);
    public IReadOnlyList<MediaTrack> GetTracks(TrackKind kind) => Volatile.Read(ref _binding)?.Engine.GetTracks(kind) ?? [];
    public Task SelectTrackAsync(TrackKind kind, string trackId, CancellationToken token) => ControlAsync(engine => engine.SelectTrackAsync(kind, trackId, token), token);
    public Task AddSubtitleAsync(string path, CancellationToken token) => ControlAsync(async engine =>
    {
        await engine.AddSubtitleAsync(path, token).ConfigureAwait(false);
        if (engine is IPlaybackAdvancedControls advanced) await advanced.SetSubtitleDelayAsync(_subtitleDelay, token).ConfigureAwait(false);
    }, token);
    public async Task StopAsync(CancellationToken token)
    {
        Check(); await _gate.WaitAsync(token).ConfigureAwait(false);
        try { Check(); token.ThrowIfCancellationRequested(); Interlocked.Increment(ref _version); var binding = Interlocked.Exchange(ref _binding, null); if (binding is not null) await binding.Engine.StopAsync(token).ConfigureAwait(false);
            _paused = false; Publish(binding?.Session ?? 0, new(PlaybackState.Idle, TimeSpan.Zero, TimeSpan.Zero, false)); }
        finally { _gate.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel(); Interlocked.Increment(ref _version); await _gate.WaitAsync().ConfigureAwait(false);
        var errors = new List<Exception>();
        try
        {
            Volatile.Write(ref _binding, null);
            foreach (var entry in _engines.Values)
            {
                entry.Engine.StateChanged -= entry.Handler;
                try { await entry.Engine.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { errors.Add(error); }
            }
            _engines.Clear();
        }
        finally { _gate.Release(); }
        Task[] pending; lock (_backgroundLock) pending = _background.ToArray(); await Task.WhenAll(pending).ConfigureAwait(false); _lifetime.Dispose();
        if (errors.Count > 0) throw new AggregateException("播放内核释放失败。", errors);
    }
}
