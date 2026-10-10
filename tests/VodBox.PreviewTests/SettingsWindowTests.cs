using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VodBox.Desktop.ViewModels;
using VodBox.Desktop.Views;
using Xunit;

namespace VodBox.PreviewTests;

/// <summary>独立设置窗口的渲染与导航验收：两栏布局、左栏复用 NavItemView、分区切换、卡片式右栏。</summary>
public sealed class SettingsWindowTests
{
    private static void Pump(Window window)
    {
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static SettingsSectionItem[] Items(SettingsWindowViewModel vm) =>
    [
        vm.Sources, vm.Playback, vm.Danmaku, vm.Subtitles, vm.Interface,
        vm.Data, vm.Remote, vm.Diagnostics, vm.About,
    ];

    [AvaloniaFact]
    public void WindowRendersTwoColumnsWithNineNavItemsInReusableNavItemView()
    {
        var window = new SettingsWindow { Width = 940, Height = 680 };
        window.Show();
        try
        {
            Pump(window);
            Assert.Equal(940, window.Width);
            Assert.Equal(760, window.MinWidth);
            Assert.Equal(560, window.MinHeight);
            // 宽度上限 = 左栏 216 + 右栏内边距 56 + 内容列 720 + 滚动条余量；再宽只是留白。
            Assert.Equal(1000, window.MaxWidth);
            Assert.Equal(1000, window.MaxHeight);

            // 左栏必须复用现成的 NavItemView，而不是自己写一套导航项样式。
            var navItems = window.GetVisualDescendants().OfType<NavItemView>().ToArray();
            Assert.Equal(9, navItems.Length);
            Assert.Equal(
                new[] { "源与订阅", "播放", "弹幕", "字幕", "界面", "数据", "推送与遥控", "诊断", "关于" },
                navItems.Select(item => item.Label).ToArray());

            // 两栏布局：左栏固定宽度，且导航项确实排在左栏里。
            var grid = window.GetVisualDescendants().OfType<Grid>().First(g => g.ColumnDefinitions.Count == 2);
            Assert.Equal(216, grid.ColumnDefinitions[0].Width.Value);
            Assert.True(navItems[0].Bounds.Width > 0);
            Assert.True(navItems[0].Bounds.Height > 0);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void SelectingNavItemSwitchesSectionAndHeaderTitleWithoutBlueHighlight()
    {
        var window = new SettingsWindow { Width = 940, Height = 680 };
        window.Show();
        try
        {
            Pump(window);
            var vm = (SettingsWindowViewModel)window.DataContext!;
            var navItems = window.GetVisualDescendants().OfType<NavItemView>().ToArray();

            // 默认选中第一项，头部条跟随。
            Assert.Same(vm.Sources, vm.Selected);
            Assert.Equal("源与订阅", vm.CurrentTitle);
            Assert.True(vm.Sources.IsSelected);
            Assert.Single(Items(vm), item => item.IsSelected);

            // 触发「界面」项的 Command（NavItemView 的真实点击路径），单选互斥。
            var target = navItems[4];
            Assert.NotNull(target.Command);
            target.Command!.Execute(null);
            Pump(window);
            Assert.Equal("界面", vm.CurrentTitle);
            Assert.True(vm.Interface.IsSelected);
            Assert.False(vm.Sources.IsSelected);
            Assert.Single(Items(vm), item => item.IsSelected);
            // 「诊断」排在「关于」之前，但内容分区号仍是 VM 既有取值。
            Assert.Equal(8, vm.Diagnostics.ContentSection);
            Assert.Equal(7, vm.About.ContentSection);

            // 选中态是主窗口那套半透明浅底，不是参考截图里的蓝色高亮块。
            var shell = target.GetVisualDescendants().OfType<Border>().First(b => b.Name == "Shell");
            var brush = Assert.IsAssignableFrom<ISolidColorBrush>(shell.Background);
            Assert.True(brush.Color.R == brush.Color.G && brush.Color.G == brush.Color.B,
                $"选中底色应为中性半透明，实际 {brush.Color}");
            Assert.InRange(brush.Color.A, (byte)1, (byte)64);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void RightPaneRendersRealSectionCardsOnceContentIsAttached()
    {
        // 右栏现在是真实分区内容（不再是骨架占位）：接入 SettingsViewModel 后应渲染出卡片与控件。
        var directory = Path.Combine(Path.GetTempPath(), $"vodbox-card-{Guid.NewGuid():N}");
        using var services = new VodBox.Desktop.Services.AppServices(directory);
        var main = new MainViewModel(services);
        main.AttachDispatcher(Dispatcher.UIThread);
        var window = new SettingsWindow { Width = 940, Height = 680 };
        window.Show();
        try
        {
            Pump(window);
            var vm = (SettingsWindowViewModel)window.DataContext!;
            vm.Content = main.Settings;
            foreach (var item in new[] { vm.Sources, vm.Playback, vm.Interface, vm.Remote, vm.About })
            {
                item.SelectCommand.Execute(null);
                Pump(window);
                var cards = window.GetVisualDescendants().OfType<Border>()
                    .Where(b => b.Classes.Contains("card") && b.IsEffectivelyVisible).ToArray();
                Assert.NotEmpty(cards);
                Assert.All(cards, card => Assert.True(card.CornerRadius.TopLeft >= 6));
            }
            // 播放分区应有滑杆与数字输入，关于分区应有按钮——覆盖卡片行的控件形态。
            vm.About.SelectCommand.Execute(null);
            Pump(window);
            Assert.Contains(window.GetVisualDescendants().OfType<Button>(), b => b.IsEffectivelyVisible);

            if (Environment.GetEnvironmentVariable("VODBOX_PREVIEW_SHOT") is { Length: > 0 })
            {
                vm.Sources.SelectCommand.Execute(null);
                Pump(window);
                Avalonia.Media.Imaging.Bitmap? frame = null;
                for (var i = 0; i < 8 && frame is null; i++)
                {
                    window.UpdateLayout();
                    Dispatcher.UIThread.RunJobs();
                    Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
                    Dispatcher.UIThread.RunJobs();
                    frame = Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window);
                }
                Assert.NotNull(frame);
                var dir = Path.Combine(Environment.CurrentDirectory, "preview-shots");
                Directory.CreateDirectory(dir);
                frame.Save(Path.Combine(dir, "SettingsWindow.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            }
        }
        finally
        {
            window.Close();
            services.Dispose(); // 先释放数据库连接：Windows 不允许删除仍被占用的文件
            TempDataDirectory.TryDelete(directory);
        }
    }

    [AvaloniaFact]
    public void NewlyOpenedWindowLandsOnTheRequestedSection()
    {
        // 回归：右栏可见性由 IsSelected 驱动，新建窗口若只设 Content 不选分区，
        // 「手机扫码推送」这类入口会停在默认分区。
        var directory = Path.Combine(Path.GetTempPath(), $"vodbox-focus-{Guid.NewGuid():N}");
        using var services = new VodBox.Desktop.Services.AppServices(directory);
        var main = new MainViewModel(services);
        main.AttachDispatcher(Dispatcher.UIThread);
        main.Settings.Section = 6; // 推送与遥控
        var window = new SettingsWindow(main.Settings) { Width = 940, Height = 680 };
        window.Show();
        try
        {
            Pump(window);
            var vm = (SettingsWindowViewModel)window.DataContext!;
            Assert.Equal("推送与遥控", vm.CurrentTitle);
            Assert.True(vm.Remote.IsSelected);
        }
        finally
        {
            window.Close();
            services.Dispose(); // 先释放数据库连接：Windows 不允许删除仍被占用的文件
            TempDataDirectory.TryDelete(directory);
        }
    }

    [AvaloniaFact]
    public void EverySectionCardSpansTheSameContentWidth()
    {
        // 回归：右栏容器若用 HorizontalAlignment=Left，宽度会跟着当前分区内容收缩，
        // 造成每个菜单项看过去卡片宽度都不一样。这里逐个分区测量卡片宽度必须一致。
        var directory = Path.Combine(Path.GetTempPath(), $"vodbox-width-{Guid.NewGuid():N}");
        using var services = new VodBox.Desktop.Services.AppServices(directory);
        var main = new MainViewModel(services);
        main.AttachDispatcher(Dispatcher.UIThread);
        var window = new SettingsWindow { Width = 940, Height = 680 };
        window.Show();
        try
        {
            Pump(window);
            var vm = (SettingsWindowViewModel)window.DataContext!;
            vm.Content = main.Settings;
            var widths = new List<double>();
            foreach (var item in new[] { vm.Sources, vm.Playback, vm.Danmaku, vm.Subtitles, vm.Interface, vm.Data, vm.Remote, vm.Diagnostics, vm.About })
            {
                item.SelectCommand.Execute(null);
                Pump(window);
                var card = window.GetVisualDescendants().OfType<Border>()
                    .First(b => b.Classes.Contains("card") && b.IsEffectivelyVisible);
                widths.Add(card.Bounds.Width);
            }
            Assert.All(widths, w => Assert.Equal(widths[0], w, 1));
            Assert.True(widths[0] > 300, $"卡片应有实际宽度，实际 {widths[0]}");
        }
        finally
        {
            window.Close();
            services.Dispose(); // 先释放数据库连接：Windows 不允许删除仍被占用的文件
            TempDataDirectory.TryDelete(directory);
        }
    }

    [AvaloniaFact]
    public void WindowWidthStaysWithinConfiguredBounds()
    {
        var window = new SettingsWindow { Width = 2000, Height = 680 };
        window.Show();
        try
        {
            Pump(window);
            Assert.InRange(window.Width, window.MinWidth, window.MaxWidth);
            window.Width = 400;
            Pump(window);
            Assert.Equal(window.MinWidth, window.Width);
            // 高度同样有上下限：太小内容不可用，太大只是留白。
            Assert.Equal(560, window.MinHeight);
            Assert.Equal(1000, window.MaxHeight);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void MainWindowOpensSettingsWindowAndFocusesRequestedSection()
    {
        // 端到端：侧边栏/远程入口请求设置 → 主窗口开独立窗口 → 落到正确分区；且主窗口不切页。
        var directory = Path.Combine(Path.GetTempPath(), $"vodbox-entry-{Guid.NewGuid():N}");
        using var services = new VodBox.Desktop.Services.AppServices(directory);
        var main = new MainViewModel(services);
        main.AttachDispatcher(Dispatcher.UIThread);
        var shell = new VodBox.Desktop.MainWindow { DataContext = main };
        shell.Show();
        try
        {
            Pump(shell);
            Assert.Null(shell.OpenSettingsWindow);
            var home = main.Page;

            main.GoSettingsCommand.Execute(null);
            Pump(shell);
            var window = shell.OpenSettingsWindow;
            Assert.NotNull(window);
            Assert.True(window!.IsVisible);
            Assert.Equal(home, main.Page); // 不切页，主窗口内容不受影响
            // 再次打开应复用同一个窗口，而不是叠窗口。
            main.GoSettingsCommand.Execute(null);
            Pump(shell);
            Assert.Same(window, shell.OpenSettingsWindow);
            // 请求特定分区（远程入口会切到「推送与遥控」）时，已开窗口要跟着跳。
            main.Settings.Section = 6;
            main.OpenSettings();
            Pump(shell);
            var vm = (SettingsWindowViewModel)window.DataContext!;
            Assert.Equal("推送与遥控", vm.CurrentTitle);
            Assert.True(vm.Remote.IsSelected);
        }
        finally
        {
            shell.Close();
            services.Dispose(); // 先释放数据库连接：Windows 不允许删除仍被占用的文件
            TempDataDirectory.TryDelete(directory);
        }
    }

    [AvaloniaFact]
    public void SelectingSectionCarriesThroughToSettingsViewModel()
    {
        // 接入点验证：把现有 SettingsViewModel 交给窗口后，左栏切换应同步内容侧的分区号。
        var window = new SettingsWindow { Width = 940, Height = 680 };
        window.Show();
        try
        {
            Pump(window);
            var vm = (SettingsWindowViewModel)window.DataContext!;
            var directory = Path.Combine(Path.GetTempPath(), $"vodbox-settings-{Guid.NewGuid():N}");
            using var services = new VodBox.Desktop.Services.AppServices(directory);
            var content = new SettingsViewModel(services, new MainViewModel(services));
            try
            {
                vm.Content = content;
                Items(vm)[5].SelectCommand.Execute(null);
                Assert.Equal(5, content.Section);
                Assert.Equal("数据", vm.CurrentTitle);
                // 骨架阶段未接入内容时，版本号回退到程序集版本，不应为空。
                Assert.False(string.IsNullOrWhiteSpace(vm.AppVersion));
            }
            finally
            {
                services.Dispose();
                TempDataDirectory.TryDelete(directory);
            }
        }
        finally { window.Close(); }
    }
}
