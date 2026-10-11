using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using VodBox.Core;
using VodBox.Desktop.Services;
using VodBox.Desktop.ViewModels;
using Xunit;

namespace VodBox.PreviewTests;

/// <summary>
/// 点播「虚拟化无限滚动」的分页回归：滚到底 append、去重、空页/末页停止、
/// 并发触发合并为一次请求、换分类取消在途页面且陈旧写入不回填。
/// 用可控异步闸门的假源复现「迟到旧页」与「重复触发」。
/// </summary>
public sealed class VodInfiniteScrollTests
{
    private static Task Done(Task task) => task.WaitAsync(TimeSpan.FromSeconds(10));
    private static MediaItem Item(string id) => new() { Id = id, Title = id };
    private static TaskCompletionSource<MediaPage> Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>等待条件成立；期间持续泵 UI 队列（VM 的回填都在 UI 线程上排队）。</summary>
    private static async Task PumpUntil(Func<bool> condition, int milliseconds = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (!condition())
        {
            Dispatcher.UIThread.RunJobs();
            if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not met");
            await Task.Delay(20);
        }
    }

    [AvaloniaFact]
    public async Task Vod_ScrollToBottomAppendsNextPageDedupesAndStopsAtEnd()
    {
        var directory = TempDataDirectory.Create("vod-infinite");
        using var services = new AppServices(directory);
        var main = new MainViewModel(services, true);
        main.AttachDispatcher(Dispatcher.UIThread);
        var source = new FakeSource();
        try
        {
            source.Items = (_, page, _, _) => page switch
            {
                1 => Task.FromResult(new MediaPage([Item("a"), Item("b")], 1, 3)),
                2 => Task.FromResult(new MediaPage([Item("a"), Item("c")], 2, 3)), // "a" 重复到达
                _ => Task.FromResult(new MediaPage([], 3, 3)),                      // 空页
            };
            var vod = new VodViewModel(services, main, () => source);

            await Done(vod.LoadAsync());
            Assert.Equal(["a", "b"], vod.Items.Select(item => item.Id));
            Assert.True(vod.HasMore);
            Assert.Equal(VodFooterState.Hidden, vod.FooterState);

            // 下一页 append：重复 id 不再加入，已加载内容保留（滚动位置与虚拟化不重置）。
            await Done(vod.LoadMoreAsync());
            Assert.Equal(["a", "b", "c"], vod.Items.Select(item => item.Id));
            Assert.Equal(2, vod.Page);
            Assert.True(vod.HasMore);

            // 空页 → 明确停止：页脚给出终点，不再继续请求。
            await Done(vod.LoadMoreAsync());
            Assert.Equal(3, vod.Page);
            Assert.Equal(["a", "b", "c"], vod.Items.Select(item => item.Id));
            Assert.False(vod.HasMore);
            Assert.Equal(VodFooterState.End, vod.FooterState);

            var calls = source.ItemCalls;
            await Done(vod.LoadMoreAsync());
            Assert.Equal(calls, source.ItemCalls); // 终点后不再发请求（无无穷请求）
            Assert.Equal(VodFooterState.End, vod.FooterState);
        }
        finally
        {
            services.Dispose();
            TempDataDirectory.TryDelete(directory);
        }
    }

    [AvaloniaFact]
    public async Task Vod_RepeatedScrollTriggersCoalesceIntoOneInFlightRequest()
    {
        var directory = TempDataDirectory.Create("vod-coalesce");
        using var services = new AppServices(directory);
        var main = new MainViewModel(services, true);
        main.AttachDispatcher(Dispatcher.UIThread);
        var source = new FakeSource();
        try
        {
            var gate = Gate();
            source.Items = (_, page, _, _) => page == 1
                ? Task.FromResult(new MediaPage([Item("a1")], 1, 4))
                : gate.Task;
            var vod = new VodViewModel(services, main, () => source);

            await Done(vod.LoadAsync());
            var first = vod.LoadMoreAsync();
            var second = vod.LoadMoreAsync(); // 同一时刻的第二次触发必须被合并，不得另发请求

            Assert.True(vod.Paging);
            Assert.Equal(2, source.ItemCalls); // 1 次首屏 + 1 次翻页
            gate.SetResult(new MediaPage([Item("a2")], 2, 4));
            await Done(Task.WhenAll(first, second));

            Assert.Equal(["a1", "a2"], vod.Items.Select(item => item.Id));
            Assert.Equal(2, vod.Page);
            Assert.False(vod.Paging);
        }
        finally
        {
            services.Dispose();
            TempDataDirectory.TryDelete(directory);
        }
    }

    [AvaloniaFact]
    public async Task Vod_CategorySwitchCancelsInFlightPageAndStalePageCannotAppend()
    {
        var directory = TempDataDirectory.Create("vod-stale-page");
        using var services = new AppServices(directory);
        var main = new MainViewModel(services, true);
        main.AttachDispatcher(Dispatcher.UIThread);
        var source = new FakeSource();
        try
        {
            var gate = Gate();
            var pageToken = default(CancellationToken);
            var captured = false;
            source.Items = (category, page, _, ct) => (category, page) switch
            {
                ("a", 1) => Task.FromResult(new MediaPage([Item("a1")], 1, 5)),
                ("a", 2) => Capture(ct),
                _ => Task.FromResult(new MediaPage([Item("b1")], 1, 5)),
            };
            Task<MediaPage> Capture(CancellationToken ct)
            {
                pageToken = ct;
                captured = true;
                return gate.Task;
            }
            var vod = new VodViewModel(services, main, () => source);

            await Done(vod.LoadAsync());
            Assert.Equal("a1", Assert.Single(vod.Items).Id);

            var stale = vod.LoadMoreAsync();
            Assert.True(vod.Paging);
            await PumpUntil(() => captured);
            Assert.False(pageToken.IsCancellationRequested);

            // 切分类 = 新代次：取消在途页面请求。
            vod.SelectedCategory = new Category("b", "分类B");
            Assert.True(pageToken.IsCancellationRequested);
            await PumpUntil(() => vod.Items.Count == 1 && vod.Items[0].Id == "b1");

            // 旧页面迟到：代次门控丢弃陈旧写入，不得 append 到新分类。
            gate.SetResult(new MediaPage([Item("a2")], 2, 5));
            await Done(stale);
            Assert.Equal("b1", Assert.Single(vod.Items).Id);
            Assert.Equal(1, vod.Page);
            Assert.False(vod.Paging);
            Assert.NotEqual(VodFooterState.Error, vod.FooterState);
        }
        finally
        {
            services.Dispose();
            TempDataDirectory.TryDelete(directory);
        }
    }

    [AvaloniaFact]
    public async Task Vod_FailedPageStopsWithRetryFooterAndKeepsLoadedContent()
    {
        var directory = TempDataDirectory.Create("vod-page-failure");
        using var services = new AppServices(directory);
        var main = new MainViewModel(services, true);
        main.AttachDispatcher(Dispatcher.UIThread);
        var source = new FakeSource();
        try
        {
            var attempts = 0;
            source.Items = (_, page, _, _) => page == 1
                ? Task.FromResult(new MediaPage([Item("a1"), Item("a2")], 1, 3))
                : ++attempts == 1
                    ? Task.FromException<MediaPage>(new IOException("next page failed"))
                    : Task.FromResult(new MediaPage([Item("a3")], 2, 3));
            var vod = new VodViewModel(services, main, () => source);

            await Done(vod.LoadAsync());
            await Done(vod.LoadMoreAsync());

            // 失败：保留已加载内容、页脚转为可重试，页码不推进。
            Assert.Equal(["a1", "a2"], vod.Items.Select(item => item.Id));
            Assert.Equal(VodFooterState.Error, vod.FooterState);
            Assert.True(vod.FooterCanRetry);
            Assert.Equal(1, vod.Page);
            Assert.False(vod.Paging);

            // 重试复用同一条 append 路径；成功后页脚离开错误态。
            await Done(vod.RetryFooterCommand.ExecuteAsync(null));
            Assert.Equal(["a1", "a2", "a3"], vod.Items.Select(item => item.Id));
            Assert.Equal(2, vod.Page);
            Assert.NotEqual(VodFooterState.Error, vod.FooterState);
        }
        finally
        {
            services.Dispose();
            TempDataDirectory.TryDelete(directory);
        }
    }

    /// <summary>可控假源：分类固定 [a]，条目按页返回，记录调用次数便于断言「不重复请求」。</summary>
    private sealed class FakeSource : IContentSource
    {
        public string Key => "fake";
        public string Name => "测试站";
        public int ItemCalls { get; private set; }
        public Func<string, int, IReadOnlyDictionary<string, string>?, CancellationToken, Task<MediaPage>> Items { get; set; } =
            (_, _, _, _) => Task.FromResult(new MediaPage([], 1, 1));

        public Task<IReadOnlyList<Category>> GetCategoriesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Category>>([new Category("a", "分类A")]);
        public Task<MediaPage> GetHomeAsync(CancellationToken ct = default) => Task.FromResult(new MediaPage([], 1, 1));
        public Task<MediaPage> GetItemsAsync(string categoryId, int page, IReadOnlyDictionary<string, string>? filters, CancellationToken ct = default)
        {
            ItemCalls++;
            return Items(categoryId, page, filters, ct);
        }
        public Task<MediaDetail> GetDetailAsync(string mediaId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<MediaPage> SearchAsync(string query, int page, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
