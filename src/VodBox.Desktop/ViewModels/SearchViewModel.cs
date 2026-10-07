using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VodBox.Core;
using VodBox.Desktop.Services;

namespace VodBox.Desktop.ViewModels;

/// <summary>搜索：多站点并发聚合 + 按站点切换结果。</summary>
public sealed partial class SearchViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly MainViewModel _main;

    public ObservableCollection<SearchSiteResult> SiteResults { get; } = [];
    public ObservableCollection<MediaItem> AllResults { get; } = [];

    [ObservableProperty] private string _keyword = "";
    [ObservableProperty] private bool _searching;
    [ObservableProperty] private string _summary = "";

    public SearchViewModel(AppServices services, MainViewModel main)
    {
        _services = services;
        _main = main;
    }

    [RelayCommand]
    public async Task RunSearchAsync()
    {
        if (string.IsNullOrWhiteSpace(Keyword)) return;
        Searching = true;
        Summary = $"「{Keyword}」搜索中…";
        SiteResults.Clear();
        try
        {
            var results = await _services.Registry.SearchAllAsync(Keyword);
            AllResults.Clear();
            foreach (var (_, item) in results.Take(60)) AllResults.Add(item);
            var bySite = results.GroupBy(r => r.Site).ToList();
            foreach (var group in bySite)
            {
                var site = new SearchSiteResult(group.Key.Name, group.Key.Key);
                SiteResults.Add(site);
                foreach (var (_, item) in group.Take(40))
                    site.Items.Add(item);
            }
            Summary = $"「{Keyword}」在 {bySite.Count} 个站点找到 {results.Count} 条结果";
        }
        catch (Exception error)
        {
            Summary = $"搜索失败：{error.Message}";
        }
        finally
        {
            Searching = false;
        }
    }

    [RelayCommand]
    private void OpenItem(MediaItem item)
    {
        var site = SiteResults.FirstOrDefault(s => s.Items.Contains(item));
        if (site is null) return;
        _main.Detail.Open(site.SourceKey, item);
    }
}

/// <summary>单站点搜索结果组。</summary>
public sealed partial class SearchSiteResult : ObservableObject
{
    public string SiteName { get; }
    public string SourceKey { get; }
    public ObservableCollection<MediaItem> Items { get; } = [];

    [ObservableProperty] private bool _selected;

    public SearchSiteResult(string siteName, string sourceKey)
    {
        SiteName = siteName;
        SourceKey = sourceKey;
        Selected = true;
    }
}
