using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VodBox.Core;
using VodBox.Desktop.Services;

namespace VodBox.Desktop.ViewModels;

/// <summary>点播：分类 Tab + 海报网格 + 分页。</summary>
public sealed partial class VodViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly MainViewModel _main;
    private readonly Func<IContentSource?> _getDefault;
    private IContentSource? _source;
    private CancellationTokenSource? _request;
    private long _generation;
    private bool _settingCategory;
    private string? _filterCategory;
    public ObservableCollection<FilterGroup> DynamicFilters{get;}=[];
    private readonly Dictionary<string,string> _dynamicValues=new();
    public void SelectFilter(string key,string value)
    {
        var group=DynamicFilters.FirstOrDefault(group=>group.Key==key);
        if(group is null||!group.Values.Any(option=>option.Value==value))return;
        _dynamicValues[key]=value;Page=1;_=ReloadAsync();
    }

    /// <summary>聚合搜索入口（默认走注册表的流式聚合，测试可注入）；null = 当前无可用入口。</summary>
    private Func<string, CancellationToken, IAsyncEnumerable<VodBox.Infrastructure.AggregateSearchBatch>>? _searchStream;
    /// <summary>搜索结果条目 → 来源站点键；网格里每条卡片都据此保留来源身份（含详情/播放）。</summary>
    private readonly Dictionary<MediaItem, string> _resultSources = new(ReferenceEqualityComparer.Instance);
    private IReadOnlyList<MediaItem> _browseItems = [];
    private Category? _browseCategory;
    private int _browsePage = 1, _browsePageCount = 1;
    private string _browseSourceKey = "";
    private string _browseHint = "";

    private double _gridWidth=1100;
    private int _columns=6;
    public double PosterWidth=>_services.Prefs.GetInt("ui.poster-density",1) switch {0=>190,2=>130,_=>158};
    public double PosterHeight=>PosterWidth*1.42;
    /// <summary>当前海报列数；骨架屏按同一列数铺占位卡，避免加载完成时跳列。</summary>
    public int Columns=>_columns;
    public void SetGridWidth(double width)
    {
        if(width<=0)return; // 隐藏时布局宽度为 0，忽略以免把列数压成 1
        _gridWidth=width;
        var columns=Math.Max(1,(int)Math.Floor(Math.Max(0,width-16)/(PosterWidth+18)));
        if(columns==_columns)return;_columns=columns;OnPropertyChanged(nameof(Columns));RebuildRows();
    }
    public void RefreshPosterDensity()
    {
        OnPropertyChanged(nameof(PosterWidth));OnPropertyChanged(nameof(PosterHeight));SetGridWidth(_gridWidth);RebuildRows();
    }
    [ObservableProperty] private string _filterYear = "";
    [ObservableProperty] private string _filterArea = "";
    [ObservableProperty] private string _filterLanguage = "";
    [ObservableProperty] private string _filterClass = "";
    public bool SupportsBasicFilters => _source is VodBox.Infrastructure.MacCmsSource;
    [RelayCommand] private async Task ApplyFilters() { Page = 1; await ReloadAsync(); }
    [RelayCommand] private async Task ClearFilters()
    {
        FilterYear="";FilterArea="";FilterLanguage="";FilterClass="";
        _dynamicValues.Clear();foreach(var group in DynamicFilters)_dynamicValues[group.Key]=group.Init;
        Page=1;await ReloadAsync();
    }
    [ObservableProperty] private string _sourceName = "未配置";
    public IReadOnlyList<SourceInfo> AvailableSources => _services.Registry.SelectableSources;
    public string SourceCountLabel => $"共 {AvailableSources.Count} 个站点";
    public string SourceSelectionHint => AvailableSources.Count > 0 ? ""
        // 只有「能运行但被隐藏」时才提示显示状态；列出了但没有一个能运行，是桌面运行时边界。
        : _services.Registry.Sources.Any(source => _services.Registry.Get(source.Key) is not null) ? "没有可选站点，请到设置中检查站点显示状态"
        : _services.Registry.ImportWarnings.Count > 0 ? "订阅中的站点暂无桌面运行时支持，请到设置中查看导入警告"
        : "尚未配置内容源，请到设置中添加 TVBox 配置地址";

    public ObservableCollection<Category> Categories { get; } = [];
    public ObservableCollection<MediaItem> Items { get; } = [];

    /// <summary>海报行集合（每行 = 当前列数张，虚拟化网格数据源；无限滚动只追加末行）。</summary>
    public ObservableCollection<MediaRow> Rows { get; } = [];

    [ObservableProperty] private Category? _selectedCategory;
    [ObservableProperty] private int _page = 1;
    [ObservableProperty] private int _pageCount = 1;
    [ObservableProperty] private bool _loading;
    /// <summary>无限滚动取下一页中：保留已加载内容，仅底部页脚显示加载中（不清空网格、不显示骨架）。</summary>
    [ObservableProperty] private bool _paging;
    /// <summary>网格空态/错误态文案（非空时覆盖网格位置，不留白屏）。</summary>
    [ObservableProperty] private string _itemsHint = "";

    /// <summary>无限滚动页脚状态：明确区分「加载中 / 已到底 / 失败可重试」，不留静默空白。</summary>
    [ObservableProperty] private VodFooterState _footerState = VodFooterState.Hidden;
    /// <summary>页脚补充文案（失败原因等）。</summary>
    [ObservableProperty] private string _footerMessage = "";

    public string FooterText => FooterState switch
    {
        VodFooterState.Loading => "正在加载更多…",
        VodFooterState.End => "没有更多了",
        VodFooterState.Error => FooterMessage.Length > 0 ? FooterMessage : "加载失败",
        _ => "",
    };
    public bool FooterVisible => FooterState != VodFooterState.Hidden;
    public bool FooterLoading => FooterState == VodFooterState.Loading;
    public bool FooterCanRetry => FooterState == VodFooterState.Error;
    /// <summary>当前分类是否还有下一页可加载；到达末页/空页后为 false（不再发请求，避免无穷请求）。</summary>
    public bool HasMore => !SearchMode && !_reachedEnd && _source is not null
        && SelectedCategory is not null && Page < PageCount;

    partial void OnFooterStateChanged(VodFooterState value)
    {
        OnPropertyChanged(nameof(FooterText));
        OnPropertyChanged(nameof(FooterVisible));
        OnPropertyChanged(nameof(FooterLoading));
        OnPropertyChanged(nameof(FooterCanRetry));
    }
    partial void OnFooterMessageChanged(string value) => OnPropertyChanged(nameof(FooterText));
    partial void OnPageChanged(int value) => OnPropertyChanged(nameof(HasMore));
    partial void OnPageCountChanged(int value) => OnPropertyChanged(nameof(HasMore));
    partial void OnSearchModeChanged(bool value) => OnPropertyChanged(nameof(HasMore));

    /// <summary>点播页关键词：非空并执行后聚合搜索全部启用搜索且已注册的站点；清空即恢复当前站点浏览。</summary>
    [ObservableProperty] private string _keyword = "";
    /// <summary>网格当前展示的是聚合搜索结果（此时站点分类/筛选/分页让位给搜索结果）。</summary>
    [ObservableProperty] private bool _searchMode;
    /// <summary>聚合搜索进行中（各站点结果分批到达）。</summary>
    [ObservableProperty] private bool _searching;
    /// <summary>搜索汇总：命中数 + 失败站点数（部分失败必须可见）。</summary>
    [ObservableProperty] private string _searchSummary = "";

    /// <summary>已加载内容所属的站点键；null = 从未加载（导航复用据此判断是否重发请求）。</summary>
    private string? _loadedSourceKey;
    private string? _selectedSourceKey;
    /// <summary>网格当前内容对应的页码。</summary>
    private int _loadedPage = 1;
    /// <summary>已加载条目的 id 集合：无限滚动 append 时按 id 去重，同一条目只保留一次。</summary>
    private readonly HashSet<string> _loadedIds = new(StringComparer.Ordinal);
    /// <summary>当前分类已到达末页或返回空页：无限滚动据此停止，不再发请求。</summary>
    private bool _reachedEnd;

    public string LoadedSourceKey => _loadedSourceKey ?? "";

    /// <summary>配置换源后调用：丢弃已加载内容，下次进入点播页重新加载。</summary>
    public void Invalidate()
    {
        CancelPending();
        // 先退出搜索态再清关键词：避免 OnKeywordChanged 再触发一次「恢复浏览」的异步加载。
        SearchMode = false; SearchSummary = ""; _resultSources.Clear(); ResetBrowseSnapshot();
        Keyword = "";
        Categories.Clear(); ClearItems();
        _source = null; _selectedSourceKey = null;
        _loadedSourceKey = null; _loadedPage = 1; SourceName = "未配置"; ItemsHint = "";
        FooterState = VodFooterState.Hidden; FooterMessage = "";
    }

    /// <summary>导航进入点播页：同一站点已加载过则复用，否则（重新）加载。</summary>
    public void EnsureLoaded()
    {
        var source = GetSelectedSource();
        if (_loadedSourceKey is not null && _loadedSourceKey == (source?.Key ?? "")) return;
        _ = LoadAsync();
    }

    partial void OnSelectedCategoryChanged(Category? value)
    {
        Page = 1;
        OnPropertyChanged(nameof(HasMore));
        if (!_settingCategory) _ = ReloadAsync();
    }

    public VodViewModel(AppServices services, MainViewModel main) : this(services, main, services.Registry.Default)
    {
        // 点播搜索复用注册表的聚合入口：全部「启用搜索 + 实际注册」的站点、有限并发、逐源超时与部分失败保留。
        _searchStream = services.Registry.SearchStreamAsync;
    }

    /// <summary>测试/宿主注入聚合搜索入口（默认是注册表的流式聚合）。</summary>
    internal void SetSearchStream(Func<string, CancellationToken, IAsyncEnumerable<VodBox.Infrastructure.AggregateSearchBatch>> search) => _searchStream = search;

    internal VodViewModel(AppServices services, MainViewModel main, Func<IContentSource?> getDefault)
    {
        _services = services;
        _main = main;
        _getDefault = getDefault;
        services.Registry.SourcesChanged += () => _main.RunOnUi(() =>
        {
            OnPropertyChanged(nameof(AvailableSources));
            OnPropertyChanged(nameof(SourceCountLabel));
            OnPropertyChanged(nameof(SourceSelectionHint));
        });
    }

    public void CancelPending()
    {
        ++_generation;_request?.Cancel();_request=null;Loading=false;Paging=false;Searching=false;
        FooterState=VodFooterState.Hidden;FooterMessage="";
    }

    private IContentSource? GetSelectedSource() => _selectedSourceKey is { } key
        && AvailableSources.Any(source => source.Key == key)
        ? _services.Registry.Get(key) : _getDefault();

    public Task LoadAsync() => LoadSourceAsync(GetSelectedSource());

    public Task SwitchSourceAsync(string key)
    {
        if (!AvailableSources.Any(source => source.Key == key) || _services.Registry.Get(key) is not { } source)
            return Task.CompletedTask;
        _selectedSourceKey = key;
        return LoadSourceAsync(source);
    }

    private async Task LoadSourceAsync(IContentSource? source)
    {
        _request?.Cancel();
        var generation = ++_generation;
        using var scope = new CancellationTokenSource();
        _request = scope;
        // 换源/重新加载属于浏览行为：先退出搜索态并丢掉结果与快照，避免搜索汇总盖在浏览网格上。
        SearchMode = false; Searching = false; SearchSummary = ""; _resultSources.Clear(); ResetBrowseSnapshot();
        if (Keyword.Length > 0) Keyword = "";
        _source = source;
        SourceName = source?.Name ?? "未配置";
        FilterYear = ""; FilterArea = ""; FilterLanguage = ""; FilterClass = "";
        _filterCategory=null;DynamicFilters.Clear();_dynamicValues.Clear();
        OnPropertyChanged(nameof(SupportsBasicFilters));
        OnPropertyChanged(nameof(SourceCountLabel));
        Categories.Clear(); ClearItems();
        ItemsHint="";Paging=false;_loadedSourceKey=source?.Key??"";
        FooterState=VodFooterState.Hidden;FooterMessage="";
        // 换源是一次内容切换：分页与总页数必须回到第一页，不能沿用上一个站点的进度。
        Page=1;PageCount=1;_loadedPage=1;
        _settingCategory = true;
        SelectedCategory = null;
        _settingCategory = false;
        if (source is null)
        {
            Loading = false; _request=null;
            ItemsHint = SourceSelectionHint;
            return;
        }
        Loading = true;
        try
        {
            var categories = await source.GetCategoriesAsync(scope.Token);
            scope.Token.ThrowIfCancellationRequested();
            await _main.RunOnUiAsync(() =>
            {
                if (generation != _generation) return;
                foreach (var category in categories) Categories.Add(category);
                _settingCategory = true;
                SelectedCategory = Categories.FirstOrDefault();
                _settingCategory = false;
            });
            if (generation == _generation) await ReloadAsync();
        }
        catch (OperationCanceledException) when (scope.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (generation == _generation)
            {
                _main.StatusMessage = $"分类加载失败：{error.Message}";
                ItemsHint = _main.StatusMessage;
                _loadedSourceKey = null;
            }
        }
        finally { if (generation == _generation) { Loading = false; _request = null; } }
    }

    internal Task ReloadAsync() => ReloadAsyncCore();

    /// <summary>
    /// 重新拉取当前分类的第一页（切分类/筛选/换源/退出搜索）。
    /// 先清空网格再显示骨架，避免把上一个分类的内容留在屏幕上；整批替换而非 append。
    /// </summary>
    private async Task ReloadAsyncCore()
    {
        _request?.Cancel();
        var generation = ++_generation;
        using var scope = new CancellationTokenSource();
        _request = scope;
        var source = _source;
        var category = SelectedCategory;
        if (source is null || category is null)
        {
            Loading = false; Paging = false; _request = null;
            ItemsHint = source is null ? SourceSelectionHint : source is IInformationalContentSource notice ? notice.Notice : "该站点暂无分类";
            return;
        }
        var requestedPage = Page;
        ItemsHint = "";
        Paging = false; FooterState = VodFooterState.Hidden; FooterMessage = "";
        ClearItems(); Loading = true;
        try
        {
            if(_filterCategory!=category.Id)
            {
                var groups=await source.GetFiltersAsync(category.Id,scope.Token);
                scope.Token.ThrowIfCancellationRequested();
                await _main.RunOnUiAsync(()=>
                {
                    if(generation!=_generation)return;
                    DynamicFilters.Clear();_dynamicValues.Clear();
                    foreach(var group in groups){DynamicFilters.Add(group);_dynamicValues[group.Key]=group.Init;}
                    _filterCategory=category.Id;
                });
            }
            var result = await source.GetItemsAsync(category.Id, requestedPage, BuildFilters(), scope.Token);
            scope.Token.ThrowIfCancellationRequested();
            await _main.RunOnUiAsync(() =>
            {
                if (generation != _generation) return;
                ReplaceItems(result.Items);
                PageCount = Math.Max(1, result.PageCount);
                _loadedPage = requestedPage;
                // 空页或已到末页：明确给出终点状态，避免继续请求。
                _reachedEnd = result.Items.Count == 0 || Page >= PageCount;
                ItemsHint = Items.Count == 0 ? "该分类暂无内容，可切换分类或调整筛选" : "";
                FooterState = Items.Count > 0 && _reachedEnd ? VodFooterState.End : VodFooterState.Hidden;
            });
        }
        catch (OperationCanceledException) when (scope.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (generation != _generation) return;
            _main.StatusMessage = $"加载失败：{error.Message}";
            ItemsHint = $"加载失败：{error.Message}";
            FooterState = VodFooterState.Hidden;
        }
        finally { if (generation == _generation) { Loading = false; Paging = false; _request = null; } }
    }

    /// <summary>滚动到底触发的取下一页（append 到已加载内容之后，不清空、不回到顶部）。</summary>
    [RelayCommand]
    public Task LoadMore() => LoadMoreAsync();

    /// <summary>兼容旧入口：程序化取下一页，与滚动触发共用同一条 append 路径。</summary>
    public Task NextPage() => LoadMoreAsync();

    /// <summary>页脚「重试」：失败后再取同一页，不改动已加载内容。</summary>
    [RelayCommand]
    private Task RetryFooter() => LoadMoreAsync();

    /// <summary>
    /// 无限滚动取下一页：
    /// <list type="bullet">
    /// <item>身份 = (站点, 当前分类, 筛选, 代次, 页码)；同一时刻只允许一个在途请求（并发去重）。</item>
    /// <item>append 而不清空 → 保留滚动位置与行虚拟化；条目按 id 去重。</item>
    /// <item>空页 / 到达末页 → 停止并显示终点；不再发请求（避免无穷请求）。</item>
    /// <item>失败只把页脚置为可重试，不清空已加载内容，也不推进页码。</item>
    /// <item>换源/切分类/筛选/搜索等新代次会取消在途请求，迟到结果无法回填（取消陈旧写入）。</item>
    /// </list>
    /// </summary>
    public async Task LoadMoreAsync()
    {
        if (Loading || Paging) return;
        var source = _source;
        var category = SelectedCategory;
        if (SearchMode || source is null || category is null) return;
        if (_reachedEnd || Page >= PageCount)
        {
            if (Items.Count > 0) FooterState = VodFooterState.End;
            return;
        }
        _request?.Cancel();
        var generation = ++_generation;
        using var scope = new CancellationTokenSource();
        _request = scope;
        var requestedPage = Page + 1;
        Paging = true; FooterState = VodFooterState.Loading; FooterMessage = "";
        try
        {
            var result = await source.GetItemsAsync(category.Id, requestedPage, BuildFilters(), scope.Token);
            scope.Token.ThrowIfCancellationRequested();
            await _main.RunOnUiAsync(() =>
            {
                if (generation != _generation) return;
                AppendItems(result.Items);
                Page = requestedPage;
                PageCount = Math.Max(1, result.PageCount);
                _reachedEnd = result.Items.Count == 0 || Page >= PageCount;
                ItemsHint = Items.Count == 0 ? "该分类暂无内容，可切换分类或调整筛选" : "";
                FooterState = _reachedEnd
                    ? (Items.Count == 0 ? VodFooterState.Hidden : VodFooterState.End)
                    : VodFooterState.Hidden;
            });
        }
        catch (OperationCanceledException) when (scope.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (generation != _generation) return;
            FooterState = VodFooterState.Error;
            FooterMessage = $"加载更多失败：{error.Message}";
            _main.StatusMessage = FooterMessage;
        }
        finally { if (generation == _generation) { Paging = false; _request = null; } }
    }

    /// <summary>当前分类的筛选参数字典（静态四项 + 动态筛选值）。</summary>
    private Dictionary<string, string> BuildFilters()
    {
        var filters = new Dictionary<string, string>
        {
            ["year"] = FilterYear.Trim(), ["area"] = FilterArea.Trim(),
            ["lang"] = FilterLanguage.Trim(), ["class"] = FilterClass.Trim(),
        };
        foreach(var (key,value) in _dynamicValues)filters[key]=value;
        return filters;
    }

    /// <summary>
    /// 打开卡片：搜索结果的来源是聚合里那个站点（不是当前浏览站点），详情与后续播放都据此保留源身份。
    /// </summary>
    [RelayCommand]
    private void OpenItem(MediaItem item)
    {
        var key = _resultSources.TryGetValue(item, out var resultKey) ? resultKey : _source?.Key;
        if (key is not null) _main.Detail.Open(key, item);
    }

    partial void OnKeywordChanged(string value)
    {
        // 清空关键词 = 退出搜索：取消在途聚合、把网格还给当前站点的浏览内容。
        if (value.Trim().Length == 0 && SearchMode) _ = ExitSearchAsync();
    }

    /// <summary>执行搜索：关键词为空时等同于「回到当前站点浏览」。</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    public async Task RunSearchAsync()
    {
        var query = Keyword.Trim();
        if (query.Length == 0) { await ExitSearchAsync(); return; }
        await SearchAsync(query);
    }

    /// <summary>
    /// 聚合搜索全部启用搜索且已注册的站点：结果分批到达并就地追加，慢站/失败站不阻塞已到的成功结果。
    /// 代次 + 取消令牌保证旧请求（换词、清空、换源、翻页）不能回填新状态。
    /// </summary>
    private async Task SearchAsync(string query)
    {
        _request?.Cancel();
        var generation = ++_generation;
        using var scope = new CancellationTokenSource();
        _request = scope;
        var stream = _searchStream;
        var succeeded = 0;
        var failed = 0;
        var sites = 0;
        await _main.RunOnUiAsync(() =>
        {
            if (generation != _generation) return;
            if (!SearchMode) SnapshotBrowse(); // 首次进入搜索才保留当前浏览，重复搜索不覆盖快照
            SearchMode = true; Searching = true; Paging = false; Loading = true;
            _resultSources.Clear(); ClearItems();
            ItemsHint = "";
            SearchSummary = $"「{query}」搜索中…";
        });
        if (stream is null)
        {
            await _main.RunOnUiAsync(() =>
            {
                if (generation != _generation) return;
                Searching = false; Loading = false;
                SearchSummary = "";
                ItemsHint = "没有可搜索的站点，请到设置中开启站点搜索";
            });
            return;
        }
        try
        {
            await foreach (var batch in stream(query, scope.Token).ConfigureAwait(false))
            {
                if (generation != _generation || scope.IsCancellationRequested) return;
                await _main.RunOnUiAsync(() =>
                {
                    if (generation != _generation) return;
                    sites++;
                    if (batch.Error is { } error) failed++;
                    // 每张卡片都带上来源站点名：结果是跨站点的，卡片必须能看出它来自哪个站点。
                    else foreach (var item in batch.Items)
                    {
                        var card = item with { SourceName = batch.Site.Name };
                        _resultSources[card] = batch.Site.Key;
                        Items.Add(card);
                        succeeded++;
                    }
                    // 先到先得：有成功条目就撤掉骨架，后续批次继续往同一网格里追加。
                    if (Items.Count > 0) Loading = false;
                    SearchSummary = SearchSummaryFor(query, sites, succeeded, failed);
                    RebuildRows();
                });
            }
        }
        catch (OperationCanceledException) when (scope.IsCancellationRequested) { return; }
        catch (Exception error)
        {
            await _main.RunOnUiAsync(() =>
            {
                if (generation != _generation) return;
                _main.StatusMessage = $"搜索失败：{error.Message}";
                ItemsHint = $"搜索失败：{error.Message}";
            });
        }
        finally
        {
            await _main.RunOnUiAsync(() =>
            {
                if (generation != _generation) return;
                Loading = false; Searching = false; _request = null;
                if (Items.Count == 0 && failed == 0)
                    ItemsHint = $"没有找到「{query}」，换个关键词试试";
                else if (Items.Count == 0)
                    ItemsHint = $"全部 {failed} 个站点搜索失败，请稍后重试";
                SearchSummary = SearchSummaryFor(query, sites, succeeded, failed);
            });
        }
    }

    private static string SearchSummaryFor(string query, int sites, int hits, int failures) =>
        failures > 0
            ? $"「{query}」在 {sites} 个站点找到 {hits} 条结果；{failures} 个站点失败"
            : $"「{query}」在 {sites} 个站点找到 {hits} 条结果";

    /// <summary>进入搜索前保留当前站点的浏览状态（分类/页码/条目），退出时原样还回去。</summary>
    private void SnapshotBrowse()
    {
        _browseItems = Items.ToArray();
        _browseCategory = SelectedCategory;
        _browsePage = Page; _browsePageCount = PageCount;
        _browseSourceKey = _loadedSourceKey ?? "";
        _browseHint = ItemsHint;
    }

    private void ResetBrowseSnapshot()
    {
        _browseItems = []; _browseCategory = null;
        _browsePage = 1; _browsePageCount = 1;
        _browseSourceKey = ""; _browseHint = "";
    }

    /// <summary>清空关键词：取消在途搜索，恢复当前站点的浏览网格（没有快照时按当前站点重新加载）。</summary>
    private async Task ExitSearchAsync()
    {
        _request?.Cancel();
        var generation = ++_generation;
        _request = null;
        // 有快照（浏览确实加载过）就原样还回去，不重复请求；否则按当前站点重新加载。
        var restore = _browseSourceKey.Length > 0 && (_browseItems.Count > 0 || _browseHint.Length > 0);
        await _main.RunOnUiAsync(() =>
        {
            if (generation != _generation) return;
            SearchMode = false; Searching = false; Loading = false; Paging = false;
            SearchSummary = ""; _resultSources.Clear();
            if (restore)
            {
                _settingCategory = true;
                SelectedCategory = _browseCategory;
                _settingCategory = false;
                ReplaceItems(_browseItems);
                Page = _browsePage; PageCount = _browsePageCount; _loadedPage = _browsePage;
                _loadedSourceKey = _browseSourceKey;
                ItemsHint = _browseHint;
                FooterState = VodFooterState.Hidden; FooterMessage = "";
            }
        });
        if (!restore && generation == _generation) await LoadAsync(); // 还没浏览过（未加载/无站点）：按当前站点重新加载
    }

    /// <summary>清空网格内容（条目 + 行 + 去重集合 + 终点标记），保持虚拟化数据源一致。</summary>
    private void ClearItems()
    {
        Items.Clear(); Rows.Clear(); _loadedIds.Clear(); _reachedEnd = false;
    }

    /// <summary>整批替换网格内容（首屏/切分类/筛选/换源/退出搜索恢复）：按当前列数重建行虚拟化数据源。</summary>
    private void ReplaceItems(IEnumerable<MediaItem> items)
    {
        ClearItems();
        foreach (var item in items) { Items.Add(item); _loadedIds.Add(item.Id); }
        RebuildRows();
    }

    /// <summary>
    /// 追加下一页条目：按 id 去重（同一条目只保留一次），按当前列数补齐末行。
    /// 只改动集合末尾 → ListBox 保留滚动位置，虚拟化只实例化可见行，不会一次性创建全部卡片。
    /// </summary>
    private void AppendItems(IReadOnlyList<MediaItem> items)
    {
        foreach (var item in items)
        {
            if (!_loadedIds.Add(item.Id)) continue;
            Items.Add(item);
            if (Rows.Count == 0 || Rows[^1].Items.Count >= _columns) Rows.Add(new MediaRow());
            Rows[^1].Items.Add(item);
        }
    }

    /// <summary>海报按当前列数分块（虚拟化网格数据源）。</summary>
    private void RebuildRows()
    {
        Rows.Clear();
        for (var i = 0; i < Items.Count; i += _columns)
            Rows.Add(new MediaRow(Items.Skip(i).Take(_columns)));
    }
}

/// <summary>点播无限滚动页脚状态。</summary>
public enum VodFooterState { Hidden, Loading, End, Error }

/// <summary>
/// 海报网格行。Items 是可观察集合：无限滚动追加下一页时只改动末行，
/// 避免整表重建导致的滚动位置丢失与全量卡片重建。
/// </summary>
public sealed class MediaRow
{
    public ObservableCollection<MediaItem> Items { get; } = [];
    public MediaRow() { }
    public MediaRow(IEnumerable<MediaItem> items) { foreach (var item in items) Items.Add(item); }
}
