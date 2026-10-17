using VodBox.Core;
using VodBox.Infrastructure;
using Xunit;

namespace VodBox.Tests;

public sealed class XPathNoticeSourceTests
{
    [Fact]
    public async Task NullRuleXPathIsAnInformationalSourceWithoutVideoCards()
    {
        var config = ConfigLoader.Parse("""{"sites":[{"key":"nav","name":"导航 https://example.invalid/","api":"csp_XPath"},{"key":"warning","name":"请勿相信广告","api":"csp_XPath","ext":null}]}""")!;
        var sources = ConfigLoader.ToSources(config);
        Assert.Equal(2, sources.Count);
        using var lifetime = new CancellationTokenSource();
        foreach (var info in sources)
        {
            var source = Assert.IsType<XPathNoticeSource>(NativeSpiders.Create(info));
            Assert.Contains(info.Name, source.Notice);
            Assert.Empty(await source.GetCategoriesAsync(TestContext.Current.CancellationToken));
            Assert.Empty((await source.GetHomeAsync(TestContext.Current.CancellationToken)).Items);
            Assert.Empty((await source.SearchAsync("test", 1, TestContext.Current.CancellationToken)).Items);
            await Assert.ThrowsAsync<NotSupportedException>(() => source.GetDetailAsync("anything", TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task ScrapingXPathRemainsUnsupportedAndGuardBecomesAnInformationalNotice()
    {
        var config = ConfigLoader.Parse("""{"sites":[{"key":"rules","name":"规则","api":"csp_XPath","ext":{"homeUrl":"https://example.invalid"}},{"key":"guard","name":"提醒","api":"csp_XPathGuard"}]}""")!;
        // 带抓取规则的 csp_XPath 不是空规则，固定插件里也没有对应 XPath 实现——保持不支持。
        Assert.False(NativeSpiders.IsSupported("csp_XPath"));
        // csp_XPathGuard 是配置里的提醒项：作为信息源登记，不伪造内容，也不冒充 XPath 抓取。
        Assert.True(NativeSpiders.IsSupported("csp_XPathGuard"));
        var sources = ConfigLoader.ToSources(config);
        var guard = Assert.Single(sources);
        Assert.Equal("guard", guard.Key);
        var source = Assert.IsType<XPathGuardNoticeSource>(NativeSpiders.Create(guard));
        Assert.Contains("提醒", source.Notice);
        Assert.Empty(await source.GetCategoriesAsync(TestContext.Current.CancellationToken));
        Assert.Empty((await source.GetHomeAsync(TestContext.Current.CancellationToken)).Items);
        await Assert.ThrowsAsync<NotSupportedException>(() => source.GetDetailAsync("anything", TestContext.Current.CancellationToken));
    }
}
