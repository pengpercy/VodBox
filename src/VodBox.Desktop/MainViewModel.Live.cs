using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using Avalonia.Threading;
using VodBox.Application;
using VodBox.Core;
using VodBox.Infrastructure;

namespace VodBox.Desktop;

public sealed record ProgrammeRow(string Title, string Time, bool IsCurrent);

public partial class MainViewModel
{
    private readonly List<LiveChannel> _allChannels = [];
    private CancellationTokenSource? _epgCancellation;
    private LiveChannel? _playingChannel;
    private int _liveMirror;
    private readonly LiveRetryPolicy _liveRetry = new();
    private readonly HashSet<(string Source, string Channel)> _liveFavorites = [];
    private readonly DispatcherTimer _liveTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private readonly DispatcherTimer _liveHealthTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private long _liveRetrySession;
    private TimeSpan _livePosition;
    private DateTimeOffset _liveProgressAt = DateTimeOffset.UtcNow;
    private string? _lastLiveConfigId, _lastLiveSourceId, _lastLiveChannelId;
    [ObservableProperty] private bool _onlyFavoriteChannels;
    [ObservableProperty] private bool _autoLiveFallback = true;
    [ObservableProperty] private bool _resumeLiveOnStartup;
    partial void OnOnlyFavoriteChannelsChanged(bool value) => FilterChannels();
    partial void OnAutoLiveFallbackChanged(bool value) => SchedulePreferencesSave();
    partial void OnResumeLiveOnStartupChanged(bool value) => SchedulePreferencesSave();
    private void InitializeLiveRefresh()
    {
        _liveTimer.Tick += async (_, _) =>
        { if (_playingChannel is { } channel && _epgCancellation is null) await LoadProgrammeAsync(channel); };
        _liveHealthTimer.Tick += (_, _) => { if (_playingChannel is not null) ObserveLivePlayback(new(_coordinator.SessionId, Engine.Snapshot)); };
        if (!_designMode) { _liveTimer.Start(); _liveHealthTimer.Start(); }
    }
    private async Task RefreshLiveFavoritesAsync()
    {
        _liveFavorites.Clear();
        foreach (var favorite in await _store.GetFavoritesAsync(_lifetime.Token))
            if (favorite.ConfigId == _config.Id && favorite.SourceId.StartsWith("live/", StringComparison.Ordinal)) _liveFavorites.Add((favorite.SourceId[5..], favorite.MediaId));
        FilterChannels();
    }
    [RelayCommand] private Task ToggleLiveFavoriteAsync() => RunAsync(async () =>
    {
        if (_playingChannel is not { } channel) { Status = "请先选择直播频道。"; return; }
        bool exists = _liveFavorites.Contains((channel.LiveSourceId, channel.Id));
        await _store.SetFavoriteAsync(new(_config.Id, "live/" + channel.LiveSourceId, channel.Id, channel.Name), !exists, _lifetime.Token);
        await RefreshLiveFavoritesAsync(); Status = exists ? "已取消频道收藏。" : "已收藏频道。";
    });
    [RelayCommand] private Task ResumeLastLiveAsync() => RunAsync(async () =>
    {
        if (_lastLiveConfigId is null || _lastLiveSourceId is null || _lastLiveChannelId is null) { Status = "还没有上次频道记录。"; return; }
        await OpenStoredLiveAsync(_lastLiveConfigId, _lastLiveSourceId, _lastLiveChannelId, null);
    });
    private async Task OpenStoredLiveAsync(string configId, string sourceId, string channelId, string? fallbackUri)
    {
        await EnsureStoredConfigurationAsync(configId);
        var channel = _allChannels.FirstOrDefault(x => x.LiveSourceId == sourceId && (x.Id == channelId || fallbackUri is not null && x.Uris.Contains(fallbackUri)))
            ?? throw new InvalidDataException("该直播频道已从配置中移除。");
        await NavigateAsync("直播"); await PlayChannelAsync(channel);
    }
    private void ObserveLivePlayback(PlaybackEvent e)
    {
        if (_playingChannel is null) return;
        if (e.Snapshot.Position != _livePosition || e.Snapshot.State == PlaybackState.Paused)
        { _livePosition = e.Snapshot.Position; _liveProgressAt = DateTimeOffset.UtcNow; }
        if (AutoLiveFallback && (e.Snapshot.State is PlaybackState.Failed or PlaybackState.Ended
            || e.Snapshot.State is PlaybackState.Loading or PlaybackState.Buffering or PlaybackState.Playing && DateTimeOffset.UtcNow - _liveProgressAt > TimeSpan.FromSeconds(30)))
            _ = RetryLiveAsync(e.SessionId);
    }
    private Task RetryLiveAsync(long session) => RunAsync(async () =>
    {
        if (_playingChannel is not { } channel || session != _coordinator.SessionId || _liveRetrySession == session) return;
        _liveRetrySession = session;
        if (!_liveRetry.TryAdvance(out int mirror)) { Status = "该频道所有线路都已尝试，请手动重试或选择其他频道。"; return; }
        _liveMirror = mirror; Status = $"直播线路无响应，尝试备用地址 {_liveMirror + 1}/{channel.Uris.Count}。";
        await OpenChannelMirrorAsync(channel);
    }, false);
    partial void OnLiveSearchChanged(string value) => FilterChannels();
    partial void OnSelectedLiveGroupChanged(string value) => FilterChannels();
    private void FilterChannels()
    {
        Channels.Clear();
        foreach (var channel in _allChannels.Where(x => (SelectedLiveGroup == "全部" || x.Group == SelectedLiveGroup)
            && x.Name.Contains(LiveSearch, StringComparison.OrdinalIgnoreCase) && (!OnlyFavoriteChannels || _liveFavorites.Contains((x.LiveSourceId, x.Id))))) Channels.Add(channel);
    }
    [RelayCommand] private Task PlayChannelAsync(LiveChannel? channel) => RunAsync(async () =>
    {
        if (channel is null || channel.Uris.Count == 0) return;
        _playlist.Clear(); _playingChannel = channel; _liveMirror = 0; _liveRetry.Reset(channel.Uris.Count);
        if (!Incognito) { _lastLiveConfigId = _config.Id; _lastLiveSourceId = channel.LiveSourceId; _lastLiveChannelId = channel.Id; SchedulePreferencesSave(); }
        await OpenChannelMirrorAsync(channel); _ = LoadProgrammeAsync(channel);
    });
    private async Task OpenChannelMirrorAsync(LiveChannel channel)
    {
        _liveProgressAt = DateTimeOffset.UtcNow; _livePosition = TimeSpan.Zero;
        await _coordinator.PlayAsync(_ => Task.FromResult(new PlaybackRequest
        { Uri = channel.Uris[_liveMirror], Title = channel.Name, IsLive = true, MediaId = channel.Id, SourceId = "live/" + channel.LiveSourceId, Headers = channel.Headers ?? [] }), _config.Id, _lifetime.Token);
    }
    [RelayCommand] private Task NextLiveMirrorAsync() => RunAsync(async () =>
    {
        if (_playingChannel is not { } channel || channel.Uris.Count < 2) { Status = "该频道没有备用地址。"; return; }
        _liveMirror = (_liveMirror + 1) % channel.Uris.Count; _liveRetry.Reset(channel.Uris.Count, _liveMirror); await OpenChannelMirrorAsync(channel);
        Status = $"使用备用地址 {_liveMirror + 1}/{channel.Uris.Count}。";
    });
    private Task LoadProgrammeAsync(LiveChannel channel) => RunAsync(async () =>
    {
        _epgCancellation?.Cancel(); using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _epgCancellation = cancellation;
        try
        {
            Programmes.Clear(); var source = _config.LiveSources.FirstOrDefault(x => x.Id == channel.LiveSourceId);
            if (source?.Epg is null) { EpgStatus = "该直播源未配置节目表。"; return; }
            EpgStatus = "正在加载节目表…";
            var schedule = await _epg.LoadAsync(source.Epg, cancellation.Token); cancellation.Token.ThrowIfCancellationRequested();
            var now = DateTimeOffset.UtcNow;
            foreach (var programme in EpgService.ForChannel(schedule, channel, source.EpgMap?.GetValueOrDefault(channel.Id), now))
                Programmes.Add(new(programme.Title, $"{programme.Start.ToLocalTime():MM-dd HH:mm} – {programme.End.ToLocalTime():HH:mm}", programme.Start <= now && programme.End > now));
            EpgStatus = Programmes.Count > 0 ? channel.Name + " · 节目表" : "没有匹配到该频道的节目；可在配置 epgMap 中指定频道 ID。";
        }
        finally { if (ReferenceEquals(_epgCancellation, cancellation)) _epgCancellation = null; }
    }, false);
}
