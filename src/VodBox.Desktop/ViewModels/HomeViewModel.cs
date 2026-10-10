using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VodBox.Core;
using VodBox.Desktop.Services;

namespace VodBox.Desktop.ViewModels;

/// <summary>首页：推荐位 + 最近观看（带进度）。</summary>
public sealed partial class HomeViewModel : ObservableObject
{
    private readonly Func<IContentSource?> _getSource;
    private readonly ILibraryStore _store;
    private CancellationTokenSource? _request;
    private long _generation;
    private string _sourceKey = "";
    [ObservableProperty] private string _sourceName = "";
    private readonly MainViewModel _main;

    public ObservableCollection<MediaItem> Recommendations { get; } = [];
    public ObservableCollection<HeroSlide> HeroSlides { get; } = [];
    [ObservableProperty] private int _heroIndex;
    public bool CanRotateHero => HeroSlides.Count > 1;
    public string HeroPositionLabel => HeroSlides.Count == 0 ? "今日推荐" : $"今日推荐 · {HeroIndex + 1}/{HeroSlides.Count}";

    public sealed partial class HeroSlide(MediaItem item, int index) : ObservableObject
    {
        public MediaItem Item { get; } = item;
        public int Index { get; } = index;
        [ObservableProperty] private bool _isSelected;
    }

    private void RebuildHeroSlides()
    {
        var candidates = Recommendations.Where(item => !string.IsNullOrWhiteSpace(item.Poster))
            .DistinctBy(item => item.Poster).Take(5).ToArray();
        if (candidates.Length < 2) candidates = Recommendations.DistinctBy(item => item.Id).Take(5).ToArray();
        HeroSlides.Clear();
        for (var i = 0; i < candidates.Length; i++) HeroSlides.Add(new HeroSlide(candidates[i], i));
        OnPropertyChanged(nameof(CanRotateHero));
        if (HeroSlides.Count == 0) { HeroIndex = 0; SetHero(null); }
        else SelectHero(Math.Min(HeroIndex, HeroSlides.Count - 1));
        OnPropertyChanged(nameof(HeroPositionLabel));
    }

    public void SelectHero(int index)
    {
        if (index < 0 || index >= HeroSlides.Count) return;
        HeroIndex = index;
        foreach (var slide in HeroSlides) slide.IsSelected = slide.Index == index;
        SetHero(HeroSlides[index].Item);
        OnPropertyChanged(nameof(HeroPositionLabel));
    }

    [RelayCommand]
    private void NextHero()
    {
        if (CanRotateHero) SelectHero((HeroIndex + 1) % HeroSlides.Count);
    }

    [RelayCommand]
    private void PreviousHero()
    {
        if (CanRotateHero) SelectHero((HeroIndex + HeroSlides.Count - 1) % HeroSlides.Count);
    }
    public ObservableCollection<HistoryEntry> Recent { get; } = [];

    /// <summary>首页最近观看上限：固定一行、可横向滑动，最多前 20 条。</summary>
    internal const int RecentLimit = 20;

    [ObservableProperty] private bool _loading;
    [ObservableProperty] private bool _recentLoading;
    [ObservableProperty] private string _recentHint = "";
    /// <summary>推荐区空态/错误态文案（非空时显示在网格位置，避免白屏）。</summary>
    [ObservableProperty] private string _recommendationsHint = "";
    [ObservableProperty] private string? _heroPoster;
    [ObservableProperty] private string _heroTitle = "";
    [ObservableProperty] private string _heroRemarks = "";
    [ObservableProperty] private string _heroDescription = "";
    private MediaItem? _hero;

    public HomeViewModel(AppServices services, MainViewModel main) : this(services.Registry.Default, services.Store, main) { }

    internal HomeViewModel(Func<IContentSource?> getSource, ILibraryStore store, MainViewModel main)
    {
        _getSource = getSource;
        _store = store;
        _main = main;
        Recent.CollectionChanged += (_, _) => NotifyRecentState();
        Recommendations.CollectionChanged += (_, _) => RebuildHeroSlides();
    }

    /// <summary>最近观看：加载中且尚无内容 → 骨架行（本地库读取极快，只在首屏占位）。</summary>
    public bool ShowRecentSkeleton => RecentLoading && Recent.Count == 0;

    /// <summary>最近观看：加载结束仍为空 → 空态文案，不留一段空白。</summary>
    public bool ShowRecentEmpty => !RecentLoading && Recent.Count == 0 && string.IsNullOrEmpty(RecentHint);

    partial void OnRecentLoadingChanged(bool value) => NotifyRecentState();
    partial void OnRecentHintChanged(string value) => NotifyRecentState();

    private void NotifyRecentState()
    {
        OnPropertyChanged(nameof(ShowRecentSkeleton));
        OnPropertyChanged(nameof(ShowRecentEmpty));
    }

    public async Task LoadAsync()
    {
        CancellationTokenSource? scope = null;
        IContentSource? source = null;
        long generation = 0;
        await _main.RunOnUiAsync(() =>
        {
            _request?.Cancel();
            scope = new CancellationTokenSource();
            _request = scope;
            generation = ++_generation;
            source = _getSource();
            Loading = true;
            Recommendations.Clear();
            RecentLoading = true;
            RecentHint = "";
            RecommendationsHint = "";
            _sourceKey = "";
            SourceName = "";
            SetHero(null);
        });
        var ct = scope!.Token;
        try
        {
            // 两个分区并行加载、独立收尾，慢站点不阻塞最近观看。
            await Task.WhenAll(LoadRecentAsync(generation, ct), LoadRecommendationsAsync(source, generation, ct));
        }
        finally
        {
            await _main.RunOnUiAsync(() =>
            {
                if (generation != _generation) return;
                _request = null;
                Loading = false;
                RecentLoading = false;
            });
            scope.Dispose();
        }
    }

    private async Task LoadRecommendationsAsync(IContentSource? source, long generation, CancellationToken ct)
    {
        try
        {
            if (source is null)
            {
                await _main.RunOnUiAsync(() =>
                {
                    if (generation != _generation || ct.IsCancellationRequested) return;
                    _main.StatusMessage = "尚未配置内容源，请到设置中添加 TVBox 配置地址";
                    RecommendationsHint = _main.StatusMessage;
                });
                return;
            }
            var page = await source.GetHomeAsync(ct);
            ct.ThrowIfCancellationRequested();
            await _main.RunOnUiAsync(() =>
            {
                if (generation != _generation || ct.IsCancellationRequested) return;
                _sourceKey = source.Key;
                SourceName = source.Name;
                foreach (var item in page.Items.Take(24)) Recommendations.Add(item);
                SelectHero(0);
                RecommendationsHint = Recommendations.Count == 0 ? source is IInformationalContentSource notice ? notice.Notice : "该站点暂时没有返回推荐内容" : "";
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception error)
        {
            await _main.RunOnUiAsync(() =>
            {
                if (generation != _generation || ct.IsCancellationRequested) return;
                _main.StatusMessage = $"加载推荐失败：{error.Message}";
                RecommendationsHint = _main.StatusMessage;
            });
        }
        finally
        {
            await _main.RunOnUiAsync(() => { if (generation == _generation) Loading = false; });
        }
    }

    private async Task LoadRecentAsync(long generation, CancellationToken ct)
    {
        try
        {
            // 首页最近观看固定一行、可横向滑动，最多展示前 20 条。
            // 上限在 VM 兜底：存储层可能忽略 limit（或返回超出请求条数），不能只靠查询参数。
            var recent = await _store.GetHistoryAsync(RecentLimit, ct);
            ct.ThrowIfCancellationRequested();
            await _main.RunOnUiAsync(() =>
            {
                if (generation != _generation || ct.IsCancellationRequested) return;
                Recent.Clear();
                foreach (var entry in recent.Take(RecentLimit)) Recent.Add(entry);
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception error)
        {
            await _main.RunOnUiAsync(() =>
            {
                if (generation == _generation && !ct.IsCancellationRequested)
                    RecentHint = $"最近观看加载失败：{error.Message}";
            });
        }
        finally
        {
            await _main.RunOnUiAsync(() => { if (generation == _generation) RecentLoading = false; });
        }
    }

    private void SetHero(MediaItem? hero)
    {
        _hero = hero;
        HeroPoster = hero?.Poster;
        HeroTitle = hero?.Title ?? "";
        HeroRemarks = hero?.Remarks ?? "";
        HeroDescription = string.Join(" · ", new[] { hero?.Year, hero?.Area, hero?.TypeName }.Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    [RelayCommand]
    private void OpenHero()
    {
        if (_hero is not null) _main.Detail.Open(_sourceKey, _hero);
    }

    [RelayCommand]
    private void OpenItem(MediaItem item) => _main.Detail.Open(_sourceKey, item);

    [RelayCommand]
    private void Resume(HistoryEntry entry) => _main.ResumeEntry(entry);

}
