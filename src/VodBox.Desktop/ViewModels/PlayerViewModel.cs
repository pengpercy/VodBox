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
    private readonly MainViewModel _main;
    private readonly PlaybackCoordinator _coordinator;
    private long _intent;

    public ObservableCollection<MediaTrack> Tracks { get; } = [];

    [ObservableProperty] private PlaybackState _state = PlaybackState.Idle;
    [ObservableProperty] private string _title = "媒体";
    [ObservableProperty] private TimeSpan _position;
    [ObservableProperty] private TimeSpan _duration;
    [ObservableProperty] private int _volume = 100;
    [ObservableProperty] private double _rate = 1.0;
    [ObservableProperty] private bool _visible;
    [ObservableProperty] private string? _error;
    /// <summary>顶栏副标题（集数 · 站源）。</summary>
    [ObservableProperty] private string _subtitle = "";
    /// <summary>中央 toast（倍速/跳片头提示），3 秒自动消失。</summary>
    [ObservableProperty] private bool _showToast;
    [ObservableProperty] private string _toastText = "";

    public PlayerViewModel(AppServices services, MainViewModel main) : this(services.Player, services.Store, main)
    {
    }

    internal PlayerViewModel(IPlaybackEngine engine, ILibraryStore store, MainViewModel main)
    {
        _engine = engine;
        _store = store;
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
                // 即发即忘：此回调可能在 mpv 事件线程 / UI 线程上，绝不能同步等待 SQLite（会死锁）
                if (State is PlaybackState.Ended or PlaybackState.Failed)
                    _ = QueueHistory(CaptureHistory());
            });
        };
    }

    private PlaybackRequest? _current;
    private Task _historyWrites = Task.CompletedTask;

    public void Play(PlaybackRequest request) => _ = PlayResolvedAsync(_ => Task.FromResult(request));

    /// <summary>解析也属于播放会话；新播放和关闭都会取消旧解析。</summary>
    public async Task PlayResolvedAsync(Func<CancellationToken, Task<PlaybackRequest>> resolve)
    {
        long intent = 0;
        Task open = Task.CompletedTask;
        try
        {
            await _main.RunOnUiAsync(() =>
            {
                intent = ++_intent;
                // 在任何 await/清空进度之前截取旧会话；后续写库不再读取可变 VM。
                var saved = QueueHistory(CaptureHistory());
                _current = null;
                Visible = true;
                Error = null;
                State = PlaybackState.Resolving;
                open = _coordinator.OpenAsync(async ct =>
                {
                    var request = await resolve(ct);
                    ct.ThrowIfCancellationRequested();
                    await saved.WaitAsync(ct);
                    ct.ThrowIfCancellationRequested();
                    await _main.RunOnUiAsync(() =>
                    {
                        ct.ThrowIfCancellationRequested();
                        if (intent != _intent) throw new OperationCanceledException(ct);
                        _current = request;
                        Title = request.Title;
                        Position = TimeSpan.Zero;
                        Duration = TimeSpan.Zero;
                    });
                    return request;
                });
            });
            await open;
        }
        catch (OperationCanceledException) { /* 新请求/关闭取消旧会话，不显示失败。 */ }
        catch (Exception error)
        {
            await _main.RunOnUiAsync(() =>
            {
                if (intent != _intent) return;
                State = PlaybackState.Failed;
                Error = $"无法播放：{error.Message}";
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
            saved = QueueHistory(CaptureHistory());
            _current = null;
            stop = _coordinator.CloseAsync(); // 立即取消解析，不等待历史写入。
        });
        await Task.WhenAll(saved, stop);
        await _main.RunOnUiAsync(() =>
        {
            if (intent != _intent) return;
            Visible = false;
            State = PlaybackState.Idle;
        });
    }

    partial void OnVolumeChanged(int value) => _ = _engine.SetVolumeAsync(value);
    partial void OnRateChanged(double value)
    {
        _ = _engine.SetRateAsync(value);
        FlashToast($"{value:0.##}x 倍速");
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
    private HistoryEntry? CaptureHistory() => _current is { } request ? new HistoryEntry
    {
        SourceKey = request.SourceKey,
        SourceName = string.IsNullOrWhiteSpace(request.SourceName) ? "本地" : request.SourceName,
        MediaId = request.MediaId,
        Title = request.Title,
        Poster = request.Poster,
        Remarks = request.Remarks,
        LineId = request.LineId,
        EpisodeId = request.EpisodeId,
        PositionMs = (long)Position.TotalMilliseconds,
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
