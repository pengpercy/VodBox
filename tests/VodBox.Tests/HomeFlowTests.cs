using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using VodBox.Desktop;
using Xunit;

namespace VodBox.Tests;

public sealed class HomeFlowTests
{
    [AvaloniaFact]
    public async Task FreshInstallShowsEmptyHomeWithoutLoadingBundledExamples()
    {
        string directory = Path.Combine(Path.GetTempPath(), "vodbox-home-" + Guid.NewGuid());
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
            Assert.True(model.ShowSettings); Assert.False(model.ShowHome);
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
            Assert.True(model.ShowSettings); Assert.False(model.ShowHome);
            Assert.Equal(1, model.ConfigurationLoadVersion); Assert.Equal(recommendations, model.HomeCards);
            Assert.Null(model.Engine.ActiveEngine);
        }
        finally { await model.DisposeAsync(); Directory.Delete(directory, true); }
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
            Assert.False(window.FindControl<Border>("NavigationPane")!.IsVisible);
            window.WindowState = WindowState.FullScreen; Dispatcher.UIThread.RunJobs();
            Assert.True(model.ShowPlaybackPage);
            window.WindowState = WindowState.Normal; Dispatcher.UIThread.RunJobs();
            Assert.True(model.ShowHome); Assert.False(model.ShowPlaybackPage);
            Assert.False(window.FindControl<Border>("NavigationPane")!.IsVisible);
            await model.NavigateCommand.ExecuteAsync("设置"); Dispatcher.UIThread.RunJobs();
            Assert.True(window.FindControl<Border>("NavigationPane")!.IsVisible);
        }
        finally { window.Close(); await model.DisposeAsync(); }
    }
}
