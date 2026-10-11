using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
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
