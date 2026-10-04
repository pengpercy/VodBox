using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Threading;
using VodBox.Core;
using VodBox.Application;
using VodBox.Infrastructure;
using VodBox.Playback.LibVlc;

namespace VodBox.Desktop;

public partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly ILibraryStore _store;
    private readonly PlaybackCoordinator _coordinator;
    private readonly ProviderFactory _factory;
    private IContentProvider? _provider;
    private CancellationTokenSource? _browse;
    private VodBoxConfig _config = new();
    private MediaDetail? _detail;
    private string? _nextCursor;
    private readonly DispatcherTimer _saveTimer;
    public LibVlcEngine Engine { get; } = new();
    public ObservableCollection<SourceDefinition> Sources { get; } = [];
    public ObservableCollection<Category> Categories { get; } = [];
    public ObservableCollection<MediaItem> Items { get; } = [];
    public ObservableCollection<Episode> Episodes { get; } = [];
    public ObservableCollection<PlaybackLine> Lines { get; } = [];
    public ObservableCollection<LiveChannel> Channels { get; } = [];
    public ObservableCollection<HistoryEntry> History { get; } = [];
    public ObservableCollection<FavoriteEntry> Favorites { get; } = [];
    public ObservableCollection<MediaTrack> AudioTracks { get; } = [];
    public ObservableCollection<MediaTrack> SubtitleTracks { get; } = [];

    [ObservableProperty] private string _url = "";
    [ObservableProperty] private string _configLocation = "";
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _status = "添加配置，或打开本地媒体开始播放。";
    [ObservableProperty] private string _nowPlaying = "尚未播放";
    [ObservableProperty] private string _description = "选择内容查看详情和播放线路。";
    [ObservableProperty] private string _pageTitle = "发现";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _showLibrary = true;
    [ObservableProperty] private bool _showLive;
    [ObservableProperty] private bool _showHistory;
    [ObservableProperty] private bool _showFavorites;
    [ObservableProperty] private bool _showSettings;
    [ObservableProperty] private bool _incognito;
    [ObservableProperty] private double _position;
    [ObservableProperty] private double _duration;
    [ObservableProperty] private double _volume = 80;
    [ObservableProperty] private double _rate = 1;
    [ObservableProperty] private string _timeText = "00:00 / 00:00";
    [ObservableProperty] private SourceDefinition? _selectedSource;
    [ObservableProperty] private Category? _selectedCategory;
    [ObservableProperty] private MediaItem? _selectedItem;
    [ObservableProperty] private PlaybackLine? _selectedLine;
    [ObservableProperty] private MediaTrack? _selectedAudio;
    [ObservableProperty] private MediaTrack? _selectedSubtitle;
    public bool IsScrubbing { get; set; }

    public MainViewModel(bool designMode = false)
    {
        _store = designMode ? new PreviewStore() : new LibraryStore(Path.Combine(AppPaths.DataDirectory, "library.db"));
        _coordinator = new(Engine, _store);
        var host = Path.Combine(AppLayout.PluginHostDirectory, OperatingSystem.IsWindows() ? "VodBox.PluginHost.exe" : "VodBox.PluginHost");
        _factory = new(_http, host, AppLayout.AssetsDirectory);
        Engine.StateChanged += OnPlaybackState;
        _coordinator.RequestChanged += (_, request) => Dispatcher.UIThread.Post(() =>
        {
            NowPlaying = request.Title;
            SelectedAudio = null; SelectedSubtitle = null;
            AudioTracks.Clear(); SubtitleTracks.Clear();
            Position = 0; Duration = 0;
        });
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _saveTimer.Tick += async (_, _) => await RunAsync(() => _coordinator.SaveProgressAsync(), busy: false);
        if (!designMode) _saveTimer.Start();
    }
    public async Task InitializeAsync()
    {
        var saved = Path.Combine(AppPaths.DataDirectory, "config.json");
        ConfigLocation = File.Exists(saved) ? saved : Path.Combine(AppLayout.AssetsDirectory, "examples", "vodbox.json");
        if (File.Exists(ConfigLocation)) await LoadConfigAsync();
    }
    private async Task RunAsync(Func<Task> action, bool busy = true)
    {
        if (busy) IsBusy = true;
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Status = ex.Message; Console.Error.WriteLine(ex); }
        finally { if (busy) IsBusy = false; }
    }
    [RelayCommand] private Task LoadConfigAsync() => RunAsync(async () =>
    {
        var config = await new ConfigLoader(_http).LoadAsync(ConfigLocation);
        _config = config;
        Sources.Clear(); foreach (var source in config.Sources) Sources.Add(source);
        SelectedSource = Sources.FirstOrDefault();
        Channels.Clear();
        foreach (var live in config.LiveSources)
        {
            var uri = new Uri(live.Uri); var text = uri.IsFile ? await File.ReadAllTextAsync(uri.LocalPath) : await _http.GetStringAsync(uri);
            foreach (var channel in LiveParser.Parse(text)) Channels.Add(channel);
        }
        Status = $"已加载 {Sources.Count} 个内容源、{Channels.Count} 个直播频道。";
    });
    partial void OnSelectedSourceChanged(SourceDefinition? value) { if (value is not null) _ = SwitchSourceAsync(value); }
    private Task SwitchSourceAsync(SourceDefinition source) => RunAsync(async () =>
    {
        _browse?.Cancel(); _browse = new(); var token = _browse.Token;
        if (_provider is not null) await _provider.DisposeAsync();
        _provider = _factory.Create(source);
        var categories = await _provider.GetCategoriesAsync(token);
        if (token.IsCancellationRequested) return;
        Categories.Clear(); Categories.Add(new("", "全部")); foreach (var category in categories) Categories.Add(category);
        SelectedCategory = Categories.First();
    });
    partial void OnSelectedCategoryChanged(Category? value) { if (value is not null) _ = BrowseAsync(); }
    [RelayCommand] private Task BrowseAsync() => RunAsync(async () =>
    {
        if (_provider is null) return;
        var page = await _provider.GetItemsAsync(string.IsNullOrEmpty(SelectedCategory?.Id) ? null : SelectedCategory.Id, null, _browse?.Token ?? default);
        Items.Clear(); foreach (var item in page.Items) Items.Add(item); _nextCursor = page.NextCursor;
    });
    [RelayCommand] private Task LoadMoreAsync() => RunAsync(async () =>
    {
        if (_provider is null || _nextCursor is null) return;
        var page = await _provider.GetItemsAsync(string.IsNullOrEmpty(SelectedCategory?.Id) ? null : SelectedCategory.Id, _nextCursor, _browse?.Token ?? default);
        foreach (var item in page.Items) Items.Add(item); _nextCursor = page.NextCursor;
    });
    [RelayCommand] private Task SearchAsync() => RunAsync(async () =>
    {
        if (_provider is null) return;
        var page = await _provider.SearchAsync(SearchText, _browse?.Token ?? default);
        Items.Clear(); foreach (var item in page.Items) Items.Add(item); _nextCursor = page.NextCursor;
    });
    partial void OnSelectedItemChanged(MediaItem? value) { if (value is not null) _ = DetailAsync(value); }
    private Task DetailAsync(MediaItem item) => RunAsync(async () =>
    {
        if (_provider is null) return;
        var detail = await _provider.GetDetailAsync(item.Id, _browse?.Token ?? default);
        if (SelectedItem?.Id != item.Id) return;
        _detail = detail; Description = detail.Description;
        Lines.Clear(); foreach (var line in detail.PlaybackLines) Lines.Add(line); SelectedLine = Lines.FirstOrDefault();
    });
    partial void OnSelectedLineChanged(PlaybackLine? value)
    { Episodes.Clear(); if (value is not null) foreach (var episode in value.Episodes) Episodes.Add(episode); }
    [RelayCommand] private Task PlayEpisodeAsync(Episode? episode) => RunAsync(async () =>
    {
        if (_provider is null || _detail is null || episode is null) return;
        var provider = _provider; var id = _detail.Item.Id;
        await _coordinator.PlayAsync(token => provider.ResolvePlaybackAsync(id, episode.Id, token), _config.Id);
    });
    public Task OpenFileAsync(string path) => PlayRequestAsync(new() { Uri = new Uri(Path.GetFullPath(path)).AbsoluteUri, Title = Path.GetFileName(path), MediaId = Path.GetFullPath(path) });
    [RelayCommand] private Task OpenUrlAsync() => PlayRequestAsync(new() { Uri = Url.Trim(), Title = Url.Trim(), MediaId = Url.Trim() });
    private Task PlayRequestAsync(PlaybackRequest request) => RunAsync(async () =>
    {
        if (!Uri.TryCreate(request.Uri, UriKind.Absolute, out _)) throw new InvalidDataException("请输入完整媒体 URL。");
        await _coordinator.PlayAsync(_ => Task.FromResult(request), _config.Id);
    });
    [RelayCommand] private Task TogglePauseAsync() => RunAsync(() => Engine.Snapshot.State == PlaybackState.Paused ? Engine.PlayAsync(default) : Engine.PauseAsync(default));
    [RelayCommand] private Task StopAsync() => RunAsync(() => _coordinator.StopAsync());
    public Task SeekAsync() => RunAsync(() => Engine.SeekAsync(TimeSpan.FromMilliseconds(Position), default), false);
    partial void OnVolumeChanged(double value) => _ = RunAsync(() => Engine.SetVolumeAsync(value / 100, default), false);
    partial void OnRateChanged(double value) => _ = RunAsync(() => Engine.SetRateAsync(value, default), false);
    partial void OnIncognitoChanged(bool value) => _coordinator.Incognito = value;
    partial void OnSelectedAudioChanged(MediaTrack? value) { if (value is not null) _ = RunAsync(() => Engine.SelectTrackAsync(TrackKind.Audio, value.Id, default)); }
    partial void OnSelectedSubtitleChanged(MediaTrack? value) { if (value is not null) _ = RunAsync(() => Engine.SelectTrackAsync(TrackKind.Subtitle, value.Id, default)); }
    public Task AddSubtitleAsync(string path) => RunAsync(() => Engine.AddSubtitleAsync(path, default));
    [RelayCommand] private Task PlayChannelAsync(LiveChannel? channel) => channel is null ? Task.CompletedTask : PlayRequestAsync(new() { Uri = channel.Uris.First(), Title = channel.Name, IsLive = true, SourceId = "live" });
    [RelayCommand] private Task ResumeHistoryAsync(HistoryEntry? entry) => entry is null ? Task.CompletedTask : PlayRequestAsync(new() { Uri = entry.Uri, Title = entry.Title, SourceId = entry.SourceId, MediaId = entry.MediaId, EpisodeId = entry.EpisodeId, StartPositionMs = entry.PositionMs });
    [RelayCommand] private Task ToggleFavoriteAsync() => RunAsync(async () =>
    {
        if (SelectedItem is null || SelectedSource is null) return;
        var entry = new FavoriteEntry(_config.Id, SelectedSource.Id, SelectedItem.Id, SelectedItem.Title);
        bool exists = (await _store.GetFavoritesAsync()).Any(x => x.ConfigId == entry.ConfigId && x.SourceId == entry.SourceId && x.MediaId == entry.MediaId);
        await _store.SetFavoriteAsync(entry, !exists); Status = exists ? "已取消收藏。" : "已加入收藏。";
    });
    [RelayCommand] private Task NavigateAsync(string page) => RunAsync(async () =>
    {
        ShowLibrary = page == "发现"; ShowLive = page == "直播"; ShowHistory = page == "历史"; ShowFavorites = page == "收藏"; ShowSettings = page == "设置"; PageTitle = page;
        if (ShowHistory) { History.Clear(); foreach (var entry in await _store.GetHistoryAsync()) History.Add(entry); }
        if (ShowFavorites) { Favorites.Clear(); foreach (var entry in await _store.GetFavoritesAsync()) Favorites.Add(entry); }
    });
    private void OnPlaybackState(object? sender, PlaybackEvent e) => Dispatcher.UIThread.Post(() =>
    {
        if (e.SessionId != _coordinator.SessionId) return;
        if (!IsScrubbing) Position = e.Snapshot.Position.TotalMilliseconds;
        Duration = e.Snapshot.Duration.TotalMilliseconds;
        TimeText = $"{e.Snapshot.Position:hh\\:mm\\:ss} / {e.Snapshot.Duration:hh\\:mm\\:ss}";
        if (e.Snapshot.Error is not null) Status = e.Snapshot.Error;
        if (e.Snapshot.State == PlaybackState.Playing && AudioTracks.Count == 0)
        {
            foreach (var track in Engine.GetTracks(TrackKind.Audio)) AudioTracks.Add(track);
            foreach (var track in Engine.GetTracks(TrackKind.Subtitle)) SubtitleTracks.Add(track);
        }
    });
    public async ValueTask DisposeAsync()
    {
        _saveTimer.Stop(); _browse?.Cancel(); Engine.StateChanged -= OnPlaybackState;
        if (_provider is not null) await _provider.DisposeAsync();
        await _coordinator.DisposeAsync(); _http.Dispose(); _browse?.Dispose();
    }
}

internal sealed class PreviewStore : ILibraryStore
{
    public Task SaveHistoryAsync(HistoryEntry entry, CancellationToken token = default) => Task.CompletedTask;
    public Task<IReadOnlyList<HistoryEntry>> GetHistoryAsync(CancellationToken token = default) => Task.FromResult<IReadOnlyList<HistoryEntry>>([]);
    public Task SetFavoriteAsync(FavoriteEntry entry, bool favorite, CancellationToken token = default) => Task.CompletedTask;
    public Task<IReadOnlyList<FavoriteEntry>> GetFavoritesAsync(CancellationToken token = default) => Task.FromResult<IReadOnlyList<FavoriteEntry>>([]);
}
