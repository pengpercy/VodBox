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
    private long _sessionId;

    public ObservableCollection<MediaTrack> Tracks { get; } = [];

    [ObservableProperty] private PlaybackState _state = PlaybackState.Idle;
    [ObservableProperty] private string _title = "媒体";
    [ObservableProperty] private TimeSpan _position;
    [ObservableProperty] private TimeSpan _duration;
    [ObservableProperty] private int _volume = 100;
    [ObservableProperty] private double _rate = 1.0;
    [ObservableProperty] private bool _visible;
    [ObservableProperty] private string? _error;

    public PlayerViewModel(AppServices services, MainViewModel main)
    {
        _services = services;
        _main = main;
        var engine = services.Player;
        engine.StateChanged += (_, evt) =>
        {
            _main.RunOnUi(() =>
            {
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

    public void Play(PlaybackRequest request)
    {
        _current = request;
        Title = request.Title;
        Visible = true;
        Error = null;
        _sessionId = DateTime.UtcNow.Ticks;
        _ = OpenSafeAsync(request);
    }

    /// <summary>异步打开媒体并兜底：libmpv 缺失/渲染面失败/代理头拒绝等异常落到 Error，而不是未观察任务。</summary>
    private async Task OpenSafeAsync(PlaybackRequest request)
    {
        try
        {
            await _services.Player.OpenAsync(request, _sessionId);
        }
        catch (Exception error)
        {
            State = PlaybackState.Failed;
            Error = error is DllNotFoundException or InvalidOperationException or NotSupportedException
                ? $"无法播放：{error.Message}（libmpv 缺失时请安装 mpv 或设置 VODBOX_MPV_LIB 指向 libmpv 动态库）"
                : $"无法播放：{error.Message}";
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
        await SaveHistoryAsync(); // 关闭前落库，保证最后一次进度不丢
        await _services.Player.StopAsync();
        Visible = false;
        State = PlaybackState.Idle;
        _current = null;
    }

    partial void OnVolumeChanged(int value) => _ = _services.Player.SetVolumeAsync(value);
    partial void OnRateChanged(double value) => _ = _services.Player.SetRateAsync(value);

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
