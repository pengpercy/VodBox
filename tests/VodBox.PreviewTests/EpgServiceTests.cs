using System.Text;
using VodBox.Core;
using VodBox.Desktop.Services;
using Xunit;

namespace VodBox.PreviewTests;

/// <summary>
/// 内置节目表服务：后台下载 / 解压 / 解析 / 建索引 + 2 小时刷新、失败退避、
/// 请求去重与取消代次。所有网络都通过注入的 fetch，测试不触网。
/// </summary>
public sealed class EpgServiceTests
{
    private const string PrimaryXml =
        "<tv><channel id=\"CCTV1\"><display-name>CCTV1</display-name></channel>" +
        "<programme channel=\"CCTV1\" start=\"20261011080000 +0800\" stop=\"20261011090000 +0800\"><title>新闻</title></programme></tv>";

    private const string SupplementXml =
        "<tv><channel id=\"1\"><display-name>CCTV1</display-name></channel>" +
        "<programme channel=\"1\" start=\"20261011080000 +0800\" stop=\"20261011090000 +0800\"><title>新闻</title></programme></tv>";

    private static byte[] Bytes(string xml) => Encoding.UTF8.GetBytes(xml);

    private static Func<Uri, CancellationToken, Task<byte[]>> Routed(
        Func<Uri, bool>? shouldFail = null, Action? onCall = null) =>
        (url, _) =>
        {
            onCall?.Invoke();
            if (shouldFail?.Invoke(url) == true) throw new IOException("simulated failure");
            return Task.FromResult(Bytes(url.AbsoluteUri.Contains("51zmt") ? SupplementXml : PrimaryXml));
        };

    [Fact]
    public async Task SuccessfulRefreshBuildsIndexesAndRecordsLastSuccess()
    {
        var directory = TempDataDirectory.Create("epg-service");
        try
        {
            var service = new EpgService(directory, new FakePreferences(), Routed());

            Assert.True(await service.RefreshAsync(force: true, TestContext.Current.CancellationToken));

            Assert.Equal(EpgServiceState.Ready, service.State);
            Assert.True(service.HasData);
            Assert.NotNull(service.PrimaryIndex);
            Assert.NotNull(service.SupplementIndex);
            Assert.NotNull(service.LastSuccessAt);
            Assert.NotNull(service.LastAttemptAt);
            Assert.Equal("内置节目表", service.StatusLabel);
        }
        finally { TempDataDirectory.TryDelete(directory); }
    }

    [Fact]
    public async Task PrimaryFailureKeepsPreviouslyCachedPrimaryIndexInsteadOfBlankingEpd()
    {
        var directory = TempDataDirectory.Create("epg-service");
        try
        {
            var failPrimary = false;
            var service = new EpgService(directory, new FakePreferences(),
                Routed(url => failPrimary && url.AbsoluteUri.Contains("112114")));

            Assert.True(await service.RefreshAsync(force: true, TestContext.Current.CancellationToken));
            var primaryBefore = service.PrimaryIndex;

            failPrimary = true;
            Assert.False(await service.RefreshAsync(force: true, TestContext.Current.CancellationToken));

            // 数据错误保留有效缓存：主源索引必须是同一个对象，而不是被失败结果清空。
            Assert.Same(primaryBefore, service.PrimaryIndex);
            Assert.True(service.HasData);
            Assert.Equal(EpgServiceState.Failed, service.State);
            Assert.Equal("更新失败，使用缓存", service.StatusLabel);
        }
        finally { TempDataDirectory.TryDelete(directory); }
    }

    [Fact]
    public async Task ManualOverrideDisablesBuiltInAutomation()
    {
        var directory = TempDataDirectory.Create("epg-service");
        try
        {
            var prefs = new FakePreferences();
            prefs.Set(EpgService.ManualUrlKey, "https://example.com/manual.xml");
            var service = new EpgService(directory, prefs, Routed());

            Assert.True(service.IsManualOverride);
            Assert.False(await service.RefreshAsync(force: true, TestContext.Current.CancellationToken));
            Assert.False(service.HasData);
            Assert.Equal("手动订阅节目表", service.StatusLabel);

            prefs.Set(EpgService.ManualUrlKey, "");
            service.NotifyManualOverrideChanged();
            Assert.False(service.IsManualOverride);
            Assert.Equal("尚未加载节目表", service.StatusLabel);
        }
        finally { TempDataDirectory.TryDelete(directory); }
    }

    [Fact]
    public async Task ConcurrentRefreshRequestsAreDeduplicated()
    {
        var directory = TempDataDirectory.Create("epg-service");
        try
        {
            var gate = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;
            var service = new EpgService(directory, new FakePreferences(), (_, _) =>
            {
                Interlocked.Increment(ref calls);
                return gate.Task;
            });

            var first = service.RefreshAsync(force: true, TestContext.Current.CancellationToken);
            var second = service.RefreshAsync(force: true, TestContext.Current.CancellationToken);
            Assert.Same(first, second);

            gate.SetResult(Bytes(PrimaryXml));
            Assert.True(await first);
            Assert.Equal(2, calls); // 两个来源各取一次，没有被第二次请求重复下载
        }
        finally { TempDataDirectory.TryDelete(directory); }
    }

    [Fact]
    public async Task CancelledRefreshDoesNotCommitStaleResult()
    {
        var directory = TempDataDirectory.Create("epg-service");
        try
        {
            var gate = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            var service = new EpgService(directory, new FakePreferences(), (_, _) => gate.Task);

            var pending = service.RefreshAsync(force: true, TestContext.Current.CancellationToken);
            service.Cancel();
            gate.SetResult(Bytes(PrimaryXml));

            Assert.False(await pending);
            Assert.False(service.HasData);
        }
        finally { TempDataDirectory.TryDelete(directory); }
    }

    [Fact]
    public async Task FailedRefreshStartsBackoffWindowAndSkipsImmediateRetry()
    {
        var directory = TempDataDirectory.Create("epg-service");
        try
        {
            var failPrimary = false;
            var fetchCalls = 0;
            var service = new EpgService(directory, new FakePreferences(),
                Routed(url => failPrimary && url.AbsoluteUri.Contains("112114"), () => fetchCalls++));

            Assert.True(await service.RefreshAsync(force: true, TestContext.Current.CancellationToken));
            failPrimary = true;
            Assert.False(await service.RefreshAsync(force: true, TestContext.Current.CancellationToken));

            var afterFailure = fetchCalls;
            Assert.False(await service.RefreshAsync(force: false, TestContext.Current.CancellationToken)); // 退避窗口内直接返回
            Assert.Equal(afterFailure, fetchCalls);
        }
        finally { TempDataDirectory.TryDelete(directory); }
    }

    [Fact]
    public async Task EnsureFreshSkipsNetworkWithinRefreshInterval()
    {
        var directory = TempDataDirectory.Create("epg-service");
        try
        {
            var fetchCalls = 0;
            var service = new EpgService(directory, new FakePreferences(), Routed(onCall: () => fetchCalls++));

            Assert.True(await service.RefreshAsync(force: true, TestContext.Current.CancellationToken));
            var afterSuccess = fetchCalls;

            await service.EnsureFreshAsync(TestContext.Current.CancellationToken); // 距上次成功不足 2 小时
            Assert.Equal(afterSuccess, fetchCalls);
        }
        finally { TempDataDirectory.TryDelete(directory); }
    }

    [Fact]
    public async Task LoadCacheRebuildsIndexesWithoutNetwork()
    {
        var directory = TempDataDirectory.Create("epg-service");
        try
        {
            var writer = new EpgService(directory, new FakePreferences(), Routed());
            Assert.True(await writer.RefreshAsync(force: true, TestContext.Current.CancellationToken));

            var reader = new EpgService(directory, new FakePreferences(),
                (_, _) => throw new InvalidOperationException("读缓存不应联网"));
            await reader.LoadCacheAsync(TestContext.Current.CancellationToken);

            Assert.True(reader.HasData);
            Assert.NotNull(reader.PrimaryIndex);
            Assert.Equal(EpgServiceState.Ready, reader.State);

            // 缓存重建后仍能按来源内的 tvg-id 命中频道。
            var match = VodBox.Infrastructure.EpgChannelMatcher.Resolve(
                reader.PrimaryIndex!, new LiveChannel { Name = "CCTV1", TvgId = "CCTV1" }, out var key);
            Assert.Equal(VodBox.Infrastructure.EpgMatchKind.ByTvgId, match);
            Assert.Equal("CCTV1", key.ChannelId);
        }
        finally { TempDataDirectory.TryDelete(directory); }
    }

    /// <summary>测试用内存偏好，避免为一个键值接口拉起数据库。</summary>
    private sealed class FakePreferences : IPreferences
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
        public string GetString(string key, string fallback = "") => _values.GetValueOrDefault(key, fallback);
        public int GetInt(string key, int fallback = 0) => _values.TryGetValue(key, out var value) && int.TryParse(value, out var parsed) ? parsed : fallback;
        public bool GetBool(string key, bool fallback = false) => _values.TryGetValue(key, out var value) && bool.TryParse(value, out var parsed) ? parsed : fallback;
        public double GetDouble(string key, double fallback = 0) => _values.TryGetValue(key, out var value) && double.TryParse(value, out var parsed) ? parsed : fallback;
        public void Set(string key, string value) => _values[key] = value;
        public void Set(string key, int value) => _values[key] = value.ToString();
        public void Set(string key, bool value) => _values[key] = value.ToString();
        public void Set(string key, double value) => _values[key] = value.ToString();
    }
}
