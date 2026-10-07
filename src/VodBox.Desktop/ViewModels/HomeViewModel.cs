using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VodBox.Core;
using VodBox.Desktop.Services;

namespace VodBox.Desktop.ViewModels;

/// <summary>首页：推荐位 + 最近观看（带进度）。</summary>
public sealed partial class HomeViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly MainViewModel _main;

    public ObservableCollection<MediaItem> Recommendations { get; } = [];
    public ObservableCollection<HistoryEntry> Recent { get; } = [];

    [ObservableProperty] private bool _loading;
    [ObservableProperty] private string _heroTitle = "";
    [ObservableProperty] private string _heroRemarks = "";
    [ObservableProperty] private string _heroDescription = "";
    private MediaItem? _hero;

    public HomeViewModel(AppServices services, MainViewModel main)
    {
        _services = services;
        _main = main;
    }

    public async Task LoadAsync()
    {
        Recent.Clear();
        foreach (var entry in await _services.Store.GetHistoryAsync(12))
            Recent.Add(entry);
        var source = _services.Registry.Default();
        if (source is null)
        {
            _main.StatusMessage = "尚未配置内容源，请到设置中添加 TVBox 配置地址";
            return;
        }
        Loading = true;
        try
        {
            var page = await source.GetHomeAsync();
            Recommendations.Clear();
            foreach (var item in page.Items.Take(24))
                Recommendations.Add(item);
            _hero = page.Items.FirstOrDefault();
            HeroTitle = _hero?.Title ?? "";
            HeroRemarks = _hero?.Remarks ?? "";
            HeroDescription = $"{_hero?.Year ?? ""} · {_hero?.Area ?? ""} · {_hero?.TypeName ?? ""}";
        }
        catch (Exception error)
        {
            _main.StatusMessage = $"加载推荐失败：{error.Message}";
        }
        finally
        {
            Loading = false;
        }
    }

    [RelayCommand]
    private void OpenHero()
    {
        if (_hero is not null) _main.Detail.Open(_services.Registry.Sources[0].Key, _hero);
    }

    [RelayCommand]
    private void OpenItem(MediaItem item) => _main.Detail.Open(CurrentSourceKey(), item);

    [RelayCommand]
    private void Resume(HistoryEntry entry) => _main.Detail.Resume(entry);

    private string CurrentSourceKey() =>
        _services.Registry.Sources.FirstOrDefault(s => s.Runtime == SourceRuntime.MacCms)?.Key ?? "";
}
