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
    private readonly PlaybackResolutionService _resolution;
    private readonly ProviderFactory _factory;
    private IContentProvider? _provider;
    private CancellationTokenSource? _browse;
    private VodBoxConfig _config = new();
    private MediaDetail? _detail;
    private string? _nextCursor;
    private readonly DispatcherTimer _saveTimer;
    private readonly bool _designMode;
    private readonly ConfigurationRepository _configurations;
    private readonly PreferencesStore _preferencesStore;
    private readonly PlaylistSession _playlist;
    private readonly AggregateSearch _aggregateSearch;
    private readonly EpgService _epg;
    private bool _initialized;
    private CancellationTokenSource? _preferencesSave;
    private Task _preferencesSaveTask = Task.CompletedTask;
    private long _advanceSession;
    public ObservableCollection<SavedConfiguration> SavedConfigurations { get; } = [];
    public ObservableCollection<SearchHit> SearchResults { get; } = [];
    public ObservableCollection<string> SearchErrors { get; } = [];
    public ObservableCollection<string> LiveGroups { get; } = [];
    public ObservableCollection<ProgrammeRow> Programmes { get; } = [];
    public LibVlcEngine Engine { get; } = new();
    public ObservableCollection<ResolverDefinition> Resolvers { get; } = [new() { Id = "_direct", Name = "直接播放", Kind = ResolutionKind.Direct }];
    [ObservableProperty] private ResolverDefinition? _selectedResolver;
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
    [ObservableProperty] private SavedConfiguration? _selectedSavedConfiguration;
    [ObservableProperty] private bool _aggregateResultsVisible;
    [ObservableProperty] private bool _autoNext = true;
    [ObservableProperty] private bool _resumePlayback = true;
    [ObservableProperty] private int _skipIntroSeconds;
    [ObservableProperty] private int _skipOutroSeconds;
    [ObservableProperty] private string _theme = "Dark";
    [ObservableProperty] private string _liveSearch = "";
    [ObservableProperty] private string _selectedLiveGroup = "全部";
    [ObservableProperty] private string _epgStatus = "选择频道查看节目表。";
    public bool IsScrubbing { get; set; }

    public MainViewModel(bool designMode = false)
    {
        _designMode = designMode;
        SelectedResolver = Resolvers[0];
        _store = designMode ? new PreviewStore() : new LibraryStore(Path.Combine(AppPaths.DataDirectory, "library.db"));
        _resolution = new(_http);
        _coordinator = new(Engine, _store, _resolution);
        var host = Path.Combine(AppLayout.PluginHostDirectory, OperatingSystem.IsWindows() ? "VodBox.PluginHost.exe" : "VodBox.PluginHost");
        _factory = new(_http, host, AppLayout.AssetsDirectory);
        _configurations = new(Path.Combine(AppPaths.ConfigurationDirectory, "configurations"));
        _preferencesStore = new(Path.Combine(AppPaths.ConfigurationDirectory, "preferences.json"));
        _playlist = new(_coordinator, _factory);
        _aggregateSearch = new(_factory);
        _epg = new(_http, AppPaths.CacheDirectory);
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
    private async Task RunAsync(Func<Task> action, bool busy = true)
    {
        if (busy) IsBusy = true;
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Status = ex.Message; Console.Error.WriteLine(ex); }
        finally { if (busy) IsBusy = false; }
    }
    [RelayCommand] private Task PlayEpisodeAsync(Episode? episode) => RunAsync(async () =>
    {
        var detail = _detail; var source = SelectedSource; var line = SelectedLine;
        if (detail is null || source is null || line is null || episode is null) return;
        _playingChannel = null;
        long position = SkipIntroSeconds * 1000L;
        if (ResumePlayback)
        {
            var history = (await _store.GetHistoryAsync()).FirstOrDefault(x => x.ConfigId == _config.Id && x.SourceId == source.Id && x.MediaId == detail.Item.Id && x.EpisodeId == episode.Id);
            if (history is not null) position = Math.Max(position, history.PositionMs);
        }
        await _playlist.PlayAsync(source, detail, line, episode, _config.Id, position, _lifetime.Token);
    });
    [RelayCommand] private Task NextEpisodeAsync() => RunAsync(async () => { if (!await _playlist.MoveAsync(1, SkipIntroSeconds * 1000L, _lifetime.Token)) Status = "已经是最后一集。"; });
    [RelayCommand] private Task PreviousEpisodeAsync() => RunAsync(async () => { if (!await _playlist.MoveAsync(-1, SkipIntroSeconds * 1000L, _lifetime.Token)) Status = "已经是第一集。"; });
    public Task OpenFileAsync(string path) => PlayRequestAsync(new() { Uri = new Uri(Path.GetFullPath(path)).AbsoluteUri, Title = Path.GetFileName(path), MediaId = Path.GetFullPath(path) });
    [RelayCommand] private Task OpenUrlAsync() => PlayRequestAsync(new() { Uri = Url.Trim(), Title = Url.Trim(), MediaId = Url.Trim(), ResolverId = SelectedResolver?.Id });
    private Task PlayRequestAsync(PlaybackRequest request) => RunAsync(async () =>
    {
        if (!Uri.TryCreate(request.Uri, UriKind.Absolute, out _)) throw new InvalidDataException("请输入完整媒体 URL。");
        _playlist.Clear(); _playingChannel = null;
        await _coordinator.PlayAsync(_ => Task.FromResult(request), _config.Id, _lifetime.Token);
    });
    [RelayCommand] private Task TogglePauseAsync() => RunAsync(() => Engine.Snapshot.State == PlaybackState.Paused ? Engine.PlayAsync(default) : Engine.PauseAsync(default));
    [RelayCommand] private Task StopAsync() => RunAsync(async () =>
    { _playlist.Clear(); await _coordinator.StopAsync(); Position = 0; Duration = 0; TimeText = "00:00 / 00:00"; NowPlaying = "已停止"; });
    public Task SeekAsync() => RunAsync(() => Engine.SeekAsync(TimeSpan.FromMilliseconds(Position), default), false);
    partial void OnVolumeChanged(double value) { _ = RunAsync(() => Engine.SetVolumeAsync(value / 100, default), false); SchedulePreferencesSave(); }
    partial void OnRateChanged(double value) { _ = RunAsync(() => Engine.SetRateAsync(value, default), false); SchedulePreferencesSave(); }
    partial void OnIncognitoChanged(bool value) => _coordinator.Incognito = value;
    partial void OnSelectedAudioChanged(MediaTrack? value) { if (value is not null) _ = RunAsync(() => Engine.SelectTrackAsync(TrackKind.Audio, value.Id, default)); }
    partial void OnSelectedSubtitleChanged(MediaTrack? value) { if (value is not null) _ = RunAsync(() => Engine.SelectTrackAsync(TrackKind.Subtitle, value.Id, default)); }
    public Task AddSubtitleAsync(string path) => RunAsync(() => Engine.AddSubtitleAsync(path, default));
    [RelayCommand] private Task ResumeHistoryAsync(HistoryEntry? entry) => RunAsync(async () =>
    {
        if (entry is null) return;
        if (entry.SourceId == "local")
        { await PlayRequestAsync(new() { Uri = entry.Uri, Title = entry.Title, MediaId = entry.MediaId, ResolutionKind = entry.ResolutionKind, ResolverId = entry.ResolverId, StartPositionMs = ResumePlayback ? entry.PositionMs : 0 }); return; }
        await OpenStoredItemAsync(entry.ConfigId, entry.SourceId, entry.MediaId);
        var detail = _detail ?? throw new InvalidDataException("无法加载该媒体详情。");
        var line = detail.PlaybackLines.FirstOrDefault(x => x.Episodes.Any(e => e.Id == entry.EpisodeId)) ?? throw new InvalidDataException("记录中的集数已经被移除。");
        SelectedLine = line;
        await _playlist.PlayAsync(SelectedSource!, detail, line, line.Episodes.First(x => x.Id == entry.EpisodeId), _config.Id,
            ResumePlayback ? Math.Max(entry.PositionMs, SkipIntroSeconds * 1000L) : SkipIntroSeconds * 1000L, _lifetime.Token);
    });
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
        if (e.Snapshot.State is PlaybackState.Paused or PlaybackState.Ended) _ = RunAsync(() => _coordinator.SaveProgressAsync(), false);
        bool advance = e.Snapshot.State == PlaybackState.Ended || (SkipOutroSeconds > 0 && e.Snapshot.State == PlaybackState.Playing
            && e.Snapshot.Duration.TotalSeconds > SkipOutroSeconds && e.Snapshot.Position.TotalSeconds >= e.Snapshot.Duration.TotalSeconds - SkipOutroSeconds);
        if (AutoNext && advance && _playlist.HasNext && _advanceSession != e.SessionId)
        { _advanceSession = e.SessionId; _ = NextEpisodeAsync(); }
        if (e.Snapshot.State == PlaybackState.Playing && AudioTracks.Count == 0)
        {
            foreach (var track in Engine.GetTracks(TrackKind.Audio)) AudioTracks.Add(track);
            foreach (var track in Engine.GetTracks(TrackKind.Subtitle)) SubtitleTracks.Add(track);
        }
    });
    public async ValueTask DisposeAsync()
    {
        _disposed = true; _saveTimer.Stop(); Engine.StateChanged -= OnPlaybackState;
        _lifetime.Cancel(); _browse?.Cancel(); _aggregateCancellation?.Cancel(); _epgCancellation?.Cancel();
        _preferencesSave?.Cancel(); await _preferencesSaveTask;
        if (!_designMode && _initialized) await _preferencesStore.SaveAsync(CapturePreferences());
        await _sourceTask; await _sourceGate.WaitAsync();
        try { if (_provider is not null) await _provider.DisposeAsync(); }
        finally { _sourceGate.Release(); }
        await _coordinator.DisposeAsync(); await _resolution.DisposeAsync(); _http.Dispose(); _browse?.Dispose(); _preferencesSave?.Dispose(); _lifetime.Dispose();
    }

}

internal sealed class PreviewStore : ILibraryStore
{
    public Task SaveHistoryAsync(HistoryEntry entry, CancellationToken token = default) => Task.CompletedTask;
    public Task<IReadOnlyList<HistoryEntry>> GetHistoryAsync(CancellationToken token = default) => Task.FromResult<IReadOnlyList<HistoryEntry>>([]);
    public Task SetFavoriteAsync(FavoriteEntry entry, bool favorite, CancellationToken token = default) => Task.CompletedTask;
    public Task<IReadOnlyList<FavoriteEntry>> GetFavoritesAsync(CancellationToken token = default) => Task.FromResult<IReadOnlyList<FavoriteEntry>>([]);
}
