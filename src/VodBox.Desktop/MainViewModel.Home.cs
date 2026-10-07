using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VodBox.Core;

namespace VodBox.Desktop;

public partial class MainViewModel
{
    [ObservableProperty, NotifyPropertyChangedFor(nameof(BrowseReturnLabel))] private bool _showHome = true;
    [ObservableProperty] private string _homeRecommendationStatus = "请在设置中配置点播播放源。";
    public string BrowseReturnLabel => ShowHome ? "返回首页" : "返回浏览";
    public string HomeTitle => SelectedSource?.Name ?? Sources.FirstOrDefault()?.Name ?? "影视";
    public ObservableCollection<string> ConfigurationWarnings { get; } = [];
    public bool HasConfigurationWarnings => ConfigurationWarnings.Count > 0;
    public string ConfigurationImportSummary => HasConfigurationWarnings ? $"此订阅有 {ConfigurationWarnings.Count} 个站点暂未适配，请在设置中查看导入说明。" : "";
    public bool HasConfiguredSources => Sources.Count > 0 || _config.LiveSources.Count > 0;
    public string VodSourceSummary => Sources.Count == 0 ? "未配置" : $"已配置（{Sources.Count} 个站点）";
    public string LiveSourceSummary => _config.LiveSources.Count == 0 ? "未配置" : $"{_config.LiveSources.Count} 个直播源";
    public ObservableCollection<HistoryEntry> RecentHistory { get; } = [];
    public ObservableCollection<MediaCard> HomeCards { get; } = [];
    public ObservableCollection<MediaCardRow> HomeCardRows { get; } = [];
    public bool HasRecentHistory => RecentHistory.Count > 0;
    public bool HasHomeRecommendations => HomeCards.Count > 0;
    private int _homeColumns = 5;
    private void NotifyHomeConfiguration()
    {
        OnPropertyChanged(nameof(HomeTitle)); OnPropertyChanged(nameof(HasConfiguredSources));
        OnPropertyChanged(nameof(VodSourceSummary)); OnPropertyChanged(nameof(LiveSourceSummary));
    }
    public void UpdateHomeColumns(double width)
    {
        if (width <= 0) return;
        int columns = Math.Clamp((int)(width / 160), 2, 10);
        if (_homeColumns == columns) return;
        _homeColumns = columns; RebuildHomeRows();
    }
    private void RebuildHomeRows()
    {
        HomeCardRows.Clear();
        foreach (var card in HomeCards)
        {
            if (HomeCardRows.Count == 0 || HomeCardRows[^1].Cards.Count == _homeColumns) HomeCardRows.Add(new(_homeColumns));
            HomeCardRows[^1].Cards.Add(card);
        }
    }
    public void SetHomeRecommendations(IEnumerable<MediaItem> items)
    {
        foreach (var card in HomeCards) card.Dispose();
        HomeCards.Clear();
        foreach (var item in items.Take(40)) HomeCards.Add(new(item));
        RebuildHomeRows(); OnPropertyChanged(nameof(HasHomeRecommendations));
    }
    private int _historyRefreshVersion;
    private async Task RefreshRecentHistoryAsync()
    {
        int version = ++_historyRefreshVersion;
        if (Incognito)
        {
            RecentHistory.Clear(); OnPropertyChanged(nameof(HasRecentHistory)); return;
        }
        var entries = await _store.GetHistoryAsync(_lifetime.Token);
        if (version != _historyRefreshVersion || Incognito || _disposed) return;
        RecentHistory.Clear();
        foreach (var entry in entries.OrderByDescending(x => x.UpdatedAt).Take(8)) RecentHistory.Add(entry);
        OnPropertyChanged(nameof(HasRecentHistory));
    }
    private async Task LoadHomeRecommendationsAsync(IContentProvider provider, CancellationToken token)
    {
        HomeRecommendationStatus = "正在加载更新推荐…";
        try
        {
            var page = await provider.GetHomeAsync(token);
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(provider, _provider)) return;
            SetHomeRecommendations(page.Items);
            HomeRecommendationStatus = HomeCards.Count == 0 ? "当前站点暂无推荐，可进入点播浏览。" : "";
        }
        catch (Exception error) when (error is not OperationCanceledException)
        { HomeRecommendationStatus = $"推荐加载失败：{error.Message}"; }
    }
    [RelayCommand] private async Task ConfigureSourcesAsync()
    { await NavigateCommand.ExecuteAsync("设置"); }
}
