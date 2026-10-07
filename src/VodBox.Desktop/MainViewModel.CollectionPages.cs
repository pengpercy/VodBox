using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VodBox.Core;

namespace VodBox.Desktop;

public partial class MainViewModel
{
    [ObservableProperty] private bool _showSearch;
    [ObservableProperty] private bool _searchSubmitted;
    [ObservableProperty] private string _searchPageStatus = "输入关键词搜索所有站点。";
    [ObservableProperty] private SourceDefinition? _searchSourceFilter;
    public ObservableCollection<string> SearchHistory { get; } = [];
    public ObservableCollection<string> SearchSuggestions { get; } = [];
    public ObservableCollection<SourceDefinition> SearchSourceFilters { get; } = [];
    public ObservableCollection<CollectionCard> FilteredSearchCards { get; } = [];
    public ObservableCollection<CollectionCard> FavoriteCards { get; } = [];
    public ObservableCollection<CollectionCard> HistoryCards { get; } = [];
    public bool HasFavoriteCards => FavoriteCards.Count > 0;
    public bool HasHistoryCards => HistoryCards.Count > 0;
    public bool HasSearchHistory => SearchHistory.Count > 0;
    public bool HasSearchSuggestions => SearchSuggestions.Count > 0;
    private readonly List<CollectionCard> _searchCards = [];
    private void InitializeCollectionPages()
    {
        SearchHistory.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasSearchHistory));
        SearchSuggestions.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasSearchSuggestions));
        FavoriteCards.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasFavoriteCards));
        HistoryCards.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasHistoryCards));
        Favorites.CollectionChanged += (_, _) => RebuildFavoriteCards();
        History.CollectionChanged += (_, _) => RebuildHistoryCards();
        SearchResults.CollectionChanged += (_, args) =>
        {
            if (args.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add && args.NewItems is not null)
            {
                foreach (SearchHit hit in args.NewItems)
                {
                    var card = new CollectionCard(hit.Item, hit.SourceName, hit.Item.Remarks ?? "", searchHit: hit);
                    _searchCards.Add(card);
                    if (SearchSourceFilter is null || SearchSourceFilter.Id == "_all" || SearchSourceFilter.Id == hit.Source.Id) FilteredSearchCards.Add(card);
                }
            }
            else RebuildSearchCards();
        };
    }
    private string StoredSourceName(string id, string? name) => name ?? Sources.FirstOrDefault(x => x.Id == id)?.Name ?? (id == "local" ? "本地媒体" : "已保存站点");
    private string? StoredPoster(string id, string? poster) => poster ?? Cards.Concat(HomeCards).FirstOrDefault(x => x.Item.Id == id)?.Item.Poster;
    private static void ClearCards(ObservableCollection<CollectionCard> cards)
    { foreach (var card in cards) card.Card.Dispose(); cards.Clear(); }
    private void RebuildFavoriteCards()
    {
        ClearCards(FavoriteCards);
        foreach (var entry in Favorites) FavoriteCards.Add(new(new(entry.MediaId, entry.Title, StoredPoster(entry.MediaId, entry.Poster)), StoredSourceName(entry.SourceId, entry.SourceName), favorite: entry));
    }
    private void RebuildHistoryCards()
    {
        ClearCards(HistoryCards);
        foreach (var entry in History.GroupBy(x => (x.ConfigId, x.SourceId, x.MediaId)).Select(group => group.OrderByDescending(x => x.UpdatedAt).First()).OrderByDescending(x => x.UpdatedAt))
            HistoryCards.Add(new(new(entry.MediaId, entry.Title, StoredPoster(entry.MediaId, entry.Poster)), StoredSourceName(entry.SourceId, entry.SourceName), entry.PositionMs > 0 ? $"已看到 {TimeSpan.FromMilliseconds(entry.PositionMs):hh\\:mm\\:ss}" : "尚未播放 / 已看完", history: entry));
    }
    private void RebuildSearchCards()
    {
        foreach (var card in _searchCards) card.Card.Dispose(); _searchCards.Clear();
        foreach (var hit in SearchResults) _searchCards.Add(new(hit.Item, hit.SourceName, hit.Item.Remarks ?? "", searchHit: hit));
        ApplySearchFilter();
    }
    partial void OnSearchSourceFilterChanged(SourceDefinition? value) => ApplySearchFilter();
    private void ApplySearchFilter()
    {
        FilteredSearchCards.Clear();
        foreach (var card in _searchCards.Where(x => SearchSourceFilter is null || SearchSourceFilter.Id == "_all" || x.SearchHit?.Source.Id == SearchSourceFilter.Id)) FilteredSearchCards.Add(card);
    }
    private void RebuildSearchSuggestions()
    { SearchSuggestions.Clear(); foreach (var title in HomeCards.Select(x => x.Title).Distinct().Take(20)) SearchSuggestions.Add(title); }
    [RelayCommand] private Task SubmitSearchAsync(string? keyword) => RunAsync(async () =>
    {
        if (keyword is not null) SearchText = keyword;
        SearchText = SearchText.Trim(); if (SearchText.Length == 0) return;
        if (!Incognito) { SearchHistory.Remove(SearchText); SearchHistory.Insert(0, SearchText); while (SearchHistory.Count > 20) SearchHistory.RemoveAt(20); SchedulePreferencesSave(); }
        SearchSubmitted = true;
        SearchSourceFilters.Clear(); SearchSourceFilters.Add(new() { Id = "_all", Name = "全部" });
        foreach (var source in Sources) SearchSourceFilters.Add(source);
        SearchSourceFilter = SearchSourceFilters[0];
        SearchPageStatus = Sources.Count == 0 ? "尚未配置播放源，请先在设置中配置。" : "正在搜索…";
        await SearchAllCommand.ExecuteAsync(null);
        SearchPageStatus = $"找到 {SearchResults.Count} 条结果" + (SearchErrors.Count > 0 ? $"，{SearchErrors.Count} 个站点未完成搜索。" : "。");
        if (Sources.Count == 0) SearchPageStatus = "尚未配置播放源，请先在设置中配置。";
    });
    [RelayCommand] private void ShowPlayer() => ShowPlaybackPage = true;
    [RelayCommand] private void PlaybackBack() => ShowPlaybackPage = false;
    [RelayCommand] private Task SearchBackAsync()
    {
        if (!SearchSubmitted) return NavigateCommand.ExecuteAsync("首页");
        CancelSearchCommand.Execute(null); SearchSubmitted = false; return Task.CompletedTask;
    }
    [RelayCommand] private void ClearSearchHistory() { SearchHistory.Clear(); SchedulePreferencesSave(); }
    [RelayCommand] private Task RefreshFavoritesAsync() => NavigateCommand.ExecuteAsync("收藏");
    [RelayCommand] private Task RefreshHistoryAsync() => NavigateCommand.ExecuteAsync("历史");
    [RelayCommand] private Task ClearFavoritesAsync() => RunAsync(async () =>
    { foreach (var entry in Favorites.ToArray()) await _store.SetFavoriteAsync(entry, false, _lifetime.Token); Favorites.Clear(); });
    private void DisposeCollectionCards()
    { ClearCards(FavoriteCards); ClearCards(HistoryCards); foreach (var card in _searchCards) card.Card.Dispose(); }
}
