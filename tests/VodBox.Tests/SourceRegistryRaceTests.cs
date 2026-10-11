using VodBox.Core;
using VodBox.Infrastructure;
using Xunit;

namespace VodBox.Tests;

public sealed class SourceRegistryRaceTests
{
    [Fact]
    public async Task UnsupportedAndDuplicateSourcesHaveVisibleImportWarnings()
    {
        var config=new TvBoxConfig{Sites=[
            new TvBoxSite{Key="unsupported",Name="未知Java",Api="csp_NoSuchPlugin",Type=3},
            new TvBoxSite{Key="same",Name="首项",Api="https://example.com/a",Type=1},
            new TvBoxSite{Key="same",Name="重复项",Api="https://example.com/b",Type=1},
        ]};
        using var registry=new SourceRegistry((_,_)=>Task.FromResult(config));
        await registry.LoadConfigAsync("test",TestContext.Current.CancellationToken);
        Assert.Equal("首项",Assert.Single(registry.Sources).Name);
        Assert.Contains(registry.ImportWarnings,warning=>warning.Contains("未知Java"));
        Assert.Contains(registry.ImportWarnings,warning=>warning.Contains("重复"));
    }

    [Fact]
    public async Task OlderConfigurationCannotOverwriteNewerSelection()
    {
        var old=new TaskCompletionSource<TvBoxConfig>(TaskCreationOptions.RunContinuationsAsynchronously);
        static TvBoxConfig Config(string key)=>new(){Sites=[new TvBoxSite {Key=key,Name=key,Api="https://example.com/api",Type=1}]};
        using var registry=new SourceRegistry((url,_)=>url=="old"?old.Task:Task.FromResult(Config("new")));
        var first=registry.LoadConfigAsync("old",TestContext.Current.CancellationToken);
        await registry.LoadConfigAsync("new",TestContext.Current.CancellationToken);
        old.SetResult(Config("old"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>first);
        Assert.Equal("new",Assert.Single(registry.Sources).Key);
    }

    /// <summary>
    /// 两个离线源：csp_Push 用一个合法 URL 必然命中 1 条（回显查询）；MacCMS 站点用非 HTTP(S) 的 api，
    /// SearchAsync 会抛「必须是有效的 HTTP(S) 地址」——不触网，失败稳定可复现。
    /// </summary>
    private static TvBoxConfig StreamingConfig() => new()
    {
        Sites =
        [
            new TvBoxSite { Key = "push", Name = "推送站", Api = "csp_Push", Type = 3 },
            new TvBoxSite { Key = "broken", Name = "坏站", Api = "file:///not-http", Type = 1 },
        ],
    };

    private const string StreamingQuery = "https://example.invalid/video.mp4";

    [Fact]
    public async Task StreamingSearchEmitsOneBatchPerSiteAndLabelsEachBatchWithItsSource()
    {
        using var registry = new SourceRegistry((_, _) => Task.FromResult(StreamingConfig()));
        await registry.LoadConfigAsync("test", TestContext.Current.CancellationToken);
        Assert.Equal(2, registry.SearchTargets.Count);

        var batches = new List<AggregateSearchBatch>();
        await foreach (var batch in registry.SearchStreamAsync(StreamingQuery, TestContext.Current.CancellationToken))
            batches.Add(batch);

        // 流式：每个站点恰好产出一批（命中或失败），批次里带上站点身份。
        Assert.Equal(2, batches.Count);
        Assert.Equal(["broken", "push"], batches.Select(batch => batch.Site.Key).Order().ToArray());

        var push = batches.Single(batch => batch.Site.Key == "push");
        Assert.False(push.Failed);
        Assert.Equal("推送站", push.Site.Name);
        Assert.Equal(StreamingQuery, Assert.Single(push.Items).Id);

        var broken = batches.Single(batch => batch.Site.Key == "broken");
        Assert.True(broken.Failed);
        Assert.Equal("坏站", broken.Site.Name);
        Assert.Empty(broken.Items);
        Assert.False(string.IsNullOrEmpty(broken.Error));
    }

    [Fact]
    public async Task AggregateSearchKeepsSuccessfulSitesWhenOtherSitesFail()
    {
        using var registry = new SourceRegistry((_, _) => Task.FromResult(StreamingConfig()));
        await registry.LoadConfigAsync("test", TestContext.Current.CancellationToken);

        var aggregate = await registry.SearchWithFailuresAsync(StreamingQuery, TestContext.Current.CancellationToken);

        // 部分失败：坏站进入 Failures，推送站的命中照常保留且带正确来源。
        var hit = Assert.Single(aggregate.Results);
        Assert.Equal("push", hit.Site.Key);
        Assert.Equal("推送站", hit.Site.Name);
        Assert.Equal(StreamingQuery, hit.Item.Id);

        var failure = Assert.Single(aggregate.Failures);
        Assert.Equal("broken", failure.Site.Key);
        Assert.False(string.IsNullOrEmpty(failure.Error));

        // SearchAllAsync 只是丢掉失败原因，成功结果不变。
        var all = await registry.SearchAllAsync(StreamingQuery, TestContext.Current.CancellationToken);
        Assert.Equal("推送站", Assert.Single(all).Site.Name);
    }
}
