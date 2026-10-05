using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using VodBox.Core;

namespace VodBox.Desktop;

public partial class MainViewModel
{
    public string DetailTitle => SelectedItem?.Title ?? "影片详情";
    public string DetailRemarks => SelectedItem?.Remarks ?? "";
    public string DetailSource => SelectedSource?.Name ?? "本地媒体";
    [ObservableProperty] private string _episodeSearch = "";
    [ObservableProperty] private bool _reverseEpisodes;
    [ObservableProperty] private IReadOnlyList<Episode> _visibleEpisodes = [];
    [ObservableProperty] private string _episodeCountText = "共 0 集";
    private bool _episodeRefreshQueued;

    private void InitializeEpisodeBrowser() => Episodes.CollectionChanged += (_, _) =>
    {
        // A line may add thousands of episodes; filter once after the complete batch.
        if (_episodeRefreshQueued) return;
        _episodeRefreshQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _episodeRefreshQueued = false;
            if (!_disposed) RefreshVisibleEpisodes();
        }, DispatcherPriority.Background);
    };
    partial void OnEpisodeSearchChanged(string value) => RefreshVisibleEpisodes();
    partial void OnReverseEpisodesChanged(bool value) => RefreshVisibleEpisodes();
    private void RefreshVisibleEpisodes()
    {
        string query = EpisodeSearch.Trim();
        IEnumerable<Episode> episodes = Episodes;
        if (query.Length > 0) episodes = episodes.Where(episode => episode.Title.Contains(query, StringComparison.OrdinalIgnoreCase));
        if (ReverseEpisodes) episodes = episodes.Reverse();
        VisibleEpisodes = episodes.ToArray();
        EpisodeCountText = query.Length == 0 ? $"共 {Episodes.Count} 集" : $"找到 {VisibleEpisodes.Count} / {Episodes.Count} 集";
    }
}
