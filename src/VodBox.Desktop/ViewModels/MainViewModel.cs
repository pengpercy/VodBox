using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VodBox.Core;
using VodBox.Desktop.Services;

namespace VodBox.Desktop.ViewModels;

public enum AppPage { Home, Vod, Live, Favorites, History, Settings, Detail }

public sealed partial class MainViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly bool _designTime;
    private readonly Func<string, IContentSource?> _getSource;
    private readonly ILibraryStore _store;

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
        _getSource = getSource;
        _store = store;
        _wallpaperPath=designTime?"":services.Prefs.GetString("ui.wallpaper");
        Home = new HomeViewModel(services, this);
        Vod = new VodViewModel(services, this);
        Live = new LiveViewModel(services, this);
        Search = new SearchViewModel(services, this);
        Favorites = new FavoritesViewModel(services, this);
        History = new HistoryViewModel(services, this);
        Settings = new SettingsViewModel(services, this);
        Detail = new DetailViewModel(getSource, store, this);
        Player = new PlayerViewModel(services, this);
        if (!designTime)
        {
            UpdateSourceName();
        }
    }

    /// <summary>直接播放一个本地媒体文件（拖放与文件列表共用）。</summary>
    /// <summary>
    /// 请求主窗口弹出系统「打开文件」对话框（侧栏「打开文件…」、⌘O/Ctrl+O、菜单栏、媒体收件箱共用）。
    /// 参数为起始目录，null 表示用系统默认位置。VM 不直接碰窗口与存储提供器。
    /// </summary>
    public event Action<string?>? OpenFileRequested;

    public void RequestOpenFile(string? startDirectory = null) => OpenFileRequested?.Invoke(startDirectory);

    [RelayCommand]
    private void OpenFile() => RequestOpenFile();

    /// <summary>
    /// 打开本地文件（拖放与「打开文件」对话框共用）：m3u/m3u8 作为直播源打开，媒体文件直接播放；
    /// 多个文件时播放列表优先。返回是否识别出可处理的文件。
    /// </summary>
    public async Task<bool> OpenLocalFilesAsync(IEnumerable<string?> paths)
    {
        var (intent, path) = VodBox.Infrastructure.DroppedFiles.Classify(paths);
        switch (intent)
        {
            case VodBox.Infrastructure.DroppedIntent.PlayLive:
                await OpenLivePlaylistAsync(path);
                return true;
            case VodBox.Infrastructure.DroppedIntent.PlayMedia:
                PlayLocalFile(path);
                return true;
            default:
                StatusMessage = "请选择 .m3u/.m3u8 直播播放列表或媒体文件";
                return false;
        }
    }

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
        await Settings.StopUpdatesAsync();
        await _services.LocalControl.DisposeAsync();
        Settings.ClearPairingPresentation();
        await Player.CloseCommand.ExecuteAsync(null);
    }

    public void UpdateSourceName() =>
        CurrentSourceName = _services.Registry.VisibleSources.Count > 0
            ? $"当前源：{_services.Registry.VisibleSources[0].Name}"
            : "未配置";

    /// <summary>
    /// 统一的续播入口：历史/收藏里的记录按 source_key 分流。
    /// "live" 是直播的合成键、不在内容源注册表里，交给直播页按频道地址续播；
    /// 其余键才走详情页，避免把直播记录当成点播站点而报「在当前配置中不可用」。
    /// </summary>
    public void ResumeEntry(HistoryEntry entry)
    {
        switch (entry.SourceKey)
        {
            case "live":
                if (Live.TryPlayByUri(entry.MediaId)) return;
                // 频道已不在当前直播源里：给出明确原因，而不是"站点不可用"这种误导性文案。
                // 直播源仍在加载时 TryPlayByUri 已给出等待提示，此处不得覆盖。
                if (!Live.Loading) StatusMessage = $"直播频道「{entry.Title}」不在当前直播源中，无法续播";
                Navigate(AppPage.Live);
                return;
            case "catchup":
                StatusMessage = "回看记录需要所在频道仍在当前直播源中，无法直接续播";
                return;
            case "local":
                PlayLocalFile(entry.MediaId);
                return;
            default:
                Detail.Resume(entry);
                return;
        }
    }

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

    public Avalonia.Thickness PageContentMargin => GetPageContentMargin(Page, OperatingSystem.IsMacOS());

    /// <summary>
    /// 页面伸进标题栏行后会盖住下面的拖拽区（例如收藏页整页是一个滚动区，会吃掉指针）：
    /// 在最上层补一条拖拽区，高度到页头顶边为止，不压页头控件。首页沉浸式整行；未上移的页面不需要。
    /// </summary>
    public double TopDragStripHeight => GetTopDragStripHeight(Page, OperatingSystem.IsMacOS());

    internal static double GetTopDragStripHeight(AppPage page, bool isMacOS) => page switch
    {
        AppPage.Home => TitleBarHeight,
        AppPage.Vod or AppPage.Favorites or AppPage.History => PageHeaderTop(isMacOS),
        _ => 0,
    };

    /// <summary>设置改为独立窗口后，由主窗口订阅此事件来打开窗口（VM 不直接创建窗口）。</summary>
    public event Action? SettingsRequested;

    /// <summary>
    /// 搜索改为独立窗口后，由主窗口订阅此事件来打开/复用窗口（VM 不直接创建窗口）。
    /// 参数是已 trim 的非空关键词；窗口的去重与激活由主窗口负责。
    /// </summary>
    public event Action<string>? SearchRequested;

    /// <summary>
    /// 打开设置窗口。设置不再是主窗口的一页，所以这里**不切页**（切过去只会留下空白内容区），
    /// 而是刷新站点列表并请求主窗口开窗口。侧边栏、远程配置、推送回调都走这一条路径。
    /// </summary>
    public void OpenSettings()
    {
        if (!_designTime) Settings.RefreshSites();
        SettingsRequested?.Invoke();
    }

    /// <summary>主窗口标题栏行高（与 MainWindow 的第一行一致）。</summary>
    internal const double TitleBarHeight = 44;
    /// <summary>
    /// Windows/Linux 窗口按钮（右上角）的高度：MainWindow 在这两个平台把标题栏高度提示设为 32（Win11 标准高度），
    /// 按钮只占顶部 32，下面这一截标题栏行可以给页面用。macOS 的交通灯在左侧侧栏上方，不受影响。
    /// </summary>
    public const double CaptionButtonsHeight = 32;
    /// <summary>Windows/Linux 窗口按钮区宽度（三个 46 宽的按钮，Win11 标准）。</summary>
    public const double CaptionButtonsWidth = 138;
    /// <summary>点播/收藏/历史页自带的顶边距。</summary>
    private const double PageHeaderInset = 12;

    /// <summary>
    /// 点播/收藏/历史的页头顶边在窗口中的位置：macOS 与直播页工具栏齐平（y=20）；
    /// Windows/Linux 落在窗口按钮下方并留 4px，页头右侧的控件不会压到最小化/最大化/关闭按钮。
    /// </summary>
    internal static double PageHeaderTop(bool isMacOS) => isMacOS ? TitleBarHeight - 24 : CaptionButtonsHeight + 4;

    // 首页沉浸式：内容上移一整行标题栏高度，首屏横幅从窗口顶边开始，滚动时内容从标题栏下穿过
    // （标题栏拖拽区仍在最上层；滚动后标题栏处渐显毛玻璃，保证窗口按钮可读）。
    // 点播/收藏/历史整体上移进标题栏行（见 PageHeaderTop）；直播页保持原有位置：macOS 上移 24，其他平台不动。
    internal static Avalonia.Thickness GetPageContentMargin(AppPage page, bool isMacOS) => page switch
    {
        AppPage.Home => new(0, -TitleBarHeight, 0, 0),
        AppPage.Vod or AppPage.Favorites or AppPage.History => new(0, PageHeaderTop(isMacOS) - TitleBarHeight - PageHeaderInset, 0, 0),
        AppPage.Live when isMacOS => new(0, -24, 0, 0),
        _ => new(0),
    };

    partial void OnPageChanged(AppPage value)
    {
        OnPropertyChanged(nameof(PageContentMargin));
        OnPropertyChanged(nameof(TopDragStripHeight));
        if (value != AppPage.Detail) Detail.CancelPending();
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

    /// <summary>另一个窗口（搜索窗口）自用的详情：同一套站点与存储，但打开时不切主窗口的页面。</summary>
    internal DetailViewModel CreateDetachedDetail() => new(_getSource, _store, this, navigateMainWindow: false);

    [RelayCommand]
    private void GoHome() => Navigate(AppPage.Home);

    [RelayCommand]
    private void GoVod() => Navigate(AppPage.Vod);

    [RelayCommand]
    private void GoLive() => Navigate(AppPage.Live);

    [RelayCommand]
    private void GoFavorites() => Navigate(AppPage.Favorites);

    [RelayCommand]
    private void GoHistory() => Navigate(AppPage.History);

    [RelayCommand]
    private void GoSettings() => OpenSettings();

    /// <summary>
    /// 侧栏搜索框提交（回车）：发起全站聚合搜索请求。结果不落在主窗口的页面里，而是由主窗口打开/复用
    /// 独立搜索结果窗口，避免进入空白搜索页，也避免主窗口被搜索结果页占住。
    /// </summary>
    [RelayCommand]
    private void SubmitSearch() => RequestSearch(SearchKeyword);

    /// <summary>请求打开（或复用）搜索窗口并对关键词发起搜索；空白关键词直接忽略。</summary>
    public void RequestSearch(string keyword)
    {
        var query = keyword.Trim();
        if (query.Length == 0) return;
        SearchRequested?.Invoke(query);
    }
}
