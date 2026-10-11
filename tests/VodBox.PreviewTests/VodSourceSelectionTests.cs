using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VodBox.Desktop.Services;
using VodBox.Desktop.ViewModels;
using VodBox.Desktop.Views;
using Xunit;

namespace VodBox.PreviewTests;

public sealed class VodSourceSelectionTests
{
    [AvaloniaFact]
    public async Task ManualMenuShowsNamesAndSwitchesChangeableZeroSourcesWithoutResettingOnNavigation()
    {
        var directory = TempDataDirectory.Create("vod-source-selection");
        using var services = new AppServices(directory);
        var main = new MainViewModel(services, true);
        main.AttachDispatcher(Dispatcher.UIThread);
        var path = Path.Combine(directory, "config.json");
        await File.WriteAllTextAsync(path, """
            {"sites":[
              {"key":"first","name":"第一个站点","api":"csp_Push","type":3,"changeable":0},
              {"key":"second","name":"第二个站点","api":"csp_Push","type":3,"changeable":0},
              {"key":"unsupported","name":"未支持站点","api":"csp_UnimplementedGuard","type":3,"changeable":1}
            ]}
            """, TestContext.Current.CancellationToken);
        Window? window = null;
        try
        {
            await services.Registry.LoadConfigAsync(path, TestContext.Current.CancellationToken);
            await main.Vod.LoadAsync();
            Assert.Equal("共 2 个站点", main.Vod.SourceCountLabel);
            Assert.Empty(services.Registry.ChangeableSources);
            Assert.Single(services.Registry.ImportWarnings);

            var view = new VodView { DataContext = main };
            window = new Window { Width = 1000, Height = 600, Content = view };
            window.Show();
            Pump(window);
            var selector = view.FindControl<Button>("SourceSelector")!;
            Assert.Contains(selector.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "第一个站点");
            selector.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(window);
            var menu = Assert.IsType<ContextMenu>(selector.ContextMenu);
            var items = menu.Items.OfType<MenuItem>().ToArray();
            Assert.Equal(new[] { "第一个站点", "第二个站点" }, items.Select(item => Assert.IsType<string>(item.Header)));
            Assert.All(items, item => Assert.True(item.IsEnabled));
            Assert.Contains(menu.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "第二个站点" && text.Bounds.Width > 0);

            items[1].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            menu.Close();
            Pump(window);
            Assert.Equal("second", main.Vod.LoadedSourceKey);
            Assert.Equal("第二个站点", main.Vod.SourceName);
            Assert.Contains(selector.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "第二个站点");
            main.Navigate(AppPage.Home);
            main.Navigate(AppPage.Vod);
            main.Vod.EnsureLoaded();
            await main.Vod.LoadAsync();
            Assert.Equal("second", main.Vod.LoadedSourceKey);

            await main.Vod.SwitchSourceAsync("unsupported");
            Assert.Equal("second", main.Vod.LoadedSourceKey);
            Assert.DoesNotContain(view.GetVisualDescendants().OfType<TextBlock>(), text => text.Text is "网格" or "列表");
            Assert.DoesNotContain(view.GetVisualDescendants().OfType<Button>(), button => Equals(button.Content, "重置筛选") || Equals(button.Content, "清空"));

            services.Registry.SetHidden("second", true);
            Assert.Equal("first", Assert.Single(main.Vod.AvailableSources).Key);
            Assert.Equal("共 1 个站点", main.Vod.SourceCountLabel);
            main.Vod.EnsureLoaded();
            Assert.Equal("first", main.Vod.LoadedSourceKey);
            main.Vod.Invalidate();
            await main.Vod.LoadAsync();
            Assert.Equal("first", main.Vod.LoadedSourceKey);
        }
        finally
        {
            window?.Close();
            services.Dispose();
            TempDataDirectory.TryDelete(directory);
        }
    }

    [AvaloniaFact]
    public async Task UnsupportedSubscriptionExplainsRuntimeBoundaryInsteadOfClaimingItIsMissing()
    {
        var directory = TempDataDirectory.Create("vod-source-boundary");
        using var services = new AppServices(directory);
        var main = new MainViewModel(services, true);
        var path = Path.Combine(directory, "config.json");
        try
        {
            await File.WriteAllTextAsync(path, """
                {"sites":[{"key":"unknown","name":"未知 Java 源","api":"csp_UnimplementedGuard","type":3}]}
                """, TestContext.Current.CancellationToken);
            await services.Registry.LoadConfigAsync(path, TestContext.Current.CancellationToken);
            await main.Vod.LoadAsync();
            Assert.Empty(main.Vod.AvailableSources);
            Assert.Contains("桌面运行时支持", main.Vod.ItemsHint);
            Assert.DoesNotContain("尚未配置", main.Vod.ItemsHint);
        }
        finally
        {
            services.Dispose();
            TempDataDirectory.TryDelete(directory);
        }
    }

    private static void Pump(Window window)
    {
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }
}
