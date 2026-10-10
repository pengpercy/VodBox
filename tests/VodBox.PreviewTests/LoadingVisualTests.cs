using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VodBox.Desktop.Services;
using VodBox.Desktop.ViewModels;
using VodBox.Desktop.Views;
using Xunit;

namespace VodBox.PreviewTests;

public sealed class LoadingVisualTests
{
    [AvaloniaTheory]
    [InlineData("Home")]
    [InlineData("Vod")]
    [InlineData("Search")]
    [InlineData("Detail")]
    [InlineData("Favorites")]
    [InlineData("History")]
    [InlineData("Live")]
    public void LoadingSkeletonIsPaintedAndDisappearsAfterCompletion(string page)
    {
        var directory = Path.Combine(Environment.CurrentDirectory, "loading-test-" + Guid.NewGuid().ToString("N"));
        using var services = new AppServices(directory);
        var main = new MainViewModel(services, true);
        main.AttachDispatcher(Dispatcher.UIThread);
        Control view;
        Action<bool> loading;
        switch (page)
        {
            case "Home": view = new HomeView(); loading = value => { main.Home.Loading = value; main.Home.RecentLoading = value; }; break;
            case "Vod": view = new VodView(); loading = value => main.Vod.Loading = value; break;
            case "Search": view = new SearchView(); loading = value => main.Search.Searching = value; break;
            case "Detail": view = new DetailView(); loading = value => main.Detail.Loading = value; break;
            case "Favorites": view = new FavoritesView(); loading = value => main.Favorites.Loading = value; break;
            case "History": view = new HistoryView(); loading = value => main.History.Loading = value; break;
            default: view = new LiveView(); loading = value => main.Live.Loading = value; break;
        }
        view.DataContext = main;
        var window = new Window { Width = 1280, Height = 800, Content = view };
        try
        {
            loading(true);
            window.Show();
            foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            {
                window.RequestedThemeVariant = theme;
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
                var blocks = view.GetVisualDescendants().OfType<Border>()
                    .Where(border => border.Classes.Contains("skeleton") && border.IsEffectivelyVisible && border.Bounds.Width > 0 && border.Bounds.Height > 0).ToArray();
                Assert.NotEmpty(blocks);
                if (page is "Vod" or "Search")
                {
                    var posters = blocks.Where(border => border.Classes.Contains("poster"))
                        .Select(border => (Border: border, Point: border.TranslatePoint(new Point(0, 0), view)!.Value))
                        .OrderBy(item => item.Point.Y).ToArray();
                    foreach (var first in posters)
                    foreach (var next in posters.Where(item => Math.Abs(item.Point.X - first.Point.X) < 1 && item.Point.Y > first.Point.Y + 1))
                        Assert.True(next.Point.Y >= first.Point.Y + first.Border.Bounds.Height + 32, "骨架行必须为标题留出空间，不能重叠");
                }
                Assert.All(blocks, border =>
                {
                    var brush = Assert.IsAssignableFrom<ISolidColorBrush>(border.Background);
                    Assert.True(brush.Color.A > 0);
                    Assert.False(border.IsHitTestVisible);
                });
                if (Environment.GetEnvironmentVariable("VODBOX_LOADING_SHOTS") is { Length: > 0 } output)
                {
                    Directory.CreateDirectory(output);
                    using var frame = window.CaptureRenderedFrame();
                    Assert.NotNull(frame);
                    frame.Save(Path.Combine(output, page + "-" + theme.Key + ".png"), new PngBitmapEncoderOptions());
                }
            }
            loading(false);
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            Assert.DoesNotContain(view.GetVisualDescendants().OfType<Border>(), border => border.Classes.Contains("skeleton") && border.IsEffectivelyVisible);
        }
        finally
        {
            window.Close();
            services.Dispose();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
