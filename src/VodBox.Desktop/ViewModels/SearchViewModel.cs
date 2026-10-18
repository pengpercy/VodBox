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
/// <summary>流式聚合：每个站点完成即回填，慢站点不阻塞快站点。</summary>
    private Func<string,CancellationToken,IAsyncEnumerable<VodBox.Infrastructure.AggregateSearchBatch>>? _streamSearch;
    private Func<int> _targetCount = () => 0;
    /// <summary>结果总数上限（卡片网格与归属映射共用）。</summary>
    private const int ResultLimit = 500;
    private readonly Dictionary<MediaItem, string> _sourceKeys = new(ReferenceEqualityComparer.Instance);
    private CancellationTokenSource? _request;
    private long _generation;
    private readonly MainViewModel _main;
    private readonly IPreferences? _preferences;

    public ObservableCollection<SearchSiteResult> SiteResults { get; } = [];
    public ObservableCollection<MediaItem> AllResults { get; } = [];
    /// <summary>搜索历史胶囊（点击回搜、× 删除）。</summary>
    public ObservableCollection<string> SearchHistory { get; } = [];

    /// <summary>未返回结果的站点（失败/超时/插件不受支持），侧栏单独一组列出，不再塞进汇总文案。</summary>
    public ObservableCollection<SearchFailure> Failures { get; } = [];

    /// <summary>结果行集合：按可用宽度分块（虚拟化网格的数据源，行容器数 = 可见行数）。</summary>
    public ObservableCollection<ResultRow> ResultRows { get; } = [];

    [ObservableProperty] private string _keyword = "";
    [ObservableProperty] private bool _searching;
    [ObservableProperty] private string _summary = "";
    /// <summary>结果区空态/错误态文案（非空时覆盖网格位置，避免白屏）。</summary>
    [ObservableProperty] private string _hint = "";
    /// <summary>
    /// 搜索窗口里打开的影片详情：在本窗口内展示（返回即回到原结果与滚动位置），不再把主窗口切到详情页。
    /// 独立实例，主窗口同时开着详情页也互不影响。
    /// </summary>
    public DetailViewModel Detail => _detail ??= _main.CreateDetachedDetail();
    private DetailViewModel? _detail;
    internal DetailViewModel? DetailIfCreated => _detail;
    /// <summary>当前在看详情（true）还是结果网格（false）。</summary>
    [ObservableProperty] private bool _showingDetail;

    /// <summary>当前选中的站点结果组（右侧网格随它切换）；null = 全部结果。</summary>
    [ObservableProperty] private SearchSiteResult? _selectedSite;
    /// <summary>界面上的简短状态行：搜索中为进度，结束为站点数与条数（失败明细在侧栏）。</summary>
    [ObservableProperty] private string _status = "";
    /// <summary>已完成站点占比（0~1），搜索中的细进度条用。</summary>
    [ObservableProperty] private double _progress;

    /// <summary>侧栏「全部」是否为当前选择。</summary>
    public bool IsAllSelected => SelectedSite is null;
    public bool HasFailures => Failures.Count > 0;
    /// <summary>空态（尚未搜索或没有结果）且有历史时展示最近搜索，结果页本身不再挂历史条。</summary>
    public bool ShowHistory => !Searching && ResultRows.Count == 0 && SearchHistory.Count > 0;

    /// <summary>搜索中且尚无任何结果 → 结果区骨架（清空旧结果后不留白屏）。</summary>
    public bool ShowSkeleton => Searching && ResultRows.Count == 0;
    /// <summary>搜索结束且没有结果 → 空态/失败文案提示。</summary>
    public bool ShowEmpty => !Searching && ResultRows.Count == 0;

    partial void OnSearchingChanged(bool value) => NotifyResultState();
    partial void OnHintChanged(string value) => NotifyResultState();

    private void NotifyResultState()
    {
        OnPropertyChanged(nameof(ShowSkeleton));
        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(ShowHistory));
    }

    public SearchViewModel(AppServices services, MainViewModel main) : this(services.Registry.SearchAllAsync, main, services.Prefs)
    {
        _detailedSearch=services.Registry.SearchWithFailuresAsync;
        _streamSearch=services.Registry.SearchStreamAsync;
        _targetCount=()=>services.Registry.SearchTargets.Count;
    }
    /// <summary>注入一次性返回的聚合搜索（测试用）；会关闭流式路径。</summary>
    internal void SetDetailedSearch(Func<string,CancellationToken,Task<VodBox.Infrastructure.AggregateSearchResult>> search){_detailedSearch=search;_streamSearch=null;}
    /// <summary>注入流式聚合搜索与站点总数（测试用）。</summary>
    internal void SetSearchStream(Func<string,CancellationToken,IAsyncEnumerable<VodBox.Infrastructure.AggregateSearchBatch>> search,int targets){_streamSearch=search;_targetCount=()=>targets;}

    internal SearchViewModel(Func<string, CancellationToken, Task<IReadOnlyList<(SourceInfo Site, MediaItem Item)>>> search, MainViewModel main, IPreferences? preferences = null)
    {
        _search = search;
        _main = main;
        _preferences = preferences;
        SearchHistory.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ShowHistory));
        Failures.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasFailures));
        try
        {
            var values = System.Text.Json.JsonSerializer.Deserialize(preferences?.GetString("search.history", "[]") ?? "[]", Json.TypeInfo<List<string>>()) ?? [];
            foreach (var query in values.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct().Take(HistoryLimit)) SearchHistory.Add(query);
        }
        catch (System.Text.Json.JsonException) { }
    }

    /// <summary>搜索历史保留条数（持久化到偏好，跨启动保留；最近的在前）。</summary>
    internal const int HistoryLimit = 20;
    /// <summary>
    /// 搜索历史下拉的条目：刚聚焦（<paramref name="all"/>）列出全部历史，最近的在前（像 FongMi/TV）；
    /// 输入后按关键词过滤，与输入完全相同的词不算联想（点了也只是原样再搜一次）。
    /// 侧栏、点播、搜索结果三个搜索框共用这一份历史与规则。
    /// </summary>
    public static IReadOnlyList<string> FilterHistory(IEnumerable<string> history, string typed, bool all)
    {
        typed = typed.Trim();
        var matches = all
            ? history
            : history.Where(query => typed.Length > 0 && query.Contains(typed, StringComparison.OrdinalIgnoreCase) && !string.Equals(query, typed, StringComparison.OrdinalIgnoreCase));
        return matches.Take(HistoryLimit).ToArray();
    }

    /// <summary>记一条搜索历史（移到最前、去重、截断并持久化）；点播页的站内搜索也走这里，三个搜索框共享。</summary>
    public void RecordSearchHistory(string query)
    {
        query = query.Trim();
        if (query.Length == 0) return;
        SearchHistory.Remove(query);
        SearchHistory.Insert(0, query);
        while (SearchHistory.Count > HistoryLimit) SearchHistory.RemoveAt(SearchHistory.Count - 1);
        SaveSearchHistory();
    }

    private void SaveSearchHistory()
    {
        try { _preferences?.Set("search.history", System.Text.Json.JsonSerializer.Serialize(SearchHistory.ToList(), Json.TypeInfo<List<string>>())); }
        catch (Exception error) { System.Diagnostics.Debug.WriteLine(error.Message); }
    }

    public void CancelPending()
    {
        ++_generation;_request?.Cancel();_request=null;Searching=false;
        CloseDetail();
    }

    /// <summary>从详情回到结果网格（返回按钮 / Esc）；取消详情在途请求。</summary>
    [RelayCommand]
    public void CloseDetail()
    {
        ShowingDetail = false;
        _detail?.CancelPending();
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
            Status = "正在搜索…";
            // 在看详情时回到结果（如详情里的「跨站搜索」）；历史下拉由搜索框自己在回车/选词时收起。
            CloseDetail();
            Progress = 0;
            Hint = "";
            SelectedSite = null;
            Failures.Clear();
            SiteResults.Clear();
            AllResults.Clear();
            _sourceKeys.Clear();
            RebuildResultRows();
        });
        if (scope is null) return;
        var ct = scope.Token;
        try
        {
            if (_streamSearch is { } stream)
            {
                var failures = new List<(SourceInfo Site, string Error)>();
                var total = _targetCount();
                var done = 0;
                await foreach (var batch in stream(query, ct))
                {
                    await _main.RunOnUiAsync(() =>
                    {
                        if (generation != _generation || ct.IsCancellationRequested) return;
                        done++;
                        if (batch.Error is { } error) { failures.Add((batch.Site, error)); Failures.Add(new SearchFailure(batch.Site.Name, error)); }
                        else if (AddSiteResults(batch.Site, batch.Items)) RebuildResultRows();
                        var all = Math.Max(done, total);
                        Summary = $"「{query}」已搜索 {done}/{all} 个站点，{SiteResults.Count} 个站点找到 {AllResults.Count} 条结果…";
                        Status = $"已搜索 {done}/{all} 个站点 · {AllResults.Count} 条结果";
                        Progress = (double)done / all;
                    });
                }
                ct.ThrowIfCancellationRequested();
                await _main.RunOnUiAsync(() =>
                {
                    if (generation != _generation || ct.IsCancellationRequested) return;
                    FinishSearch(query, failures);
                });
                return;
            }
            var aggregate=_detailedSearch is null?null:await _detailedSearch(query,ct);
            var results = aggregate?.Results??await _search(query, ct);
            ct.ThrowIfCancellationRequested();
            await _main.RunOnUiAsync(() =>
            {
                if (generation != _generation || ct.IsCancellationRequested) return;
                foreach (var group in results.GroupBy(r => r.Site.Key))
                    AddSiteResults(group.First().Site, group.Select(r => r.Item));
                RebuildResultRows();
                FinishSearch(query, aggregate?.Failures ?? []);
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception error)
        {
            await _main.RunOnUiAsync(() =>
            {
                if (generation == _generation && !ct.IsCancellationRequested)
                {
                    Status = "搜索失败";
                    Summary = $"搜索失败：{error.Message}";
                    Hint = $"搜索失败：{error.Message}";
                }
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

    /// <summary>
    /// 把一个站点的结果并入：卡片、站点过滤与归属映射共用同一批实例，避免展示可见却打不开的卡片；
    /// 入集合前给每条结果打上来源站点名。返回是否有新增。
    /// </summary>
    private bool AddSiteResults(SourceInfo site, IEnumerable<MediaItem> items)
    {
        var room = ResultLimit - AllResults.Count;
        if (room <= 0) return false;
        var tagged = items.Take(room).Select(item => item with { SourceName = site.Name }).ToArray();
        if (tagged.Length == 0) return false;
        var group = SiteResults.FirstOrDefault(result => result.SourceKey == site.Key);
        if (group is null) SiteResults.Add(group = new SearchSiteResult(site.Name, site.Key));
        foreach (var item in tagged)
        {
            AllResults.Add(item);
            _sourceKeys[item] = site.Key;
            group.Items.Add(item);
        }
        return true;
    }

    private void FinishSearch(string query, IReadOnlyList<(SourceInfo Site, string Error)> failures)
    {
        // 流式路径边到边加；一次性路径在这里补齐。
        if (Failures.Count != failures.Count)
        {
            Failures.Clear();
            foreach (var (site, error) in failures) Failures.Add(new SearchFailure(site.Name, error));
        }
        Progress = 1;
        Status = AllResults.Count == 0 ? "没有找到结果" : $"{SiteResults.Count} 个站点 · {AllResults.Count} 条结果";
        Summary = $"「{query}」在 {SiteResults.Count} 个站点找到 {AllResults.Count} 条结果";
        if (failures.Count > 0) Summary += $"；{failures.Count} 个站点失败：" + string.Join("；", failures.Take(5).Select(failure => failure.Site.Name + "：" + failure.Error));
        Hint = AllResults.Count == 0 ? "没有找到匹配的结果，换个关键词或站点试试" : "";
        RecordSearchHistory(query);
    }

    [RelayCommand]
    private void DeleteSearchHistory(string word)
    {
        SearchHistory.Remove(word);SaveSearchHistory();
    }

    [RelayCommand]
    private void ClearSearchHistory()
    {
        SearchHistory.Clear();SaveSearchHistory();
    }

    [RelayCommand]
    private void OpenItem(MediaItem item)
    {
        if (!_sourceKeys.TryGetValue(item, out var key))
            key = SiteResults.FirstOrDefault(s => s.Items.Contains(item))?.SourceKey;
        if (key is null) return;
        ShowingDetail = true;
        Detail.Open(key, item);
    }

    private int _columns = 5;

    /// <summary>当前结果列数；骨架屏按同一列数铺占位卡，避免加载完成时跳列。</summary>
    public int Columns => _columns;

    /// <summary>卡片间距（右、下），与网格外沿对齐。</summary>
    public const double CardGap = 18;
    private double _cardWidth = 170;
    /// <summary>卡片宽度：按列数平分可用宽度，网格铺满、右侧不留大块空白。</summary>
    public double CardWidth => _cardWidth;
    /// <summary>海报高度保持 2:3 竖版比例。</summary>
    public double PosterHeight => Math.Round(_cardWidth * 1.4);

    public void SetResultWidth(double width)
    {
        if (width <= 0) return; // 隐藏时布局宽度为 0，忽略以免把列数压成 1
        var columns = Math.Max(1, (int)Math.Floor(Math.Max(0, width - 16) / 188));
        var cardWidth = Math.Max(120, Math.Floor((width - 4) / columns - CardGap));
        if (cardWidth != _cardWidth)
        {
            _cardWidth = cardWidth;
            OnPropertyChanged(nameof(CardWidth));
            OnPropertyChanged(nameof(PosterHeight));
        }
        if (columns == _columns) return;
        _columns = columns;
        OnPropertyChanged(nameof(Columns));
        RebuildResultRows();
    }

    partial void OnSelectedSiteChanged(SearchSiteResult? value)
    {
        OnPropertyChanged(nameof(IsAllSelected));
        RebuildResultRows();
    }

    private void RebuildResultRows()
    {
        var items = SelectedSite is { } site ? site.Items.ToList() : AllResults.ToList();
        ResultRows.Clear();
        for (var i = 0; i < items.Count; i += _columns)
            ResultRows.Add(new ResultRow(items.Skip(i).Take(_columns).ToList()));
        NotifyResultState();
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

/// <summary>未返回结果的站点及原因（侧栏「未返回结果」分组，原因放悬停提示）。</summary>
public sealed record SearchFailure(string SiteName, string Error);

/// <summary>搜索历史下拉的一行；<see cref="IsSelected"/> 是键盘 ↑/↓ 的当前行。</summary>
public sealed partial class SuggestItem : ObservableObject
{
    public string Text { get; init; } = "";
    [ObservableProperty] private bool _isSelected;
}

/// <summary>结果网格行（动态列数）。</summary>
public sealed class ResultRow
{
    public IReadOnlyList<MediaItem> Items { get; }
    public ResultRow(IReadOnlyList<MediaItem> items) => Items = items;
}
