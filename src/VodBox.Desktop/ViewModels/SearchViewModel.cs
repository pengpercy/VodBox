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
    /// <summary>输入联想（站点 suggest 接口 / 本地历史混合，S5 接真接口）。</summary>
    public ObservableCollection<SuggestItem> Suggestions { get; } = [];
    /// <summary>搜索历史胶囊（点击回搜、× 删除）。</summary>
    public ObservableCollection<string> SearchHistory { get; } = [];

    /// <summary>结果行集合：每行 5 张卡（虚拟化网格的数据源，行容器数 = 可见行数）。</summary>
    public ObservableCollection<ResultRow> ResultRows { get; } = [];

    [ObservableProperty] private string _keyword = "";
    [ObservableProperty] private bool _searching;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private bool _showSuggestions;
    /// <summary>当前选中的站点结果组（右侧网格随它切换）；null = 全部结果。</summary>
    [ObservableProperty] private SearchSiteResult? _selectedSite;

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
            foreach (var (_, item) in results.Take(500)) AllResults.Add(item);
            RebuildResultRows();
            var bySite = results.GroupBy(r => r.Site).ToList();
            foreach (var group in bySite)
            {
                var site = new SearchSiteResult(group.Key.Name, group.Key.Key);
                SiteResults.Add(site);
                foreach (var (_, item) in group.Take(40))
                    site.Items.Add(item);
            }
            SelectedSite = null;
            Summary = $"「{Keyword}」在 {bySite.Count} 个站点找到结果";
            ShowSuggestions = false;
            if (!SearchHistory.Contains(Keyword))
            {
                SearchHistory.Insert(0, Keyword);
                if (SearchHistory.Count > 8) SearchHistory.RemoveAt(SearchHistory.Count - 1);
            }
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

    private int _columns = 5;

    public void SetResultWidth(double width)
    {
        var columns = Math.Max(1, (int)Math.Floor(Math.Max(0, width - 16) / 188));
        if (columns == _columns) return;
        _columns = columns;
        RebuildResultRows();
    }

    partial void OnSelectedSiteChanged(SearchSiteResult? value) => RebuildResultRows();

    private void RebuildResultRows()
    {
        var items = SelectedSite is { } site ? site.Items.ToList() : AllResults.ToList();
        ResultRows.Clear();
        for (var i = 0; i < items.Count; i += _columns)
            ResultRows.Add(new ResultRow(items.Skip(i).Take(_columns).ToList()));
    }

}

/// <summary>单站点搜索结果组。</summary>
public sealed partial class SearchSiteResult : ObservableObject
{
    public string SiteName { get; }
    public string SourceKey { get; }
    public ObservableCollection<MediaItem> Items { get; } = [];

    [ObservableProperty] private bool _selected;
    /// <summary>该站点是否仍在并行搜索（列表行显示转圈）。</summary>
    [ObservableProperty] private bool _loading;

    public SearchSiteResult(string siteName, string sourceKey)
    {
        SiteName = siteName;
        SourceKey = sourceKey;
        Selected = true;
    }
}

/// <summary>搜索联想行（热词/历史）。</summary>
public sealed class SuggestItem
{
    public string Text { get; init; } = "";
    public bool Hot { get; init; }
}

/// <summary>结果网格行（5 张卡）。</summary>
public sealed class ResultRow
{
    public IReadOnlyList<MediaItem> Items { get; }
    public ResultRow(IReadOnlyList<MediaItem> items) => Items = items;
}
