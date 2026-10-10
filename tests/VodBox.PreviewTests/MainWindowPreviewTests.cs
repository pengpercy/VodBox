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
            Assert.Equal(8, items.Length);
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
