using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VodBox.Core;
using VodBox.Desktop.Services;

namespace VodBox.Desktop.ViewModels;

/// <summary>直播：频道面板（分组/收藏/历史）+ 播放。</summary>
public sealed partial class LiveViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly MainViewModel _main;

    public ObservableCollection<LiveGroup> Groups { get; } = [];
    public ObservableCollection<LiveChannel> Favorites { get; } = [];

    [ObservableProperty] private LiveGroup? _selectedGroup;
    [ObservableProperty] private LiveChannel? _currentChannel;
    [ObservableProperty] private bool _loading;
    [ObservableProperty] private string _filterText = "";

    public ObservableCollection<LiveChannel> VisibleChannels { get; } = [];

    public LiveViewModel(AppServices services, MainViewModel main)
    {
        _services = services;
        _main = main;
    }

    partial void OnSelectedGroupChanged(LiveGroup? value) => ApplyFilter();
    partial void OnFilterTextChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        VisibleChannels.Clear();
        var source = SelectedGroup?.Channels ?? [];
        foreach (var channel in source)
        {
            if (string.IsNullOrWhiteSpace(FilterText) ||
                channel.Name.Contains(FilterText, StringComparison.OrdinalIgnoreCase))
                VisibleChannels.Add(channel);
        }
    }

    public async Task LoadAsync()
    {
        var url = _services.CurrentLiveConfig;
        if (string.IsNullOrWhiteSpace(url))
        {
            _main.StatusMessage = "未配置直播源，请到设置中添加 m3u 地址";
            return;
        }
        Loading = true;
        try
        {
            var loader = new VodBox.Infrastructure.LiveSources(new VodBox.Infrastructure.DefaultHttp());
            var groups = await loader.LoadAsync(url);
            Groups.Clear();
            foreach (var group in groups) Groups.Add(group);
            SelectedGroup = Groups.FirstOrDefault();
        }
        catch (Exception error)
        {
            _main.StatusMessage = $"直播源加载失败：{error.Message}";
        }
        finally
        {
            Loading = false;
        }
    }

    [RelayCommand]
    public void PlayChannel(LiveChannel channel)
    {
        CurrentChannel = channel;
        var uri = channel.Uris.FirstOrDefault();
        if (uri is null) return;
        _main.Player.Play(new PlaybackRequest
        {
            Uri = uri,
            Title = channel.Name,
            // MediaId 用频道地址，否则所有直播频道会挤进历史表同一行（source_key+media_id 是主键）
            SourceKey = "live",
            SourceName = "直播",
            MediaId = uri,
            IsLive = true,
        });
    }
}
