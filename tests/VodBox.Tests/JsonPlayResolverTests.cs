using VodBox.Core;
using VodBox.Infrastructure;
using Xunit;

namespace VodBox.Tests;

public sealed class JsonPlayResolverTests
{
    [Fact]
    public async Task ResolvesEncodedInputAndPreservesPlaybackMetadata()
    {
        string? called = null;
        var resolver = new JsonPlayResolver(new TvBoxParse { Name = "解析", Type = 1, Url = "https://parse.example/?url={url}" },
            (url, _, _) => { called = url; return Task.FromResult("{\"data\":{\"url\":\"https://media.example/stream.m3u8\",\"header\":{\"Referer\":\"https://source.example/\"}}}"); });
        var request = new PlaybackRequest { Uri = "https://video.example/?id=1&x=2", MediaId = "m1", StartPositionMs = 42000, Resolution = ResolutionKind.Json };
        var result = await resolver.ResolveAsync(request, TestContext.Current.CancellationToken);
        Assert.Contains("%26x%3D2", called);
        Assert.Equal("m1", result!.MediaId);
        Assert.Equal(42000, result.StartPositionMs);
        Assert.Equal(ResolutionKind.Direct, result.Resolution);
        Assert.Equal("https://source.example/", result.Headers["Referer"]);
    }

    [Fact]
    public async Task ExtHeadersArePassedAndLowercaseMediaHeadersAreAccepted()
    {
        using var ext = System.Text.Json.JsonDocument.Parse("{\"header\":{\"User-Agent\":\"TV Client\"}}");
        IReadOnlyDictionary<string, string>? headers = null;
        var resolver = new JsonPlayResolver(new TvBoxParse { Type = 2, Url = "https://parse.example/?url=", Ext = ext.RootElement.Clone() },
            (_, values, _) => { headers = values; return Task.FromResult("{\"url\":\"https://media.example/a\",\"header\":{\"referer\":\"https://source.example/\"}}"); });
        var result = await resolver.ResolveAsync(new PlaybackRequest { Uri = "https://example.com" }, TestContext.Current.CancellationToken);
        Assert.Equal("TV Client", headers!["User-Agent"]);
        Assert.Equal("https://source.example/", result!.Headers["Referer"]);
    }

    [Fact]
    public async Task InvalidMediaProtocolAndHeaderInjectionDoNotReachEngine()
    {
        var parse = new TvBoxParse { Type = 2, Url = "https://parse.example/?url=" };
        var resolver = new JsonPlayResolver(parse, (_, _, _) => Task.FromResult("{\"url\":\"file:///secret\"}"));
        Assert.Null(await resolver.ResolveAsync(new PlaybackRequest { Uri = "https://example.com" }, TestContext.Current.CancellationToken));
        var bad = new JsonPlayResolver(parse, (_, _, _) => Task.FromResult("{\"url\":\"https://media.example/a\",\"header\":{\"Referer\":\"bad\\r\\nheader\"}}"));
        await Assert.ThrowsAsync<InvalidDataException>(() => bad.ResolveAsync(new PlaybackRequest { Uri = "https://example.com" }, TestContext.Current.CancellationToken));
    }
}
