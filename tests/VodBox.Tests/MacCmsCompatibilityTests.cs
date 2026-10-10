using VodBox.Core;
using VodBox.Infrastructure;
using Xunit;

namespace VodBox.Tests;

public sealed class MacCmsCompatibilityTests
{
    private static MacCmsSource Source(string api, Func<string, CancellationToken, Task<string>> fetch) =>
        new(new SourceInfo { Key = "cms", Name = "采集", Runtime = SourceRuntime.MacCms, Api = api }, fetch);

    [Fact]
    public async Task SearchReplacesOldOperationParametersAndRetainsCustomQuery()
    {
        string? requested = null;
        var source = Source("https://example.invalid/api?AC=list&pg=99&ids=old&t=5&wd=old&token=opaque%2Bvalue#fragment", (url, _) =>
        {
            requested = url;
            return Task.FromResult("{\"page\":2,\"pagecount\":8,\"list\":[]}");
        });
        await source.SearchAsync("测试 & 空格", 2, TestContext.Current.CancellationToken);
        var uri = new Uri(requested!);
        Assert.Empty(uri.Fragment);
        Assert.Contains("token=opaque%2Bvalue", uri.Query);
        Assert.Contains("ac=videolist", uri.Query);
        Assert.Contains("pg=2", uri.Query);
        Assert.Contains("wd=" + Uri.EscapeDataString("测试 & 空格"), uri.Query);
        Assert.DoesNotContain("ids=", uri.Query);
        Assert.DoesNotContain("t=5", uri.Query);
        Assert.DoesNotContain("pg=99", uri.Query);
    }

    [Fact]
    public async Task DetailSelectsRequestedIdInsteadOfFirstResponseEntry()
    {
        var source = Source("https://example.invalid/api", (_, _) => Task.FromResult("""{"list":[{"vod_id":"wrong","vod_name":"错误条目"},{"vod_id":"right","vod_name":"正确条目"}]}"""));
        Assert.Equal("right", (await source.GetDetailAsync("right", TestContext.Current.CancellationToken)).Item.Id);
        await Assert.ThrowsAsync<InvalidDataException>(() => source.GetDetailAsync("missing", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task NullListsAndClassesAreEmptyAndBomJsonIsAccepted()
    {
        var source = Source("https://example.invalid/api", (_, _) => Task.FromResult("\uFEFF{\"class\":null,\"list\":null,\"page\":3,\"pagecount\":0}"));
        Assert.Empty(await source.GetCategoriesAsync(TestContext.Current.CancellationToken));
        var page = await source.SearchAsync("测试", 3, TestContext.Current.CancellationToken);
        Assert.Empty(page.Items);
        Assert.Equal(3, page.PageCount);
    }

    [Fact]
    public void MissingFlagsBareUrlsAndDuplicateFlagsRetainAllPlayableLines()
    {
        var detail = MacCmsSource.ToDetail(new MacCmsVod
        {
            VodId = "1", VodName = "测试", VodPlayFrom = "mp4$$$mp4",
            VodPlayUrl = "https://example.invalid/one.mp4$$$第2集$https://example.invalid/two.mp4$$$第3集$https://example.invalid/three.mp4"
        });
        Assert.Equal(3, detail.Lines.Count);
        Assert.Equal(3, detail.Lines.Select(line => line.Id).Distinct().Count());
        Assert.Equal("mp4", detail.Lines[0].Id);
        Assert.Equal("mp4", detail.Lines[1].Name);
        Assert.Equal("线路3", detail.Lines[2].Name);
        Assert.Equal("第1集", Assert.Single(detail.Lines[0].Episodes).Title);
    }

    [Fact]
    public void RejectsExecutableEpisodeAddressesAndKeepsHttpsQueryIntact()
    {
        var detail = MacCmsSource.ToDetail(new MacCmsVod { VodId = "1", VodName = "测试", VodPlayFrom = "mp4", VodPlayUrl = "坏$javascript:alert(1)#好$https://example.invalid/one.mp4?a=$b&c=2" });
        var episode = Assert.Single(Assert.Single(detail.Lines).Episodes);
        Assert.Equal("https://example.invalid/one.mp4?a=$b&c=2", episode.Uri);
    }

    [Fact]
    public void DescriptionRetainsParagraphsAndDecodesEntities()
    {
        var detail = MacCmsSource.ToDetail(new MacCmsVod { VodId = "1", VodName = "测试", VodContent = "<P>第一段 &amp; 简介</P><div>第二段<br />第三行</div>" });
        Assert.Contains("第一段 & 简介", detail.Description);
        Assert.Contains("第二段", detail.Description);
        Assert.Contains("第三行", detail.Description);
        Assert.DoesNotContain("<", detail.Description);
    }

    [Fact]
    public async Task InvalidEndpointFailsBeforeNetworkRequest()
    {
        var requested = false;
        var source = Source("file:///etc/passwd", (_, _) => { requested = true; return Task.FromResult("{}"); });
        await Assert.ThrowsAsync<InvalidDataException>(() => source.GetHomeAsync(TestContext.Current.CancellationToken));
        Assert.False(requested);
    }
}
