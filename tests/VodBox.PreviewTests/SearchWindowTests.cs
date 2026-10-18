using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VodBox.Core;
using VodBox.Infrastructure;
using VodBox.Desktop.Services;
using VodBox.Desktop.ViewModels;
using VodBox.Desktop.Views;
using Xunit;

namespace VodBox.PreviewTests;

/// <summary>
/// 独立搜索结果窗口：窗口去重/激活、关窗取消在途搜索、旧结果不回填新请求、主窗口关闭清理。
/// 侧栏搜索不再进入主窗口内的空白搜索页——提交只发请求，由主窗口开（或复用）一扇搜索窗口。
/// </summary>
public sealed class SearchWindowTests
{
    private static TaskCompletionSource<T> Gate<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task Until(Func<bool> condition, int milliseconds = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not met");
            await Task.Delay(20);
        }
    }

    private static MediaItem Item(string id) => new() { Id = id, Title = id, Poster = "https://example/" + id + ".jpg" };
    private static SourceInfo Site(string key, string name) => new() { Key = key, Name = name, Runtime = SourceRuntime.MacCms };
    private static IReadOnlyList<(SourceInfo Site, MediaItem Item)> Hit(string siteKey, string siteName, string id)
        => [(Site(siteKey, siteName), Item(id))];
    private static AggregateSearchResult Result(string siteKey, string siteName, string id, IReadOnlyList<(SourceInfo Site, string Error)>? failures = null)
        => new(Hit(siteKey, siteName, id), failures ?? []);

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), $"vodbox-searchwin-{Guid.NewGuid():N}");
        private readonly AppServices _services;
        public MainViewModel Main { get; }
        public Fixture()
        {
            _services = new AppServices(_directory);
            Main = new MainViewModel(_services, designTime: true);
            Main.AttachDispatcher(Dispatcher.UIThread);
        }
        public void Dispose()
        {
            Dispatcher.UIThread.RunJobs();
            _services.Dispose();
            Directory.Delete(_directory, recursive: true);
        }
    }

    /// <summary>
    /// 搜索结果页布局：左侧站点栏常驻（宽 220，无边框），分割线贯穿整个窗口高度；
    /// 「全部结果」与搜索框都在 macOS 交通灯（约 y≤28）下方，搜索框紧挨站点栏右侧。
    /// </summary>
    [AvaloniaFact]
    public void SearchFieldSitsNextToSidebarBelowMacTrafficLights()
    {
        using var fixture = new Fixture();
        var window = new SearchWindow { DataContext = fixture.Main, Width = 1040, Height = 720 };
        window.Show();
        try
        {
            window.UpdateLayout();
            T Named<T>(string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
            var field = Named<Grid>("SearchField");
            var origin = Avalonia.VisualExtensions.TranslatePoint(field, default, window)!.Value;
            Assert.Equal(240, origin.X, 1);
            Assert.Equal(380, field.Bounds.Width);
            if (OperatingSystem.IsMacOS()) Assert.True(origin.Y >= 28, $"搜索框不能高过交通灯：y={origin.Y}");

            var sidebar = Named<Border>("SiteSidebar");
            Assert.True(sidebar.IsEffectivelyVisible);
            Assert.Equal(default, sidebar.BorderThickness);
            var divider = Named<Border>("SidebarDivider");
            var top = Avalonia.VisualExtensions.TranslatePoint(divider, default, window)!.Value;
            Assert.Equal(0, top.Y, 1);
            Assert.Equal(window.Bounds.Height, divider.Bounds.Height, 1);

            var all = window.GetVisualDescendants().OfType<TextBlock>().First(text => text.Text == "全部结果");
            var allTop = Avalonia.VisualExtensions.TranslatePoint(all, default, window)!.Value.Y;
            if (OperatingSystem.IsMacOS()) Assert.InRange(allTop, 28, 60);
            // 只剩搜索框里内嵌按钮上的「搜索」两字，没有独立的标题文字。
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Text == "搜索" && text.IsEffectivelyVisible && !text.GetVisualAncestors().OfType<Button>().Any());
        }
        finally { window.Close(); }
    }

    private static void Key(Control target, Avalonia.Input.Key key)
        => target.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = key });

    private static void Click(Window window, Control target)
    {
        var center = Avalonia.VisualExtensions.TranslatePoint(target, new Avalonia.Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window)!.Value;
        Avalonia.Headless.HeadlessWindowExtensions.MouseDown(window, center, Avalonia.Input.MouseButton.Left);
        Avalonia.Headless.HeadlessWindowExtensions.MouseUp(window, center, Avalonia.Input.MouseButton.Left);
        Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
    }

    private static string[] Listed(SearchHistoryPopup popup)
        => ((IEnumerable<SuggestItem>)popup.FindControl<ItemsControl>("HistoryItems")!.ItemsSource!).Select(item => item.Text).ToArray();

    /// <summary>
    /// 搜索历史下拉（对齐 FongMi/TV）：聚焦即列出全部历史；输入按关键词过滤；单条删除不收起；
    /// Esc、搜索框失焦都收起；↓/回车可键盘选；点一条即回搜。
    /// </summary>
    [AvaloniaFact]
    public async Task FocusingSearchFieldDropsDownHistoryThatCanBeFilteredDeletedAndPicked()
    {
        using var fixture = new Fixture();
        var search = fixture.Main.Search;
        var searched = new List<string>();
        search.SetDetailedSearch((query, _) => { searched.Add(query); return Task.FromResult(Result("source", "站点", query)); });
        foreach (var word in new[] { "庆余年", "繁花", "三体", "庆余年第二季" }) search.SearchHistory.Add(word);
        var window = new SearchWindow { DataContext = fixture.Main };
        window.Show();
        try
        {
            window.UpdateLayout();
            var box = window.GetVisualDescendants().OfType<TextBox>().Single(text => text.Name == "ResultSearch");
            var popup = window.GetLogicalDescendants().OfType<SearchHistoryPopup>().Single();
            box.Focus();
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.True(popup.IsOpen);
            Assert.Equal(["庆余年", "繁花", "三体", "庆余年第二季"], Listed(popup));

            box.Text = "庆余";
            Assert.Equal(["庆余年", "庆余年第二季"], Listed(popup));

            box.Text = "";
            Key(box, Avalonia.Input.Key.Escape);
            Assert.False(popup.IsOpen);
            Key(box, Avalonia.Input.Key.Down);
            Assert.True(popup.IsOpen);
            search.DeleteSearchHistoryCommand.Execute("繁花");
            Assert.True(popup.IsOpen);
            Assert.DoesNotContain("繁花", Listed(popup));
            Assert.DoesNotContain("繁花", search.SearchHistory);

            // 焦点移出搜索框（如 Tab 到别的控件）即收起。
            var other = window.GetVisualDescendants().OfType<Button>().First(button => button.IsEffectivelyVisible && button.Focusable);
            other.Focus();
            Dispatcher.UIThread.RunJobs();
            Assert.False(popup.IsOpen);

            box.Focus();
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.True(popup.IsOpen);
            Key(box, Avalonia.Input.Key.Down);
            Key(box, Avalonia.Input.Key.Down);
            Key(box, Avalonia.Input.Key.Enter);
            await Until(() => searched.Contains("三体"));
            Assert.Equal("三体", search.Keyword);
            Assert.False(popup.IsOpen);
            Assert.Equal("三体", search.SearchHistory[0]);

            // 回车搜索后焦点仍在搜索框：再点一下搜索框重新列出历史。
            Click(window, box);
            Assert.True(popup.IsOpen);
            var row = popup.GetLogicalDescendants().OfType<Border>().First(border => border.Tag is SuggestItem { Text: "庆余年" });
            var center = Avalonia.VisualExtensions.TranslatePoint(row, new Avalonia.Point(40, row.Bounds.Height / 2), window)!.Value;
            Avalonia.Headless.HeadlessWindowExtensions.MouseDown(window, center, Avalonia.Input.MouseButton.Left);
            Avalonia.Headless.HeadlessWindowExtensions.MouseUp(window, center, Avalonia.Input.MouseButton.Left);
            await Until(() => searched.Contains("庆余年"));
            Assert.False(popup.IsOpen);
            Assert.Equal("庆余年", search.SearchHistory[0]);

            // 点下拉与搜索框以外的空白处：light dismiss 收起。
            Click(window, box);
            Assert.True(popup.IsOpen);
            var blank = new Avalonia.Point(window.Bounds.Width - 20, window.Bounds.Height - 20);
            Avalonia.Headless.HeadlessWindowExtensions.MouseDown(window, blank, Avalonia.Input.MouseButton.Left);
            Avalonia.Headless.HeadlessWindowExtensions.MouseUp(window, blank, Avalonia.Input.MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.False(popup.IsOpen);
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// 详情页两种宿主（主窗口给 MainViewModel、搜索窗口给独立 DetailViewModel）都不能让绑定强转失败：
    /// Avalonia 会吞掉这类异常，但调试器会在第一次抛出时中断，表现为页面「打不开」。
    /// </summary>
    [AvaloniaFact]
    public void DetailViewBindsCleanlyInBothHosts()
    {
        using var fixture = new Fixture();
        var seen = new List<string>();
        void Handler(object? s, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e)
        {
            if (e.Exception is InvalidCastException or NullReferenceException) seen.Add(e.Exception.Message);
        }
        AppDomain.CurrentDomain.FirstChanceException += Handler;
        try
        {
            foreach (object context in new object[] { fixture.Main, fixture.Main.Search.Detail })
            {
                // 先上屏、再给 DataContext：这是最容易让宿主 DataContext 先流进子元素的顺序。
                var view = new DetailView();
                var window = new Window { Width = 1000, Height = 700, Content = view };
                window.Show();
                window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
                view.DataContext = context;
                window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
                Assert.Same(context is MainViewModel main ? main.Detail : context,
                    view.GetVisualDescendants().OfType<ScrollViewer>().First(scroller => scroller.Name == "DetailRoot").DataContext);
                window.Close();
            }
        }
        finally { AppDomain.CurrentDomain.FirstChanceException -= Handler; }
        Assert.Empty(seen);
        Assert.NotSame(fixture.Main.Detail, fixture.Main.Search.Detail);
    }

    [AvaloniaFact]
    public void SubmitDedupesToOneWindowAndActivatesIt()
    {
        using var fixture = new Fixture();
        var service = new SearchWindowService(fixture.Main);
        fixture.Main.Search.SetDetailedSearch((_, _) => Task.FromResult(Result("source", "站点", "a")));

        Assert.False(service.IsOpen);
        var created = service.Submit("甲");
        Assert.False(created);           // 首次：新建一扇窗口
        Assert.True(service.IsOpen);
        var window = service.Window;
        Assert.NotNull(window);
        Assert.Equal("甲", fixture.Main.Search.Keyword);

        var reused = service.Submit("乙");
        Assert.True(reused);             // 再次提交：复用同一扇窗口（去重 + 激活）
        Assert.Same(window, service.Window);
        Assert.Equal("乙", fixture.Main.Search.Keyword);

        service.Close();
        Assert.False(service.IsOpen);
        Assert.Null(service.Window);
    }

    [AvaloniaFact]
    public async Task ClosingWindowCancelsInFlightSearchAndLateResultsCannotReachNewRequest()
    {
        using var fixture = new Fixture();
        var service = new SearchWindowService(fixture.Main);
        var late = Gate<AggregateSearchResult>();
        fixture.Main.Search.SetDetailedSearch((query, _) => query == "旧"
            ? late.Task
            : Task.FromResult(Result("new", "新站", "新")));

        // 旧请求在途（挂起）
        service.Submit("旧");
        await Until(() => fixture.Main.Search.Searching);

        // 关窗即取消搜索
        service.Close();
        Assert.False(fixture.Main.Search.Searching);
        Assert.False(service.IsOpen);

        // 新请求（新窗口）先完成
        service.Submit("新");
        await Until(() => !fixture.Main.Search.Searching && fixture.Main.Search.AllResults.Count == 1);

        // 放行旧请求的迟到结果 —— 已被取消，不得写进新请求
        late.SetResult(Result("old", "旧站", "旧"));
        await Task.Delay(120);

        var only = Assert.Single(fixture.Main.Search.AllResults);
        Assert.Equal("新", only.Id);
        Assert.Equal("新站", only.SourceName);
        Assert.DoesNotContain(fixture.Main.Search.AllResults, item => item.SourceName == "旧站");

        service.Close();
    }

    [AvaloniaFact]
    public async Task ReusedWindowSecondQueryKeepsSourceIdentityAndDropsStaleResults()
    {
        using var fixture = new Fixture();
        var service = new SearchWindowService(fixture.Main);
        var late = Gate<AggregateSearchResult>();
        fixture.Main.Search.SetDetailedSearch((query, _) => query == "甲"
            ? late.Task
            : Task.FromResult(Result("b", "乙站", "b1")));

        service.Submit("甲");
        await Until(() => fixture.Main.Search.Searching);
        // 同一扇窗里再提交：新请求取消旧请求
        service.Submit("乙");
        await Until(() => !fixture.Main.Search.Searching && fixture.Main.Search.AllResults.Count == 1);
        late.SetResult(Result("a", "甲站", "a1"));
        await Task.Delay(120);

        // 结果保留自己站点的来源身份，旧站结果不会串进新查询。
        var only = Assert.Single(fixture.Main.Search.AllResults);
        Assert.Equal("b1", only.Id);
        Assert.Equal("乙站", only.SourceName);

        service.Close();
    }

    [AvaloniaFact]
    public void SidebarSubmitOpensOneWindowAndMainWindowCloseCleansItUp()
    {
        using var fixture = new Fixture();
        fixture.Main.Search.SetDetailedSearch((_, _) => Task.FromResult(Result("source", "站点", "a")));
        var window = new VodBox.Desktop.MainWindow { DataContext = fixture.Main };
        window.Show();
        try
        {
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Assert.Null(window.OpenSearchWindow);

            // 侧栏搜索框回车 → 打开独立搜索窗口（不切主窗口页面）
            fixture.Main.SearchKeyword = "庆余年";
            fixture.Main.SubmitSearchCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            var searchWindow = window.OpenSearchWindow;
            Assert.NotNull(searchWindow);
            Assert.Equal("庆余年", fixture.Main.Search.Keyword);
            Assert.Equal(AppPage.Home, fixture.Main.Page);

            // 再次提交：复用同一扇窗口（去重）
            fixture.Main.SearchKeyword = "梦华录";
            fixture.Main.SubmitSearchCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Same(searchWindow, window.OpenSearchWindow);

            // 主窗口关闭清理：搜索窗口一并关闭
            window.Close();
            Dispatcher.UIThread.RunJobs();
            Assert.False(searchWindow!.IsVisible);
            Assert.Null(window.OpenSearchWindow);
        }
        finally { if (window.IsVisible) window.Close(); }
    }

    [AvaloniaFact]
    public void EmptyKeywordDoesNotOpenAWindow()
    {
        using var fixture = new Fixture();
        var service = new SearchWindowService(fixture.Main);
        Assert.False(service.Submit("   "));
        Assert.False(service.IsOpen);

        var window = new VodBox.Desktop.MainWindow { DataContext = fixture.Main };
        window.Show();
        try
        {
            fixture.Main.SearchKeyword = "   ";
            fixture.Main.SubmitSearchCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Null(window.OpenSearchWindow);
        }
        finally { window.Close(); }
    }
}
