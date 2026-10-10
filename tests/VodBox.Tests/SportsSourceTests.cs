using System.Text.Json;
using VodBox.Core;
using VodBox.Infrastructure;
using Xunit;

namespace VodBox.Tests;

public sealed class SportsSourceTests
{
    private static SourceInfo Site(string api) => new() { Key = api, Name = api, Api = api, Runtime = SourceRuntime.NativeSpider };

    [Theory]
    [InlineData("csp_QiutongTY", typeof(QiutongTySource))]
    [InlineData("csp_QingtingFM", typeof(QingtingFmSource))]
    [InlineData("csp_KafeiTY", typeof(KafeiTySource))]
    [InlineData("csp_GuaziTY", typeof(GuaziTySource))]
    [InlineData("csp_919TY", typeof(Sports919Source))]
    public void Registered(string api, Type implementation)
    {
        Assert.True(NativeSpiders.IsSupported(api));
        Assert.IsType(implementation, NativeSpiders.Create(Site(api)));
    }

    [Fact]
    public async Task QiutongListDetailAndLiveResolution()
    {
        var requests = new List<string>();
        var source = new QiutongTySource(Site("csp_QiutongTY"), (url, _) =>
        {
            requests.Add(url);
            return Task.FromResult(url.Contains("room/page", StringComparison.Ordinal)
                ? "{\"data\":{\"list\":[{\"roomId\":41,\"title\":\"球赛\",\"cover\":\"https://img.example/a\",\"navName\":\"足球\"}]}}"
                : "{\"data\":{\"title\":\"球赛\",\"pushUrl\":\"https://cdn.example/live.flv\",\"pullUrl\":\"https://cdn.example/live.m3u8\"}}");
        });
        Assert.Equal("41", Assert.Single((await source.GetItemsAsync("2", 1, null, TestContext.Current.CancellationToken)).Items).Id);
        var detail = await source.GetDetailAsync("41", TestContext.Current.CancellationToken);
        Assert.Equal(2, detail.Lines.Count);
        var play = await source.ResolvePlaybackAsync("41", "m3u8", TestContext.Current.CancellationToken);
        Assert.Equal("https://cdn.example/live.m3u8", play.Uri);
        Assert.True(play.IsLive);
        Assert.Contains("navId=2", requests[0]);
        await Assert.ThrowsAsync<InvalidDataException>(() => source.GetDetailAsync("41&evil=1", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<NotSupportedException>(() => source.SearchAsync("x", 1, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task QingtingGraphEscapingAndRadio()
    {
        var bodies = new List<string>();
        var source = new QingtingFmSource(Site("csp_QingtingFM"), (url, _) => Task.FromResult("{\"album\":{\"id\":\"88\",\"title\":\"音乐台\",\"cover\":\"https://img.example/radio\"}}"), (body, _) =>
        {
            bodies.Add(body);
            return Task.FromResult(body.Contains("searchResultsPage", StringComparison.Ordinal)
                ? "{\"data\":{\"searchResultsPage\":{\"searchData\":[{\"id\":\"88\",\"title\":\"音乐台\",\"cover\":\"https://img.example/radio\"}]}}}"
                : "{\"data\":{\"radioPage\":{\"contents\":{\"items\":[{\"id\":\"88\",\"title\":\"音乐台\",\"imgUrl\":\"//img.example/radio\"}]}}}}" );
        });
        var page = await source.GetItemsAsync("217", 1, null, TestContext.Current.CancellationToken);
        Assert.Equal("https://img.example/radio", Assert.Single(page.Items).Poster);
        Assert.Single((await source.SearchAsync("歌\"}恶意", 2, TestContext.Current.CancellationToken)).Items);
        using var body = JsonDocument.Parse(bodies[1]);
        Assert.Contains("\\u0022}", body.RootElement.GetProperty("query").GetString());
        var detail = await source.GetDetailAsync("88", TestContext.Current.CancellationToken);
        var play = await source.ResolvePlaybackAsync("88", Assert.Single(detail.Lines[0].Episodes).Id, TestContext.Current.CancellationToken);
        Assert.Equal("https://lhttp-hw.qtfm.cn/live/88/64k.mp3", play.Uri);
    }

    [Fact]
    public async Task KafeiScheduleAndSignals()
    {
        var source = new KafeiTySource(Site("csp_KafeiTY"), (url, _) => Task.FromResult(url.Contains("schedule", StringComparison.Ordinal)
            ? "{\"code\":200,\"data\":[{\"archor\":{\"room_id\":\"17\"},\"home_team\":\"甲\",\"away_team\":\"乙\"}]}"
            : "{\"code\":200,\"data\":{\"room_info\":{\"room_id\":\"17\",\"title\":\"甲 VS 乙\"},\"signals\":[{\"name\":\"清晰\",\"stream_url\":\"https://cdn.example/live.m3u8\"}]}}"));
        Assert.Equal("17", Assert.Single((await source.GetHomeAsync(TestContext.Current.CancellationToken)).Items).Id);
        var detail = await source.GetDetailAsync("17", TestContext.Current.CancellationToken);
        var play = await source.ResolvePlaybackAsync("17", detail.Lines[0].Episodes[0].Id, TestContext.Current.CancellationToken);
        Assert.Equal("https://cdn.example/live.m3u8", play.Uri);
    }

    [Fact]
    public async Task GuaziIndependentOpensslVectorAndEncryptedResponses()
    {
        // Expected ciphertext generated with openssl enc -aes-128-cbc, independently of this adapter.
        Assert.Equal("9xrfAezz+yS9Ppc+C1nNMw==", GuaziTySource.Encrypt("{\"mid\":\"42\"}"));
        Assert.Equal("{\"mid\":\"42\"}", GuaziTySource.Decrypt("9xrfAezz+yS9Ppc+C1nNMw=="));
        var source = new GuaziTySource(Site("csp_GuaziTY"), (path, encrypted, _) =>
        {
            using var request = JsonDocument.Parse(GuaziTySource.Decrypt(encrypted));
            var data = path.EndsWith("sports", StringComparison.Ordinal)
                ? "[{\"mid\":42,\"match_time\":1781000000,\"m_status\":0,\"event_name\":\"联赛\",\"match_status_info\":\"进行中\",\"home\":{\"name\":\"甲\",\"logo\":\"https://img.example/a\"},\"visiting\":{\"name\":\"乙\"}}]"
                : "{\"home\":{\"name\":\"甲\",\"logo\":\"https://img.example/a\"},\"visiting\":{\"name\":\"乙\"},\"match_status_info\":\"进行中\",\"live_line\":[{\"name\":\"主线\",\"m3u8\":\"https://cdn.example/a.m3u8\"}]}";
            return Task.FromResult("{\"data\":\"" + GuaziTySource.Encrypt(data) + "\"}");
        }, () => DateTimeOffset.FromUnixTimeSeconds(1781000000));
        Assert.Equal("42", Assert.Single((await source.GetHomeAsync(TestContext.Current.CancellationToken)).Items).Id);
        var play = await source.ResolvePlaybackAsync("42", "0", TestContext.Current.CancellationToken);
        Assert.Equal("https://cdn.example/a.m3u8", play.Uri);
        Assert.True(play.IsLive);
    }

    [Fact]
    public async Task Sports919TwoLinesAndDistinctEpisodes()
    {
        var source = new Sports919Source(Site("csp_919TY"), (url, _) => Task.FromResult(url.Contains("detail", StringComparison.Ordinal)
            ? "{\"code\":200,\"data\":{\"detail\":{\"home_team_zh\":\"甲\",\"away_team_zh\":\"乙\"},\"more\":[{\"username\":\"主持\",\"screen_url\":\"https://cdn.example/one.flv\",\"screen_url_m3u8\":\"https://cdn.example/two.m3u8\"}]}}"
            : "{\"code\":200,\"data\":{\"data\":[{\"type\":1,\"tournament_id\":20,\"member_id\":30,\"home_team_zh\":\"甲\",\"away_team_zh\":\"乙\"}]}}"));
        Assert.Equal("1|20|30", Assert.Single((await source.GetHomeAsync(TestContext.Current.CancellationToken)).Items).Id);
        var detail = await source.GetDetailAsync("1|20|30", TestContext.Current.CancellationToken);
        Assert.Equal(2, detail.Lines[0].Episodes.Count);
        Assert.Equal("https://cdn.example/two.m3u8", (await source.ResolvePlaybackAsync("1|20|30", "0:线路二", TestContext.Current.CancellationToken)).Uri);
        await Assert.ThrowsAsync<InvalidDataException>(() => source.GetDetailAsync("1|../20|30", TestContext.Current.CancellationToken));
    }
}
