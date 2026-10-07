using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VodBox.Desktop;
using VodBox.Desktop.Views;
using Xunit;

namespace VodBox.Tests;

public sealed class HomeFlowTests
{
    [AvaloniaFact]
    public async Task FreshInstallShowsEmptyHomeWithoutLoadingBundledExamples()
    {
        string directory = Path.Combine(Path.GetTempPath(), "vodbox-home-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "config.json"), """
            {"schemaVersion":1,"id":"vodbox-examples","sources":[{"id":"catalog","name":"示例目录 · C#","runtime":"csharp","provider":"catalog","entry":"catalog.json"}]}
            """);
        var model = new MainViewModel(designMode: true, configurationDirectory: directory);
        try
        {
            await model.InitializeAsync();
            Assert.True(model.ShowHome); Assert.False(model.ShowLibrary);
            Assert.Empty(model.Sources); Assert.Empty(model.HomeCards); Assert.Empty(model.RecentHistory);
            Assert.Equal("影视", model.HomeTitle); Assert.Equal("未配置", model.VodSourceSummary);
            Assert.Equal("", model.ConfigLocation); Assert.Null(model.Engine.ActiveEngine);
            Assert.Contains("设置", model.HomeRecommendationStatus);
        }
        finally { await model.DisposeAsync(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [AvaloniaFact]
    public async Task ConfigureFromSettingsReturnsHomeAndKeepsRecommendationsSeparateFromBrowsing()
    {
        string directory = Path.Combine(Path.GetTempPath(), "vodbox-home-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "catalog.json"), """
            {"categories":[{"id":"demo","name":"测试分类"}],"items":[
              {"id":"one","title":"首页推荐","categoryId":"demo"},
              {"id":"two","title":"另一影片","categoryId":"demo"}]}
            """);
        string location = Path.Combine(directory, "config.json");
        await File.WriteAllTextAsync(location, """
            {"schemaVersion":1,"id":"test-home","sources":[
              {"id":"catalog","name":"测试播放源","runtime":"csharp","provider":"catalog","entry":"catalog.json"}],"liveSources":[]}
            """);
        var model = new MainViewModel(designMode: true, configurationDirectory: directory);
        try
        {
            await model.NavigateCommand.ExecuteAsync("设置");
            Assert.True(model.ShowSettings); Assert.True(model.ShowHome);
            model.ConfigLocation = location;
            await model.LoadConfigCommand.ExecuteAsync(null);
            Assert.True(model.ShowHome); Assert.False(model.ShowSettings); Assert.False(model.ShowLibrary);
            Assert.Equal("测试播放源", model.HomeTitle); Assert.Equal(2, model.HomeCards.Count);
            Assert.Empty(model.Items); Assert.Equal(1, model.ConfigurationLoadVersion);
            var recommendations = model.HomeCards.ToArray();
            await model.NavigateCommand.ExecuteAsync("点播");
            Assert.True(model.ShowLibrary); Assert.False(model.ShowHome); Assert.Equal(2, model.Items.Count);
            model.SearchText = "首页";
            await model.SearchCommand.ExecuteAsync(null);
            Assert.Single(model.Items); Assert.Equal(recommendations, model.HomeCards);
            await model.NavigateCommand.ExecuteAsync("设置");
            model.ConfigLocation = Path.Combine(directory, "missing.json");
            await model.LoadConfigCommand.ExecuteAsync(null);
            Assert.True(model.ShowSettings); Assert.True(model.ShowLibrary); Assert.False(model.ShowHome);
            Assert.Equal(1, model.ConfigurationLoadVersion); Assert.Equal(recommendations, model.HomeCards);
            Assert.Null(model.Engine.ActiveEngine);
        }
        finally { await model.DisposeAsync(); Directory.Delete(directory, true); }
    }

    [AvaloniaTheory]
    [InlineData(800)]
    [InlineData(1280)]
    public async Task HomeNavigationCentersIconAndLabelTogether(int width)
    {
        var model = new DesignMainViewModel();
        var view = new VodBox.Desktop.Views.HomeView { DataContext = model };
        var window = new Window { Content = view, Width = width, Height = 720 };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            var buttons = view.GetVisualDescendants().OfType<Button>().Where(button => button.Classes.Contains("homeEntry")).ToArray();
            Assert.Equal(7, buttons.Length);
            foreach (var button in buttons)
            {
                var content = Assert.IsType<StackPanel>(button.Content);
                var center = content.TranslatePoint(new Avalonia.Point(content.Bounds.Width / 2, content.Bounds.Height / 2), button)!.Value;
                Assert.InRange(center.Y, button.Bounds.Height / 2 - 1, button.Bounds.Height / 2 + 1);
                Assert.InRange(center.X, button.Bounds.Width / 2 - 1, button.Bounds.Width / 2 + 1);
            }
        }
        finally { window.Close(); await model.DisposeAsync(); }
    }

    [AvaloniaFact]
    public async Task SettingsWindowUsesSidebarAndClosingRetainsPlaybackPageAndSurface()
    {
        var window = new MainWindow(preview: true);
        var model = (DesignMainViewModel)window.DataContext!;
        try
        {
            model.ShowPlaybackPage = true;
            window.Show(); Dispatcher.UIThread.RunJobs();
            var surface = window.PlaybackView.VideoContainer;
            await model.NavigateCommand.ExecuteAsync("设置"); Dispatcher.UIThread.RunJobs();
            var settings = Assert.IsType<SettingsWindow>(window.SettingsWindow);
            Assert.True(model.ShowPlaybackPage); Assert.True(window.PlaybackView.IsEffectivelyVisible);
            Assert.Empty(settings.GetVisualDescendants().OfType<TabControl>());
            var sidebar = settings.SettingsView.FindControl<Border>("SidebarBackground")!;
            Assert.Equal(0, sidebar.TranslatePoint(default, settings)!.Value.Y);
            Assert.Equal(settings.Bounds.Height, sidebar.Bounds.Height);
            var scroll = settings.GetVisualDescendants().OfType<ScrollViewer>().First(view => view.Parent is VodBox.Desktop.Views.SettingsGeneralView);
            Assert.Equal(settings.Bounds.Width, scroll.TranslatePoint(new Point(scroll.Bounds.Width, 0), settings)!.Value.X);
            var menu = settings.SettingsView.FindControl<ListBox>("SettingsMenu")!;
            Assert.Equal(3, menu.Items.Count);
            foreach (int index in new[] { 1, 2, 0 })
            {
                menu.SelectedIndex = index; Dispatcher.UIThread.RunJobs();
                var item = Assert.IsType<ListBoxItem>(menu.ContainerFromIndex(index));
                Assert.Equal(new CornerRadius(6), item.CornerRadius);
                Assert.Equal(36, item.Bounds.Height); Assert.Equal(3, item.Margin.Bottom);
                item.Focus(Avalonia.Input.NavigationMethod.Tab); Dispatcher.UIThread.RunJobs();
                Assert.Contains(settings.GetVisualDescendants().OfType<Border>(), border => border.Classes.Contains("settingsMenuFocus") && border.CornerRadius == new CornerRadius(6));
                Assert.Equal(index == 0, settings.SettingsView.FindControl<VodBox.Desktop.Views.SettingsGeneralView>("GeneralPage")!.IsVisible);
                Assert.Equal(index == 1, settings.SettingsView.FindControl<VodBox.Desktop.Views.SettingsPlaybackView>("PlaybackPage")!.IsVisible);
                Assert.Equal(index == 2, settings.SettingsView.FindControl<VodBox.Desktop.Views.SettingsDanmakuView>("DanmakuPage")!.IsVisible);
            }
            settings.Close(); Dispatcher.UIThread.RunJobs();
            Assert.Null(window.SettingsWindow); Assert.False(model.ShowSettings);
            Assert.True(model.ShowPlaybackPage); Assert.Same(surface, window.PlaybackView.VideoContainer);
            Assert.Null(model.Engine.ActiveEngine);
            await model.NavigateCommand.ExecuteAsync("设置");
            Assert.NotSame(settings, window.SettingsWindow);
            window.Close(); Assert.Null(window.SettingsWindow);
        }
        finally { window.Close(); await model.DisposeAsync(); }
    }

    [AvaloniaFact]
    public async Task HomeUsesItsOwnHeaderAndRestoresHomeAfterFullscreen()
    {
        var window = new MainWindow(preview: true);
        var model = (DesignMainViewModel)window.DataContext!;
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            Assert.True(model.ShowHome);
            Assert.True(window.FindControl<VodBox.Desktop.Views.HomeView>("HomePage")!.IsVisible);
            Assert.Null(window.FindControl<Border>("NavigationPane"));
            window.WindowState = WindowState.FullScreen; Dispatcher.UIThread.RunJobs();
            Assert.True(model.ShowPlaybackPage);
            window.WindowState = WindowState.Normal; Dispatcher.UIThread.RunJobs();
            Assert.True(model.ShowHome); Assert.False(model.ShowPlaybackPage);
            Assert.Null(window.FindControl<Border>("NavigationPane"));
            await model.NavigateCommand.ExecuteAsync("设置"); Dispatcher.UIThread.RunJobs();
            Assert.Null(window.FindControl<Border>("NavigationPane"));
            var settings = Assert.IsType<SettingsWindow>(window.SettingsWindow);
            Assert.True(settings.IsVisible); Assert.Same(model, settings.DataContext);
            settings.WindowState = WindowState.Minimized;
            await model.NavigateCommand.ExecuteAsync("设置");
            Assert.Same(settings, window.SettingsWindow);
            Assert.Equal(WindowState.Normal, settings.WindowState);
            Assert.DoesNotContain(settings.GetVisualDescendants().OfType<Button>(), button => Equals(button.Content, "返回首页"));
            settings.Close(); Dispatcher.UIThread.RunJobs();
            Assert.True(model.ShowHome); Assert.False(model.ShowSettings);
            Assert.Null(window.SettingsWindow); Assert.False(settings.IsVisible);
        }
        finally { window.Close(); await model.DisposeAsync(); }
    }
}
