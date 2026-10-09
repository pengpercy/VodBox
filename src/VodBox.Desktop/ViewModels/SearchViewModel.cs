using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VodBox.Core;
using VodBox.Desktop.Services;

namespace VodBox.Desktop.ViewModels;

/// <summary>搜索：多站点并发聚合 + 按站点切换结果。</summary>
public sealed partial class SearchViewModel : ObservableObject
{
    private readonly Func<string, CancellationToken, Task<IReadOnlyList<(SourceInfo Site, MediaItem Item)>>> _search;
    private Func<string,CancellationToken,Task<VodBox.Infrastructure.AggregateSearchResult>>? _detailedSearch;
    private readonly Dictionary<MediaItem, string> _sourceKeys = new(ReferenceEqualityComparer.Instance);
    private CancellationTokenSource? _request;
    private long _generation;
    private readonly MainViewModel _main;
    private readonly IPreferences? _preferences;

    public ObservableCollection<SearchSiteResult> SiteResults { get; } = [];
    public ObservableCollection<MediaItem> AllResults { get; } = [];
    /// <summary>输入联想（站点 suggest 接口 / 本地历史混合，S5 接真接口）。</summary>
    public ObservableCollection<SuggestItem> Suggestions { get; } = [];
    /// <summary>搜索历史胶囊（点击回搜、× 删除）。</summary>
    public ObservableCollection<string> SearchHistory { get; } = [];

    /// <summary>结果行集合：按可用宽度分块（虚拟化网格的数据源，行容器数 = 可见行数）。</summary>
    public ObservableCollection<ResultRow> ResultRows { get; } = [];

    [ObservableProperty] private string _keyword = "";
    [ObservableProperty] private bool _searching;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private bool _showSuggestions;
    /// <summary>当前选中的站点结果组（右侧网格随它切换）；null = 全部结果。</summary>
    [ObservableProperty] private SearchSiteResult? _selectedSite;

    public SearchViewModel(AppServices services, MainViewModel main) : this(services.Registry.SearchAllAsync, main, services.Prefs)
    { _detailedSearch=services.Registry.SearchWithFailuresAsync; }
    internal void SetDetailedSearch(Func<string,CancellationToken,Task<VodBox.Infrastructure.AggregateSearchResult>> search)=>_detailedSearch=search;

    internal SearchViewModel(Func<string, CancellationToken, Task<IReadOnlyList<(SourceInfo Site, MediaItem Item)>>> search, MainViewModel main, IPreferences? preferences = null)
    {
        _search = search;
        _main = main;
        _preferences = preferences;
        try
        {
            var values = System.Text.Json.JsonSerializer.Deserialize(preferences?.GetString("search.history", "[]") ?? "[]", Json.TypeInfo<List<string>>()) ?? [];
            foreach (var query in values.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct().Take(8)) SearchHistory.Add(query);
        }
        catch (System.Text.Json.JsonException) { }
    }

    partial void OnKeywordChanged(string value)
    {
        Suggestions.Clear();
        foreach (var query in SearchHistory.Where(query => query.Contains(value, StringComparison.OrdinalIgnoreCase)).Take(8))
            Suggestions.Add(new SuggestItem { Text = query });
        ShowSuggestions = value.Length > 0 && Suggestions.Count > 0;
    }

    private void SaveSearchHistory()
    {
        try { _preferences?.Set("search.history", System.Text.Json.JsonSerializer.Serialize(SearchHistory.ToList(), Json.TypeInfo<List<string>>())); }
        catch (Exception error) { System.Diagnostics.Debug.WriteLine(error.Message); }
    }

    public void CancelPending()
    {
        ++_generation;_request?.Cancel();_request=null;Searching=false;
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    public async Task RunSearchAsync()
    {
        string query = "";
        long generation = 0;
        CancellationTokenSource? scope = null;
        await _main.RunOnUiAsync(() =>
        {
            query = Keyword.Trim();
            if (query.Length == 0) return;
            _request?.Cancel();
            scope = new CancellationTokenSource();
            _request = scope;
            generation = ++_generation;
            Searching = true;
            Summary = $"「{query}」搜索中…";
            SelectedSite = null;
            SiteResults.Clear();
            AllResults.Clear();
            _sourceKeys.Clear();
            RebuildResultRows();
        });
        if (scope is null) return;
        var ct = scope.Token;
        try
        {
            var aggregate=_detailedSearch is null?null:await _detailedSearch(query,ct);
            var results = aggregate?.Results??await _search(query, ct);
            ct.ThrowIfCancellationRequested();
            await _main.RunOnUiAsync(() =>
            {
                if (generation != _generation || ct.IsCancellationRequested) return;
                // 单一结果集合用于卡片、站点过滤及归属映射，避免展示可见却打不开的卡片。
                var retained = results.Take(500).ToArray();
                foreach (var (site, item) in retained)
                {
                    AllResults.Add(item);
                    _sourceKeys[item] = site.Key;
                }
                foreach (var group in retained.GroupBy(r => r.Site.Key))
                {
                    var first = group.First().Site;
                    var site = new SearchSiteResult(first.Name, first.Key);
                    foreach (var (_, item) in group) site.Items.Add(item);
                    SiteResults.Add(site);
                }
                RebuildResultRows();
                Summary = $"「{query}」在 {SiteResults.Count} 个站点找到 {retained.Length} 条结果";
                if(aggregate?.Failures.Count>0)Summary+=$"；{aggregate.Failures.Count} 个站点失败："+string.Join("；",aggregate.Failures.Take(5).Select(failure=>failure.Site.Name+"："+failure.Error));
                ShowSuggestions = false;
                SearchHistory.Remove(query);
                SearchHistory.Insert(0, query);
                if (SearchHistory.Count > 8) SearchHistory.RemoveAt(SearchHistory.Count - 1);
                SaveSearchHistory();
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception error)
        {
            await _main.RunOnUiAsync(() =>
            {
                if (generation == _generation && !ct.IsCancellationRequested) Summary = $"搜索失败：{error.Message}";
            });
        }
        finally
        {
            await _main.RunOnUiAsync(() =>
            {
                if (generation != _generation) return;
                _request = null;
                Searching = false;
            });
            scope.Dispose();
        }
    }

    [RelayCommand]
    private void DeleteSearchHistory(string word)
    {
        SearchHistory.Remove(word);SaveSearchHistory();OnKeywordChanged(Keyword);
    }

    [RelayCommand]
    private void ClearSearchHistory()
    {
        SearchHistory.Clear();Suggestions.Clear();ShowSuggestions=false;SaveSearchHistory();
    }

    [RelayCommand]
    private void OpenItem(MediaItem item)
    {
        if (!_sourceKeys.TryGetValue(item, out var key))
            key = SiteResults.FirstOrDefault(s => s.Items.Contains(item))?.SourceKey;
        if (key is not null) _main.Detail.Open(key, item);
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

/// <summary>结果网格行（动态列数）。</summary>
public sealed class ResultRow
{
    public IReadOnlyList<MediaItem> Items { get; }
    public ResultRow(IReadOnlyList<MediaItem> items) => Items = items;
}
