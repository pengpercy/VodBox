using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using VodBox.Desktop.Services;
using VodBox.Infrastructure;
using VodBox.Desktop.ViewModels;

namespace VodBox.Desktop;

public partial class MainWindow : Window
{
    private PlayerViewModel? _observedPlayer;
    internal Views.PlayerWindow? PlaybackWindow { get; private set; }
    private Views.SettingsWindow? _settingsWindow;
    private Services.SearchWindowService? _searchWindow;
    private bool _shutdownStarted;
    private bool _shutdownComplete;
    public MainWindow()
    {
        InitializeComponent();
        WindowsResizeFrame.Attach(this);
        Closed += (_, _) =>
        {
            _searchWindow?.Close();
            if (DataContext is MainViewModel main)
            {
                main.SettingsRequested -= ShowSettingsWindow;
                main.SearchRequested -= OnSearchRequested;
                main.OpenFileRequested -= OnOpenFileRequested;
            }
            StopObservingPlayer();
        };
        Closing+=OnClosing;
        // 拖放：m3u/m3u8 作为直播源打开，媒体文件直接播放。
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    /// <summary>拖入内容不可用时把 DragEffects 置为 None，光标会显示“禁止”。</summary>
    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var (intent, _) = DroppedFiles.Classify(DroppedPaths(e));
        e.DragEffects = intent == DroppedIntent.None ? DragDropEffects.None : DragDropEffects.Copy;
        e.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        if (DataContext is not MainViewModel main) return;
        var paths = DroppedPaths(e);
        var (intent, path) = DroppedFiles.Classify(paths);
        VodBox.Core.VodBoxLog.Event("drop", "files", ("intent", intent.ToString()), ("path", path));
        if (!await main.OpenLocalFilesAsync(paths)) main.StatusMessage = "请拖入 .m3u/.m3u8 直播播放列表或媒体文件";
    }

    /// <summary>系统「打开文件」对话框：只列可播放的媒体与 m3u 播放列表；选中后与拖放走同一条打开路径。</summary>
    private async void OnOpenFileRequested(string? startDirectory)
    {
        if (DataContext is not MainViewModel main || !StorageProvider.CanOpen) return;
        try
        {
            static string[] Patterns(IEnumerable<string> extensions) => extensions.Select(extension => "*" + extension).ToArray();
            var options = new FilePickerOpenOptions
            {
                Title = "打开文件",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("视频、音频与直播播放列表") { Patterns = Patterns(LocalMedia.MediaExtensions.Concat(LocalMedia.PlaylistExtensions)) },
                    new FilePickerFileType("直播播放列表（m3u）") { Patterns = Patterns(LocalMedia.PlaylistExtensions) },
                    FilePickerFileTypes.All,
                ],
            };
            if (startDirectory is not null) options.SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(startDirectory);
            var files = await StorageProvider.OpenFilePickerAsync(options);
            if (files.Count == 0) return;
            await main.OpenLocalFilesAsync(files.Select(file => file.TryGetLocalPath()));
        }
        catch (Exception error)
        {
            VodBox.Core.VodBoxLog.Error("local", "打开文件失败", error);
            main.StatusMessage = $"打开文件失败：{error.Message}";
        }
    }

    /// <summary>
    /// 取出拖入的本地路径。Avalonia 12 用 IDataTransfer（不是旧的 IDataObject）：
    /// 逐项按 DataFormat.File 取值，再解析成本地路径。
    /// </summary>
    private static string?[] DroppedPaths(DragEventArgs e) =>
        e.DataTransfer.GetItems(DataFormat.File)
            .Select(item => item.TryGetValue(DataFormat.File))
            .OfType<IStorageItem>()
            .Select(storage => storage.TryGetLocalPath())
            .Where(path => !string.IsNullOrEmpty(path))
            .ToArray();

    private async void OnClosing(object? sender,WindowClosingEventArgs e)
    {
        if(_shutdownComplete||Avalonia.Application.Current is not App||DataContext is not MainViewModel main)return;
        e.Cancel=true;
        if(_shutdownStarted)return;
        _shutdownStarted=true;
        try
        {
            // 主窗口关闭清理：先关独立搜索窗口（关窗会取消在途搜索，避免回调打在已停机的服务上），再整体停机。
            _searchWindow?.Close();
            await main.ShutdownAsync();
        }
        catch(Exception error) { System.Diagnostics.Debug.WriteLine($"[shutdown] {error.Message}"); }
        finally
        {
            try { App.Services.Dispose(); }
            catch(Exception error){System.Diagnostics.Debug.WriteLine($"[shutdown] release failed: {error.Message}");}
            _shutdownComplete=true;Close();
        }
    }

    private MainViewModel VM => (MainViewModel)DataContext!;

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        StopObservingPlayer();
        base.OnDataContextChanged(e);
        if (DataContext is MainViewModel playerOwner)
        {
            _observedPlayer = playerOwner.Player;
            _observedPlayer.PropertyChanged += OnPlayerVisibilityChanged;
            playerOwner.SettingsRequested += ShowSettingsWindow;
            playerOwner.SearchRequested += OnSearchRequested;
            playerOwner.OpenFileRequested += OnOpenFileRequested;
            SyncPlayerWindow();
        }
        if (DataContext is MainViewModel vm && Avalonia.Application.Current is App)
        {
            vm.AttachDispatcher(Avalonia.Threading.Dispatcher.UIThread);
            // 启动时若有历史配置则自动加载
            Loaded += async (_, _) =>
            {
                // 直播源（多为本地 m3u）与点播配置互不依赖，并行加载。
                var live = App.Services.CurrentLiveConfig is { Length: > 0 } ? vm.Live.LoadAsync() : Task.CompletedTask;
                if (App.Services.CurrentVodConfig is { Length: > 0 } url)
                    await LoadStartupConfigAsync(vm, url);
                await live;
            };
        }
    }

    /// <summary>
    /// 冷启动加载点播配置：首页立即进入加载态；有本地副本时先用副本出首页，再后台向网络刷新，
    /// 只有配置确实变了才重建源并重载首页。没有副本（首次使用）才等网络。
    /// </summary>
    private static async Task LoadStartupConfigAsync(MainViewModel vm, string url)
    {
        var registry = App.Services.Registry;
        vm.Home.BeginStartupLoading();
        try
        {
            if (await registry.TryLoadCachedConfigAsync(url))
            {
                vm.UpdateSourceName();
                var home = vm.Home.LoadAsync();
                await RefreshStartupConfigAsync(vm, url);
                await home;
                return;
            }
            await registry.LoadConfigAsync(url);
            vm.UpdateSourceName();
            await vm.Home.LoadAsync();
        }
        catch (Exception error)
        {
            VodBox.Core.VodBoxLog.Error("config", "启动加载配置失败", error);
            vm.Home.CancelStartupLoading();
            vm.StatusMessage = "上次配置加载失败，请到设置中重新配置";
        }
    }

    private static async Task RefreshStartupConfigAsync(MainViewModel vm, string url)
    {
        try
        {
            if (!await App.Services.Registry.RefreshConfigAsync(url)) return;
            VodBox.Core.VodBoxLog.Info("config", "远程配置已更新，重载首页");
            vm.UpdateSourceName();
            await vm.Home.LoadAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            VodBox.Core.VodBoxLog.Warn("config", $"后台刷新配置失败，继续使用本地副本：{error.Message}");
        }
    }

    /// <summary>菜单栏「文件 › 搜索…」（⌘F）：把焦点放到侧栏顶部的全站搜索框。</summary>
    internal void FocusGlobalSearch()
    {
        if (this.FindControl<TextBox>("GlobalSearch") is not { } box) return;
        box.Focus();
        box.SelectAll();
    }

    /// <summary>把播放窗口提到最前：先还原最小化，再激活（不改变用户的置顶选择）。</summary>
    private static void RaisePlaybackWindow(Window window)
    {
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        if (!window.IsActive) window.Activate();
    }

    private void StopObservingPlayer()
    {
        if (_observedPlayer is not null) _observedPlayer.PropertyChanged -= OnPlayerVisibilityChanged;
        _observedPlayer = null;
        PlaybackWindow?.CloseAfterPlayback();
        PlaybackWindow = null;
    }

    private void OnPlayerVisibilityChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Visible 决定窗口的存在；Title 变化说明换了台/换了集，此时要把窗口提到最前。
        if (e.PropertyName is nameof(PlayerViewModel.Visible) or nameof(PlayerViewModel.Title)) SyncPlayerWindow();
    }

    /// <summary>当前已打开的设置窗口（只读观察用，没有副作用：不会顺手创建一个）。</summary>
    internal Views.SettingsWindow? OpenSettingsWindow => _settingsWindow;

    /// <summary>UI smoke 用：拿到（必要时先创建）设置窗口，以便逐个分区验证渲染。</summary>
    internal Views.SettingsWindow? SettingsWindowForSmoke
    {
        get
        {
            if (_settingsWindow is null) ShowSettingsWindow();
            return _settingsWindow;
        }
    }

    /// <summary>当前已打开的搜索窗口（只读观察用，没有副作用：不会顺手创建一个）。</summary>
    internal Views.SearchWindow? OpenSearchWindow => _searchWindow?.Window;

    /// <summary>UI smoke 用：拿到（必要时先创建）搜索窗口，验证独立搜索结果窗口能创建并渲染。</summary>
    internal Views.SearchWindow? SearchWindowForSmoke
    {
        get
        {
            if (DataContext is not MainViewModel main) return null;
            _searchWindow ??= new Services.SearchWindowService(main);
            return _searchWindow.EnsureWindow();
        }
    }

    /// <summary>
    /// 搜索请求（侧栏搜索框回车、「跨站搜索」按钮）：打开或复用独立搜索结果窗口。
    /// VM 只发布事件，窗口的开/关、去重与激活由这里统一管理。
    /// </summary>
    private void OnSearchRequested(string keyword)
    {
        if (DataContext is not MainViewModel main) return;
        _searchWindow ??= new Services.SearchWindowService(main);
        _searchWindow.Submit(keyword);
    }

    /// <summary>设置改为独立窗口：已开则提到最前（并切到请求的分区），否则新建一个。</summary>
    private void ShowSettingsWindow()
    {
        if (DataContext is not MainViewModel main) return;
        if (_settingsWindow is { } existing)
        {
            existing.SyncSelectionFrom(main.Settings);
            existing.Activate();
            return;
        }
        var window = new Views.SettingsWindow(main.Settings);
        MacMenus.Attach(window, main);
        _settingsWindow = window;
        window.Closed += (_, _) => { if (ReferenceEquals(_settingsWindow, window)) _settingsWindow = null; };
        // 非模态、不设 Owner：与播放窗口一致，主窗口仍可独立操作。
        window.Show();
    }

    private void SyncPlayerWindow()
    {
        if (DataContext is not MainViewModel main) return;
        if (!main.Player.Visible) { PlaybackWindow?.CloseAfterPlayback(); return; }
        if (PlaybackWindow is { } existing)
        {
            // 窗口已开（换台/换集）：把它提到最前，否则被主窗口挡住时用户会以为没播。
            VodBox.Core.VodBoxLog.Trace("player", $"播放窗口已存在，提到最前：{main.Player.Title}");
            RaisePlaybackWindow(existing);
            return;
        }
        VodBox.Core.VodBoxLog.Info("player", $"创建独立播放窗口：{main.Player.Title}");
        var player = new Views.PlayerWindow { DataContext = main, Topmost = main.Player.AlwaysOnTop };
        MacMenus.Attach(player, main);
        PlaybackWindow = player;
        player.Closed += (_, _) => { if (ReferenceEquals(PlaybackWindow, player)) PlaybackWindow = null; };
        // Non-modal and unowned: the main/detail window remains independently interactive.
        player.Show();
        RaisePlaybackWindow(player);
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Return && !e.Handled) VM.SubmitSearchCommand.Execute(null);
    }

    /// <summary>侧栏搜索框的历史下拉选了一条：词已写回搜索框，直接提交到搜索窗口。</summary>
    private void OnSearchHistoryPicked(object? sender, string word) => VM.SubmitSearchCommand.Execute(null);
}
