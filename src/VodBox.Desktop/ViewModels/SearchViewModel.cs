using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VodBox.Core;
using VodBox.Desktop.Services;
using VodBox.Infrastructure;

namespace VodBox.Desktop.ViewModels;

/// <summary>搜索（LunaTV 搜索页）：全部采集源流式聚合；「聚合」视图按片名+年份+类型合并，「全部」视图逐条展示；侧栏按源筛选。</summary>
public sealed partial class SearchViewModel : ObservableObject
{
    /// <summary>流式聚合：每个源完成即回填，慢源不阻塞快源。</summary>
    private Func<string,CancellationToken,IAsyncEnumerable<SourceSearchBatch>>? _streamSearch;
    private Func<int> _targetCount = () => 0;
    /// <summary>结果总数上限（卡片网格与归属映射共用）。</summary>
    private const int ResultLimit = 500;
    private readonly Dictionary<MediaItem, VodResult> _itemResults = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<MediaItem, IReadOnlyList<VodResult>> _cardGroups = new(ReferenceEqualityComparer.Instance);
    private readonly List<VodResult> _results = [];
    /// <summary>聚合卡片（同名同年同类型合并，LunaTV 默认视图）。</summary>
    public ObservableCollection<MediaItem> AggregatedResults { get; } = [];
    /// <summary>true = 聚合视图，false = 逐条（全部）视图。</summary>
    [ObservableProperty] private bool _aggregate = true;
    partial void OnAggregateChanged(bool value) { _preferences?.Set("search.aggregate", value); RebuildResultRows(); }
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
    /// <summary>侧栏「未返回结果」分组是否展开（默认折叠，每次新搜索重新折叠）。</summary>
    [ObservableProperty] private bool _failuresExpanded;
    [RelayCommand] private void ToggleFailures() => FailuresExpanded = !FailuresExpanded;
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

    public SearchViewModel(AppServices services, MainViewModel main) : this(main, services.Prefs)
    {
        _streamSearch=services.Catalog.SearchStreamAsync;
        _targetCount=()=>services.Catalog.SearchTargets.Count;
    }
    /// <summary>注入流式聚合搜索与源总数（测试用）。</summary>
    internal void SetSearchStream(Func<string,CancellationToken,IAsyncEnumerable<SourceSearchBatch>> search,int targets){_streamSearch=search;_targetCount=()=>targets;}

    internal SearchViewModel(MainViewModel main, IPreferences? preferences = null)
    {
        _main = main;
        _preferences = preferences;
        _aggregate = preferences?.GetBool("search.aggregate", true) ?? true;
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
            FailuresExpanded = false;
            SiteResults.Clear();
            AllResults.Clear();
            AggregatedResults.Clear();
            _itemResults.Clear(); _cardGroups.Clear(); _results.Clear();
            RebuildResultRows();
        });
        if (scope is null) return;
        var ct = scope.Token;
        try
        {
            if (_streamSearch is not { } stream)
            {
                await _main.RunOnUiAsync(() => { if (generation == _generation) { Hint = "没有可搜索的采集源，请到设置中添加订阅"; Status = "没有采集源"; } });
                return;
            }
            var failures = new List<(ApiSite Site, string Error)>();
            var total = _targetCount();
            if (total == 0)
            {
                await _main.RunOnUiAsync(() => { if (generation == _generation) { Hint = "没有可搜索的采集源，请到设置中添加订阅或启用采集源"; Status = "没有采集源"; } });
                return;
            }
            var done = 0;
            await foreach (var batch in stream(query, ct))
            {
                await _main.RunOnUiAsync(() =>
                {
                    if (generation != _generation || ct.IsCancellationRequested) return;
                    done++;
                    if (batch.Error is { } error) { failures.Add((batch.Site, error)); Failures.Add(new SearchFailure(batch.Site.Name, error)); }
                    else if (AddSiteResults(batch.Site, batch.Results)) SyncResultRows();
                    var all = Math.Max(done, total);
                    Summary = $"「{query}」已搜索 {done}/{all} 个源，{SiteResults.Count} 个源找到 {AllResults.Count} 条结果…";
                    Status = $"已搜索 {done}/{all} 个源 · {AggregatedResults.Count} 部影片";
                    Progress = (double)done / all;
                });
            }
            ct.ThrowIfCancellationRequested();
            await _main.RunOnUiAsync(() =>
            {
                if (generation != _generation || ct.IsCancellationRequested) return;
                FinishSearch(query, failures);
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
    /// 把一个源的结果并入：逐条卡片（侧栏按源筛选用）+ 重算聚合卡片。返回是否有新增。
    /// </summary>
    private bool AddSiteResults(ApiSite site, IReadOnlyList<VodResult> results)
    {
        var room = ResultLimit - AllResults.Count;
        if (room <= 0 || results.Count == 0) return false;
        var accepted = results.Take(room).ToArray();
        var group = SiteResults.FirstOrDefault(result => result.SourceKey == site.Key);
        if (group is null) SiteResults.Add(group = new SearchSiteResult(site.Name, site.Key));
        foreach (var result in accepted)
        {
            var item = SearchAggregation.ToItem(result);
            _itemResults[item] = result;
            _results.Add(result);
            AllResults.Add(item);
            group.Items.Add(item);
        }
        // 重算聚合卡片，但内容没变的卡片沿用原实例：网格只为新卡片创建控件，已有卡片原地不动（不闪、不重载海报）。
        var previous = AggregatedResults.ToArray();
        _cardGroups.Clear();
        var index = 0;
        foreach (var cardGroup in SearchAggregation.Group(_results))
        {
            var card = SearchAggregation.ToCard(cardGroup);
            if (index < previous.Length && LooksSame(previous[index], card)) card = previous[index];
            _cardGroups[card] = cardGroup;
            if (index < AggregatedResults.Count) { if (!ReferenceEquals(AggregatedResults[index], card)) AggregatedResults[index] = card; }
            else AggregatedResults.Add(card);
            index++;
        }
        while (AggregatedResults.Count > index) AggregatedResults.RemoveAt(AggregatedResults.Count - 1);
        return true;
    }

    /// <summary>
    /// 卡片上看得见的字段都没变就沿用原实例：新源只让「N 个源」/豆瓣 id 变化时，不必替换卡片
    /// （替换会让网格销毁重建该卡片、海报重载、入场动画重播，流式搜索时整片闪烁）。
    /// 打开详情走 <see cref="_cardGroups"/> 里的最新分组，不依赖卡片上的源数量。
    /// </summary>
    private static bool LooksSame(MediaItem shown, MediaItem next) =>
        shown.Equals(next) || (shown.Title == next.Title && shown.Poster == next.Poster && shown.Year == next.Year
            && shown.Remarks == next.Remarks && shown.Rating == next.Rating && shown.Area == next.Area && shown.TypeName == next.TypeName);

    private void FinishSearch(string query, IReadOnlyList<(ApiSite Site, string Error)> failures)
    {
        Progress = 1;
        Status = AllResults.Count == 0 ? "没有找到结果" : $"{SiteResults.Count} 个源 · {AggregatedResults.Count} 部影片 · {AllResults.Count} 条结果";
        Summary = $"「{query}」在 {SiteResults.Count} 个源找到 {AggregatedResults.Count} 部影片（{AllResults.Count} 条结果）";
        if (failures.Count > 0) Summary += $"；{failures.Count} 个源失败";
        Hint = AllResults.Count == 0 ? "没有找到匹配的结果，换个关键词试试" : "";
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
        if (_cardGroups.TryGetValue(item, out var group)) { ShowingDetail = true; Detail.OpenGroup(group); return; }
        if (!_itemResults.TryGetValue(item, out var result)) return;
        // 逐条视图点了具体某个源：同组其他源一起作为播放源，但先展示用户点的这个。
        var key = SearchAggregation.GroupKey(result);
        var siblings = _results.Where(other => SearchAggregation.GroupKey(other) == key).ToArray();
        ShowingDetail = true;
        Detail.OpenGroup(siblings, result);
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

    private List<MediaItem> VisibleItems() => SelectedSite is { } site ? site.Items.ToList() : Aggregate ? AggregatedResults.ToList() : AllResults.ToList();

    private void RebuildResultRows()
    {
        _entering.Clear();
        var items = VisibleItems();
        ResultRows.Clear();
        for (var i = 0; i < items.Count; i += _columns)
            ResultRows.Add(new ResultRow(items.Skip(i).Take(_columns)));
        NotifyResultState();
    }

    /// <summary>
    /// 流式到达的新结果：按位置增量同步行，已有卡片保持原控件；首次出现的卡片记入入场队列，
    /// 视图在卡片挂上时按顺序错开播放淡入上移（<see cref="TakeEntrance"/>）。
    /// </summary>
    private void SyncResultRows()
    {
        // 流式结果只会追加（分组按首次出现顺序），所以只有排在已显示数量之后的才是新卡片；
        // 已有位置上换了实例（例如补上了海报）只是原地更新，不再重播入场动画。
        var shownCount = 0;
        foreach (var row in ResultRows) shownCount += row.Items.Count;
        var items = VisibleItems();
        _entering.Clear();
        for (var i = shownCount; i < items.Count; i++) _entering.TryAdd(items[i], i - shownCount);
        var rows = (items.Count + _columns - 1) / _columns;
        for (var r = 0; r < rows; r++)
        {
            var chunk = items.Skip(r * _columns).Take(_columns).ToArray();
            if (r >= ResultRows.Count) { ResultRows.Add(new ResultRow(chunk)); continue; }
            var target = ResultRows[r].Items;
            for (var c = 0; c < chunk.Length; c++)
            {
                if (c >= target.Count) target.Add(chunk[c]);
                else if (!ReferenceEquals(target[c], chunk[c])) target[c] = chunk[c];
            }
            while (target.Count > chunk.Length) target.RemoveAt(target.Count - 1);
        }
        while (ResultRows.Count > rows) ResultRows.RemoveAt(ResultRows.Count - 1);
        NotifyResultState();
    }

    /// <summary>本批新出现的卡片 → 入场顺序（错开动画用）；只在流式追加时填充，切站点/改列数不播动画。</summary>
    private readonly Dictionary<MediaItem, int> _entering = new(ReferenceEqualityComparer.Instance);

    /// <summary>卡片控件挂上时调用：本批新卡片返回 true 与错开序号（取出即失效，滚动回收不会重播）。</summary>
    public bool TakeEntrance(MediaItem item, out int order) => _entering.Remove(item, out order);

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
    /// <summary>可变集合：流式追加时原地补卡，不重建整行。</summary>
    public ObservableCollection<MediaItem> Items { get; }
    public ResultRow(IEnumerable<MediaItem> items) => Items = new ObservableCollection<MediaItem>(items);
}
