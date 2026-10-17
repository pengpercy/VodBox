using System.Net;
using System.Text.Json;
using VodBox.Core;
using VodBox.Infrastructure;
using Xunit;

namespace VodBox.Tests;

public sealed class LiveGzSourceTests
{
    private const string Key = "KANGEQIU@8868!~.", Iv = "0200010900030207";
    private static SourceInfo Site => new() { Key = "吃瓜", Name = "吃瓜", Api = "csp_LiveGzGuard", Runtime = SourceRuntime.NativeSpider, Ext = "http://127.0.0.1:4906" };
    private static JsonDocument Fixture(string name) => JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", name)));
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<string>> run) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => new(HttpStatusCode.OK) { Content = new StringContent(await run(request, ct)) };
    }
    private static string Envelope(string plaintext) => "{\"data\":\"" + CatVodAes.Encrypt(plaintext, Key, Iv) + "\"}";

    [Fact]
    public async Task EncryptedRequestsScheduleAndPlaybackMatchOriginalGuard()
    {
        using var responses = Fixture("fty-livegz-responses.json");
        using var oracle = Fixture("fty-livegz-oracle.json");
        int requests = 0;
        using var handler = new Handler(async (request, ct) =>
        {
            requests++;
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("okhttp/3.12.0", request.Headers.UserAgent.ToString());
            Assert.Equal("3.0.4.0", Assert.Single(request.Headers.GetValues("client-version")));
            Assert.Equal("?parameter=key", request.RequestUri!.Query);
            var form = await request.Content!.ReadAsStringAsync(ct);
            Assert.StartsWith("parameter=", form, StringComparison.Ordinal);
            var value = WebUtility.UrlDecode(form["parameter=".Length..]);
            string operation = request.RequestUri.Segments[^1];
            Assert.Equal(operation == "sports" ? "LJ6JyZxcMKdqnJ7tXLMN9NylzvnsiMlNRaqnGxo90BjuKq55llTh4igohoe1fRQ+\n" : "kNlQtzWIK8F98VZdKpBf49jEq06bh9jp/aS3c6IJrIeaGgENJSwYdK/aAhGuRHL2\n", value);
            return Envelope(responses.RootElement.GetProperty(operation).GetRawText());
        });
        using var http = new HttpClient(handler);
        var source = new LiveGzSource(Site, http);
        var categories = await source.GetCategoriesAsync(TestContext.Current.CancellationToken);
        Assert.Equal(new[] { "1-0-0", "0-37-0", "0-0-1", "0-0-2" }, categories.Select(category => category.Id));
        var home = await source.GetHomeAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, home.Items.Count); // completed matches are omitted by the original
        Assert.Equal("直播中 测试联赛 第一轮 比分2:1", home.Items[0].Remarks);
        Assert.StartsWith("未开赛 ", home.Items[1].Remarks, StringComparison.Ordinal);
        Assert.Empty((await source.GetItemsAsync("1-0-0", 2, null, TestContext.Current.CancellationToken)).Items);
        Assert.Empty((await source.SearchAsync("主队", 1, TestContext.Current.CancellationToken)).Items);
        var detail = await source.GetDetailAsync("100-200-1", TestContext.Current.CancellationToken);
        var expected = oracle.RootElement.GetProperty("results");
        Assert.Equal(2, detail.Lines.Count);
        for (int index = 0; index < detail.Lines.Count; index++)
        {
            var episode = Assert.Single(detail.Lines[index].Episodes);
            var original = expected[index + 3]; var originalRequest = original.GetProperty("request");
            Assert.Equal(originalRequest.GetProperty("flag").GetString() + '\u0001' + originalRequest.GetProperty("play").GetString(), episode.Id);
            var play = await source.ResolvePlaybackAsync(detail.Item.Id, episode.Id, TestContext.Current.CancellationToken);
            Assert.Equal(original.GetProperty("response").GetProperty("url").GetString(), play.Uri);
            Assert.True(play.IsLive); Assert.Equal(ResolutionKind.Direct, play.Resolution);
        }
        Assert.Equal(4, requests);
    }

    [Fact]
    public async Task AChangedSignedAddressRefreshesTheSingleStreamOnTheSameLine()
    {
        using var responses = Fixture("fty-livegz-responses.json");
        int calls = 0;
        using var handler = new Handler((_, _) =>
        {
            string response = responses.RootElement.GetProperty("detail").GetRawText();
            if (++calls > 1) response = response.Replace("live.m3u8", "live.m3u8?fresh=1", StringComparison.Ordinal);
            return Task.FromResult(Envelope(response));
        });
        using var http = new HttpClient(handler);
        var source = new LiveGzSource(Site, http);
        var detail = await source.GetDetailAsync("100-200-1", TestContext.Current.CancellationToken);
        string oldId = detail.Lines[0].Episodes[0].Id;
        var play = await source.ResolvePlaybackAsync("100-200-1", oldId, TestContext.Current.CancellationToken);
        Assert.Equal("https://media.example/live.m3u8?fresh=1", play.Uri);
        Assert.Equal(oldId, play.EpisodeId);
    }

    [Fact]
    public async Task EmptyLinesInvalidIdentityCancellationAndBadEncryptionAreExplicit()
    {
        using var responses = Fixture("fty-livegz-responses.json");
        using var handler = new Handler((_, _) => Task.FromResult(Envelope(responses.RootElement.GetProperty("detail").GetRawText())));
        using var http = new HttpClient(handler);
        var source = new LiveGzSource(Site, http);
        await Assert.ThrowsAsync<InvalidDataException>(() => source.GetDetailAsync("1-2-invalid", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidDataException>(() => source.GetDetailAsync("100-201-1", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidDataException>(() => source.ResolvePlaybackAsync("100-200-1", "missing", TestContext.Current.CancellationToken));
        using var malformedHandler = new Handler((_, _) => Task.FromResult("{\"data\":\"invalid\"}"));
        using var malformed = new HttpClient(malformedHandler);
        await Assert.ThrowsAsync<InvalidDataException>(() => new LiveGzSource(Site, malformed).GetHomeAsync(TestContext.Current.CancellationToken));
        using var slowHandler = new Handler(async (_, ct) => { await Task.Delay(Timeout.InfiniteTimeSpan, ct); return "{}"; });
        using var slow = new HttpClient(slowHandler); using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new LiveGzSource(Site, slow).GetHomeAsync(timeout.Token));
        Assert.True(LiveGzSource.Supports(null));
        Assert.False(LiveGzSource.Supports("file:///tmp"));
    }
}
