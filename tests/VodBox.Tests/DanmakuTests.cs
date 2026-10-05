using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using VodBox.Core;
using VodBox.Infrastructure;
using Xunit;

namespace VodBox.Tests;

public sealed class DanmakuTests
{
    [Fact]
    public void XmlParsesAdjacentCommentsModesColorsAndInvariantTimes()
    {
        var comments = DanmakuLoader.Parse(Encoding.UTF8.GetBytes("""
            <i><d p="2.5,1,25,16777215">滚动 &amp; 弹幕</d><d p="1.2,5,25,255">顶部</d><d p="3,4,25,16711680">底部</d><d p="4,7,25,1">高级不支持</d><d p="NaN,1,25,1">无效时间</d></i>
            """));
        Assert.Equal(3, comments.Count); Assert.Equal(1200, comments[0].TimeMs); Assert.Equal(DanmakuMode.Top, comments[0].Mode);
        Assert.Equal("滚动 & 弹幕", comments[1].Text); Assert.Equal(DanmakuMode.Bottom, comments[2].Mode); Assert.Equal(0xFF0000u, comments[2].Color);
    }
    [Fact]
    public void Utf16XmlAndCancellationAreHandled()
    {
        byte[] bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("<i><d p='0,1,25,16777215'>中文</d></i>")).ToArray();
        Assert.Equal("中文", Assert.Single(DanmakuLoader.Parse(bytes)).Text);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => DanmakuLoader.Parse(bytes, cancellation.Token));
    }
    [Fact]
    public async Task CatalogResolvesRelativeDanmakuAddress()
    {
        string root = Path.Combine(Path.GetTempPath(), "vodbox-danmaku-catalog-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            string file = Path.Combine(root, "catalog.json");
            await File.WriteAllTextAsync(file, "{\"categories\":[],\"items\":[{\"id\":\"m\",\"title\":\"Movie\",\"description\":\"\",\"categoryId\":\"c\",\"episodes\":[{\"id\":\"e\",\"title\":\"Episode\",\"uri\":\"movie.mp4\",\"danmakuUri\":\"comments.json\"}]}]}");
            using var http = new HttpClient(); await using var provider = new CatalogProvider(new() { Id = "test", Name = "test", Entry = new Uri(file).AbsoluteUri }, http);
            var request = await provider.ResolvePlaybackAsync("m", "e", default);
            Assert.Equal(new Uri(Path.Combine(root, "comments.json")).AbsoluteUri, request.DanmakuUri);
            var serialized = JsonSerializer.SerializeToUtf8Bytes(request, VodBoxJson.Default.PlaybackRequest);
            Assert.Equal(request.DanmakuUri, JsonSerializer.Deserialize(serialized, VodBoxJson.Default.PlaybackRequest)!.DanmakuUri);
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public void XmlRefusesDtdInsteadOfResolvingEntities()
    {
        Assert.Throws<System.Xml.XmlException>(() => DanmakuLoader.Parse(Encoding.UTF8.GetBytes("<!DOCTYPE i [<!ENTITY data SYSTEM 'file:///etc/passwd'>]><i><d p='0,1,25,1'>&data;</d></i>")));
    }
    [Fact]
    public void GeneratedJsonNormalizesTextAndDropsInvalidComments()
    {
        var document = new DanmakuDocument { Comments = [new(20, "first\nline"), new(-1, "invalid"), new(10, "second", DanmakuMode.Top), new(10, "third"), new(0, new string('x', 513))] };
        var parsed = DanmakuLoader.Parse(JsonSerializer.SerializeToUtf8Bytes(document, VodBoxJson.Default.DanmakuDocument));
        Assert.Equal(["second", "third", "first line"], parsed.Select(x => x.Text).ToArray());
        Assert.Throws<InvalidDataException>(() => DanmakuLoader.Parse("{\"version\":99}"u8.ToArray()));
        var defaults = Assert.Single(DanmakuLoader.Parse("{\"comments\":[{\"timeMs\":0,\"text\":\"defaults\"}]}"u8.ToArray()));
        Assert.Equal(0xFFFFFFu, defaults.Color); Assert.Equal(DanmakuMode.Scroll, defaults.Mode);
    }
    [Fact]
    public async Task GzipHttpLoadIsBoundedAfterDecompression()
    {
        using var http = new HttpClient(new FixtureHandler()); var loader = new DanmakuLoader(http);
        var comments = await loader.LoadAsync("https://example.com/comments.xml.gz"); Assert.Equal("测试", Assert.Single(comments).Text);
        await Assert.ThrowsAsync<InvalidDataException>(() => loader.LoadAsync("https://example.com/bomb.gz"));
    }
    [Theory]
    [InlineData("gzip")]
    [InlineData("deflate")]
    [InlineData("zlib")]
    [InlineData("br")]
    public async Task HttpContentEncodingIsDecodedWithExpansionLimits(string coding)
    {
        using var http = new HttpClient(new EncodedHandler(coding));
        var loader = new DanmakuLoader(http);
        Assert.Equal("HTTP 编码", Assert.Single(await loader.LoadAsync("https://example.com/comments.xml")).Text);
        await Assert.ThrowsAsync<InvalidDataException>(() => loader.LoadAsync("https://example.com/bomb.xml"));
    }
    private sealed class EncodedHandler(string coding) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            byte[] content = request.RequestUri!.AbsolutePath.Contains("bomb", StringComparison.Ordinal) ? new byte[DanmakuLoader.MaximumBytes + 1] : Encoding.UTF8.GetBytes("<i><d p='0,1,25,16777215'>HTTP 编码</d></i>");
            using var output = new MemoryStream();
            using (Stream encoder = coding switch
            {
                "gzip" => new GZipStream(output, CompressionLevel.Fastest, true),
                "deflate" => new DeflateStream(output, CompressionLevel.Fastest, true),
                "zlib" => new ZLibStream(output, CompressionLevel.Fastest, true),
                _ => new BrotliStream(output, CompressionLevel.Fastest, true)
            }) encoder.Write(content);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(output.ToArray()) };
            response.Content.Headers.ContentEncoding.Add(coding == "zlib" ? "deflate" : coding);
            return Task.FromResult(response);
        }
    }
    private sealed class FixtureHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            byte[] content = request.RequestUri!.AbsolutePath.Contains("bomb", StringComparison.Ordinal) ? new byte[DanmakuLoader.MaximumBytes + 1] : Encoding.UTF8.GetBytes("<i><d p='0,1,25,16777215'>测试</d></i>");
            using var buffer = new MemoryStream(); using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, true)) gzip.Write(content);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(buffer.ToArray()) });
        }
    }
    [Fact]
    public void TimelineMovesWithMediaTimeAndRebuildsOnBackwardSeek()
    {
        var timeline = new DanmakuTimeline(); timeline.Configure([new(0, "first"), new(10000, "second")], 640, 360, 24);
        double first = Assert.Single(timeline.Update(1000, _ => 120)).X;
        Assert.Equal(first, Assert.Single(timeline.Update(1000, _ => 120)).X); // paused media clock
        Assert.True(Assert.Single(timeline.Update(2000, _ => 120)).X < first);
        Assert.Empty(timeline.Update(9000, _ => 120));
        Assert.Equal("second", Assert.Single(timeline.Update(11000, _ => 120)).Comment.Text);
        Assert.Equal(first, Assert.Single(timeline.Update(1000, _ => 120)).X);
    }
    [Fact]
    public void DenseTimelineLimitsMeasurementsAndAvoidsLaneOverlap()
    {
        var timeline = new DanmakuTimeline();
        var comments = Enumerable.Range(0, 10000).Select(i => new DanmakuComment(i / 100, "comment " + i)).ToArray();
        timeline.Configure(comments, 640, 480, 24);
        int measured = 0; var frame = timeline.Update(200, _ => { measured++; return 140; });
        Assert.True(measured <= DanmakuTimeline.MaximumCandidatesPerFrame); Assert.InRange(frame.Count, 1, DanmakuTimeline.MaximumActive);
        Assert.Equal(frame.Count, frame.Select(x => x.Y).Distinct().Count());
        timeline.Configure(comments, 0, 0, 24); Assert.Empty(timeline.Update(200, _ => throw new InvalidOperationException("hidden")));
    }
    [Fact]
    public void OversizedOrInvalidMeasurementIsDroppedInsteadOfBreakingCollisionBounds()
    {
        var timeline = new DanmakuTimeline(); timeline.Configure([new(0, "giant")], 640, 360, 24);
        Assert.Empty(timeline.Update(0, _ => 10000));
        timeline.Configure([new(0, "invalid")], 640, 360, 24);
        Assert.Empty(timeline.Update(0, _ => double.NaN));
    }
    [Fact]
    public void FasterLongCommentCannotCatchPreviousCommentInSameLane()
    {
        var timeline = new DanmakuTimeline(); timeline.Configure([new(0, "short"), new(1800, "long")], 640, 100, 24);
        Assert.Single(timeline.Update(0, _ => 80));
        var frame = timeline.Update(1800, comment => comment.Text == "short" ? 80 : 600);
        Assert.Single(frame); Assert.Equal("short", frame[0].Comment.Text);
    }
    [Theory]
    [InlineData(480)]
    [InlineData(180)]
    public void TopAndBottomCommentsExpireAndUseSeparateRegions(int height)
    {
        var timeline = new DanmakuTimeline(); timeline.Configure([new(0, "top", DanmakuMode.Top), new(0, "bottom", DanmakuMode.Bottom)], 640, height, 24);
        var frame = timeline.Update(1000, _ => 100); Assert.Equal(2, frame.Count); Assert.True(frame[1].Y > frame[0].Y);
        Assert.Empty(timeline.Update(8000, _ => 100));
    }
}
