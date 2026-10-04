using CommunityToolkit.Mvvm.Input;
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
    partial void OnLiveSearchChanged(string value) => FilterChannels();
    partial void OnSelectedLiveGroupChanged(string value) => FilterChannels();
    private void FilterChannels()
    {
        Channels.Clear();
        foreach (var channel in _allChannels.Where(x => (SelectedLiveGroup == "全部" || x.Group == SelectedLiveGroup)
            && x.Name.Contains(LiveSearch, StringComparison.OrdinalIgnoreCase))) Channels.Add(channel);
    }
    [RelayCommand] private Task PlayChannelAsync(LiveChannel? channel) => RunAsync(async () =>
    {
        if (channel is null || channel.Uris.Count == 0) return;
        _playlist.Clear(); _playingChannel = channel; _liveMirror = 0;
        await OpenChannelMirrorAsync(channel); _ = LoadProgrammeAsync(channel);
    });
    private Task OpenChannelMirrorAsync(LiveChannel channel) => _coordinator.PlayAsync(_ => Task.FromResult(new PlaybackRequest
    { Uri = channel.Uris[_liveMirror], Title = channel.Name, IsLive = true, SourceId = "live/" + channel.LiveSourceId, Headers = channel.Headers ?? [] }), _config.Id);
    [RelayCommand] private Task NextLiveMirrorAsync() => RunAsync(async () =>
    {
        if (_playingChannel is not { } channel || channel.Uris.Count < 2) { Status = "该频道没有备用地址。"; return; }
        _liveMirror = (_liveMirror + 1) % channel.Uris.Count; await OpenChannelMirrorAsync(channel);
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
