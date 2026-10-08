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
    public ObservableCollection<HistoryEntry> Recent { get; } = [];

    [ObservableProperty] private bool _loading;
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
            Recent.Clear();
            _sourceKey = "";
            SourceName = "";
            SetHero(null);
        });
        var ct = scope!.Token;
        try
        {
            var recent = await _store.GetHistoryAsync(12, ct);
            ct.ThrowIfCancellationRequested();
            await _main.RunOnUiAsync(() =>
            {
                if (generation != _generation || ct.IsCancellationRequested) return;
                foreach (var entry in recent) Recent.Add(entry);
            });
            if (source is null)
            {
                await _main.RunOnUiAsync(() =>
                {
                    if (generation == _generation && !ct.IsCancellationRequested)
                        _main.StatusMessage = "尚未配置内容源，请到设置中添加 TVBox 配置地址";
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
                SetHero(page.Items.FirstOrDefault());
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception error)
        {
            await _main.RunOnUiAsync(() =>
            {
                if (generation == _generation && !ct.IsCancellationRequested)
                    _main.StatusMessage = $"加载推荐失败：{error.Message}";
            });
        }
        finally
        {
            await _main.RunOnUiAsync(() =>
            {
                if (generation != _generation) return;
                _request = null;
                Loading = false;
            });
            scope.Dispose();
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
    private void Resume(HistoryEntry entry) => _main.Detail.Resume(entry);

}
