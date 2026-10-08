using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VodBox.Core;
using VodBox.Desktop.Services;

namespace VodBox.Desktop.ViewModels;

/// <summary>收藏：点播收藏 / 直播频道两 Tab。</summary>
public sealed partial class FavoritesViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly MainViewModel _main;

    public ObservableCollection<FavoriteEntry> Vod { get; } = [];
    public ObservableCollection<FavoriteEntry> Live { get; } = [];

    [ObservableProperty] private int _tab; // 0=点播 1=直播
    /// <summary>管理模式（多选删除，设计稿 ①）。S5 接批量删除。</summary>
    [ObservableProperty] private bool _managing;
    /// <summary>排序方式（最近收藏/标题，S5 接）。</summary>
    [ObservableProperty] private string _sortBy = "最近收藏";

    public FavoritesViewModel(AppServices services, MainViewModel main)
    {
        _services = services;
        _main = main;
    }

    public async Task LoadAsync()
    {
        Vod.Clear();
        foreach (var entry in await _services.Store.GetFavoritesAsync(FavoriteKind.Vod)) Vod.Add(entry);
        Live.Clear();
        foreach (var entry in await _services.Store.GetFavoritesAsync(FavoriteKind.Live)) Live.Add(entry);
    }

    [RelayCommand]
    private void Open(FavoriteEntry entry)
    {
        _main.Detail.Resume(new HistoryEntry
        {
            SourceKey = entry.SourceKey,
            SourceName = entry.SourceName,
            MediaId = entry.MediaId,
            Title = entry.Title,
            Poster = entry.Poster,
        });
    }

    [RelayCommand]
    private void ToggleManage() => Managing = !Managing;

    [RelayCommand]
    private async Task Remove(FavoriteEntry entry)
    {
        await _services.Store.SetFavoriteAsync(entry, false);
        (entry.Kind == FavoriteKind.Vod ? Vod : Live).Remove(entry);
    }
}

/// <summary>历史：按时间分组、续播、删除。</summary>
public sealed partial class HistoryViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly MainViewModel _main;

    public ObservableCollection<HistoryEntry> Entries { get; } = [];

    /// <summary>无痕模式开启时页面顶部横幅（设计稿 ②）。S6 设置接开关。</summary>
    [ObservableProperty] private bool _incognitoBanner = false;
    /// <summary>今天的历史（首页同款卡片，直播含「看了 N 分钟」）。</summary>
    public ObservableCollection<HistoryEntry> Today { get; } = [];
    /// <summary>更早历史（今天之前的记录）。</summary>
    public ObservableCollection<HistoryEntry> Earlier { get; } = [];

    public HistoryViewModel(AppServices services, MainViewModel main)
    {
        _services = services;
        _main = main;
    }

    public async Task LoadAsync()
    {
        Entries.Clear();
        foreach (var entry in await _services.Store.GetHistoryAsync(200)) Entries.Add(entry);
        SplitByDay();
    }

    /// <summary>按「今天 / 更早」拆两组（设计稿：历史 · 今天 分组）。</summary>
    public void SplitByDay()
    {
        Today.Clear();
        Earlier.Clear();
        foreach (var entry in Entries)
        {
            if (entry.UpdatedAt.Date == DateTimeOffset.Now.Date) Today.Add(entry);
            else Earlier.Add(entry);
        }
    }

    [RelayCommand]
    private void Resume(HistoryEntry entry) => _main.Detail.Resume(entry);

    [RelayCommand]
    private async Task Delete(HistoryEntry entry)
    {
        await _services.Store.DeleteHistoryAsync(entry.SourceKey, entry.MediaId);
        Entries.Remove(entry);
    }

    [RelayCommand]
    private async Task ClearAll()
    {
        await _services.Store.ClearHistoryAsync();
        Entries.Clear();
    }
}
