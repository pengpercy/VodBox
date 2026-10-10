using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VodBox.Core;
using VodBox.Desktop.Services;

namespace VodBox.Desktop.ViewModels;

/// <summary>收藏：点播收藏 / 直播频道两 Tab。</summary>
public sealed partial class FavoritesViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly MainViewModel _main;

    public ObservableCollection<FavoriteEntry> Vod { get; } = [];
    public ObservableCollection<FavoriteEntry> Live { get; } = [];

    /// <summary>收藏网格行集合（每行 6 张，虚拟化数据源；LoadAsync 后重建）。</summary>
    public ObservableCollection<FavoriteRow> VodRows { get; } = [];

    [ObservableProperty] private int _tab; // 0=点播 1=直播
    /// <summary>管理模式（多选删除，设计稿 ①）。S5 接批量删除。</summary>
    [ObservableProperty] private bool _managing;
    /// <summary>排序方式（最近收藏/标题，S5 接）。</summary>
    [ObservableProperty] private string _sortBy = "最近收藏";
    [ObservableProperty] private bool _loading;
    /// <summary>加载失败文案；非空时覆盖网格位置（空态由「暂无收藏」承担）。</summary>
    [ObservableProperty] private string _hint = "";

    private long _generation;

    /// <summary>首次加载（尚无任何收藏内容）→ 网格骨架；刷新时已有内容则不闪骨架。</summary>
    public bool ShowSkeleton => Loading && Vod.Count == 0 && Live.Count == 0;
    /// <summary>加载结束且确实没有收藏 → 空态文案。</summary>
    public bool ShowEmpty => !Loading && Vod.Count == 0 && string.IsNullOrEmpty(Hint);

    public FavoritesViewModel(AppServices services, MainViewModel main)
    {
        _services = services;
        _main = main;
        Vod.CollectionChanged += (_, _) => NotifyLibraryState();
        Live.CollectionChanged += (_, _) => NotifyLibraryState();
    }

    partial void OnLoadingChanged(bool value) => NotifyLibraryState();
    partial void OnHintChanged(string value) => NotifyLibraryState();

    private void NotifyLibraryState()
    {
        OnPropertyChanged(nameof(ShowSkeleton));
        OnPropertyChanged(nameof(ShowEmpty));
    }

    /// <summary>离开收藏页：丢弃在途结果，避免回到旧页面时回填。</summary>
    public void CancelPending()
    {
        ++_generation;
        Loading = false;
    }

    /// <summary>导航进入收藏页：总是刷新（详情页可能刚改动收藏）；已有内容时保留，不闪骨架。</summary>
    public void EnsureLoaded() => _ = LoadAsync();

    public async Task LoadAsync()
    {
        var generation = ++_generation;
        await _main.RunOnUiAsync(() => { Loading = true; Hint = ""; });
        try
        {
            var vod = await _services.Store.GetFavoritesAsync(FavoriteKind.Vod);
            var live = await _services.Store.GetFavoritesAsync(FavoriteKind.Live);
            await _main.RunOnUiAsync(() =>
            {
                if (generation != _generation) return;
                // 结果到齐后再替换：刷新期间保留旧内容，不出现「有收藏却显示空态」的中间帧。
                Vod.Clear();
                foreach (var entry in vod) Vod.Add(entry);
                Live.Clear();
                foreach (var entry in live) Live.Add(entry);
                RebuildVodRows();
            });
        }
        catch (Exception error)
        {
            await _main.RunOnUiAsync(() =>
            {
                if (generation != _generation) return;
                _main.StatusMessage = $"收藏加载失败：{error.Message}";
                Hint = $"收藏加载失败：{error.Message}";
            });
        }
        finally
        {
            await _main.RunOnUiAsync(() => { if (generation == _generation) Loading = false; });
        }
    }

    /// <summary>收藏按 6 张/行分块（虚拟化网格）。</summary>
    public void RebuildVodRows()
    {
        VodRows.Clear();
        for (var i = 0; i < Vod.Count; i += 6)
            VodRows.Add(new FavoriteRow(Vod.Skip(i).Take(6).ToList()));
    }

    [RelayCommand]
    private void Open(FavoriteEntry entry) => _main.ResumeEntry(new HistoryEntry
    {
        SourceKey = entry.SourceKey,
        SourceName = entry.SourceName,
        MediaId = entry.MediaId,
        Title = entry.Title,
        Poster = entry.Poster,
    });

    [RelayCommand]
    private void ToggleManage() => Managing = !Managing;

    [RelayCommand]
    private async Task Remove(FavoriteEntry entry)
    {
        await _services.Store.SetFavoriteAsync(entry, false);
        (entry.Kind == FavoriteKind.Vod ? Vod : Live).Remove(entry);
        RebuildVodRows();
    }
}

/// <summary>历史：按时间分组、续播、删除。</summary>
public sealed partial class HistoryViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly MainViewModel _main;

    public ObservableCollection<HistoryEntry> Entries { get; } = [];

    /// <summary>无痕模式开启时页面顶部横幅（设计稿 ②）。S6 设置接开关。</summary>
    [ObservableProperty] private bool _incognitoBanner = false;
    /// <summary>最近的历史，与首页共用最近 20 条的范围。</summary>
    public ObservableCollection<HistoryEntry> Recent { get; } = [];
    /// <summary>更早历史（最近 20 条之外的记录）。</summary>
    public ObservableCollection<HistoryEntry> Earlier { get; } = [];

    [ObservableProperty] private bool _loading;
    /// <summary>加载失败文案；非空时覆盖列表位置（空态由「暂无观看记录」承担）。</summary>
    [ObservableProperty] private string _hint = "";

    private long _generation;

    /// <summary>首次加载（尚无任何记录）→ 列表骨架；刷新时已有内容则不闪骨架。</summary>
    public bool ShowSkeleton => Loading && Entries.Count == 0;
    /// <summary>加载结束且确实没有记录 → 空态文案。</summary>
    public bool ShowEmpty => !Loading && Entries.Count == 0 && string.IsNullOrEmpty(Hint);
    public bool ShowRecentArea => ShowSkeleton || Recent.Count > 0;

    public HistoryViewModel(AppServices services, MainViewModel main)
    {
        _services = services;
        _main = main;
        Entries.CollectionChanged += (_, _) => NotifyHistoryState();
        Recent.CollectionChanged += (_, _) => NotifyHistoryState();
    }

    partial void OnLoadingChanged(bool value) => NotifyHistoryState();
    partial void OnHintChanged(string value) => NotifyHistoryState();

    private void NotifyHistoryState()
    {
        OnPropertyChanged(nameof(ShowSkeleton));
        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(ShowRecentArea));
    }

    /// <summary>离开历史页：丢弃在途结果，避免回到旧页面时回填。</summary>
    public void CancelPending()
    {
        ++_generation;
        Loading = false;
    }

    /// <summary>导航进入历史页：总是刷新（刚看完一集就该出现在最前）；已有内容时保留，不闪骨架。</summary>
    public void EnsureLoaded() => _ = LoadAsync();

    public async Task LoadAsync()
    {
        var generation = ++_generation;
        await _main.RunOnUiAsync(() => { Loading = true; Hint = ""; });
        try
        {
            var entries = await _services.Store.GetHistoryAsync(200);
            await _main.RunOnUiAsync(() =>
            {
                if (generation != _generation) return;
                // 结果到齐后再替换：刷新期间保留旧列表，不出现「有记录却显示空态」的中间帧。
                Entries.Clear();
                foreach (var entry in entries) Entries.Add(entry);
                SplitHistory();
            });
        }
        catch (Exception error)
        {
            await _main.RunOnUiAsync(() =>
            {
                if (generation != _generation) return;
                _main.StatusMessage = $"历史加载失败：{error.Message}";
                Hint = $"历史加载失败：{error.Message}";
            });
        }
        finally
        {
            await _main.RunOnUiAsync(() => { if (generation == _generation) Loading = false; });
        }
    }

    /// <summary>与首页一致：前 20 条为最近观看，剩余记录为更早，不以午夜为分界。</summary>
    public void SplitHistory()
    {
        Recent.Clear();
        Earlier.Clear();
        foreach (var entry in Entries.Take(HomeViewModel.RecentLimit)) Recent.Add(entry);
        foreach (var entry in Entries.Skip(HomeViewModel.RecentLimit)) Earlier.Add(entry);
    }

    [RelayCommand]
    private void Resume(HistoryEntry entry) => _main.ResumeEntry(entry);

    [RelayCommand]
    private async Task Delete(HistoryEntry entry)
    {
        await _services.Store.DeleteHistoryAsync(entry.SourceKey, entry.MediaId);
        Entries.Remove(entry);
        SplitHistory();
    }

    [RelayCommand]
    private async Task ClearAll()
    {
        await _services.Store.ClearHistoryAsync();
        Entries.Clear();
        SplitHistory();
    }
}

/// <summary>收藏网格行（6 张卡）。</summary>
public sealed class FavoriteRow
{
    public IReadOnlyList<FavoriteEntry> Items { get; }
    public FavoriteRow(IReadOnlyList<FavoriteEntry> items) => Items = items;
}
