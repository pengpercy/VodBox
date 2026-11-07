using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VodBox.Core;
using VodBox.Infrastructure;

namespace VodBox.Desktop.ViewModels;

/// <summary>
/// 设置 › 源与订阅（LunaTV 管理后台的桌面版）：配置订阅、采集源维护（启用/禁用/排序/添加/删除/有效性检测）、
/// 豆瓣数据与图片代理、搜索与播放偏好、自定义分类、直播源。
/// </summary>
public sealed partial class SettingsViewModel
{
    public ObservableCollection<SourceRow> Sites { get; } = [];
    /// <summary>按 <see cref="SiteFilter"/> / <see cref="SiteStatusFilterIndex"/> 过滤后实际显示的源。</summary>
    public ObservableCollection<SourceRow> FilteredSites { get; } = [];

    // 过滤与批量选择
    [ObservableProperty] private string _siteFilter = "";
    [ObservableProperty] private int _siteStatusFilterIndex;
    public IReadOnlyList<string> SiteStatusFilterOptions { get; } = ["全部状态", "已启用", "已禁用", "有效", "无结果", "无效", "未检测"];
    // 删除二次确认弹窗（SettingsWindow 覆盖层）
    [ObservableProperty] private bool _deleteDialogOpen;
    [ObservableProperty] private string _deleteDialogMessage = "";
    [ObservableProperty] private string _deleteDialogNames = "";
    [ObservableProperty] private string _deleteDialogNote = "";
    private string[] _pendingDeleteKeys = [];
    partial void OnSiteFilterChanged(string value) => ApplySiteFilter();
    partial void OnSiteStatusFilterIndexChanged(int value) => ApplySiteFilter();
    public int SelectedSiteCount => Sites.Count(row => row.IsSelected);
    public bool HasSelectedSites => SelectedSiteCount > 0;
    public bool IsSiteFilterActive => SiteFilter.Trim().Length > 0 || SiteStatusFilterIndex != 0;
    public string SiteFilterSummary => IsSiteFilterActive ? $"匹配 {FilteredSites.Count} / {Sites.Count}" : $"共 {Sites.Count} 个";
    public string SelectionSummary => HasSelectedSites ? $"已选 {SelectedSiteCount} 个" : "未选择";
    public int DeletedSourceCount => _services.SourceConfig.DeletedSourceCount;
    public bool HasDeletedSources => DeletedSourceCount > 0;
    public string ValidateButtonLabel => HasSelectedSites ? $"检测已选 {SelectedSiteCount} 个" : IsSiteFilterActive ? $"检测匹配的 {FilteredSites.Count} 个" : "检测全部";

    /// <summary>可见行全选状态：全选 true、全不选 false、部分 null（三态复选框）。</summary>
    public bool? AllVisibleSelected
    {
        get
        {
            if (FilteredSites.Count == 0) return false;
            var selected = FilteredSites.Count(row => row.IsSelected);
            return selected == 0 ? false : selected == FilteredSites.Count ? true : null;
        }
        set
        {
            var select = value != false && FilteredSites.Any(row => !row.IsSelected);
            _bulkSelecting = true;
            foreach (var row in FilteredSites) row.IsSelected = select;
            _bulkSelecting = false;
            OnSelectionChanged();
        }
    }
    private bool _bulkSelecting;
    public ObservableCollection<CustomCategory> Categories { get; } = [];
    public ObservableCollection<LiveSite> LiveSites { get; } = [];

    [ObservableProperty] private bool _subscriptionAutoUpdate = true;
    partial void OnSubscriptionAutoUpdateChanged(bool value)
    {
        if (_loadingSourceSettings) return;
        var current = _services.SourceConfig.Data.Subscription;
        _services.SourceConfig.SetSubscription(current with { AutoUpdate = value });
    }
    public string SubscriptionStatus => _services.SourceConfig.Data.Subscription switch
    {
        { Url.Length: 0 } => "尚未订阅；也可以在下方直接粘贴配置 JSON",
        { LastCheck: { } last } => $"上次拉取：{last.LocalDateTime:yyyy-MM-dd HH:mm}",
        _ => "尚未拉取",
    };
    public string SourceSummary
    {
        get
        {
            var sources = _services.SourceConfig.Sources;
            return sources.Count == 0 ? "暂无采集源" : $"共 {sources.Count} 个采集源，已启用 {sources.Count(site => !site.Disabled)} 个";
        }
    }

    /// <summary>粘贴配置文件（LunaTV「配置文件」编辑框）。</summary>
    [ObservableProperty] private string _configText = "";

    // 新增采集源
    [ObservableProperty] private string _newSiteName = "";
    [ObservableProperty] private string _newSiteKey = "";
    [ObservableProperty] private string _newSiteApi = "";
    [ObservableProperty] private string _newSiteDetail = "";

    // 有效性检测
    [ObservableProperty] private string _validateKeyword = "斗罗大陆";
    [ObservableProperty] private bool _validating;
    partial void OnValidatingChanged(bool value) => OnPropertyChanged(nameof(ValidateButtonLabel));
    [ObservableProperty] private string _validationSummary = "";
    private CancellationTokenSource? _validation;

    // 站点设置
    public IReadOnlyList<string> DoubanProxyOptions { get; } = ["直连（本机请求豆瓣）", "Cors Proxy By Zwei", "豆瓣 CDN By CMLiussss（腾讯云）", "豆瓣 CDN By CMLiussss（阿里云）", "Cors Anywhere", "自定义代理"];
    public IReadOnlyList<string> DoubanImageProxyOptions { get; } = ["直连（带豆瓣 Referer）", "豆瓣 CDN By CMLiussss（腾讯云）", "豆瓣 CDN By CMLiussss（阿里云）", "自定义代理"];
    [ObservableProperty] private int _doubanProxyIndex;
    [ObservableProperty] private string _doubanProxy = "";
    [ObservableProperty] private int _doubanImageProxyIndex;
    [ObservableProperty] private string _doubanImageProxy = "";
    [ObservableProperty] private int _searchMaxPage = 5;
    [ObservableProperty] private bool _yellowFilter = true;
    [ObservableProperty] private bool _preferBestSource = true;
    [ObservableProperty] private bool _blockAds = true;
    public bool DoubanProxyIsCustom => DoubanProxyIndex == Array.IndexOf(DoubanProxyTypes.All, DoubanProxyTypes.Custom);
    public bool DoubanImageProxyIsCustom => DoubanImageProxyIndex == Array.IndexOf(DoubanImageProxyTypes.All, DoubanImageProxyTypes.Custom);

    // 自定义分类 / 直播源
    [ObservableProperty] private string _newCategoryName = "";
    [ObservableProperty] private string _newCategoryQuery = "";
    [ObservableProperty] private int _newCategoryTypeIndex;
    [ObservableProperty] private string _newLiveName = "";
    [ObservableProperty] private string _newLiveUrl = "";
    [ObservableProperty] private string _newLiveUa = "";

    private bool _loadingSourceSettings;

    private void InitializeSourceSettings()
    {
        _loadingSourceSettings = true;
        var data = _services.SourceConfig.Data;
        SubscriptionAutoUpdate = data.Subscription.AutoUpdate;
        var settings = data.Settings;
        DoubanProxyIndex = Math.Max(0, Array.IndexOf(DoubanProxyTypes.All, settings.DoubanProxyType));
        DoubanProxy = settings.DoubanProxy;
        DoubanImageProxyIndex = Math.Max(0, Array.IndexOf(DoubanImageProxyTypes.All, settings.DoubanImageProxyType));
        DoubanImageProxy = settings.DoubanImageProxy;
        SearchMaxPage = settings.SearchMaxPage;
        YellowFilter = !settings.DisableYellowFilter;
        PreferBestSource = settings.PreferBestSource;
        BlockAds = settings.BlockAds;
        _loadingSourceSettings = false;
        _services.SourceConfig.Changed += RequestRefreshSites;
        RefreshSites();
    }

    private int _refreshQueued;

    /// <summary>存储变化（可能在后台线程、可能连发多次）：合并成一次 UI 刷新。</summary>
    private void RequestRefreshSites()
    {
        if (Interlocked.Exchange(ref _refreshQueued, 1) == 1) return;
        _main.RunOnUi(() => { Volatile.Write(ref _refreshQueued, 0); RefreshSites(); });
    }

    /// <summary>从存储重建源列表/分类/直播源（保留检测结果）。源列表没变时不重建行，避免整表重新生成控件。</summary>
    public void RefreshSites()
    {
        var current = _services.SourceConfig.Sources;
        if (current.Count == Sites.Count && current.Select((site, i) => ReferenceEquals(site, Sites[i].Site) || site == Sites[i].Site).All(same => same))
        {
            RefreshSourceExtras();
            return;
        }
        var status = Sites.ToDictionary(row => row.Key, row => (row.Status, row.StatusDetail, row.IsSelected));
        foreach (var row in Sites) row.PropertyChanged -= OnSourceRowChanged;
        Sites.Clear();
        var index = 0;
        foreach (var site in _services.SourceConfig.Sources)
        {
            var row = new SourceRow(site, ++index);
            if (status.TryGetValue(site.Key, out var previous)) { row.Status = previous.Status; row.StatusDetail = previous.StatusDetail; row.IsSelected = previous.IsSelected; }
            row.PropertyChanged += OnSourceRowChanged;
            Sites.Add(row);
        }
        ApplySiteFilter();
        RefreshSourceExtras();
    }

    private void RefreshSourceExtras()
    {
        OnPropertyChanged(nameof(DeletedSourceCount));
        OnPropertyChanged(nameof(HasDeletedSources));
        Categories.Clear();
        foreach (var category in _services.SourceConfig.Data.CustomCategories) Categories.Add(category);
        LiveSites.Clear();
        foreach (var live in _services.SourceConfig.Data.Lives) LiveSites.Add(live);
        OnPropertyChanged(nameof(SourceSummary));
        OnPropertyChanged(nameof(SubscriptionStatus));
        _main.UpdateSourceName();
    }

    /// <summary>源配置变化后：点播页按需重载、首页刷新、直播源（来自配置时）重载。</summary>
    private void AfterSourceConfigChanged(bool reloadLive)
    {
        RefreshSites();
        _main.Vod.Invalidate();
        if (reloadLive && _main.Live.EffectiveLiveAddress == LiveViewModel.ConfigLivesAddress) _ = _main.Live.LoadAsync();
    }

    [RelayCommand]
    private void ApplyConfigText()
    {
        if (string.IsNullOrWhiteSpace(ConfigText)) { Message = "请先粘贴配置文件内容"; return; }
        try
        {
            var result = _services.Subscription.ApplyText(ConfigText);
            Message = $"配置文件已应用：{result.Sources} 个采集源" + (result.Lives > 0 ? $"，{result.Lives} 个直播源" : "");
            ConfigText = "";
            AfterSourceConfigChanged(reloadLive: result.Lives > 0);
        }
        catch (Exception error) { Message = $"配置文件无效：{error.Message}"; }
    }

    // ---------- 采集源：过滤与批量 ----------

    private void OnSourceRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SourceRow.IsSelected)) { if (!_bulkSelecting) OnSelectionChanged(); }
        else if (e.PropertyName == nameof(SourceRow.Status) && SiteStatusFilterIndex >= 3 && !Validating) ApplySiteFilter();
    }

    private void OnSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedSiteCount));
        OnPropertyChanged(nameof(HasSelectedSites));
        OnPropertyChanged(nameof(SelectionSummary));
        OnPropertyChanged(nameof(AllVisibleSelected));
        OnPropertyChanged(nameof(ValidateButtonLabel));
    }

    /// <summary>重建可见列表：模糊匹配名称/键/接口地址 + 状态过滤。</summary>
    private void ApplySiteFilter()
    {
        var query = SiteFilter;
        FilteredSites.Clear();
        foreach (var row in Sites)
            if (MatchesStatus(row) && SourceFilter.Matches(query, row.Name, row.Key, row.Api)) FilteredSites.Add(row);
        OnPropertyChanged(nameof(IsSiteFilterActive));
        OnPropertyChanged(nameof(SiteFilterSummary));
        OnSelectionChanged();
    }

    private bool MatchesStatus(SourceRow row) => SiteStatusFilterIndex switch
    {
        1 => row.Enabled,
        2 => row.Disabled,
        3 => row.Status == SourceValidity.Valid,
        4 => row.Status == SourceValidity.NoResults,
        5 => row.Status == SourceValidity.Invalid,
        6 => row.Status is null,
        _ => true,
    };

    [RelayCommand] private void ClearSiteFilter() { SiteFilter = ""; SiteStatusFilterIndex = 0; }

    [RelayCommand]
    private void ClearSiteSelection()
    {
        _bulkSelecting = true;
        foreach (var row in Sites) row.IsSelected = false;
        _bulkSelecting = false;
        OnSelectionChanged();
    }

    private string[] SelectedKeys() => Sites.Where(row => row.IsSelected).Select(row => row.Key).ToArray();

    [RelayCommand]
    private void EnableSelectedSites()
    {
        var keys = SelectedKeys();
        if (keys.Length == 0) { Message = "请先勾选要启用的源"; return; }
        _services.SourceConfig.SetSourcesDisabled(keys, false);
        Message = $"已启用 {keys.Length} 个源";
    }

    [RelayCommand]
    private void DisableSelectedSites()
    {
        var keys = SelectedKeys();
        if (keys.Length == 0) { Message = "请先勾选要禁用的源"; return; }
        _services.SourceConfig.SetSourcesDisabled(keys, true);
        Message = $"已禁用 {keys.Length} 个源";
    }

    /// <summary>批量删除：先弹二次确认，确认后才删。</summary>
    [RelayCommand]
    private void DeleteSelectedSites()
    {
        var rows = Sites.Where(row => row.IsSelected).ToArray();
        if (rows.Length == 0) { Message = "请先勾选要删除的源"; return; }
        RequestDelete(rows);
    }

    private void RequestDelete(IReadOnlyList<SourceRow> rows)
    {
        _pendingDeleteKeys = rows.Select(row => row.Key).ToArray();
        DeleteDialogMessage = rows.Count == 1 ? $"确定删除采集源「{rows[0].Name}」吗？" : $"确定删除选中的 {rows.Count} 个采集源吗？";
        DeleteDialogNames = rows.Count == 1 ? rows[0].Api : string.Join("、", rows.Take(8).Select(row => row.Name)) + (rows.Count > 8 ? $" 等 {rows.Count} 个" : "");
        var fromConfig = rows.Count(row => !row.IsCustom);
        DeleteDialogNote = fromConfig == 0 ? "手动添加的源删除后无法恢复，需要重新添加。"
            : fromConfig == rows.Count ? "订阅源删除后，刷新订阅也不会再加回；可以用「恢复已删除」找回。"
            : $"其中 {fromConfig} 个订阅源可用「恢复已删除」找回；{rows.Count - fromConfig} 个手动添加的源删除后无法恢复。";
        DeleteDialogOpen = true;
    }

    [RelayCommand]
    private void ConfirmDeleteSites()
    {
        DeleteDialogOpen = false;
        var keys = _pendingDeleteKeys;
        _pendingDeleteKeys = [];
        if (keys.Length == 0) return;
        var names = Sites.Where(row => keys.Contains(row.Key)).Select(row => row.Name).ToArray();
        var removed = _services.SourceConfig.DeleteSources(keys);
        Message = removed == 1 && names.Length == 1 ? $"已删除「{names[0]}」" : $"已删除 {removed} 个源";
    }

    [RelayCommand]
    private void CancelDeleteSites()
    {
        DeleteDialogOpen = false;
        _pendingDeleteKeys = [];
    }

    [RelayCommand]
    private void RestoreDeletedSites()
    {
        var restored = _services.SourceConfig.RestoreDeletedSources();
        Message = restored > 0 ? $"已恢复 {restored} 个订阅源" : "已清除删除记录；下次拉取订阅时会重新加回";
    }

    // ---------- 采集源 ----------

    [RelayCommand]
    private void ToggleSiteDisabled(SourceRow row)
    {
        _services.SourceConfig.SetSourceDisabled(row.Key, !row.Disabled);
        Message = row.Disabled ? $"已启用「{row.Name}」" : $"已禁用「{row.Name}」";
    }

    [RelayCommand] private void MoveSiteUp(SourceRow row) => _services.SourceConfig.MoveSource(row.Key, Math.Max(0, row.Index - 2));
    [RelayCommand] private void MoveSiteDown(SourceRow row) => _services.SourceConfig.MoveSource(row.Key, row.Index);
    [RelayCommand] private void MoveSiteToTop(SourceRow row) => _services.SourceConfig.MoveSource(row.Key, 0);

    [RelayCommand] private void DeleteSite(SourceRow row) => RequestDelete([row]);

    [RelayCommand]
    private void AddSite()
    {
        try
        {
            var key = NewSiteKey.Trim().Length > 0 ? NewSiteKey.Trim() : "custom_" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(NewSiteApi.Trim())))[..8].ToLowerInvariant();
            _services.SourceConfig.AddSource(key, NewSiteName, NewSiteApi, NewSiteDetail);
            Message = $"已添加采集源「{NewSiteName.Trim()}」";
            NewSiteName = NewSiteKey = NewSiteApi = NewSiteDetail = "";
        }
        catch (Exception error) { Message = $"添加失败：{error.Message}"; }
    }

    [RelayCommand] private void EnableAllSites() => _services.SourceConfig.SetSourcesDisabled(_services.SourceConfig.Sources.Select(site => site.Key), false);
    [RelayCommand] private void DisableAllSites() => _services.SourceConfig.SetSourcesDisabled(_services.SourceConfig.Sources.Select(site => site.Key), true);

    /// <summary>检测后一键禁用无效源（请求失败）。</summary>
    [RelayCommand]
    private void DisableInvalidSites()
    {
        var invalid = Sites.Where(row => row.Status == SourceValidity.Invalid).Select(row => row.Key).ToArray();
        if (invalid.Length == 0) { Message = "没有检测为无效的源，请先进行有效性检测"; return; }
        _services.SourceConfig.SetSourcesDisabled(invalid, true);
        Message = $"已禁用 {invalid.Length} 个无效源";
    }

    /// <summary>有效性检测（LunaTV source/validate）：按关键词搜索每个源，逐个回填结果。</summary>
    [RelayCommand]
    private async Task ValidateSites()
    {
        if (Validating) { _validation?.Cancel(); return; }
        var keyword = ValidateKeyword.Trim();
        if (keyword.Length == 0) { Message = "请输入检测关键词"; return; }
        // 有勾选检测勾选的，否则检测当前过滤出的（未过滤即全部）。
        var targets = (HasSelectedSites ? Sites.Where(row => row.IsSelected) : FilteredSites).ToArray();
        if (targets.Length == 0) { Message = "没有可检测的源"; return; }
        using var scope = new CancellationTokenSource();
        _validation = scope;
        Validating = true;
        foreach (var row in targets) { row.Status = null; row.StatusDetail = "检测中…"; }
        int valid = 0, empty = 0, invalid = 0;
        ValidationSummary = $"0 / {targets.Length}";
        try
        {
            await foreach (var result in _services.Catalog.ValidateStreamAsync(keyword, targets.Select(row => row.Site).ToArray(), scope.Token))
            {
                await _main.RunOnUiAsync(() =>
                {
                    if (Sites.FirstOrDefault(row => row.Key == result.Site.Key) is not { } row) return;
                    row.Status = result.Status;
                    row.StatusDetail = result.Status switch
                    {
                        SourceValidity.Valid => $"有效 · {result.Elapsed.TotalMilliseconds:0}ms",
                        SourceValidity.NoResults => $"无结果 · {result.Elapsed.TotalMilliseconds:0}ms",
                        _ => "无效 · " + result.Error,
                    };
                    switch (result.Status) { case SourceValidity.Valid: valid++; break; case SourceValidity.NoResults: empty++; break; default: invalid++; break; }
                    ValidationSummary = $"{valid + empty + invalid} / {targets.Length} · 有效 {valid} · 无结果 {empty} · 无效 {invalid}";
                });
            }
            Message = $"检测完成：有效 {valid}，无结果 {empty}，无效 {invalid}";
        }
        catch (OperationCanceledException) { Message = "已停止检测"; }
        catch (Exception error) { Message = $"检测失败：{error.Message}"; }
        finally
        {
            foreach (var row in targets.Where(row => row.Status is null)) row.StatusDetail = "";
            Validating = false;
            _validation = null;
            if (SiteStatusFilterIndex >= 3) ApplySiteFilter();
        }
    }

    // ---------- 站点设置 ----------

    private void UpdateSiteSettings(Func<SiteSettings, SiteSettings> change)
    {
        if (_loadingSourceSettings) return;
        _services.SourceConfig.UpdateSettings(change);
    }

    partial void OnDoubanProxyIndexChanged(int value)
    {
        OnPropertyChanged(nameof(DoubanProxyIsCustom));
        UpdateSiteSettings(settings => settings with { DoubanProxyType = DoubanProxyTypes.All[Math.Clamp(value, 0, DoubanProxyTypes.All.Length - 1)] });
        if (!_loadingSourceSettings) _ = _main.Home.LoadAsync();
    }
    partial void OnDoubanProxyChanged(string value) => UpdateSiteSettings(settings => settings with { DoubanProxy = value.Trim() });
    partial void OnDoubanImageProxyIndexChanged(int value)
    {
        OnPropertyChanged(nameof(DoubanImageProxyIsCustom));
        UpdateSiteSettings(settings => settings with { DoubanImageProxyType = DoubanImageProxyTypes.All[Math.Clamp(value, 0, DoubanImageProxyTypes.All.Length - 1)] });
        if (!_loadingSourceSettings) { _ = _main.Home.LoadAsync(); _main.Vod.Invalidate(); }
    }
    partial void OnDoubanImageProxyChanged(string value) => UpdateSiteSettings(settings => settings with { DoubanImageProxy = value.Trim() });
    partial void OnSearchMaxPageChanged(int value) => UpdateSiteSettings(settings => settings with { SearchMaxPage = Math.Clamp(value, 1, 20) });
    partial void OnYellowFilterChanged(bool value) => UpdateSiteSettings(settings => settings with { DisableYellowFilter = !value });
    partial void OnPreferBestSourceChanged(bool value) => UpdateSiteSettings(settings => settings with { PreferBestSource = value });
    partial void OnBlockAdsChanged(bool value) => UpdateSiteSettings(settings => settings with { BlockAds = value });

    // ---------- 自定义分类 ----------

    [RelayCommand]
    private void AddCategory()
    {
        if (string.IsNullOrWhiteSpace(NewCategoryQuery)) { Message = "请填写豆瓣标签（如：热门、经典、科幻）"; return; }
        _services.SourceConfig.AddCategory(NewCategoryName.Trim().Length > 0 ? NewCategoryName.Trim() : null, NewCategoryTypeIndex == 1 ? "tv" : "movie", NewCategoryQuery);
        NewCategoryName = NewCategoryQuery = "";
        _main.Vod.Invalidate();
        Message = "已添加自定义分类";
    }

    [RelayCommand] private void DeleteCategory(CustomCategory category) { _services.SourceConfig.DeleteCategory(category.Identity); _main.Vod.Invalidate(); }
    [RelayCommand] private void ToggleCategory(CustomCategory category) { _services.SourceConfig.SetCategoryDisabled(category.Identity, !category.Disabled); _main.Vod.Invalidate(); }

    // ---------- 直播源 ----------

    [RelayCommand]
    private void AddLiveSite()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(NewLiveName) || string.IsNullOrWhiteSpace(NewLiveUrl)) { Message = "请填写直播源名称与地址"; return; }
            var key = "live_" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(NewLiveUrl.Trim())))[..8].ToLowerInvariant();
            _services.SourceConfig.AddLive(key, NewLiveName, NewLiveUrl, NewLiveUa.Trim().Length > 0 ? NewLiveUa.Trim() : null, null);
            NewLiveName = NewLiveUrl = NewLiveUa = "";
            AfterSourceConfigChanged(reloadLive: true);
            Message = "已添加直播源";
        }
        catch (Exception error) { Message = $"添加失败：{error.Message}"; }
    }

    [RelayCommand] private void DeleteLiveSite(LiveSite live) { _services.SourceConfig.DeleteLive(live.Key); AfterSourceConfigChanged(true); }
    [RelayCommand] private void ToggleLiveSite(LiveSite live) { _services.SourceConfig.SetLiveDisabled(live.Key, !live.Disabled); AfterSourceConfigChanged(true); }

    /// <summary>直播页改用订阅配置里的直播源（清除手动 m3u 地址）。</summary>
    [RelayCommand]
    private async Task UseConfigLives()
    {
        _services.CurrentLiveConfig = "";
        LiveConfigUrl = "";
        await _main.Live.LoadAsync();
        Message = _services.SourceConfig.EnabledLives.Count > 0 ? "直播页已改用订阅配置里的直播源" : "订阅配置里没有启用的直播源";
    }
}

/// <summary>源列表一行（含检测结果）。</summary>
public sealed partial class SourceRow(ApiSite site, int index) : ObservableObject
{
    public ApiSite Site { get; } = site;
    public int Index { get; } = index;
    public string Key => Site.Key;
    public string Name => Site.Name;
    public string Api => Site.Api;
    public bool Disabled => Site.Disabled;
    public bool Enabled => !Site.Disabled;
    public bool IsCustom => Site.From == SourceOrigin.Custom;
    public string OriginLabel => IsCustom ? "自定义" : "订阅";
    public string ToggleLabel => Site.Disabled ? "启用" : "禁用";
    [ObservableProperty] private SourceValidity? _status;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private string _statusDetail = "";
}

/// <summary>源列表的模糊匹配：空格分隔的每个词都要命中（名称/键/地址包含，或名称按字符顺序出现，如「量子」匹配「量子资源站」、「lz」匹配「lzzy」）。</summary>
public static class SourceFilter
{
    /// <summary>地址只做包含匹配：长 URL 上的字符顺序匹配几乎什么都能命中。</summary>
    public static bool Matches(string? query, string name, string key, string api)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        foreach (var token in query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var hit = name.Contains(token, StringComparison.OrdinalIgnoreCase) || key.Contains(token, StringComparison.OrdinalIgnoreCase)
                || api.Contains(token, StringComparison.OrdinalIgnoreCase) || IsSubsequence(token, name) || IsSubsequence(token, key);
            if (!hit) return false;
        }
        return true;
    }

    private static bool IsSubsequence(string token, string text)
    {
        var i = 0;
        foreach (var ch in text)
            if (i < token.Length && char.ToLowerInvariant(ch) == char.ToLowerInvariant(token[i])) i++;
        return i == token.Length;
    }
}
