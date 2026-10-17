using System.Net;
using System.Text.Json;
using VodBox.Core;
using VodBox.Infrastructure;
using Xunit;

namespace VodBox.Tests;

public sealed class Tingshu275SourceTests
{
    private static SourceInfo Site => new() { Key = "有声小说", Name = "听书", Api = "csp_Tingshu275Guard", Runtime = SourceRuntime.NativeSpider };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<string>> run) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => new(HttpStatusCode.OK) { Content = new StringContent(await run(request, ct)) };
    }
    private const string Book = """{"id":"7","bookTitle":"中文小说","bookImage":"https://cdn.example/book.jpg","bookAnchor":"播音","categoryName":"玄幻奇幻","bookDesc":"简介"}""";

    [Fact]
    public void SignatureMatchesIndependentNativeOracle()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "fty-ting-sign-vectors.json")));
        foreach (var vector in document.RootElement.EnumerateArray())
            Assert.Equal(vector.GetProperty("output").GetString(), Tingshu275Source.Sign(vector.GetProperty("input").GetString()!));
    }

    [Fact]
    public async Task ContentPlaybackAndPersistedIdentitiesMatchOriginalPlugin()
    {
        using var handler = new Handler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("TingShiJie/1.8.8 (m.i275.com)", request.Headers.UserAgent.ToString());
            var uri = request.RequestUri!;
            string response = uri.AbsolutePath switch
            {
                "/listen/Apitzg2025/categoryList" => uri.Query.Contains("page=2", StringComparison.Ordinal) ? "{\"data\":{\"list\":[]}}" : "{\"data\":{\"list\":[" + Book + "]}}",
                "/listen/Apitzg2025/book" => "{\"data\":{\"bookData\":" + Book + "}}",
                "/listen/Apitzg2025/chapter" => """{"data":{"list":[{"title":"第一章","chapterId":"42"},{"title":"第二章","chapterId":"43"}]}}""",
                "/listen/Apitzg2025//listen/apphaidao/appSearch" => "{\"data\":{\"bookData\":[" + Book + "]}}",
                "/listen/Apitzg2025/AppGetChapterUrl2023" => """{"src":"https://media.example/chapter.mp3"}""",
                _ => throw new InvalidOperationException("Unexpected route: " + uri.AbsolutePath)
            };
            if (uri.AbsolutePath.EndsWith("AppGetChapterUrl2023", StringComparison.Ordinal))
                Assert.Equal("?timeStamp=1000&uid=&chapterId=42&addItParapet=708bc42ec371affc1edeee3569e7d014&bookId=7", uri.Query);
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        var source = new Tingshu275Source(Site, http, () => 1000, new Uri("http://127.0.0.1:4914/listen/Apitzg2025/"));
        Assert.Equal(11, (await source.GetCategoriesAsync(TestContext.Current.CancellationToken)).Count);
        Assert.Empty((await source.GetHomeAsync(TestContext.Current.CancellationToken)).Items);
        var page = await source.GetItemsAsync("6", 1, null, TestContext.Current.CancellationToken);
        Assert.Equal("中文小说", Assert.Single(page.Items).Title);
        Assert.Equal(1, page.PageCount);
        Assert.Empty((await source.GetItemsAsync("6", 2, null, TestContext.Current.CancellationToken)).Items);
        Assert.Single((await source.SearchAsync("中文", 1, TestContext.Current.CancellationToken)).Items);
        var detail = await source.GetDetailAsync("7", TestContext.Current.CancellationToken);
        Assert.Equal("简介", detail.Description);
        Assert.Equal("直链\u000142|||7", detail.Lines[0].Episodes[0].Id);
        var play = await source.ResolvePlaybackAsync("7", detail.Lines[0].Episodes[0].Id, TestContext.Current.CancellationToken);
        Assert.Equal("https://media.example/chapter.mp3", play.Uri);
        Assert.Equal("直链", play.LineId);
        Assert.Equal("listen/1.8.9 (Linux;Android 12) ExoPlayerLib/2.8.4", play.Headers["User-Agent"]);
        await Assert.ThrowsAsync<InvalidDataException>(() => source.ResolvePlaybackAsync("7", "直链\u000142|||8", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DiscoveryCancellationAndMalformedResponsesAreVisible()
    {
        using var discovery = new Handler((_, _) => Task.FromResult("file:///tmp/config"));
        using var http = new HttpClient(discovery);
        await Assert.ThrowsAsync<InvalidDataException>(() => new Tingshu275Source(Site, http, () => 1).GetDetailAsync("7", TestContext.Current.CancellationToken));
        using var invalid = new Handler((_, _) => Task.FromResult("{}"));
        using var malformed = new HttpClient(invalid);
        await Assert.ThrowsAsync<InvalidDataException>(() => new Tingshu275Source(Site, malformed, () => 1, new Uri("http://127.0.0.1/")).GetDetailAsync("7", TestContext.Current.CancellationToken));
        using var hanging = new Handler(async (_, ct) => { await Task.Delay(Timeout.InfiniteTimeSpan, ct); return "{}"; });
        using var slow = new HttpClient(hanging);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new Tingshu275Source(Site, slow, () => 1).GetDetailAsync("7", timeout.Token));
        Assert.False(Tingshu275Source.Supports("https://unverified.example/"));
    }
}
