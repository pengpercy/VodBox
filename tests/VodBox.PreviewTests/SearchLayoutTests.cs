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
        var a = new SourceInfo { Key = "a", Name = "甲站", Runtime = SourceRuntime.MacCms };
        var b = new SourceInfo { Key = "b", Name = "乙站", Runtime = SourceRuntime.MacCms };
        var bad = new SourceInfo { Key = "bad", Name = "坏站", Runtime = SourceRuntime.Node };
        static MediaItem Item(string id) => new() { Id = id, Title = id, Year = "2024" };
        async IAsyncEnumerable<AggregateSearchBatch> Stream(string _, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            yield return new AggregateSearchBatch(a, Enumerable.Range(0, 9).Select(i => Item("a" + i)).ToArray());
            yield return new AggregateSearchBatch(b, [Item("b0"), Item("b1")]);
            yield return new AggregateSearchBatch(bad, [], "超时（90s）");
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
            Assert.False(Named<Border>("SiteSidebar").IsEffectivelyVisible);
            Assert.True(window.GetVisualDescendants().OfType<StackPanel>().Single(panel => panel.Name == "EmptyState").IsEffectivelyVisible);
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "繁花" && text.IsEffectivelyVisible);

            main.Search.Keyword = "测试";
            await main.Search.RunSearchAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Layout();
            Assert.True(Named<Border>("SiteSidebar").IsEffectivelyVisible);
            Assert.Equal("坏站", Assert.Single(main.Search.Failures).SiteName);
            Assert.Equal("2 个站点 · 11 条结果", main.Search.Status);
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
}
