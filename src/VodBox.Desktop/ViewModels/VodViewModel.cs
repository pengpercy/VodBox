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
    public IReadOnlyList<SourceInfo> AvailableSources => _services.Registry.ChangeableSources;
    public string SourceCountLabel => $"共 {AvailableSources.Count} 个站点";

    public ObservableCollection<Category> Categories { get; } = [];
    public ObservableCollection<MediaItem> Items { get; } = [];

    /// <summary>海报行集合（每行 6 张，虚拟化网格数据源）。</summary>
    public ObservableCollection<MediaRow> Rows { get; } = [];

    [ObservableProperty] private Category? _selectedCategory;
    [ObservableProperty] private int _page = 1;
    [ObservableProperty] private int _pageCount = 1;
    [ObservableProperty] private bool _loading;
    /// <summary>翻页请求中：保留当前页内容，仅在底部显示加载条（不清空网格、不显示骨架）。</summary>
    [ObservableProperty] private bool _paging;
    /// <summary>网格空态/错误态文案（非空时覆盖网格位置，不留白屏）。</summary>
    [ObservableProperty] private string _itemsHint = "";

    /// <summary>已加载内容所属的站点键；null = 从未加载（导航复用据此判断是否重发请求）。</summary>
    private string? _loadedSourceKey;
    /// <summary>网格当前内容对应的页码；翻页失败时回滚计数（Page 已被翻页命令先行自增）。</summary>
    private int _loadedPage = 1;

    public string LoadedSourceKey => _loadedSourceKey ?? "";

    /// <summary>配置换源后调用：丢弃已加载内容，下次进入点播页重新加载。</summary>
    public void Invalidate()
    {
        CancelPending();
        Categories.Clear(); Items.Clear(); Rows.Clear();
        _loadedSourceKey = null; _loadedPage = 1; ItemsHint = "";
    }

    /// <summary>导航进入点播页：同一站点已加载过则复用，否则（重新）加载。</summary>
    public void EnsureLoaded()
    {
        var source = _getDefault();
        if (_loadedSourceKey is not null && _loadedSourceKey == (source?.Key ?? "")) return;
        _ = LoadAsync();
    }

    partial void OnSelectedCategoryChanged(Category? value)
    {
        Page = 1;
        if (!_settingCategory) _ = ReloadAsync();
    }

    public VodViewModel(AppServices services, MainViewModel main) : this(services, main, services.Registry.Default) { }

    internal VodViewModel(AppServices services, MainViewModel main, Func<IContentSource?> getDefault)
    {
        _services = services;
        _main = main;
        _getDefault = getDefault;
    }

    public void CancelPending()
    {
        ++_generation;_request?.Cancel();_request=null;Loading=false;Paging=false;
    }

    public Task LoadAsync() => LoadSourceAsync(_getDefault());

    public Task SwitchSourceAsync(string key)
    {
        if(!_services.Registry.ChangeableSources.Any(source=>source.Key==key))return Task.CompletedTask;
        return LoadSourceAsync(_services.Registry.Get(key));
    }

    private async Task LoadSourceAsync(IContentSource? source)
    {
        _request?.Cancel();
        var generation = ++_generation;
        using var scope = new CancellationTokenSource();
        _request = scope;
        _source = source;
        SourceName = source?.Name ?? "未配置";
        FilterYear = ""; FilterArea = ""; FilterLanguage = ""; FilterClass = "";
        _filterCategory=null;DynamicFilters.Clear();_dynamicValues.Clear();
        OnPropertyChanged(nameof(SupportsBasicFilters));
        OnPropertyChanged(nameof(SourceCountLabel));
        Categories.Clear(); Items.Clear(); Rows.Clear();
        ItemsHint="";Paging=false;_loadedSourceKey=source?.Key??"";
        // 换源是一次内容切换：分页与总页数必须回到第一页，不能沿用上一个站点的进度。
        Page=1;PageCount=1;_loadedPage=1;
        _settingCategory = true;
        SelectedCategory = null;
        _settingCategory = false;
        if (source is null)
        {
            Loading = false; _request=null;
            ItemsHint = "尚未配置内容源，请到设置中添加 TVBox 配置地址";
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

    internal Task ReloadAsync() => ReloadAsync(keepContent: false);

    /// <summary>
    /// 重新拉取当前分类。
    /// <paramref name="keepContent"/> 为真（翻页）时保留已展示的一页，只在底部显示加载条；
    /// 为假（切分类/筛选/换源）时先清空网格再显示骨架，避免把上一个分类的内容留在屏幕上。
    /// </summary>
    private async Task ReloadAsync(bool keepContent)
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
            ItemsHint = source is null ? "尚未配置内容源，请到设置中添加 TVBox 配置地址" : "该站点暂无分类";
            return;
        }
        var requestedPage = Page;
        var previousPage = _loadedPage;
        ItemsHint = "";
        if (keepContent) { Loading = false; Paging = true; }
        else { Paging = false; Items.Clear(); RebuildRows(); ItemsHint = ""; Loading = true; }
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
            var filters = new Dictionary<string, string>
            {
                ["year"] = FilterYear.Trim(), ["area"] = FilterArea.Trim(),
                ["lang"] = FilterLanguage.Trim(), ["class"] = FilterClass.Trim(),
            };
            foreach(var (key,value) in _dynamicValues)filters[key]=value;
            var result = await source.GetItemsAsync(category.Id, requestedPage, filters, scope.Token);
            scope.Token.ThrowIfCancellationRequested();
            await _main.RunOnUiAsync(() =>
            {
                if (generation != _generation) return;
                Items.Clear();
                foreach (var item in result.Items) Items.Add(item);
                RebuildRows();
                PageCount = Math.Max(1, result.PageCount);
                _loadedPage = requestedPage;
                ItemsHint = Items.Count == 0 ? "该分类暂无内容，可切换分类或调整筛选" : "";
            });
        }
        catch (OperationCanceledException) when (scope.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (generation != _generation) return;
            // 翻页失败时网格里仍是旧页内容，把页码一起退回去，避免指针和内容对不上。
            if (keepContent && Page == requestedPage) Page = previousPage;
            _main.StatusMessage = $"加载失败：{error.Message}";
            if (!keepContent) ItemsHint = $"加载失败：{error.Message}";
        }
        finally { if (generation == _generation) { Loading = false; Paging = false; _request = null; } }
    }

    [RelayCommand]
    public async Task NextPage()
    {
        if (Loading || Paging) return;
        if (Page < PageCount) { Page++; await ReloadAsync(keepContent: true); }
    }

    [RelayCommand]
    public async Task PrevPage()
    {
        if (Loading || Paging) return;
        if (Page > 1) { Page--; await ReloadAsync(keepContent: true); }
    }

    [RelayCommand]
    private void OpenItem(MediaItem item)
    {
        if (_source is { } source) _main.Detail.Open(source.Key, item);
    }

    /// <summary>海报按 6 张/行分块（虚拟化网格数据源）。</summary>
    private void RebuildRows()
    {
        Rows.Clear();
        for (var i = 0; i < Items.Count; i += _columns)
            Rows.Add(new MediaRow(Items.Skip(i).Take(_columns).ToList()));
    }
}

/// <summary>海报网格行（6 张卡）。</summary>
public sealed class MediaRow
{
    public IReadOnlyList<MediaItem> Items { get; }
    public MediaRow(IReadOnlyList<MediaItem> items) => Items = items;
}
