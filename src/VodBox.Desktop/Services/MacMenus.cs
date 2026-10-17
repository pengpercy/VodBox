using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using VodBox.Desktop.ViewModels;

namespace VodBox.Desktop.Services;

/// <summary>
/// macOS 菜单栏。应用菜单（「VodBox」）放关于、检查更新、设置；Avalonia 默认追加的服务/隐藏/退出保留（名字取自
/// Application.Name）。窗口菜单（文件/显示/播放/帮助）挂到每个窗口上：macOS 的菜单栏跟随当前活动窗口，
/// 搜索、设置、播放窗口在前台时同样可用。NativeMenu 实例不能被多个窗口共用，所以每个窗口各建一份。
/// </summary>
internal static class MacMenus
{
    /// <summary>设置窗口「关于」分区的编号（与 SettingsWindowViewModel 一致）。</summary>
    internal const int AboutSection = 7;

    private static KeyGesture Cmd(Key key) => new(key, KeyModifiers.Meta);

    private static NativeMenuItem Item(string header, Action action, KeyGesture? gesture = null) =>
        new(header) { Gesture = gesture, Command = new CommunityToolkit.Mvvm.Input.RelayCommand(action) };

    private static NativeMenuItem Submenu(string header, params NativeMenuItemBase[] items)
    {
        var menu = new NativeMenu();
        foreach (var item in items) menu.Add(item);
        return new NativeMenuItem(header) { Menu = menu };
    }

    /// <summary>主窗口：菜单里涉及主窗口的操作（导航、侧栏搜索、打开文件对话框）先把它带到前台。</summary>
    private static MainWindow? MainWindow =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow as MainWindow;

    private static void ActivateMain()
    {
        if (MainWindow is not { } window) return;
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }

    /// <summary>
    /// 安装应用菜单。Avalonia 的 macOS 应用菜单导出器在启动早期（AfterSetup）就创建，那时应用还没有菜单，
    /// 它会生成带「About Avalonia」的默认菜单挂到 Application 上；之后再用 SetMenu 换一个新的 NativeMenu 对象，
    /// 导出器并不监听这个替换，菜单栏仍是默认的。它只在「已导出菜单」的条目变化时重新导出，
    /// 所以这里把已挂着的那份菜单的条目原地换成我们的；还没有菜单时才直接设置（导出器随后会读到）。
    /// </summary>
    public static void InstallApplicationMenu(Application application, MainViewModel main)
    {
        var ours = CreateApplicationMenu(main);
        if (NativeMenu.GetMenu(application) is not { } exported)
        {
            NativeMenu.SetMenu(application, ours);
            return;
        }
        var items = ours.Items.ToList();
        ours.Items.Clear();
        exported.Items.Clear();
        foreach (var item in items) exported.Items.Add(item);
    }

    public static NativeMenu CreateApplicationMenu(MainViewModel main)
    {
        var menu = new NativeMenu();
        menu.Add(Item("关于 VodBox", () => { main.Settings.Section = AboutSection; main.OpenSettings(); }));
        menu.Add(Item("检查更新…", () => _ = main.Settings.CheckUpdateAsync(MainWindow, automatic: false)));
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(Item("设置…", main.OpenSettings, Cmd(Key.OemComma)));
        return menu;
    }

    /// <param name="detach">取消对播放器状态的订阅；窗口关闭时调用（播放窗口每次播放新建，不能让订阅越积越多）。</param>
    public static NativeMenu CreateWindowMenu(MainViewModel main, string dataDirectory, out Action detach)
    {
        void Show(AppPage page) { ActivateMain(); main.Navigate(page); }

        var playPause = Item("播放 / 暂停", () => main.Player.TogglePlayPause());
        var next = Item("下一集", () => main.Player.NextEpisodeCommand.Execute(null), new KeyGesture(Key.Right, KeyModifiers.Meta));
        var close = Item("关闭播放", () => main.Player.CloseCommand.Execute(null));
        void SyncPlayback()
        {
            playPause.IsEnabled = main.Player.Visible;
            close.IsEnabled = main.Player.Visible;
            next.IsEnabled = main.Player.Visible && main.Player.HasNextEpisode;
        }
        SyncPlayback();
        void OnPlayerChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(PlayerViewModel.Visible) or nameof(PlayerViewModel.HasNextEpisode)) SyncPlayback();
        }
        var player = main.Player;
        player.PropertyChanged += OnPlayerChanged;
        detach = () => player.PropertyChanged -= OnPlayerChanged;

        var menu = new NativeMenu();
        menu.Add(Submenu("文件",
            Item("打开文件…", () => { ActivateMain(); main.RequestOpenFile(); }, Cmd(Key.O)),
            new NativeMenuItemSeparator(),
            Item("搜索…", () => { ActivateMain(); MainWindow?.FocusGlobalSearch(); }, Cmd(Key.F))));
        menu.Add(Submenu("显示",
            Item("首页", () => Show(AppPage.Home), Cmd(Key.D1)),
            Item("点播", () => Show(AppPage.Vod), Cmd(Key.D2)),
            Item("直播", () => Show(AppPage.Live), Cmd(Key.D3)),
            Item("收藏", () => Show(AppPage.Favorites), Cmd(Key.D4)),
            Item("历史", () => Show(AppPage.History), Cmd(Key.D5))));
        menu.Add(Submenu("播放", playPause, next, new NativeMenuItemSeparator(), close));
        menu.Add(Submenu("帮助",
            Item("打开日志文件夹", () => OpenFolder(Path.Combine(dataDirectory, "logs")))));
        return menu;
    }

    /// <summary>给一个窗口挂上窗口菜单（仅 macOS；其他平台没有全局菜单栏）。</summary>
    public static void Attach(Window window, MainViewModel main)
    {
        if (!OperatingSystem.IsMacOS() || App.Services is null) return;
        NativeMenu.SetMenu(window, CreateWindowMenu(main, App.Services.DataDir, out var detach));
        window.Closed += (_, _) => detach();
    }

    private static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("open", [path]) { UseShellExecute = false });
        }
        catch (Exception error) { VodBox.Core.VodBoxLog.Warn("app", $"打开文件夹失败：{error.Message}"); }
    }
}
