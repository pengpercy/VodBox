using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VodBox.Desktop;
using VodBox.Desktop.Design;
using VodBox.Desktop.ViewModels;
using VodBox.Desktop.Views;
using Xunit;

namespace VodBox.PreviewTests;

public sealed class MainWindowPreviewTests
{
    // Avalonia 的平台 chrome 命中测试是 internal；用反射验证实际拖动角色，避免只断言附加属性。
    private static WindowDecorationsElementRole? HitTestChrome(Window window, Point point)
    {
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
        var inputRoot = typeof(TopLevel).GetProperty("InputRoot", flags)!.GetValue(window);
        var method = typeof(IInputRoot).GetMethod("HitTestChromeElement", flags)!;
        return (WindowDecorationsElementRole?)method.Invoke(inputRoot, [point]);
    }

    /// <summary>
    /// 侧栏搜索框同样有搜索历史下拉：聚焦弹出、Esc 收起、焦点离开搜索框收起，选一条直接发起搜索。
    /// </summary>
    [AvaloniaFact]
    public void SidebarSearchBoxDropsDownHistoryAndClosesWhenFocusLeaves()
    {
        var main = DesignData.Main;
        var saved = main.Search.SearchHistory.ToArray();
        main.Search.SearchHistory.Clear();
        foreach (var word in new[] { "繁花", "三体" }) main.Search.SearchHistory.Add(word);
        var window = new MainWindow { DataContext = main, Width = 1280, Height = 800 };
        void Layout() { for (var i = 0; i < 3; i++) { window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); } }
        try
        {
            window.Show();
            Layout();
            var box = window.FindControl<TextBox>("GlobalSearch")!;
            var popup = window.FindControl<SearchHistoryPopup>("GlobalSearchHistory")!;
            box.Focus();
            Layout();
            Assert.True(popup.IsOpen);
            var historyPanel = popup.FindControl<Border>("HistoryPanel")!;
            Assert.Equal(box.Bounds.Width, historyPanel.Bounds.Width, 5);
            Assert.True(historyPanel.Bounds.Width < 260);
            box.Width = 160;
            Layout();
            Assert.Equal(box.Bounds.Width, historyPanel.Bounds.Width, 5);
            box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
            Assert.False(popup.IsOpen);
            box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Down });
            Assert.True(popup.IsOpen);
            window.GetVisualDescendants().OfType<Button>().First(button => button.IsEffectivelyVisible && button.Focusable).Focus();
            Layout();
            Assert.False(popup.IsOpen);
        }
        finally
        {
            window.Close();
            main.Search.SearchHistory.Clear();
            foreach (var word in saved) main.Search.SearchHistory.Add(word);
        }
    }

    [AvaloniaFact]
    public void SearchSidebarBlankSpaceDragsWindowWhileSiteControlsRemainClickable()
    {
        var window = new SearchWindow { DataContext = DesignData.Main, Width = 1040, Height = 720 };
        try
        {
            window.Show();
            for (var i = 0; i < 3; i++) { window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
            Dispatcher.UIThread.RunJobs();
            var sidebar = window.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "SiteSidebar");
            foreach (var x in new[] { 5d, 110d, 215d })
            {
                var point = sidebar.TranslatePoint(new Point(x, sidebar.Bounds.Height - 30), window)!.Value;
                Assert.Equal(WindowDecorationsElementRole.TitleBar, HitTestChrome(window, point));
            }
            var all = window.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "AllSitesButton");
            var center = all.TranslatePoint(new Point(all.Bounds.Width / 2, all.Bounds.Height / 2), window)!.Value;
            Assert.Equal(WindowDecorationsElementRole.User, HitTestChrome(window, center));
            var sites = window.GetVisualDescendants().OfType<ListBox>().Single(list => list.Name == "SiteList");
            Assert.Equal(WindowDecorationsElementRole.User, WindowDecorationProperties.GetElementRole(sites));
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// 首页沉浸式标题栏：首页内容伸到标题栏下方，标题栏处仍是拖拽区；未滚动时标题栏完全透明，
    /// 向下滚动后渐显毛玻璃，切到其他页面即收起。macOS 直播页上移进标题栏行的工具栏必须仍可点。
    /// </summary>
    [AvaloniaFact]
    public void HomeScrollsUnderTransparentTitleBarThatFrostsOnceScrolled()
    {
        var main = DesignData.Main;
        var previousPage = main.Page;
        var previousPlayerVisible = main.Player.Visible;
        var window = new MainWindow { DataContext = main, Width = 1280, Height = 800 };
        void Layout() { for (var i = 0; i < 3; i++) { window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); } }
        try
        {
            main.Page = AppPage.Home;
            main.Player.Visible = false;
            window.Show();
            Layout();
            var scrim = window.FindControl<Panel>("TitleScrim")!;
            var home = window.GetVisualDescendants().OfType<HomeView>().Single();
            var scroller = home.FindControl<ScrollViewer>("HomeScroller")!;
            scroller.Offset = default;
            Layout();
            Assert.DoesNotContain("shown", scrim.Classes);
            Assert.False(scrim.IsHitTestVisible);
            // Windows/Linux 窗口按钮下垫的毛玻璃小底板：未滚动时显示，只占右上角按钮区，不挡指针。
            var plate = window.FindControl<Border>("CaptionPlate")!;
            Assert.Contains("shown", plate.Classes);
            Assert.False(plate.IsHitTestVisible);
            Assert.Equal(!OperatingSystem.IsMacOS(), plate.IsVisible);
            // 首页内容在标题栏下方（横幅从窗口顶边开始），但标题栏那一行依然能拖动窗口。
            Assert.Equal(WindowDecorationsElementRole.TitleBar, HitTestChrome(window, new Point(640, 20)));

            scroller.Offset = new Vector(0, 200);
            Layout();
            Assert.True(main.Home.ScrolledUnderTitleBar);
            Assert.Contains("shown", scrim.Classes);
            // 整行毛玻璃接替，底板淡出，不叠成两层。
            Assert.DoesNotContain("shown", plate.Classes);

            main.Page = AppPage.Vod;
            Layout();
            Assert.DoesNotContain("shown", scrim.Classes);
            Assert.DoesNotContain("shown", plate.Classes);
            // 点播页的补充拖拽区只到页头顶边，不压页头控件。
            var strip = window.FindControl<Border>("PageTitleBar")!;
            Assert.True(strip.IsVisible);
            Assert.Equal(MainViewModel.PageHeaderTop(OperatingSystem.IsMacOS()), strip.Bounds.Height, 1);
            if (OperatingSystem.IsMacOS())
            {
                // 直播页工具栏在 macOS 上上移 24 进入标题栏行：不补拖拽区，频道搜索框照常接收点击。
                main.Page = AppPage.Live;
                Layout();
                Assert.False(strip.IsVisible);
                var search = window.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "ChannelSearch");
                var point = search.TranslatePoint(new Point(search.Bounds.Width / 2, 8), window)!.Value;
                Assert.True(point.Y < MainViewModel.TitleBarHeight);
                Assert.NotEqual(WindowDecorationsElementRole.TitleBar, HitTestChrome(window, point));
            }
        }
        finally
        {
            main.Home.ScrolledUnderTitleBar = false;
            main.Page = previousPage;
            main.Player.Visible = previousPlayerVisible;
            window.Close();
        }
    }

    /// <summary>
    /// 点播/收藏/历史整体上移进标题栏行：macOS 页头与直播工具栏齐平；Windows/Linux 的窗口按钮在右上角、高 32，
    /// 页头落在按钮下方，不会压到按钮。上移后标题栏行的空白处仍须能拖动窗口。
    /// </summary>
    [AvaloniaFact]
    public void VodFavoritesAndHistoryMoveUpIntoTitleRowWithoutCoveringWindowButtons()
    {
        foreach (var page in new[] { AppPage.Vod, AppPage.Favorites, AppPage.History })
        {
            // Windows/Linux：页头顶边（页面上移后 + 页面自带 12 顶边距）必须在 32 高的窗口按钮下方。
            var nonMacTop = MainViewModel.TitleBarHeight + MainViewModel.GetPageContentMargin(page, isMacOS: false).Top + 12;
            Assert.True(nonMacTop >= MainViewModel.CaptionButtonsHeight + 4, $"{page} 页头会压到 Windows/Linux 窗口按钮");
            Assert.True(nonMacTop < MainViewModel.TitleBarHeight, $"{page} 在 Windows/Linux 上没有上移");
            var macTop = MainViewModel.TitleBarHeight + MainViewModel.GetPageContentMargin(page, isMacOS: true).Top + 12;
            Assert.Equal(MainViewModel.TitleBarHeight - 24, macTop);
        }

        var main = DesignData.Main;
        var previousPage = main.Page;
        var previousPlayerVisible = main.Player.Visible;
        var window = new MainWindow { DataContext = main, Width = 1280, Height = 800 };
        try
        {
            main.Player.Visible = false;
            window.Show();
            foreach (var (page, type) in new[] { (AppPage.Vod, typeof(VodView)), (AppPage.Favorites, typeof(FavoritesView)), (AppPage.History, typeof(HistoryView)) })
            {
                main.Page = page;
                for (var i = 0; i < 3; i++) { window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
                var view = (Control)window.GetVisualDescendants().Single(visual => visual.GetType() == type);
                var headerTop = view.TranslatePoint(default, window)!.Value.Y + 12;
                Assert.Equal(MainViewModel.PageHeaderTop(OperatingSystem.IsMacOS()), headerTop, 1);
                // 页头右侧之外的标题栏空白仍是拖拽区（页面不能把标题栏那一行整片吃掉）。
                Assert.Equal(WindowDecorationsElementRole.TitleBar, HitTestChrome(window, new Point(1000, 14)));
            }
        }
        finally
        {
            main.Page = previousPage;
            main.Player.Visible = previousPlayerVisible;
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Shell_RendersWithContinuousDividerAndInteractiveNavigationAndSearch()
    {
        var main = DesignData.Main;
        var previousPage = main.Page;
        var previousPlayerVisible = main.Player.Visible;
        var previousKeyword = main.SearchKeyword;
        var window = new MainWindow { DataContext = main };
        try
        {
            main.Page = AppPage.Home;
            main.Player.Visible = false;
            main.SearchKeyword = "";
            window.Show();
            window.UpdateLayout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(WindowDecorations.Full, window.WindowDecorations);
            Assert.NotNull(window.Icon);
            var divider = window.FindControl<Border>("SidebarDivider")!;
            Assert.Equal(0, divider.TranslatePoint(default, window)!.Value.Y);
            Assert.Equal(window.ClientSize.Height, divider.Bounds.Height);
            var brush = Assert.IsType<SolidColorBrush>(divider.Background);
            Assert.InRange(brush.Opacity, 0.05, 0.15);
            Assert.False(divider.IsHitTestVisible);
            // 侧边栏不再有品牌 logo；剩下的 Image 只能是海报或壁纸。
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<Image>(), image => image is not (RemotePoster or LocalWallpaper));
            // 品牌名称也已移除，侧边栏顶部不再出现 "VodBox" 字样。
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "VodBox");

            var titleBar = window.FindControl<Grid>("TitleBar")!;
            var search = window.FindControl<TextBox>("GlobalSearch")!;
            Assert.Equal(WindowDecorationsElementRole.TitleBar, WindowDecorationProperties.GetElementRole(titleBar));
            Assert.Equal(WindowDecorationsElementRole.User, WindowDecorationProperties.GetElementRole(search));
            Assert.Equal(WindowDecorationsElementRole.User, WindowDecorationProperties.GetElementRole((Visual)divider.Parent!));

            var items = window.GetVisualDescendants().OfType<NavItemView>().ToArray();
            Assert.Equal(7, items.Length);
            Assert.DoesNotContain(items, item => item.Label == "搜索");
            Assert.All(items, item =>
            {
                var icon = item.FindControl<PathIcon>("IconPath")!;
                Assert.NotNull(icon.Data);
                Assert.Equal(item.Foreground, icon.Foreground);
                Assert.Equal(WindowDecorationsElementRole.User, WindowDecorationProperties.GetElementRole(item));
            });
            // 高亮胶囊左右留间隙（对齐 App Store），且不再使用左侧蓝色指示条。
            var selected = items.Single(item => item.IsSelected);
            var selectedShell = selected.FindControl<Border>("Shell")!;
            var sidebarWidth = window.FindControl<Border>("SidebarDragArea")!.Bounds.Width;
            var leftGap = selectedShell.TranslatePoint(default, window)!.Value.X;
            Assert.True(leftGap >= 6, $"高亮左侧应留间隙，实际 {leftGap}");
            Assert.True(sidebarWidth - (leftGap + selectedShell.Bounds.Width) >= 6, "高亮右侧应留间隙");
            Assert.Null(selected.FindControl<Border>("Indicator"));
            Assert.All(items.Select(item => item.FindControl<Border>("Indicator")), indicator => Assert.Null(indicator));

            var sidebar = window.FindControl<Border>("SidebarDragArea")!;
            Assert.Equal(WindowDecorationsElementRole.TitleBar, WindowDecorationProperties.GetElementRole(sidebar));
            var sidebarGrid = Assert.IsType<Grid>(sidebar.Child);
            var spacer = sidebarGrid.RowDefinitions[7];
            Assert.True(spacer.ActualHeight > 0);
            var blankY = sidebarGrid.RowDefinitions.Take(7).Sum(row => row.ActualHeight) + spacer.ActualHeight / 2;
            foreach (var x in new[] { 8d, 108d, 208d })
            {
                var blankPoint = sidebarGrid.TranslatePoint(new Point(x, blankY), window)!.Value;
                Assert.Equal(WindowDecorationsElementRole.TitleBar, HitTestChrome(window, blankPoint));
            }
            var settings = items.Single(item => item.Label == "设置");
            var menuPoint = settings.TranslatePoint(new Point(settings.Bounds.Width / 2, settings.Bounds.Height / 2), window)!.Value;
            Assert.Equal(WindowDecorationsElementRole.User, HitTestChrome(window, menuPoint));
            window.MouseDown(menuPoint, MouseButton.Right);
            window.MouseUp(menuPoint, MouseButton.Right);
            Assert.Equal(AppPage.Home, main.Page);
            window.MouseDown(menuPoint, MouseButton.Left);
            window.MouseUp(menuPoint, MouseButton.Left);
            // 设置现在是独立窗口：点击后不切页（切过去只会留下空白内容区），而是弹出窗口。
            Assert.Equal(AppPage.Home, main.Page);
            Assert.NotNull(window.OpenSettingsWindow);
            Assert.True(window.OpenSettingsWindow!.IsVisible);
            Assert.False(settings.IsSelected);
            window.OpenSettingsWindow.Close();
            Assert.Null(window.OpenSettingsWindow);

            var searchPoint = search.TranslatePoint(new Point(search.Bounds.Width / 2, search.Bounds.Height / 2), window)!.Value;
            window.MouseDown(searchPoint, MouseButton.Left);
            window.MouseUp(searchPoint, MouseButton.Left);
            window.KeyTextInput("VodBox");
            Assert.Equal("VodBox", search.Text);
            Assert.Equal("VodBox", main.SearchKeyword);

            if (Environment.GetEnvironmentVariable("VODBOX_PREVIEW_SHOT") is { Length: > 0 })
            {
                Bitmap? frame = null;
                for (var i = 0; i < 8 && frame is null; i++)
                {
                    window.UpdateLayout();
                    Dispatcher.UIThread.RunJobs();
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
                    Dispatcher.UIThread.RunJobs();
                    frame = window.CaptureRenderedFrame();
                }
                Assert.NotNull(frame);
                var dir = Path.Combine(Environment.CurrentDirectory, "preview-shots");
                Directory.CreateDirectory(dir);
                frame.Save(Path.Combine(dir, "MainWindow.png"), new PngBitmapEncoderOptions());
            }
        }
        finally
        {
            window.Close();
            main.Page = previousPage;
            main.Player.Visible = previousPlayerVisible;
            main.SearchKeyword = previousKeyword;
        }
    }
}
