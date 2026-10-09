using System.Globalization;
using VodBox.Core;

namespace VodBox.Playback.Mpv;

/// <summary>惰性初始化的 mpv 引擎：命令串行化、属性观察驱动状态、SafeHandle 生命周期安全。</summary>
public sealed class MpvEngine : IPlaybackEngine
{
    private readonly Func<IMpvClient> _factory;
    private readonly bool _headless;
    private readonly bool _waitForVideoSurface;
    private readonly TaskCompletionSource _videoSurfaceReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _commands = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private IMpvClient? _client;
    private Task? _pump;
    private int _disposed;
    private long _session, _startPosition;
    private bool _loaded, _paused, _buffering;
    private int _volume = 80;
    private double _rate = 1;
    private double _aspect = -1;
    private IReadOnlyList<MediaTrack> _tracks = [];
    private PlaybackSnapshot _snapshot = new(PlaybackState.Idle, TimeSpan.Zero, TimeSpan.Zero, false);

    public PlaybackSnapshot Snapshot => Volatile.Read(ref _snapshot);

    /// <summary>已初始化的低层客户端（未初始化时为 null，供探测/诊断用）。</summary>
    public IMpvClient? Client => Volatile.Read(ref _client);

    /// <summary>libmpv 可用性。headless 模式下立即探测；GUI 模式下等渲染面安装后确认为真。</summary>
    public bool Available => _headless || _client is not null;

    public event EventHandler? Initialized;
    public event EventHandler<PlaybackEvent>? StateChanged;

    /// <summary>设计时专用：永不 P/Invoke、永不初始化的空引擎（Available=false 自然降级，事件/命令全部空转）。</summary>
    public static MpvEngine DesignDisabled() => new(factory: DesignUnavailableFactory);

    /// <summary>设计时兜底：任何初始化尝试都失败为「不可用」，绝不触碰 libmpv。</summary>
    private static IMpvClient DesignUnavailableFactory() =>
        throw new InvalidOperationException("设计时预览环境没有 libmpv。");

    public MpvEngine(bool headless = false, Func<IMpvClient>? factory = null, bool waitForVideoSurface = false)
    {
        _headless = headless;
        _waitForVideoSurface = waitForVideoSurface;
        _factory=factory??(()=>new MpvClient(DefaultOptions(headless)));
        if (headless)
        {
            try
            {
                Initialize();
            }
            catch (Exception)
            {
                // libmpv 缺失：headless 探测失败时静默降级为不可用。
                _client = null;
            }
        }
    }

    internal static IReadOnlyDictionary<string,string> DefaultOptions(bool headless)
    {
        var options=new Dictionary<string,string>{["vo"]=headless?"null":"libmpv",["hwdec"]=headless?"no":"auto-safe"};
        if(headless)options["ao"]="null"; // GUI不指定ao，交由mpv选择可用的系统驱动。
        return options;
    }

    /// <summary>渲染面已创建并安装好 render context（消除 loadfile 早于渲染上下文导致无声丢画面的竞态）。</summary>
    public void NotifyVideoSurfaceReady() => _videoSurfaceReady.TrySetResult();
    public void NotifyVideoSurfaceFailure(Exception error)
    {
        VodBoxLog.Error("mpv", "视频渲染面报告失败", error);
        _videoSurfaceReady.TrySetException(error);
    }

    private void Initialize()
    {
        if (_client is not null) return;
        IMpvClient client;
        try
        {
            client = _factory();
        }
        catch (DllNotFoundException error)
        {
            // 开发构建的 bin 目录默认没有 libmpv；把解决办法直接写进错误，避免只看到 dlopen 噪声。
            throw new InvalidOperationException(
                "未找到 libmpv 动态库。开发运行时请执行 bash build/dev-natives.sh 准备原生库" +
                "（或设置 VODBOX_MPV_LIB 指向 libmpv，或改用打包版本）。", error);
        }
        try
        {
            client.Observe("time-pos", 1, MpvPropertyFormat.Double);
            client.Observe("duration", 2, MpvPropertyFormat.Double);
            client.Observe("pause", 3, MpvPropertyFormat.Flag);
            client.Observe("seekable", 4, MpvPropertyFormat.Flag);
            client.Observe("paused-for-cache", 5, MpvPropertyFormat.Flag);
            client.Observe("track-list", 6, MpvPropertyFormat.None);
            // 订阅 mpv 日志：默认 warn（错误/警告是排障关键，必须落盘），
            // 开启诊断或 verbose 时升级到 info 以获得完整上下文。
            if(client is MpvClient logClient)
            {
                var verbose = VodBoxLog.Verbose || Environment.GetEnvironmentVariable("VODBOX_MPV_DIAGNOSTICS")=="1";
                logClient.RequestLogs(verbose ? "info" : "warn");
            }
            _client = client; Initialized?.Invoke(this, EventArgs.Empty);
            _pump = Task.Run(PumpAsync);
        }
        catch (Exception error)
        {
            VodBoxLog.Error("mpv", "初始化 libmpv 客户端失败（常见原因：缺少 libmpv 动态库）", error);
            _client = null; client.Dispose(); throw;
        }
    }

    private async Task ExecuteAsync(Action action, CancellationToken token)
    {
        Check(); await _commands.WaitAsync(token).ConfigureAwait(false);
        try { Check(); token.ThrowIfCancellationRequested(); await Task.Run(action, CancellationToken.None).ConfigureAwait(false); }
        finally { _commands.Release(); }
    }

    public async Task OpenAsync(PlaybackRequest request, long sessionId, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Uri.TryCreate(request.Uri, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https" or "file" or "rtsp" or "rtmp" or "udp" or "rtp"))
            throw new InvalidDataException("mpv 需要明确的媒体 URI。");
        VodBoxLog.Event("mpv", "open", ("session", sessionId.ToString()), ("uri", request.Uri),
            ("live", request.IsLive.ToString()), ("headers", request.Headers.Count.ToString()),
            ("startMs", request.StartPositionMs.ToString()));
        foreach (var header in request.Headers)
        {
            if (header.Value.IndexOfAny(['\r', '\n', '\0']) >= 0) throw new InvalidDataException("媒体请求头含控制字符。");
            // mpv 只接受全局 UA/Referer；其余请求头需走媒体代理（S7 实现），此处明确拒绝而非静默丢弃。
            if (!header.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase)
                && !header.Key.Equals("Referer", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException($"请求头 {header.Key} 需要通过媒体代理传递（S7 实现）。");
        }
        if (_waitForVideoSurface)
        {
            await ExecuteAsync(Initialize, token).ConfigureAwait(false);
            // loadfile 早于 render-context 创建时 libmpv 会静默丢视频。
            try
            {
                await _videoSurfaceReady.Task.WaitAsync(TimeSpan.FromSeconds(8), token).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                VodBoxLog.Error("mpv", "等待 OpenGL 渲染面超时（8 秒）：窗口可能未显示或渲染初始化失败");
                throw;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                VodBoxLog.Error("mpv", "OpenGL 渲染面创建失败", error);
                throw;
            }
        }
        await ExecuteAsync(() =>
        {
            Initialize();
            var client = _client!;
            client.Command("stop");
            for (int i = 0; i < 256 && client.PollEvent().Id != 0; i++) { }
            _session = sessionId; _loaded = _paused = _buffering = false; _startPosition = Math.Max(0, request.StartPositionMs);
            Volatile.Write(ref _tracks, []);
            SetSnapshot(new PlaybackSnapshot(PlaybackState.Loading, TimeSpan.Zero, TimeSpan.Zero, false));
            string agent = "", referer = "";
            foreach (var header in request.Headers)
            {
                if (header.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase)) agent = header.Value;
                else referer = header.Value;
            }
            // 每次打开都重置这两个全局选项，避免上一个源的请求头泄漏到下一个。
            client.Command("set", "user-agent", agent); client.Command("set", "referrer", referer);
            client.Command("set", "pause", "no"); client.Command("set", "volume", _volume.ToString(CultureInfo.InvariantCulture));
            client.Command("set", "speed", Number(_rate));
            client.Command("set", "video-aspect-override", Number(_aspect));
            try { client.Command("loadfile", uri.AbsoluteUri, "replace"); }
            catch (Exception error)
            {
                VodBoxLog.Error("mpv", $"loadfile 失败 uri={request.Uri}", error);
                SetSnapshot(Snapshot with { State = PlaybackState.Failed, Error = error.Message });
                throw;
            }
        }, token);
        VodBoxLog.Info("mpv", $"已提交 loadfile session={sessionId}");
    }

    public Task PlayAsync(CancellationToken token = default) =>
        ExecuteAsync(() => _client?.Command("set", "pause", "no"), token);

    public Task PauseAsync(CancellationToken token = default) =>
        ExecuteAsync(() => _client?.Command("set", "pause", "yes"), token);

    public Task StopAsync(CancellationToken token = default) => ExecuteAsync(() =>
    {
        VodBoxLog.Info("mpv", $"stop（结束会话 {_session}）");
        _client?.Command("stop"); _loaded = false; _startPosition = 0;
        Volatile.Write(ref _tracks, []);
        SetSnapshot(new PlaybackSnapshot(PlaybackState.Idle, TimeSpan.Zero, TimeSpan.Zero, false));
    }, token);

    public Task SeekToAsync(TimeSpan position, CancellationToken token = default) => ExecuteAsync(() =>
    {
        if (_loaded && Snapshot.CanSeek)
            _client!.Command("seek",Number(Math.Clamp(position.TotalSeconds,0,
                Snapshot.Duration>TimeSpan.Zero?Snapshot.Duration.TotalSeconds:double.MaxValue)),"absolute+exact");
    }, token);

    public Task SeekByAsync(TimeSpan delta, CancellationToken token = default) => ExecuteAsync(() =>
    {
        if (_loaded) _client!.Command("seek", Number(delta.TotalSeconds), "relative");
    }, token);

    public Task SetRateAsync(double rate, CancellationToken token = default) => ExecuteAsync(() =>
    { _rate = Math.Clamp(double.IsFinite(rate) ? rate : 1, .25, 4); _client?.Command("set", "speed", Number(_rate)); }, token);

    public Task SetVolumeAsync(int volume, CancellationToken token = default) => ExecuteAsync(() =>
    { _volume = Math.Clamp(volume, 0, 100); _client?.Command("set", "volume", _volume.ToString(CultureInfo.InvariantCulture)); }, token);

    public Task SetAspectRatioAsync(double? ratio, CancellationToken token = default)
    {
        if (ratio is { } value && (!double.IsFinite(value) || value <= 0))
            throw new ArgumentOutOfRangeException(nameof(ratio));
        return ExecuteAsync(() =>
        {
            _aspect = ratio ?? -1;
            _client?.Command("set", "video-aspect-override", Number(_aspect));
        }, token);
    }

    public Task LoadSubtitleAsync(string path, long sessionId, CancellationToken token = default)
    {
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path))
            throw new FileNotFoundException("字幕文件不存在或不是本地绝对路径。", path);
        return ExecuteAsync(() =>
        {
            if (sessionId != _session) return;
            if (!_loaded || _client is null) throw new InvalidOperationException("媒体尚未加载完成。 ");
            _client.Command("sub-add", path, "select");
        }, token);
    }

    public Task SetSubtitleStyleAsync(double delaySeconds, int fontSize, CancellationToken token = default)
    {
        if (!double.IsFinite(delaySeconds) || Math.Abs(delaySeconds) > 120) throw new ArgumentOutOfRangeException(nameof(delaySeconds));
        if (fontSize is < 12 or > 96) throw new ArgumentOutOfRangeException(nameof(fontSize));
        return ExecuteAsync(() =>
        {
            _client?.Command("set", "sub-delay", Number(delaySeconds));
            _client?.Command("set", "sub-font-size", fontSize.ToString(CultureInfo.InvariantCulture));
        }, token);
    }

    public IReadOnlyList<MediaTrack> GetTracks(TrackKind kind) =>
        Volatile.Read(ref _tracks).Where(x => x.Kind == kind).ToArray();

    public Task SelectTrackAsync(TrackKind kind, string trackId, CancellationToken token = default)
    {
        if (trackId != "no" && (!long.TryParse(trackId, NumberStyles.None, CultureInfo.InvariantCulture, out long id) || id <= 0))
            throw new InvalidDataException("mpv 轨道编号无效。");
        var property = kind switch
        {
            TrackKind.Audio => "aid",
            TrackKind.Subtitle => "sid",
            TrackKind.Video => "vid",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        return ExecuteAsync(() => _client?.Command("set", property, trackId), token);
    }

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
                        if(item.LogText is not null)
                        {
                            // 原有控制台诊断保留。
                            if(Environment.GetEnvironmentVariable("VODBOX_MPV_DIAGNOSTICS")=="1")Console.Error.WriteLine("[mpv] "+item.LogText.TrimEnd());
                            var text=item.LogText.TrimEnd();
                            // mpv 自己的错误/警告是定位解码、网络、协议问题的关键，必须落盘；
                            // 其余（info/debug）只在 verbose 下记录，避免日常噪音。
                            if(!VodBoxLog.Verbose && Environment.GetEnvironmentVariable("VODBOX_MPV_DIAGNOSTICS")!="1")VodBoxLog.Warn("mpv", text);
                            else VodBoxLog.Trace("mpv", text);
                        }
                        snapshot = Process(item, snapshot);
                    }
                    if(_loaded)
                    {
                        var ratio=_client!.GetDouble("video-out-params/aspect");
                        if(ratio is not >0||!double.IsFinite(ratio.Value))ratio=_client.GetDouble("video-params/aspect");
                        if(ratio is >0&&double.IsFinite(ratio.Value))snapshot=snapshot with{VideoAspectRatio=ratio};
                    }
                    if (snapshot != before) SetSnapshot(snapshot);
                }
                catch (ObjectDisposedException) when (_shutdown.IsCancellationRequested) { }
                catch (Exception error)
                {
                    VodBoxLog.Error("mpv", "事件泵异常", error);
                    SetSnapshot(Snapshot with { State = PlaybackState.Failed, Error = error.Message });
                }
                finally { _commands.Release(); }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
    }

    private PlaybackSnapshot Process(MpvEvent item, PlaybackSnapshot snapshot)
    {
        if (item.Id == 8) // MPV_EVENT_FILE_LOADED
        {
            _loaded = true; RefreshTracks();
            VodBoxLog.Event("mpv", "file-loaded",
                ("session", _session.ToString()),
                ("tracks", Volatile.Read(ref _tracks).Count.ToString()));
            if (_startPosition > 0) { _client!.Command("seek", Number(_startPosition / 1000d), "absolute+exact"); _startPosition = 0; }
            return snapshot with { State = CurrentState(), Error = null };
        }
        if (item.Id == 7) // MPV_EVENT_END_FILE
        {
            if (item.EndReason == 2 && !_loaded && snapshot.State == PlaybackState.Loading) return snapshot; // OpenAsync 的前置 stop
            _loaded = false;
            var next = snapshot with
            {
                State = item.EndReason == 0 ? PlaybackState.Ended : item.EndReason == 4 ? PlaybackState.Failed : PlaybackState.Idle,
                Error = item.EndReason == 4 ? $"mpv 无法播放此媒体（错误 {item.EndError}）。" : null
            };
            // 播放器只在这里能拿到 mpv 的原始结束原因，出问题时这是最关键的一行。
            VodBoxLog.Event("mpv", "end-file", ("session", _session.ToString()), ("reason", item.EndReason.ToString()),
                ("error", item.EndError.ToString()), ("wasLoaded", snapshot.State.ToString()), ("next", next.State.ToString()));
            if (next.Error is not null) VodBoxLog.Error("mpv", $"播放失败 session={_session}：{next.Error}");
            return next;
        }
        if (item.Id != 22) return snapshot; // 只关心 MPV_EVENT_PROPERTY_CHANGE
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
        int count = (int)Math.Clamp(_client!.GetDouble("track-list/count") ?? 0, 0, 256);
        var tracks = new List<MediaTrack>();
        // aid/vid/sid 只有字符串形式（mpv_get_property DOUBLE 会报 -9），统一用字符串解析。
        long selectedAudio = ParseId(_client.GetString("aid"));
        long selectedVideo = ParseId(_client.GetString("vid"));
        long selectedSub = ParseId(_client.GetString("sid"));
        for (int i = 0; i < count; i++)
        {
            string prefix = $"track-list/{i}/";
            string? type = _client.GetString(prefix + "type");
            // 新契约含视频轨，三种都收。
            var kind = type switch
            {
                "audio" => TrackKind.Audio,
                "sub" => TrackKind.Subtitle,
                "video" => TrackKind.Video,
                _ => (TrackKind?)null,
            };
            if (kind is null) continue;
            long id = (long)(_client.GetDouble(prefix + "id") ?? 0); if (id <= 0) continue;
            var name = _client.GetString(prefix + "title") ?? _client.GetString(prefix + "lang") ?? $"{type} {id}";
            var selected = kind == TrackKind.Audio ? id == selectedAudio : kind == TrackKind.Video ? id == selectedVideo : id == selectedSub;
            tracks.Add(new MediaTrack(id.ToString(CultureInfo.InvariantCulture), name, kind.Value, selected));
        }
        tracks.Add(new MediaTrack("no", "关闭字幕", TrackKind.Subtitle, selectedSub < 0));
        Volatile.Write(ref _tracks, tracks.ToArray());
    }

    private void SetSnapshot(PlaybackSnapshot snapshot) { Volatile.Write(ref _snapshot, snapshot); StateChanged?.Invoke(this, new PlaybackEvent(_session, snapshot)); }
    private static long ParseId(string? value) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : -1;
    private static TimeSpan Seconds(double value) => TimeSpan.FromSeconds(Math.Clamp(value, 0, TimeSpan.MaxValue.TotalSeconds / 2));
    private static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    private void Check() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _shutdown.Cancel();
        try { if (_client is MpvClient real) real.Wakeup(); } catch (ObjectDisposedException) { }
        await _commands.WaitAsync().ConfigureAwait(false);
        try { if (_client is { } client) await Task.Run(client.Dispose).ConfigureAwait(false); _client = null; }
        finally { _commands.Release(); }
        if (_pump is not null) await _pump.ConfigureAwait(false);
        _shutdown.Dispose();
    }
}
