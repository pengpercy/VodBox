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
    public void SetGridWidth(double width)
    {
        _gridWidth=width;
        var columns=Math.Max(1,(int)Math.Floor(Math.Max(0,width-16)/(PosterWidth+18)));
        if(columns==_columns)return;_columns=columns;RebuildRows();
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
        ++_generation;_request?.Cancel();_request=null;Loading=false;
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
        _settingCategory = true;
        SelectedCategory = null;
        _settingCategory = false;
        if (source is null) { Loading = false; _request=null; return; }
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
            if (generation == _generation) _main.StatusMessage = $"分类加载失败：{error.Message}";
        }
        finally { if (generation == _generation) { Loading = false; _request = null; } }
    }

    internal async Task ReloadAsync()
    {
        _request?.Cancel();
        var generation = ++_generation;
        using var scope = new CancellationTokenSource();
        _request = scope;
        var source = _source;
        var category = SelectedCategory;
        if (source is null || category is null) { Loading = false; _request=null; return; }
        Loading = true;
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
            var result = await source.GetItemsAsync(category.Id, Page, filters, scope.Token);
            scope.Token.ThrowIfCancellationRequested();
            await _main.RunOnUiAsync(() =>
            {
                if (generation != _generation) return;
                Items.Clear();
                foreach (var item in result.Items) Items.Add(item);
                RebuildRows();
                PageCount = Math.Max(1, result.PageCount);
            });
        }
        catch (OperationCanceledException) when (scope.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (generation == _generation) _main.StatusMessage = $"加载失败：{error.Message}";
        }
        finally { if (generation == _generation) { Loading = false; _request = null; } }
    }

    [RelayCommand]
    public async Task NextPage()
    {
        if (Page < PageCount) { Page++; await ReloadAsync(); }
    }

    [RelayCommand]
    public async Task PrevPage()
    {
        if (Page > 1) { Page--; await ReloadAsync(); }
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
