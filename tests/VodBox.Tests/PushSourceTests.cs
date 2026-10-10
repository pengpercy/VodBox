using VodBox.Core;
using VodBox.Infrastructure;
using Xunit;

namespace VodBox.Tests;

public sealed class PushSourceTests
{
    private static PushSource Source() => new(new SourceInfo { Key = "push", Name = "推送", Api = "csp_Push", Runtime = SourceRuntime.NativeSpider });

    [Fact]
    public async Task SingleUrlPreservesQueryAndFragmentAcrossPlaybackModes()
    {
        var source = Source();
        const string url = "https://example.invalid/video.mp4?q=$value#fragment";
        var detail = await source.GetDetailAsync(url, TestContext.Current.CancellationToken);
        Assert.Equal(2, detail.Lines.Count);
        var direct = await source.ResolvePlaybackAsync(url, detail.Lines[0].Episodes[0].Id, TestContext.Current.CancellationToken);
        var parse = await source.ResolvePlaybackAsync(url, detail.Lines[1].Episodes[0].Id, TestContext.Current.CancellationToken);
        Assert.Equal(url, direct.Uri);
        Assert.Equal(ResolutionKind.Direct, direct.Resolution);
        Assert.Equal(ResolutionKind.Json, parse.Resolution);
        Assert.Equal("push", direct.SourceKey);
    }

    [Fact]
    public async Task NamedPlaylistHasStableDistinctEpisodeIds()
    {
        var source = Source();
        const string input = "第一集$https://example.invalid/1.mp4\n第二集$https://example.invalid/2.mp4";
        var detail = await source.GetDetailAsync(input, TestContext.Current.CancellationToken);
        Assert.Equal(2, detail.Lines[0].Episodes.Count);
        Assert.Equal("第二集", detail.Lines[0].Episodes[1].Title);
        var search = await source.SearchAsync(input, 1, TestContext.Current.CancellationToken);
        Assert.Equal(input, Assert.Single(search.Items).Id);
        Assert.Equal("https://example.invalid/2.mp4", (await source.ResolvePlaybackAsync(input, "direct:1", TestContext.Current.CancellationToken)).Uri);
    }

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("javascript:alert(1)")]
    [InlineData("magnet:?xt=test")]
    [InlineData("https://username:password@example.invalid/video.mp4")]
    [InlineData("https://www.youtube.com/watch?v=test")]
    public async Task UnsupportedAddressesAreExplicitlyRejected(string input)
    {
        await Assert.ThrowsAsync<NotSupportedException>(() => Source().GetDetailAsync(input, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void RegisteredAsIndependentNativeProtocol()
    {
        var config = ConfigLoader.Parse("""{"sites":[{"key":"push","name":"推送","api":"csp_Push"}]}""")!;
        Assert.IsType<PushSource>(NativeSpiders.Create(Assert.Single(ConfigLoader.ToSources(config))));
    }
}
