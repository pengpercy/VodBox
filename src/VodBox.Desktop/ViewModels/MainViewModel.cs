using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VodBox.Core;
using VodBox.Desktop.Services;

namespace VodBox.Desktop.ViewModels;

public enum AppPage { Home, Vod, Live, Search, Favorites, History, Files, Settings, Detail }

public sealed partial class MainViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly bool _designTime;

    [ObservableProperty] private AppPage _page = AppPage.Home;
    [ObservableProperty] private string _currentSourceName = "未配置";
    [ObservableProperty] private string _searchKeyword = "";
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string _wallpaperPath="";

    public HomeViewModel Home { get; }
    public VodViewModel Vod { get; }
    public LiveViewModel Live { get; }
    public SearchViewModel Search { get; }
    public FavoritesViewModel Favorites { get; }
    public HistoryViewModel History { get; }
    public SettingsViewModel Settings { get; }
    public FilesViewModel Files { get; }
    public PlayerViewModel Player { get; set; }
    public DetailViewModel Detail { get; }

    /// <summary>设计时构造：纯内存 VM 图，不枚举文件系统（Files 初始为空目录）、不挂播放管线（填充发生在设置 Player 之后）。</summary>
    public MainViewModel(AppServices services, bool designTime)
        : this(services, designTime, services.Registry.Get, services.Store)
    {
    }

    internal MainViewModel(AppServices services, bool designTime, Func<string, IContentSource?> getSource, ILibraryStore store)
    {
        _services = services;
        _designTime = designTime;
        _wallpaperPath=designTime?"":services.Prefs.GetString("ui.wallpaper");
        Home = new HomeViewModel(services, this);
        Vod = new VodViewModel(services, this);
        Live = new LiveViewModel(services, this);
        Search = new SearchViewModel(services, this);
        Favorites = new FavoritesViewModel(services, this);
        History = new HistoryViewModel(services, this);
        Settings = new SettingsViewModel(services, this);
        Files = new FilesViewModel(services, designTime);
        Detail = new DetailViewModel(getSource, store, this);
        Player = new PlayerViewModel(services, this);
        if (!designTime)
        {
            // 本地文件 → 播放器。只在此处订阅一次（MainViewModel 与应用同生命周期），避免 View 层重复订阅泄漏。
            Files.PlayRequested += entry => Player.Play(new PlaybackRequest
            {
                Uri = new Uri(entry.FullPath).AbsoluteUri, // 转义 file:// URI，libmpv 可直接打开
                Title = entry.Name,
                SourceKey = "local",
                MediaId = entry.FullPath,
            });
            // 本地 m3u 播放列表 → 直播视图（文件名会作为直播源标签显示）。
            Files.OpenLiveRequested += path => _ = OpenLivePlaylistAsync(path);
            UpdateSourceName();
        }
    }

    /// <summary>直接播放一个本地媒体文件（拖放与文件列表共用）。</summary>
    public void PlayLocalFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            VodBox.Core.VodBoxLog.Warn("local", $"本地文件不存在，忽略：{path}");
            return;
        }
        VodBox.Core.VodBoxLog.Event("local", "play-file", ("path", path));
        Player.Play(new PlaybackRequest
        {
            Uri = new Uri(path).AbsoluteUri,
            Title = Path.GetFileName(path),
            SourceKey = "local",
            MediaId = path,
        });
    }

    /// <summary>把本地播放列表应用为当前直播源并切到直播页；失败时把原因写进状态栏。</summary>
    public async Task OpenLivePlaylistAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            VodBox.Core.VodBoxLog.Info("live", $"打开本地直播播放列表：{path}");
            var loaded = await Live.ApplyConfigurationAsync(path);
            if (!loaded) return;
            Navigate(AppPage.Live);
            StatusMessage = $"已打开直播播放列表：{Path.GetFileName(path)}";
        }
        catch (Exception error)
        {
            VodBox.Core.VodBoxLog.Error("live", $"打开本地直播播放列表失败：{path}", error);
            StatusMessage = $"打开直播播放列表失败：{error.Message}";
        }
    }

    public MainViewModel(AppServices services) : this(services, designTime: false)
    {
    }

    public async Task ShutdownAsync()
    {
        Detail.CancelPending();Vod.CancelPending();Search.CancelPending();Live.CancelPending();Settings.CancelSubtitleSearch();
        Favorites.CancelPending();History.CancelPending();
        await _services.LocalControl.DisposeAsync();
        Settings.ClearPairingPresentation();
        await Player.CloseCommand.ExecuteAsync(null);
    }

    public void UpdateSourceName() =>
        CurrentSourceName = _services.Registry.VisibleSources.Count > 0
            ? $"当前源：{_services.Registry.VisibleSources[0].Name}"
            : "未配置";

    /// <summary>跨线程回 UI 线程（播放器事件线程 → UI）。</summary>
    public void RunOnUi(Action action)
    {
        if (uidispatcher is null || uidispatcher.CheckAccess()) { action(); return; }
        uidispatcher.Post(action);
    }
    /// <summary>等待 UI 更新完成，供异步请求在回填前检查代次。</summary>
    public async Task RunOnUiAsync(Action action)
    {
        if (uidispatcher is null || uidispatcher.CheckAccess()) { action(); return; }
        await uidispatcher.InvokeAsync(action);
    }

    private Avalonia.Threading.Dispatcher? uidispatcher;

    public void AttachDispatcher(Avalonia.Threading.Dispatcher dispatcher) => uidispatcher = dispatcher;

    partial void OnPageChanged(AppPage value)
    {
        if (value != AppPage.Detail) Detail.CancelPending();
        if (value == AppPage.Settings && !_designTime) Settings.RefreshSites();
        if (_designTime) return; // 设计时假数据已就位，不触网、不覆盖
        if (value != AppPage.Favorites) Favorites.CancelPending();
        if (value != AppPage.History) History.CancelPending();
        // 列表页在导航进入时才发起加载（首屏只预载首页/直播），各自做加载过渡与代次门控。
        switch (value)
        {
            case AppPage.Vod: Vod.EnsureLoaded(); break;
            case AppPage.Favorites: Favorites.EnsureLoaded(); break;
            case AppPage.History: History.EnsureLoaded(); break;
        }
    }

    public void Navigate(AppPage page) => Page = page;

    [RelayCommand]
    private void GoHome() => Navigate(AppPage.Home);

    [RelayCommand]
    private void GoVod() => Navigate(AppPage.Vod);

    [RelayCommand]
    private void GoLive() => Navigate(AppPage.Live);

    [RelayCommand]
    private void GoSearch() => Navigate(AppPage.Search);

    [RelayCommand]
    private void GoFavorites() => Navigate(AppPage.Favorites);

    [RelayCommand]
    private void GoHistory() => Navigate(AppPage.History);

    [RelayCommand]
    private void GoFiles() => Navigate(AppPage.Files);

    [RelayCommand]
    private void GoSettings() => Navigate(AppPage.Settings);

    [RelayCommand]
    private void SubmitSearch()
    {
        if (string.IsNullOrWhiteSpace(SearchKeyword)) return;
        Search.Keyword = SearchKeyword;
        Search.RunSearchCommand.Execute(null);
        Navigate(AppPage.Search);
    }
}
