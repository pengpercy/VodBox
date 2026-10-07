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

    [ObservableProperty] private AppPage _page = AppPage.Home;
    [ObservableProperty] private string _clock = "";
    [ObservableProperty] private string _currentSourceName = "未配置";
    [ObservableProperty] private string _searchKeyword = "";
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private bool _busy;

    public HomeViewModel Home { get; }
    public VodViewModel Vod { get; }
    public LiveViewModel Live { get; }
    public SearchViewModel Search { get; }
    public FavoritesViewModel Favorites { get; }
    public HistoryViewModel History { get; }
    public SettingsViewModel Settings { get; }
    public FilesViewModel Files { get; }
    public PlayerViewModel Player { get; }
    public DetailViewModel Detail { get; }

    public MainViewModel(AppServices services)
    {
        _services = services;
        Home = new HomeViewModel(services, this);
        Vod = new VodViewModel(services, this);
        Live = new LiveViewModel(services, this);
        Search = new SearchViewModel(services, this);
        Favorites = new FavoritesViewModel(services, this);
        History = new HistoryViewModel(services, this);
        Settings = new SettingsViewModel(services, this);
        Files = new FilesViewModel(services);
        Player = new PlayerViewModel(services, this);
        Detail = new DetailViewModel(services, this);
        // 本地文件 → 播放器。只在此处订阅一次（MainViewModel 与应用同生命周期），避免 View 层重复订阅泄漏。
        Files.PlayRequested += entry => Player.Play(new PlaybackRequest
        {
            Uri = new Uri(entry.FullPath).AbsoluteUri, // 转义 file:// URI，libmpv 可直接打开
            Title = entry.Name,
            SourceKey = "local",
            MediaId = entry.FullPath,
        });
        UpdateSourceName();
        var timer = new System.Timers.Timer(1000) { AutoReset = true };
        timer.Elapsed += (_, _) => Clock = DateTime.Now.ToString("HH:mm");
        timer.Start();
        Clock = DateTime.Now.ToString("HH:mm");
    }

    public void UpdateSourceName() =>
        CurrentSourceName = _services.Registry.Sources.Count > 0
            ? $"当前源：{_services.Registry.Sources[0].Name}"
            : "未配置";

    /// <summary>跨线程回 UI 线程（播放器事件线程 → UI）。</summary>
    public void RunOnUi(Action action)
    {
        if (uidispatcher is null) { action(); return; }
        uidispatcher.Post(action);
    }
#pragma warning disable CS0414
    private Avalonia.Threading.Dispatcher? uidispatcher;
#pragma warning restore CS0414

    public void AttachDispatcher(Avalonia.Threading.Dispatcher dispatcher) => uidispatcher = dispatcher;

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
