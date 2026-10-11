using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VodBox.Desktop.Services;
using VodBox.Desktop.ViewModels;
using VodBox.Desktop.Views;
using Xunit;

namespace VodBox.PreviewTests;

public sealed class VodScrollLayoutTests
{
    [AvaloniaTheory]
    [InlineData("Home")]
    [InlineData("Detail")]
    [InlineData("Vod")]
    public void VerticalScrollHostReachesRightEdgeAndKeepsContentInset(string page)
    {
        var directory = Path.Combine(Path.GetTempPath(), "vod-scroll-test-" + Guid.NewGuid().ToString("N"));
        using var services = new AppServices(directory);
        var main = new MainViewModel(services, true);
        main.AttachDispatcher(Dispatcher.UIThread);
        Control view = page switch { "Home" => new HomeView(), "Detail" => new DetailView(), _ => new VodView() };
        view.DataContext = main;
        main.Vod.Loading = true;
        main.Detail.Loading = true;
        var window = new Window { Width = 1000, Height = 400, Content = view };
        try
        {
            window.Show();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            var scroll = page == "Vod"
                ? view.FindControl<Panel>("GridHost")!.GetVisualDescendants().OfType<ScrollViewer>().Single(item => item.IsEffectivelyVisible)
                : Assert.IsType<ScrollViewer>(((ContentControl)view).Content);
            Assert.Equal(0, scroll.Padding.Right);
            var position = scroll.TranslatePoint(new Point(0, 0), view)!.Value;
            Assert.InRange(Math.Abs(position.X + scroll.Bounds.Width - view.Bounds.Width), 0, 1);
            Assert.Equal(28, Assert.IsAssignableFrom<Control>(scroll.Content).Margin.Right);
            var scrollbar = scroll.GetVisualDescendants().OfType<ScrollBar>().Single(bar =>
                bar.Orientation == Avalonia.Layout.Orientation.Vertical && ReferenceEquals(bar.TemplatedParent, scroll));
            var barPosition = scrollbar.TranslatePoint(new Point(0, 0), view)!.Value;
            Assert.InRange(Math.Abs(barPosition.X + scrollbar.Bounds.Width - view.Bounds.Width), 0, 1);
            if (page == "Vod")
            {
                main.Vod.Loading = false;
                window.UpdateLayout();
                var list = view.FindControl<ListBox>("PosterList")!;
                var listPosition = list.TranslatePoint(new Point(0, 0), view)!.Value;
                Assert.Equal(0, list.Padding.Right);
                Assert.InRange(Math.Abs(listPosition.X + list.Bounds.Width - view.Bounds.Width), 0, 1);
            }
        }
        finally
        {
            window.Close();
            services.Dispose();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
