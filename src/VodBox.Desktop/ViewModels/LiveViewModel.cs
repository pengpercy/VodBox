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

    /// <summary>频道面板行集合：组头（LiveGroupHeader）+ 频道行（LiveChannel）混排，UI 按类型选模板。</summary>
    public ObservableCollection<object> VisibleChannels { get; } = [];

    /// <summary>EPG 时间轴（当前频道的节目卡片，过去可回看、现在高亮）。S4 前为演示数据。</summary>
    public ObservableCollection<LiveEpgCard> EpgTimeline { get; } = [];

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
        // 面板两级结构：组头（▾ 名称 + 计数）+ 频道行；密码分组只显示 🔒 组头不展开。
        // 筛选按频道名过滤；有筛选词时隐藏空组（含密码组）。
        foreach (var group in Groups)
        {
            var matches = group.Locked
                ? []
                : group.Channels.Where(c => string.IsNullOrWhiteSpace(FilterText) ||
                       c.Name.Contains(FilterText, StringComparison.OrdinalIgnoreCase)).ToList();
            if (!string.IsNullOrWhiteSpace(FilterText) && matches.Count == 0) continue;
            VisibleChannels.Add(new LiveGroupHeader(group.Name, group.Count, group.Locked));
            foreach (var channel in matches) VisibleChannels.Add(channel);
        }
    }

    /// <summary>频道面板组头行（▾ 央视频道 18 / ▸ 🔒 影视频道 36）。</summary>
    public sealed record LiveGroupHeader(string Name, int Count, bool Locked)
    {
        public string Arrow => Locked ? "▸" : "▾";
        public string LockIcon => Locked ? "🔒 " : "";
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
            var keep = CurrentChannel;
            Groups.Clear();
            foreach (var group in groups) Groups.Add(group);
            SelectedGroup = Groups.FirstOrDefault();
            if (keep is { } ch && VisibleChannels.Contains(ch)) SetCurrent(ch);
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
        SetCurrent(channel);
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

    /// <summary>仅更新选中态与 EPG 时间轴，不触播放（设计数据 / 无 URI 频道复用）。</summary>
    public void SetCurrent(LiveChannel channel)
    {
        foreach (var item in VisibleChannels.OfType<LiveChannel>()) item.IsCurrent = false;
        channel.IsCurrent = true;
        CurrentChannel = channel;
        RefreshEpgTimeline();
    }

    /// <summary>演示用 EPG 时间轴：以当前时刻为锚生成「过去两档 + 直播中 + 未来两档」。</summary>
    private void RefreshEpgTimeline()
    {
        EpgTimeline.Clear();
        if (CurrentChannel is not { } channel) return;
        var now = DateTimeOffset.Now;
        var titles = new[] { "晚间新闻", "新闻联播", "焦点访谈", "黄金档剧场", "晚间剧场" };
        var lengths = new[] { 60, 38, 28, 94, 110 };
        var start = now.AddMinutes(-98);
        for (var i = 0; i < titles.Length; i++)
        {
            var end = start.AddMinutes(lengths[i]);
            EpgTimeline.Add(new LiveEpgCard
            {
                Title = titles[i],
                Start = start,
                End = end,
                IsNow = now >= start && now < end,
                IsPast = now >= end,
            });
            start = end;
        }
    }

    /// <summary>上一台（循环）：按 VisibleChannels 顺序回退。</summary>
    [RelayCommand]
    public void PrevChannel() => StepChannel(-1);

    /// <summary>下一台（循环）。</summary>
    [RelayCommand]
    public void NextChannel() => StepChannel(1);

    private void StepChannel(int delta)
    {
        var channels = VisibleChannels.OfType<LiveChannel>().ToList();
        if (channels.Count == 0) return;
        var index = CurrentChannel is { } current ? channels.IndexOf(current) : -1;
        var next = (index + delta + channels.Count) % channels.Count;
        PlayChannel(channels[next]);
    }

    /// <summary>切换线路：同频道多 URL 轮换（m3u 分号多地址）。</summary>
    [RelayCommand]
    public void SwitchLine()
    {
        if (CurrentChannel is not { } channel || channel.Uris.Count < 2) return;
        var uri = channel.Uris[1]; // 演示：切到第二条线路
        _main.Player.Play(new PlaybackRequest
        {
            Uri = uri,
            Title = channel.Name,
            SourceKey = "live",
            SourceName = "直播",
            MediaId = uri,
            IsLive = true,
        });
    }

    /// <summary>回看：EPG 过去节目点击后从头播放（catchup seek 由 S4 接 EPG 接口）。</summary>
    [RelayCommand]
    public void Catchup()
    {
        _main.StatusMessage = "回看需要 EPG catchup 支持（S4 直播中心），当前直播从头播放";
    }
}

/// <summary>EPG 时间轴卡片（设计稿 p-live ④）。</summary>
public sealed partial class LiveEpgCard : ObservableObject
{
    public string Title { get; init; } = "";
    public DateTimeOffset Start { get; init; }
    public DateTimeOffset End { get; init; }
    [ObservableProperty] private bool _isNow;
    [ObservableProperty] private bool _isPast;

    public string Range => $"{Start:HH:mm} — {End:HH:mm}{(IsPast ? " · 已回看" : IsNow ? " · 直播" : "")}";
}
