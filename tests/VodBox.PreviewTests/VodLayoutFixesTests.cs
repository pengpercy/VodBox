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



    /// <summary>
    /// 站点下拉：按钮用中性底色（不再是整块主题蓝）；弹层与按钮同宽、最高 360 超出滚动（站点多时不撑满整屏）；
    /// 当前站点加粗，长站名省略号且悬停可看全名。
    /// </summary>


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
    /// <summary>筛选默认折叠成一行「筛选 · 当前值」，展开后才列出各组胶囊；改过的值显示在摘要里，可一键重置。</summary>
    [AvaloniaFact]
    public void Vod_FiltersAreCollapsedByDefaultAndSummarizeActiveValues()
    {
        var directory = TempDataDirectory.Create("vod-filters-collapsed");
        using var services = new AppServices(directory);
        var main = new MainViewModel(services, true);
        main.AttachDispatcher(Dispatcher.UIThread);
        foreach (var (key, name) in new[] { ("cat", "分类"), ("area", "地区"), ("type", "类型"), ("country", "国家"), ("year", "年代"), ("sort", "排序") })
            main.Vod.DynamicFilters.Add(new FilterChipGroup(new FilterGroup(key, name,
                [new FilterValue("全部", ""), new FilterValue(name + "A", "a"), new FilterValue(name + "B", "b")], "")));
        foreach (var name in new[] { "电影", "电视剧", "综艺", "动漫", "Top250" }) main.Vod.Categories.Add(new Category(name, name));
        main.Vod.Loading = false;
        var view = new VodView { DataContext = main };
        var window = new Window { Width = 1000, Height = 600, Content = view };
        try
        {
            window.Show();
            Pump(window);
            var groups = view.FindControl<ItemsControl>("FilterGroups")!;
            var toggle = view.FindControl<Button>("FiltersToggle")!;
            Assert.True(toggle.IsEffectivelyVisible);
            Assert.False(groups.IsEffectivelyVisible);
            Assert.Equal("全部", main.Vod.FilterSummary);
            Assert.False(view.FindControl<Button>("ResetFilters")!.IsVisible);
            // 「更多筛选」就在分类条里，紧跟最后一个分类。
            var strip = view.FindControl<ScrollViewer>("CategoryStrip")!;
            Assert.Contains(toggle, strip.GetVisualDescendants());
            Assert.Contains(toggle.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "更多筛选");

            toggle.Command!.Execute(null);
            Pump(window);
            Assert.True(groups.IsEffectivelyVisible);
            Assert.Contains("active", toggle.Classes);
            Assert.Contains(groups.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "排序");

            main.Vod.SelectFilter("area", "a");
            main.Vod.SelectFilter("year", "b");
            Pump(window);
            Assert.Equal("地区A · 年代B", main.Vod.FilterSummary);
            Assert.True(view.FindControl<Button>("ResetFilters")!.IsEffectivelyVisible);
            Assert.True(view.FindControl<Border>("ActiveFilterBadge")!.IsEffectivelyVisible);
            Assert.Equal(2, main.Vod.ActiveFilterCount);

            toggle.Command!.Execute(null);
            Pump(window);
            Assert.False(groups.IsEffectivelyVisible);
            Assert.DoesNotContain("active", toggle.Classes);
        }
        finally
        {
            window.Close();
            services.Dispose();
            TempDataDirectory.TryDelete(directory);
        }
    }
}
