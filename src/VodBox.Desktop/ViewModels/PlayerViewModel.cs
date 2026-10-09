using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VodBox.Core;
using VodBox.Desktop.Services;

namespace VodBox.Desktop.ViewModels;

/// <summary>播放器：状态、进度、控制命令。</summary>
public sealed partial class PlayerViewModel : ObservableObject
{
    private readonly IPlaybackEngine _engine;
    private readonly ILibraryStore _store;
    private readonly IPreferences? _preferences;
    private int _audibleVolume = 100;
    public double? AspectRatio { get; private set; }
    [ObservableProperty] private double? _videoAspectRatio;
    public bool IsMuted => Volume == 0;
    private readonly MainViewModel _main;
    private readonly PlaybackCoordinator _coordinator;
    private Func<IReadOnlyList<TvBoxParse>> _getParses = () => [];
    public IReadOnlyList<TvBoxParse> AvailableParsers => _getParses().Where(parse => parse.Type is 1 or 2).ToArray();
    [ObservableProperty] private string _preferredParser = "";
    partial void OnPreferredParserChanged(string value) => SavePreference(() => _preferences?.Set("player.preferred-parser", value));
    private long _intent;
    [ObservableProperty] private double _subtitleDelay;
    [ObservableProperty] private int _subtitleFontSize = 40;
    partial void OnSubtitleDelayChanged(double value)
    {
        var bounded = double.IsFinite(value) ? Math.Clamp(value, -120, 120) : 0;
        if (value != bounded) { SubtitleDelay = bounded; return; }
        SavePreference(() => _preferences?.Set("subtitle.delay", bounded));
        _ = ApplyControlAsync(() => _engine.SetSubtitleStyleAsync(SubtitleDelay, SubtitleFontSize));
    }
    partial void OnSubtitleFontSizeChanged(int value)
    {
        var bounded = Math.Clamp(value, 12, 96);
        if (value != bounded) { SubtitleFontSize = bounded; return; }
        SavePreference(() => _preferences?.Set("subtitle.font-size", bounded));
        _ = ApplyControlAsync(() => _engine.SetSubtitleStyleAsync(SubtitleDelay, SubtitleFontSize));
    }
    private CancellationTokenSource? _danmakuRequest;
    internal Task DanmakuLoadingTask{get;private set;}=Task.CompletedTask;
    public IReadOnlyList<DanmakuComment> Danmaku { get; private set; } = [];
    [ObservableProperty] private double _danmakuOpacity=.85;
    [ObservableProperty] private int _danmakuLimit=30;
    partial void OnDanmakuOpacityChanged(double value)
    {
        var bounded=double.IsFinite(value)?Math.Clamp(value,.1,1):.85;
        if(value!=bounded){DanmakuOpacity=bounded;return;}
        SavePreference(()=>_preferences?.Set("danmaku.opacity",bounded));
    }
    partial void OnDanmakuLimitChanged(int value)
    {
        var bounded=Math.Clamp(value,5,60);if(value!=bounded){DanmakuLimit=bounded;return;}
        SavePreference(()=>_preferences?.Set("danmaku.limit",bounded));
    }
    [ObservableProperty] private bool _danmakuEnabled;
    partial void OnDanmakuEnabledChanged(bool value)
    {
        SavePreference(() => _preferences?.Set("player.danmaku", value));
        if (!value) _danmakuRequest?.Cancel();
        if (value && _current?.DanmakuUri is { } uri) DanmakuLoadingTask = LoadDanmakuAsync(uri, _intent);
    }

    private async Task LoadDanmakuAsync(string uri, long intent)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var address) || address.Scheme is not ("http" or "https")) return;
        _danmakuRequest?.Cancel();
        using var scope = new CancellationTokenSource();
        _danmakuRequest = scope;
        try
        {
            using var http = new VodBox.Infrastructure.DefaultHttp();
            var bytes = await http.GetBoundedAsync(uri, 8 * 1024 * 1024, scope.Token);
            var comments = await Task.Run(() => VodBox.Infrastructure.DanmakuParser.Parse(bytes), scope.Token);
            await _main.RunOnUiAsync(() =>
            {
                if (intent != _intent || scope.IsCancellationRequested) return;
                Danmaku = comments;
                OnPropertyChanged(nameof(Danmaku));
            });
        }
        catch (OperationCanceledException) when (scope.IsCancellationRequested) { }
        catch (Exception error)
        {
            await _main.RunOnUiAsync(() => { if (intent == _intent) FlashToast($"弹幕加载失败：{error.Message}"); });
        }
        finally { if (ReferenceEquals(_danmakuRequest, scope)) _danmakuRequest = null; }
    }

    public ObservableCollection<PlaylistEntry> Playlist { get; } = [];
    [ObservableProperty] private int _playlistIndex = -1;
    [ObservableProperty] private bool _autoNext = true;
    [ObservableProperty] private bool _incognito;
    private long _handledEndSession = -1;
    private long _watchedSession = -1;
    [ObservableProperty] private int _openingSkipSeconds;
    [ObservableProperty] private int _endingSkipSeconds;
    private long _openingHandledIntent = -1;
    private long _endingHandledIntent = -1;
    partial void OnOpeningSkipSecondsChanged(int value)
    {
        var bounded = Math.Clamp(value, 0, 300);
        if (bounded != value) { OpeningSkipSeconds = bounded; return; }
        SavePreference(() => _preferences?.Set("player.skip-opening", value));
    }
    partial void OnEndingSkipSecondsChanged(int value)
    {
        var bounded = Math.Clamp(value, 0, 300);
        if (bounded != value) { EndingSkipSeconds = bounded; return; }
        SavePreference(() => _preferences?.Set("player.skip-ending", value));
    }
    public bool HasPreviousEpisode => PlaylistIndex > 0;
    public bool HasNextEpisode => PlaylistIndex >= 0 && PlaylistIndex + 1 < Playlist.Count;
    partial void OnPlaylistIndexChanged(int value)
    {
        OnPropertyChanged(nameof(HasPreviousEpisode));
        OnPropertyChanged(nameof(HasNextEpisode));
    }

    public ObservableCollection<MediaTrack> Tracks { get; } = [];

    [ObservableProperty] private bool _alwaysOnTop;
    [ObservableProperty] private bool _compactMode;
    [ObservableProperty] private bool _controlsVisible = true;
    public bool IsSeeking => _seekIntent is not null;
    public bool IsPlaying => State is PlaybackState.Playing or PlaybackState.Buffering;
    partial void OnStateChanged(PlaybackState value)
    {
        OnPropertyChanged(nameof(IsPlaying));
        // 播放状态就是排查“点了没反应”的第一现场：带上标题、进度与错误。
        if (value is PlaybackState.Playing or PlaybackState.Failed)
        {
            VodBox.Core.VodBoxLog.Event("player", "state",
                ("state", value.ToString()), ("title", Title), ("position", $"{Position.TotalSeconds:F1}s"),
                ("duration", $"{Duration.TotalSeconds:F1}s"), ("error", Error));
        }
        else
        {
            VodBox.Core.VodBoxLog.Trace("player", $"state={value} title={Title}");
        }
    }
    [ObservableProperty] private PlaybackState _state = PlaybackState.Idle;
    [ObservableProperty] private string _title = "媒体";
    [ObservableProperty] private TimeSpan _position;
    [ObservableProperty] private TimeSpan _duration;
    [ObservableProperty] private double _seekPositionSeconds;
    private long? _seekIntent;
    partial void OnPositionChanged(TimeSpan value)
    {
        if (_seekIntent is null) SeekPositionSeconds = value.TotalSeconds;
    }
    public void BeginSeek() => _seekIntent = _intent;
    public void CancelSeek()
    {
        _seekIntent = null;
        SeekPositionSeconds = Position.TotalSeconds;
    }
    public void CommitSeek()
    {
        var intent = _seekIntent;
        var seconds = SeekPositionSeconds;
        CancelSeek();
        if (intent == _intent && Duration > TimeSpan.Zero && double.IsFinite(seconds))
            Seek(TimeSpan.FromSeconds(Math.Clamp(seconds, 0, Duration.TotalSeconds)));
    }
    [ObservableProperty] private int _volume = 100;
    [ObservableProperty] private double _rate = 1.0;
    [ObservableProperty] private bool _visible;
    [ObservableProperty] private string? _error;
    /// <summary>顶栏副标题（集数 · 站源）。</summary>
    [ObservableProperty] private string _subtitle = "";
    /// <summary>中央 toast（倍速/跳片头提示），3 秒自动消失。</summary>
    [ObservableProperty] private bool _showToast;
    [ObservableProperty] private string _toastText = "";

    public PlayerViewModel(AppServices services, MainViewModel main) : this(services.Player, services.Store, main, services.Prefs)
    {
        _getParses = () => services.Registry.Parses;
    }

    internal PlayerViewModel(IPlaybackEngine engine, ILibraryStore store, MainViewModel main, IPreferences? preferences = null)
    {
        _engine = engine;
        _store = store;
        _preferences = preferences;
        _preferredParser = preferences?.GetString("player.preferred-parser", "") ?? "";
        _subtitleDelay = preferences?.GetDouble("subtitle.delay", 0) ?? 0;
        if (!double.IsFinite(_subtitleDelay)) _subtitleDelay = 0;
        _subtitleDelay = Math.Clamp(_subtitleDelay, -120, 120);
        _subtitleFontSize = Math.Clamp(preferences?.GetInt("subtitle.font-size", 40) ?? 40, 12, 96);
        var savedOpacity=preferences?.GetDouble("danmaku.opacity",.85)??.85;
        _danmakuOpacity=double.IsFinite(savedOpacity)?Math.Clamp(savedOpacity,.1,1):.85;
        _danmakuLimit=Math.Clamp(preferences?.GetInt("danmaku.limit",30)??30,5,60);
        _danmakuEnabled = preferences?.GetBool("player.danmaku", false) ?? false;
        _autoNext = preferences?.GetBool("player.auto-next", true) ?? true;
        _openingSkipSeconds = Math.Clamp(preferences?.GetInt("player.skip-opening", 0) ?? 0, 0, 300);
        _endingSkipSeconds = Math.Clamp(preferences?.GetInt("player.skip-ending", 0) ?? 0, 0, 300);
        _incognito = preferences?.GetBool("player.incognito", false) ?? false;
        _volume = Math.Clamp(preferences?.GetInt("player.volume", 100) ?? 100, 0, 100);
        var savedRate = preferences?.GetDouble("player.rate", 1) ?? 1;
        _rate = double.IsFinite(savedRate) ? Math.Clamp(savedRate, .25, 4) : 1;
        if (_volume > 0) _audibleVolume = _volume;
        var savedAspect = preferences?.GetDouble("player.aspect-ratio", 0) ?? 0;
        AspectRatio = double.IsFinite(savedAspect) && savedAspect > 0 ? savedAspect : null;
        _main = main;
        _coordinator = new PlaybackCoordinator(engine);
        engine.StateChanged += (_, evt) =>
        {
            var intent = Volatile.Read(ref _intent);
            _main.RunOnUi(() =>
            {
                if (intent != _intent || evt.SessionId != _coordinator.SessionId) return;
                State = evt.Snapshot.State;
                Position = evt.Snapshot.Position;
                Duration = evt.Snapshot.Duration;
                Error = evt.Snapshot.Error;
                VideoAspectRatio=evt.Snapshot.VideoAspectRatio;
                ApplyAutomaticSkips(evt.Snapshot);
                if (State == PlaybackState.Ended && evt.SessionId != _watchedSession && !Incognito && _current is { IsLive: false } completed && completed.EpisodeId.Length > 0)
                {
                    _watchedSession = evt.SessionId;
                    _watchedWrites=SaveWatchedAfterAsync(_watchedWrites,completed);
                }
                // 即发即忘：此回调可能在 mpv 事件线程 / UI 线程上，绝不能同步等待 SQLite（会死锁）
                if (State is PlaybackState.Ended or PlaybackState.Failed)
                    _ = QueueHistory(CaptureHistory());
                if (State == PlaybackState.Ended && AutoNext && HasNextEpisode && _handledEndSession != evt.SessionId)
                {
                    _handledEndSession = evt.SessionId;
                    _ = PlayPlaylistIndexAsync(PlaylistIndex + 1);
                }
                if(State==PlaybackState.Failed&&_current is {} failed)_main.Live.HandlePlaybackFailure(failed,intent);
            });
        };
    }

    /// <summary>仅可 seek 的点播、Playing 且已知时长触发；每会话各一次，手动回看片头不会再次跳走。</summary>
    private void ApplyAutomaticSkips(PlaybackSnapshot snapshot)
    {
        if (_current is null || _current.IsLive || snapshot.State != PlaybackState.Playing ||
            !snapshot.CanSeek || IsSeeking || snapshot.Duration <= TimeSpan.Zero) return;
        var duration = snapshot.Duration.TotalSeconds;
        if (OpeningSkipSeconds + EndingSkipSeconds >= duration) return;
        if (_openingHandledIntent != _intent && OpeningSkipSeconds > 0)
        {
            _openingHandledIntent = _intent;
            if (snapshot.Position.TotalSeconds < OpeningSkipSeconds && _current.StartPositionMs < OpeningSkipSeconds * 1000L)
            {
                _ = ApplyControlAsync(() => _engine.SeekToAsync(TimeSpan.FromSeconds(OpeningSkipSeconds)));
                return;
            }
        }
        if (_endingHandledIntent != _intent && EndingSkipSeconds > 0 &&
            snapshot.Position.TotalSeconds >= duration - EndingSkipSeconds)
        {
            _endingHandledIntent = _intent;
            // 让引擎到达 EOF，沿用真正 Ended 事件与完播/自动下一集逻辑，不伪造完成。
            _ = ApplyControlAsync(() => _engine.SeekToAsync(snapshot.Duration));
        }
    }

    private Task _watchedWrites=Task.CompletedTask;
    private async Task SaveWatchedAfterAsync(Task previous,PlaybackRequest request)
    {
        await previous;
        await MarkWatchedAsync(request);
    }

    private async Task MarkWatchedAsync(PlaybackRequest request)
    {
        try
        {
            await _store.MarkEpisodeWatchedAsync(request.SourceKey,request.MediaId,request.LineId,request.EpisodeId);
            await _main.RunOnUiAsync(() => _main.Detail.RefreshWatchedFor(request.SourceKey,request.MediaId,request.LineId));
        }
        catch(Exception error) { System.Diagnostics.Debug.WriteLine($"[watched] {error.Message}"); }
    }

    private PlaybackRequest? _current;
    private Task _historyWrites = Task.CompletedTask;

    public void Play(PlaybackRequest request) => _ = PlayResolvedAsync(_ => Task.FromResult(request));

    /// <summary>解析也属于播放会话；新播放和关闭都会取消旧解析。</summary>
    public async Task PlayResolvedAsync(Func<CancellationToken, Task<PlaybackRequest>> resolve, IReadOnlyList<PlaylistEntry>? playlist = null, int index = -1)
    {
        long intent = 0;
        Task open = Task.CompletedTask;
        try
        {
            await _main.RunOnUiAsync(() =>
            {
                intent = ++_intent;
                _danmakuRequest?.Cancel(); Danmaku = []; OnPropertyChanged(nameof(Danmaku));
                CancelSeek();
                Playlist.Clear();
                if (playlist is not null) foreach (var entry in playlist) Playlist.Add(entry);
                PlaylistIndex = index;
                // 在任何 await/清空进度之前截取旧会话；后续写库不再读取可变 VM。
                var saved = QueueHistory(CaptureHistory());
                _current = null;
                VideoAspectRatio=null;
                Visible = true;
                Error = null;
                State = PlaybackState.Resolving;
                open = _coordinator.OpenAsync(async ct =>
                {
                    var request = await resolve(ct);
                    ct.ThrowIfCancellationRequested();
                    if (request.Resolution == ResolutionKind.Json)
                    {
                        using var http = new VodBox.Infrastructure.DefaultHttp();
                        PlaybackRequest? resolved = null;
                        var failures = new List<string>();
                        var parsers=AvailableParsers.OrderByDescending(parse=>parse.Url==PreferredParser).ToList();
                        if(!string.IsNullOrWhiteSpace(request.ParseEndpoint))parsers.Insert(0,new TvBoxParse{Name="站点解析",Type=1,Url=request.ParseEndpoint});
                        foreach (var parse in parsers.DistinctBy(parse=>parse.Url).Take(8))
                        {
                            try
                            {
                                resolved = await new VodBox.Infrastructure.JsonPlayResolver(parse, http).ResolveAsync(request, ct);
                                if (resolved is not null) break;
                            }
                            catch (Exception error) when (error is not OperationCanceledException) { failures.Add(parse.Name + ": " + error.Message); }
                        }
                        request = resolved ?? throw new InvalidOperationException("JSON解析未返回媒体地址。" + string.Join("；", failures));
                    }
                    if (request.Resolution == ResolutionKind.Sniff) throw new NotSupportedException("此媒体需要网页嗅探，当前尚未实现。");
                    await saved.WaitAsync(ct);
                    ct.ThrowIfCancellationRequested();
                    int volume = 100;
                    double rate = 1;
                    await _main.RunOnUiAsync(() =>
                    {
                        ct.ThrowIfCancellationRequested();
                        volume = Volume;
                        rate = Rate;
                        if (intent != _intent) throw new OperationCanceledException(ct);
                        _current = request;
                        Title = request.Title;
                        Subtitle = index >= 0 && index < Playlist.Count
                            ? $"{Playlist[index].Title} · {request.SourceName}" : request.SourceName;
                        Position = TimeSpan.Zero;
                        Duration = TimeSpan.Zero;
                    });
                    await _engine.SetVolumeAsync(volume, ct);
                    await _engine.SetRateAsync(rate, ct);
                    if (AspectRatio is { } aspect) await _engine.SetAspectRatioAsync(aspect, ct);
                    ct.ThrowIfCancellationRequested();
                    return request;
                });
            });
            await open;
            if (intent == _intent) await ApplyControlAsync(() => _engine.SetSubtitleStyleAsync(SubtitleDelay, SubtitleFontSize));
            if (intent == _intent && DanmakuEnabled && _current?.DanmakuUri is { } danmakuUri) DanmakuLoadingTask = LoadDanmakuAsync(danmakuUri, intent);
        }
        catch (OperationCanceledException) { /* 新请求/关闭取消旧会话，不显示失败。 */ }
        catch (Exception error)
        {
            // 播放失败必须留下原因：这是排查“点了没反应/放不出来”的唯一现场。
            VodBox.Core.VodBoxLog.Error("player", $"播放失败 title={Title} uri={_current?.Uri}", error);
            await _main.RunOnUiAsync(() =>
            {
                if (intent != _intent) return;
                // 先写 Error 再切状态：状态变化会触发日志/界面读取 Error。
                Error = $"无法播放：{error.Message}";
                State = PlaybackState.Failed;
                if(_current is {} failed)_main.Live.HandlePlaybackFailure(failed,intent);
            });
        }
    }

    public Task PlayPlaylistIndexAsync(int index)
    {
        if (index < 0 || index >= Playlist.Count) return Task.CompletedTask;
        var snapshot = Playlist.ToArray();
        return PlayResolvedAsync(snapshot[index].Resolve, snapshot, index);
    }

    /// <summary>数字快捷键按原始播放顺序选第 1—9 集；无列表或越界不触发新会话。</summary>
    public Task SelectEpisodeNumberAsync(int number) => number is >= 1 and <= 9
        ? PlayPlaylistIndexAsync(number - 1) : Task.CompletedTask;

    [RelayCommand] private Task PreviousEpisode() => PlayPlaylistIndexAsync(PlaylistIndex - 1);
    [RelayCommand] private Task NextEpisode() => PlayPlaylistIndexAsync(PlaylistIndex + 1);

    public async Task SetAspectRatioAsync(double? ratio)
    {
        if (ratio is { } value && (!double.IsFinite(value) || value <= 0))
            throw new ArgumentOutOfRangeException(nameof(ratio));
        var intent = _intent;
        try
        {
            await _engine.SetAspectRatioAsync(ratio);
            await _main.RunOnUiAsync(() =>
            {
                if (intent != _intent) return;
                AspectRatio = ratio;
                OnPropertyChanged(nameof(AspectRatio));
                SavePreference(() => _preferences?.Set("player.aspect-ratio", ratio ?? 0));
            });
        }
        catch (Exception error)
        {
            await _main.RunOnUiAsync(() => { if (intent == _intent) FlashToast($"播放设置失败：{error.Message}"); });
        }
    }

    public long CurrentSessionId => _intent;

    public async Task<bool> LoadSubtitleAsync(string path, long expectedIntent)
    {
        if (expectedIntent != _intent || !Visible || _current is null) return false;
        var session = _coordinator.SessionId;
        try
        {
            await _engine.LoadSubtitleAsync(path, session);
            await _main.RunOnUiAsync(() =>
            {
                if (expectedIntent == _intent) FlashToast($"已加载字幕：{Path.GetFileName(path)}");
            });
            return expectedIntent==_intent;
        }
        catch (Exception error)
        {
            await _main.RunOnUiAsync(() =>
            {
                if (expectedIntent == _intent) FlashToast($"加载字幕失败：{error.Message}");
            });
            return false;
        }
    }

    public IReadOnlyList<MediaTrack> GetTracks(TrackKind kind) => _engine.GetTracks(kind);

    public async Task SelectTrackAsync(TrackKind kind, string id)
    {
        var intent = _intent;
        try { await _engine.SelectTrackAsync(kind, id); }
        catch (Exception error)
        {
            await _main.RunOnUiAsync(() =>
            {
                if (intent == _intent) FlashToast($"切换轨道失败：{error.Message}");
            });
        }
    }

    [RelayCommand]
    public void TogglePlayPause()
    {
        if (State is PlaybackState.Playing or PlaybackState.Buffering)
            _ = _engine.PauseAsync();
        else
            _ = _engine.PlayAsync();
    }

    [RelayCommand]
    public void Seek(TimeSpan position) => _ = _engine.SeekToAsync(position);

    [RelayCommand]
    public void SeekBy(double seconds) => _ = _engine.SeekByAsync(TimeSpan.FromSeconds(seconds));

    [RelayCommand]
    public async Task Close()
    {
        long intent = 0;
        Task stop = Task.CompletedTask;
        Task saved = Task.CompletedTask;
        await _main.RunOnUiAsync(() =>
        {
            intent = ++_intent;
            _danmakuRequest?.Cancel(); Danmaku = []; OnPropertyChanged(nameof(Danmaku));
            CancelSeek();
            saved = QueueHistory(CaptureHistory());
            _current = null;
            Playlist.Clear();
            PlaylistIndex = -1;
            stop = _coordinator.CloseAsync(); // 立即取消解析，不等待历史写入。
        });
        await Task.WhenAll(saved, stop,_watchedWrites);
        await _main.RunOnUiAsync(() =>
        {
            if (intent != _intent) return;
            VodBox.Core.VodBoxLog.Info("player", $"播放已关闭：{Title}");
            _main.Live.ClearPlaybackMarker();
            Visible = false;
            State = PlaybackState.Idle;
        });
    }

    partial void OnIncognitoChanged(bool value) => SavePreference(() => _preferences?.Set("player.incognito", value));

    partial void OnAutoNextChanged(bool value)
    {
        try { _preferences?.Set("player.auto-next", value); }
        catch (Exception error) { System.Diagnostics.Debug.WriteLine($"[preferences] {error.Message}"); }
    }

    [RelayCommand] private void ToggleMute() => Volume = Volume == 0 ? _audibleVolume : 0;

    partial void OnVolumeChanged(int value)
    {
        var bounded = Math.Clamp(value, 0, 100);
        if (value != bounded) { Volume = bounded; return; }
        if (value > 0) _audibleVolume = value;
        OnPropertyChanged(nameof(IsMuted));
        SavePreference(() => _preferences?.Set("player.volume", value));
        _ = ApplyControlAsync(() => _engine.SetVolumeAsync(value));
    }
    partial void OnRateChanged(double value)
    {
        var bounded = double.IsFinite(value) ? Math.Clamp(value, .25, 4) : 1;
        if (!value.Equals(bounded)) { Rate = bounded; return; }
        SavePreference(() => _preferences?.Set("player.rate", value));
        _ = ApplyControlAsync(() => _engine.SetRateAsync(value));
        FlashToast($"{value:0.##}x 倍速");
    }

    private static void SavePreference(Action save)
    {
        try { save(); }
        catch (Exception error) { System.Diagnostics.Debug.WriteLine($"[preferences] {error.Message}"); }
    }

    private async Task ApplyControlAsync(Func<Task> apply)
    {
        var intent = _intent;
        try { await apply(); }
        catch (Exception error)
        {
            await _main.RunOnUiAsync(() => { if (intent == _intent) FlashToast($"播放设置失败：{error.Message}"); });
        }
    }

    /// <summary>展示中央 toast（自动 3 秒隐藏）。</summary>
    public async void FlashToast(string text)
    {
        ToastText = text;
        ShowToast = true;
        await Task.Delay(3000);
        if (ToastText == text) ShowToast = false;
    }

    [RelayCommand]
    private void ToggleFullscreen()
    {
        // 全屏切换由 View 层转发（VM 不持控件引用）；此处仅设计演示态翻转。
        RequestFullscreen?.Invoke();
    }

    /// <summary>请求主窗口切换全屏（PlayerOverlay 订阅转发给 Window）。</summary>
    public event Action? RequestFullscreen;

    /// <summary>在 UI 线程截取不可变历史，避免切换/关闭后的异步写入混用新会话数据。</summary>
    private HistoryEntry? CaptureHistory() => !Incognito && _current is { } request ? new HistoryEntry
    {
        SourceKey = request.SourceKey,
        SourceName = string.IsNullOrWhiteSpace(request.SourceName) ? "本地" : request.SourceName,
        MediaId = request.MediaId,
        Title = request.Title,
        Poster = request.Poster,
        Remarks = request.Remarks,
        LineId = request.LineId,
        EpisodeId = request.EpisodeId,
        PositionMs = State == PlaybackState.Ended ? 0 : (long)Position.TotalMilliseconds,
        DurationMs = (long)Duration.TotalMilliseconds,
        Rate = Rate,
    } : null;

    private Task QueueHistory(HistoryEntry? entry)
    {
        // 同一媒体的不同选集共用历史键，旧写入不得晚于新写入完成。
        if (entry is not null) _historyWrites = SaveHistoryAsync(_historyWrites, entry);
        return _historyWrites;
    }

    /// <summary>存储异常不阻断播放；写入队列始终可继续。</summary>
    private async Task SaveHistoryAsync(Task previous, HistoryEntry entry)
    {
        await previous;
        try
        {
            await _store.SaveHistoryAsync(entry);
        }
        catch (Exception error)
        {
            System.Diagnostics.Debug.WriteLine($"[history] 保存失败：{error.Message}");
        }
    }
}

/// <summary>播放入口快照：不依赖仍可变化的详情页。</summary>
public sealed record PlaylistEntry(string Id, string Title, Func<CancellationToken, Task<PlaybackRequest>> Resolve);
