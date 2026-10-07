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

    public HistoryViewModel(AppServices services, MainViewModel main)
    {
        _services = services;
        _main = main;
    }

    public async Task LoadAsync()
    {
        Entries.Clear();
        foreach (var entry in await _services.Store.GetHistoryAsync(200)) Entries.Add(entry);
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
