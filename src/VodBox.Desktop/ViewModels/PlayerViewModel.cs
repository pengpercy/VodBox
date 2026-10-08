using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VodBox.Core;
using VodBox.Desktop.Services;

namespace VodBox.Desktop.ViewModels;

/// <summary>播放器：状态、进度、控制命令。</summary>
public sealed partial class PlayerViewModel : ObservableObject
{
    private readonly AppServices _services;
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

    public PlayerViewModel(AppServices services, MainViewModel main)
    {
        _services = services;
        _main = main;
        var engine = services.Player;
        _coordinator = new PlaybackCoordinator(engine);
        engine.StateChanged += (_, evt) =>
        {
            var intent = _intent;
            _main.RunOnUi(() =>
            {
                if (intent != _intent || evt.SessionId != _coordinator.SessionId) return;
                State = evt.Snapshot.State;
                Position = evt.Snapshot.Position;
                Duration = evt.Snapshot.Duration;
                Error = evt.Snapshot.Error;
                // 即发即忘：此回调可能在 mpv 事件线程 / UI 线程上，绝不能同步等待 SQLite（会死锁）
                if (State is PlaybackState.Ended or PlaybackState.Failed)
                    _ = SaveHistoryAsync();
            });
        };
    }

    private PlaybackRequest? _current;

    public void Play(PlaybackRequest request) => _ = PlayResolvedAsync(_ => Task.FromResult(request));

    /// <summary>解析也属于播放会话；新播放和关闭都会取消旧解析。</summary>
    public async Task PlayResolvedAsync(Func<CancellationToken, Task<PlaybackRequest>> resolve)
    {
        var intent = ++_intent;
        Visible = true;
        Error = null;
        State = PlaybackState.Resolving;
        try
        {
            await _coordinator.OpenAsync(async ct =>
            {
                var request = await resolve(ct);
                ct.ThrowIfCancellationRequested();
                if (intent == _intent)
                {
                    _current = request;
                    Title = request.Title;
                    Position = TimeSpan.Zero;
                    Duration = TimeSpan.Zero;
                }
                return request;
            });
        }
        catch (OperationCanceledException) { /* 新请求/关闭取消旧会话，不显示失败。 */ }
        catch (Exception error)
        {
            if (intent != _intent) return;
            State = PlaybackState.Failed;
            Error = $"无法播放：{error.Message}";
        }
    }

    [RelayCommand]
    public void TogglePlayPause()
    {
        if (State is PlaybackState.Playing or PlaybackState.Buffering)
            _ = _services.Player.PauseAsync();
        else
            _ = _services.Player.PlayAsync();
    }

    [RelayCommand]
    public void Seek(TimeSpan position) => _ = _services.Player.SeekToAsync(position);

    [RelayCommand]
    public void SeekBy(double seconds) => _ = _services.Player.SeekByAsync(TimeSpan.FromSeconds(seconds));

    [RelayCommand]
    public async Task Close()
    {
        var intent = ++_intent;
        var stop = _coordinator.CloseAsync(); // 先取消解析，避免保存历史期间旧结果打开。
        await SaveHistoryAsync();
        await stop;
        if (intent != _intent) return;
        Visible = false;
        State = PlaybackState.Idle;
        _current = null;
    }

    partial void OnVolumeChanged(int value) => _ = _services.Player.SetVolumeAsync(value);
    partial void OnRateChanged(double value)
    {
        _ = _services.Player.SetRateAsync(value);
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

    /// <summary>落库观看历史（含线路/选集/海报上下文）。异常在此吞掉，避免存储故障中断播放或崩溃。</summary>
    private async Task SaveHistoryAsync()
    {
        if (_current is not { } request) return;
        try
        {
            var entry = new HistoryEntry
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
            };
            await _services.Store.SaveHistoryAsync(entry);
        }
        catch (Exception error)
        {
            System.Diagnostics.Debug.WriteLine($"[history] 保存失败：{error.Message}");
        }
    }
}
