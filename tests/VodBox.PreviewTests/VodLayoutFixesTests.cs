using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VodBox.Core;
using VodBox.Desktop.Services;
using VodBox.Desktop.ViewModels;
using VodBox.Desktop.Views;
using Xunit;

namespace VodBox.PreviewTests;

/// <summary>
/// 点播页真实布局回归：
/// 1) 分类横向滚动条真的会出现（溢出时）；
/// 2) 站点弹层锚定在固定控件（不是鼠标点位），长站名固定宽度省略号 + 中性色；
/// 3) 海报行用均分列布局填满可用宽度，末端不再留「最后一列到右滚动条」的大块空白。
/// </summary>
public sealed class VodLayoutFixesTests
{
    private static void Pump(Window window)
    {
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    [AvaloniaFact]
    public void Vod_CategoryStripExposesRealHorizontalScrollbarWhenCategoriesOverflow()
    {
        var directory = TempDataDirectory.Create("vod-category-strip");
        using var services = new AppServices(directory);
        var main = new MainViewModel(services, true);
        main.AttachDispatcher(Dispatcher.UIThread);
        for (var i = 0; i < 30; i++)
            main.Vod.Categories.Add(new Category($"c{i}", $"很长很长的分类名称{i:00}"));
        main.Vod.Loading = false;
        var view = new VodView { DataContext = main };
        var window = new Window { Width = 900, Height = 400, Content = view };
        try
        {
            window.Show();
            Pump(window);
            var strip = view.FindControl<ScrollViewer>("CategoryStrip")!;
            // 分类条不再 Hidden：横向滚动条按需真实展示。
            Assert.Equal(ScrollBarVisibility.Auto, strip.HorizontalScrollBarVisibility);
            Assert.True(strip.Extent.Width > strip.Viewport.Width, "分类内容应溢出以实际需要横向滚动条");
            // 真实横向滚动条就在这条宿主上，而不是被 Hidden 掉。
            Assert.Contains(strip.GetVisualDescendants().OfType<ScrollBar>(),
                bar => bar.Orientation == Orientation.Horizontal && ReferenceEquals(bar.TemplatedParent, strip));
        }
        finally
        {
            window.Close();
            services.Dispose();
            TempDataDirectory.TryDelete(directory);
        }
    }

    [AvaloniaFact]
    public void Vod_SourcePopupAnchorsToButtonAndEllipsizesLongNameInNeutralColor()
    {
        var directory = TempDataDirectory.Create("vod-source-popup");
        using var services = new AppServices(directory);
        var main = new MainViewModel(services, true);
        main.AttachDispatcher(Dispatcher.UIThread);
        var view = new VodView { DataContext = main };
        var window = new Window { Width = 900, Height = 400, Content = view };
        try
        {
            window.Show();
            Pump(window);
            var selector = view.FindControl<Button>("SourceSelector")!;
            var menu = Assert.IsType<ContextMenu>(selector.ContextMenu);
            // 弹层锚在固定控件上展开，不跟随鼠标点位定位。
            Assert.NotEqual(PlacementMode.Pointer, menu.Placement);
            // 长站名固定宽度 + 省略号，并用中性色而不是按钮的主题蓝。
            var name = Assert.Single(selector.GetVisualDescendants().OfType<TextBlock>());
            Assert.True(name.Width > 0, "站名应有固定宽度");
            Assert.Equal(TextTrimming.CharacterEllipsis, name.TextTrimming);
            Assert.NotEqual(selector.Foreground, name.Foreground);
        }
        finally
        {
            window.Close();
            services.Dispose();
            TempDataDirectory.TryDelete(directory);
        }
    }

    [AvaloniaFact]
    public void Vod_PosterRowsFillAvailableWidthInsteadOfLeavingRightGap()
    {
        var directory = TempDataDirectory.Create("vod-poster-fill");
        using var services = new AppServices(directory);
        var main = new MainViewModel(services, true);
        main.AttachDispatcher(Dispatcher.UIThread);
        for (var i = 0; i < 30; i++)
            main.Vod.Items.Add(new MediaItem { Id = $"i{i}", Title = $"影片{i}" });
        main.Vod.SetGridWidth(900); // 先建立行数据源（列数确定后再断言行布局）
        main.Vod.Loading = false;
        var view = new VodView { DataContext = main };
        var window = new Window { Width = 1000, Height = 500, Content = view };
        try
        {
            window.Show();
            Pump(window);
            Assert.True(main.Vod.Columns > 0);
            var list = view.FindControl<ListBox>("PosterList")!;
            var row = list.GetVisualDescendants().OfType<UniformGrid>().First();
            // 行按当前列数均分宽度（不是固定卡片宽度 + 右内边距把滚动条贴右）。
            Assert.Equal(main.Vod.Columns, row.Columns);
            Assert.Equal(main.Vod.Columns, row.Children.Count);
            // 最后一格贴到行右边缘：末端没有「最后一列到右滚动条」的大块空白。
            var last = row.Children[^1];
            Assert.True(last.Bounds.Right >= row.Bounds.Width - 40,
                $"最后一列右边缘 {last.Bounds.Right:0.#} 距行宽 {row.Bounds.Width:0.#} 余白过大");
        }
        finally
        {
            window.Close();
            services.Dispose();
            TempDataDirectory.TryDelete(directory);
        }
    }
}
