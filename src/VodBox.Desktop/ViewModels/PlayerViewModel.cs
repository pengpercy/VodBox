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
                if (State is PlaybackState.Ended or PlaybackState.Failed)
                    SaveHistoryAsync().GetAwaiter().GetResult();
            });
        };
        var poll = new System.Timers.Timer(1000) { AutoReset = true };
        poll.Elapsed += (_, _) =>
        {
            if (State is not (PlaybackState.Playing or PlaybackState.Paused)) return;
            var (position, duration) = engine.PollPosition();
            _main.RunOnUi(() => { Position = position; Duration = duration; });
        };
        poll.Start();
    }

    private PlaybackRequest? _current;

    public void Play(PlaybackRequest request)
    {
        _current = request;
        Title = request.Title;
        Visible = true;
        Error = null;
        _sessionId = DateTime.UtcNow.Ticks;
        if (!_services.Player.Available)
        {
            State = PlaybackState.Failed;
            Error = "libmpv 未加载：请安装 mpv 或设置 VODBOX_MPV_LIB 指向 libmpv 动态库";
            return;
        }
        _ = _services.Player.OpenAsync(request, _sessionId);
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
    public void Close()
    {
        _ = SaveHistoryAsync();
        _ = _services.Player.StopAsync();
        Visible = false;
        State = PlaybackState.Idle;
        _current = null;
    }

    partial void OnVolumeChanged(int value) => _ = _services.Player.SetVolumeAsync(value);
    partial void OnRateChanged(double value) => _ = _services.Player.SetRateAsync(value);

    private async Task SaveHistoryAsync()
    {
        if (_current is null) return;
        var entry = new HistoryEntry
        {
            SourceKey = _current.SourceKey,
            SourceName = "本地",
            MediaId = _current.MediaId,
            Title = _current.Title,
            PositionMs = (long)Position.TotalMilliseconds,
            DurationMs = (long)Duration.TotalMilliseconds,
            Rate = Rate,
        };
        await _services.Store.SaveHistoryAsync(entry);
    }
}
