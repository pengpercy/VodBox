using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VodBox.Core;
using VodBox.Desktop.Design;
using VodBox.Desktop.Services;
using VodBox.Desktop.ViewModels;
using VodBox.Desktop.Views;
using VodBox.Infrastructure;
using Xunit;

namespace VodBox.PreviewTests;

public sealed class SearchLayoutTests
{
    [AvaloniaFact]
    public void ResizeRechunksWithoutLosingOrReorderingResults()
    {
        var search = DesignData.Main.Search;
        search.Aggregate = false; // 设计数据只填了逐条结果
        var selected = search.SelectedSite;
        search.SelectedSite = null;
        var expected = search.AllResults.ToArray();
        Assert.NotEmpty(expected);
        try
        {
            foreach (var (width, columns) in new[] { (400d, 2), (800d, 4), (1200d, 6), (180d, 1) })
            {
                search.SetResultWidth(width);
                Assert.All(search.ResultRows, row => Assert.InRange(row.Items.Count, 1, columns));
                Assert.Equal(Math.Min(columns, expected.Length), search.ResultRows[0].Items.Count);
                Assert.Equal(expected, search.ResultRows.SelectMany(row => row.Items));
            }
        }
        finally { search.SelectedSite = selected; search.SetResultWidth(956); }
    }

    /// <summary>
    /// 搜索结果页布局约定：空态不显示站点栏、列出最近搜索；有结果时卡片铺满一行（右侧不留大块空白）；
    /// 失败站点单独列出；选中单个站点后卡片不再重复来源，「全部结果」可一键回到全部。
    /// </summary>
    [AvaloniaFact]
    public async Task ResultsPageKeepsSidebarCardsAndFailuresTidy()
    {
        var directory = Path.Combine(Path.GetTempPath(), "vodbox-search-layout-" + Guid.NewGuid().ToString("N"));
        using var services = new AppServices(directory);
        var main = new MainViewModel(services, designTime: true);
        main.AttachDispatcher(Dispatcher.UIThread);
        var a = new ApiSite { Key = "a", Name = "甲站", Api = "https://a.example/api" };
        var b = new ApiSite { Key = "b", Name = "乙站", Api = "https://b.example/api" };
        var bad = new ApiSite { Key = "bad", Name = "坏站", Api = "https://bad.example/api" };
        static VodResult Item(string id) => new() { Id = id, Title = id, Year = "2024", SourceKey = "a", SourceName = "甲站",
            Lines = [new PlaybackLine("l", "l", [new Episode("1", "1", "https://example/1.m3u8"), new Episode("2", "2", "https://example/2.m3u8")])] };
        async IAsyncEnumerable<SourceSearchBatch> Stream(string _, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            yield return new SourceSearchBatch(a, Enumerable.Range(0, 9).Select(i => Item("a" + i)).ToArray());
            yield return new SourceSearchBatch(b, [Item("b0"), Item("b1")]);
            yield return new SourceSearchBatch(bad, [], "超时（90s）");
        }
        main.Search.SetSearchStream(Stream, 3);
        main.Search.SearchHistory.Add("繁花");
        var window = new SearchWindow { DataContext = main, Width = 1040, Height = 720 };
        window.Show();
        void Layout() { for (var i = 0; i < 4; i++) { window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); } }
        T Named<T>(string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
        try
        {
            Layout();
            Assert.True(Named<Border>("SiteSidebar").IsEffectivelyVisible);
            Assert.True(window.GetVisualDescendants().OfType<StackPanel>().Single(panel => panel.Name == "EmptyState").IsEffectivelyVisible);
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "繁花" && text.IsEffectivelyVisible);

            main.Search.Keyword = "测试";
            await main.Search.RunSearchAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Layout();
            Assert.True(Named<Border>("SiteSidebar").IsEffectivelyVisible);
            Assert.Equal("坏站", Assert.Single(main.Search.Failures).SiteName);
            // 未返回结果的站点默认折叠，点标题展开。
            Assert.True(Named<Button>("FailuresToggle").IsEffectivelyVisible);
            Assert.False(Named<ItemsControl>("FailedSites").IsVisible);
            Named<Button>("FailuresToggle").Command!.Execute(null);
            Layout();
            Assert.True(Named<ItemsControl>("FailedSites").IsEffectivelyVisible);
            Assert.Contains(Named<ItemsControl>("FailedSites").GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "坏站");
            Assert.Equal("2 个源 · 11 部影片 · 11 条结果", main.Search.Status);
            var list = window.GetVisualDescendants().OfType<ListBox>().Single(box => box.Name == "ResultsList");
            var cards = list.GetVisualDescendants().OfType<Border>().Where(border => border.Classes.Contains("resultCard")).ToArray();
            var firstRow = cards.Where(card => Math.Abs(card.TranslatePoint(default, list)!.Value.Y - cards[0].TranslatePoint(default, list)!.Value.Y) < 1).ToArray();
            Assert.Equal(main.Search.Columns, firstRow.Length);
            var right = firstRow.Max(card => card.TranslatePoint(new Avalonia.Point(card.Bounds.Width, 0), list)!.Value.X);
            Assert.InRange(list.Bounds.Width - right, 0, SearchViewModel.CardGap + 8);
            Assert.Contains(list.GetVisualDescendants().OfType<TextBlock>(), text => text.Classes.Contains("cardSource") && text.IsEffectivelyVisible);

            var sites = window.GetVisualDescendants().OfType<ListBox>().Single(box => box.Name == "SiteList");
            sites.SelectedIndex = 1;
            Layout();
            Assert.Equal(2, main.Search.ResultRows.Sum(row => row.Items.Count));
            Assert.DoesNotContain(list.GetVisualDescendants().OfType<TextBlock>(), text => text.Classes.Contains("cardSource") && text.IsEffectivelyVisible);
            Named<Button>("AllSitesButton").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Layout();
            Assert.True(main.Search.IsAllSelected);
            Assert.Null(sites.SelectedItem);
            Assert.Equal(11, main.Search.ResultRows.Sum(row => row.Items.Count));
        }
        finally { window.Close(); }
    }
    /// <summary>
    /// 流式到达：后到的源只给网格追加新卡片，先到的卡片控件原地保留（不整表重建、不闪）；
    /// 新卡片排入入场动画队列，侧栏站点逐个追加。
    /// </summary>
    [AvaloniaFact]
    public async Task StreamingResultsAppendCardsWithoutRebuildingExistingOnes()
    {
        var directory = Path.Combine(Path.GetTempPath(), "vodbox-search-stream-" + Guid.NewGuid().ToString("N"));
        using var services = new AppServices(directory);
        var main = new MainViewModel(services, designTime: true);
        main.AttachDispatcher(Dispatcher.UIThread);
        var a = new ApiSite { Key = "a", Name = "甲站", Api = "https://a.example/api" };
        var b = new ApiSite { Key = "b", Name = "乙站", Api = "https://b.example/api" };
        static VodResult Item(string id, string source) => new() { Id = id, Title = id, Year = "2024", SourceKey = source, SourceName = source,
            Lines = [new PlaybackLine("l", "l", [new Episode("1", "1", "https://example/1.m3u8"), new Episode("2", "2", "https://example/2.m3u8")])] };
        var gate = new TaskCompletionSource();
        var firstDone = new TaskCompletionSource();
        async IAsyncEnumerable<SourceSearchBatch> Stream(string _, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            yield return new SourceSearchBatch(a, [Item("x0", "a"), Item("x1", "a"), Item("x2", "a")]);
            firstDone.TrySetResult();
            await gate.Task;
            // x0 也出现在乙站（聚合卡片变为「2 个源」），另加两部新片。
            yield return new SourceSearchBatch(b, [Item("x0", "b"), Item("y0", "b"), Item("y1", "b")]);
        }
        main.Search.SetSearchStream(Stream, 2);
        var window = new SearchWindow { DataContext = main, Width = 1040, Height = 720 };
        window.Show();
        void Layout() { for (var i = 0; i < 4; i++) { window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); } }
        Border Card(string title) => window.GetVisualDescendants().OfType<Border>()
            .Single(border => border.Classes.Contains("resultCard") && border.Tag is MediaItem item && item.Title == title);
        try
        {
            main.Search.Keyword = "测试";
            var run = main.Search.RunSearchAsync();
            while (!firstDone.Task.IsCompleted) { Dispatcher.UIThread.RunJobs(); await Task.Delay(5); }
            Layout();
            var x1 = Card("x1");
            var x2 = Card("x2");
            var rowsBefore = main.Search.ResultRows[0];
            gate.SetResult();
            await run.WaitAsync(TimeSpan.FromSeconds(10));
            Layout();
            Assert.Same(rowsBefore, main.Search.ResultRows[0]);
            Assert.Same(x1, Card("x1"));
            Assert.Same(x2, Card("x2"));
            Assert.Equal(["x0", "x1", "x2", "y0", "y1"], main.Search.ResultRows.SelectMany(row => row.Items).Select(item => item.Title));
            Assert.Equal("2 个源", main.Search.ResultRows[0].Items[0].SourceName);
            // 新卡片挂上时已取走入场序号（动画已排队），不会残留到下次滚动回收再播。
            Assert.False(main.Search.TakeEntrance(main.Search.ResultRows.SelectMany(row => row.Items).Single(item => item.Title == "y0"), out _));
            Assert.Equal(["甲站", "乙站"], main.Search.SiteResults.Select(site => site.SiteName));
        }
        finally { window.Close(); }
    }
}
