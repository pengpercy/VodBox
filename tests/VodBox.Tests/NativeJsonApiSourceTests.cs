using System.Text;
using System.Text.Json;
using VodBox.Core;
using VodBox.Infrastructure;
using Xunit;

namespace VodBox.Tests;

/// <summary>Jpys / Alllive / Music / Doubao 独立 JSON/网页端口的离线协议回归。</summary>
public sealed class NativeJsonApiSourceTests
{
    [Fact]
    public async Task JpysHomeListSearchDetailAndPlayerMatchTheSignedCanonicalOrder()
    {
        const string home = """{"data":[{"vodId":"7","vodName":"中文电影","vodPic":"https://img/7.jpg","vodVersion":"HD","vodClass":"动作"}]}""";
        const string list = """{"data":{"list":[{"vodId":"8","vodName":"列表片","vodPic":"https://img/8.jpg","vodVersion":"更新","vodClass":"剧情"},{"vodId":"9","vodName":"其它片","vodPic":"https://img/9.jpg","vodClass":"其它"}]}}""";
        const string search = """{"data":{"result":{"list":[{"vodId":"10","vodName":"中文剧","vodPic":"https://img/10.jpg","vodRemarks":"全12集"}]}}}""";
        const string detail = """{"data":{"vodId":"7","vodName":"中文电影","vodPic":"https://img/7.jpg","vodBlurb":"简介文本","vodActor":"演员","episodeList":[{"name":"第1集","nid":"n1"},{"name":"第2集","nid":"n2"}]}}""";
        const string player = """{"data":{"list":[{"url":"https://cdn.example/7-n1.m3u8"}]}}""";
        var rec = new SyntheticSourceHelpers.Recorder((url, _) => Task.FromResult(
            url.Contains("video/list", StringComparison.Ordinal) ? list :
            url.Contains("searchByWord", StringComparison.Ordinal) ? search :
            url.Contains("video/detail", StringComparison.Ordinal) ? detail :
            url.Contains("episode/url", StringComparison.Ordinal) ? player : home));
        var source = new JpysSource(SyntheticSourceHelpers.Site("jpys", "csp_JpysGuard", "https://www.ghw9zwp5.com"),
            (url, headers, ct) => rec.Fetch(url, headers, ct), () => 1792260000000, "fixture-device");
        Assert.Equal(new[] { "电影", "电视剧", "动漫", "综艺" }, (await source.GetCategoriesAsync(TestContext.Current.CancellationToken)).Select(c => c.Name));
        var homePage = await source.GetHomeAsync(TestContext.Current.CancellationToken);
        Assert.Equal("中文电影", Assert.Single(homePage.Items).Title);
        Assert.EndsWith("/api/mw-movie/anonymous/home/hotSearch", rec.Urls[0], StringComparison.Ordinal);
        Assert.Equal("fixture-device", rec.Calls[0].Headers!["deviceid"]);
        Assert.Equal("1792260000000", rec.Calls[0].Headers!["T"]);
        Assert.Equal(JpysSource.Sign("", 1792260000000), rec.Calls[0].Headers!["sign"]);
        var listPage = await source.GetItemsAsync("1", 2, new Dictionary<string, string> { ["area"] = "中国大陆", ["year"] = "2026" }, TestContext.Current.CancellationToken);
        Assert.Single(listPage.Items);
        Assert.Equal("列表片", listPage.Items[0].Title);
        Assert.Equal(3, listPage.PageCount);
        var listUrl = rec.Urls[^1];
        Assert.Contains("type1=1", listUrl);
        Assert.Contains("pageNum=2", listUrl);
        Assert.Contains("area=%E4%B8%AD%E5%9B%BD%E5%A4%A7%E9%99%86", listUrl);
        var listCall = rec.Calls[^1];
        Assert.Equal(JpysSource.Sign("area=中国大陆&pageNum=2&type1=1&year=2026", 1792260000000), listCall.Headers!["sign"]);
        var searchPage = await source.SearchAsync("中文", 1, TestContext.Current.CancellationToken);
        Assert.Equal("中文剧", Assert.Single(searchPage.Items).Title);
        Assert.Contains("keyword=%E4%B8%AD%E6%96%87", rec.Urls[^1]);
        Assert.Equal(JpysSource.Sign("keyword=中文&pageNum=1&pageSize=8", 1792260000000), rec.Calls[^1].Headers!["sign"]);
        var detailResult = await source.GetDetailAsync("7", TestContext.Current.CancellationToken);
        Assert.Equal("简介文本", detailResult.Description);
        Assert.Single(detailResult.Lines);
        Assert.Equal("爱电影", detailResult.Lines[0].Id);
        Assert.Equal("爱电影\u0001" + "7@n1", detailResult.Lines[0].Episodes[0].Id);
        // 阅读稿明确 detail 请求保留 header[deviceId]=deviceId（而非通用 deviceid），此处按真实键断言。
        Assert.Contains(rec.Calls, c => c.Url.Contains("video/detail", StringComparison.Ordinal) && c.Headers!.ContainsKey("fixture-device") && c.Headers!["fixture-device"] == "fixture-device");
        var playback = await source.ResolvePlaybackAsync("7", "爱电影\u0001" + "7@n1", TestContext.Current.CancellationToken);
        Assert.Equal("https://cdn.example/7-n1.m3u8", playback.Uri);
        Assert.Equal("爱电影", playback.LineId);
        Assert.Equal("https://www.ghw9zwp5.com", playback.Headers["Referer"]);
        Assert.Contains("clientType=3&id=7&nid=n1", rec.Urls[^1]);
        Assert.Equal(JpysSource.Sign("clientType=3&id=7&nid=n1", 1792260000000), rec.Calls[^1].Headers!["sign"]);
    }

    [Fact]
    public void JpysSignIsDeterministicSha1OfMd5AndSupportsRejectsBadExt()
    {
        var first = JpysSource.Sign("area=中国大陆&pageNum=2&type1=1&year=2026", 1792260000000);
        Assert.Equal(40, first.Length);
        Assert.Matches("^[0-9a-f]{40}$", first);
        Assert.Equal(first, JpysSource.Sign("area=中国大陆&pageNum=2&type1=1&year=2026", 1792260000000));
        Assert.NotEqual(first, JpysSource.Sign("area=中国大陆&pageNum=3&type1=1&year=2026", 1792260000000));
        Assert.NotEqual(JpysSource.Sign("", 1792260000000), JpysSource.Sign("", 1792260000001));
        Assert.True(JpysSource.Supports("https://www.ghw9zwp5.com"));
        Assert.True(JpysSource.Supports(null));
        Assert.False(JpysSource.Supports("https://www.ghw9zwp5.com/?q=1"));
        Assert.False(JpysSource.Supports("file:///tmp"));
    }

    [Fact]
    public void AllliveHuyaSuffixMatchesFixedClockVectorAndSupportsDecodesExt()
    {
        using var doc = JsonDocument.Parse("""{"name":"虎牙","code":"fm=Zm1fMQ==&ctype=huya&fs=abc"}""");
        var suffix = AllliveSource.HuyaSuffix(doc.RootElement, "8888", 1792260000000);
        Assert.Equal(ExpectedHuyaSuffix("8888", 1792260000000, "虎牙", "fm", "huya", "abc"), suffix);
        Assert.True(AllliveSource.Supports("https://agg.example"));
        // Alllive 必须由 ext 提供解密后的站点基地址；缺失 ext 无法构造，应拒绝而不是猜默认域。
        Assert.False(AllliveSource.Supports(null));
        Assert.False(AllliveSource.Supports(""));
        using var badInfo = JsonDocument.Parse("""{"name":"虎牙","code":"fm=Zm1fMQ=="}""");
        Assert.Throws<InvalidDataException>(() => AllliveSource.HuyaSuffix(badInfo.RootElement, "8888", 1792260000000));
    }

    private static string ExpectedHuyaSuffix(string uid, long milliseconds, string name, string fm, string ctype, string fs)
    {
        string Md5(string value) => Convert.ToHexString(System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
        var time = (milliseconds / 1000 + 21600).ToString("x");
        var seq = checked(milliseconds * 1000 + long.Parse(uid)).ToString();
        var hash = Md5(seq + "|" + ctype + "|" + ctype);
        var secret = Md5(fm + "_" + uid + "_" + name + "_" + hash + "_" + time);
        return "&" + "wsSecret=" + secret + "&wsTime=" + time + "&seqid=" + seq + "&ctype=" + ctype + "&ver=1&fs=" + fs + "&t=" + ctype + "&u=" + uid;
    }

    [Fact]
    public async Task MusicDirectMp4AndAudioKeyUseFixedClock()
    {
        const string detailHtml = """
            <div class="play_left"><img src="/cover.jpg"></div>
            <div class="play_list"><h1>歌名,歌手</h1><ul>
              <li><span class="name"><a href="/song/123.mp4.html">歌名</a></span></li>
              <li><span class="name"><a href="/song/456.html">另一首</a></span></li>
            </ul></div>
            """;
        var sends = new List<(string Url, IReadOnlyDictionary<string, string>? Fields)>();
        var source = new MusicSource(SyntheticSourceHelpers.Site("music", "csp_MusicGuard", "http://www.4c44.com"),
            (url, fields, _) => { sends.Add((url, fields)); return Task.FromResult(fields is null ? detailHtml : """{"url":"https://cdn.example/song.mp3","title":"歌"}"""); },
            () => new DateTimeOffset(2026, 10, 18, 5, 6, 7, TimeSpan.Zero));
        var mp4 = await source.ResolvePlaybackAsync("/song/123.mp4.html", "易听\u0001/song/123.mp4.html", TestContext.Current.CancellationToken);
        Assert.Equal("http://www.4c44.com/data/down.php?ac=vplay&id=123.mp4&q=1080", mp4.Uri);
        var audio = await source.ResolvePlaybackAsync("/song/456.html", "易听\u0001/song/456.html", TestContext.Current.CancellationToken);
        Assert.Equal("https://cdn.example/song.mp3", audio.Uri);
        var call = sends.Last(s => s.Fields is not null);
        Assert.Equal("http://www.4c44.com/js/play.php", call.Url);
        Assert.Equal("456", call.Fields!["id"]);
        Assert.Equal("music", call.Fields!["type"]);
        Assert.Equal("2026101805064c44", call.Fields!["key"]);
    }

    [Fact]
    public void MusicSupportsAcceptsBaseAddressAndRejectsQueryFragmentAndUserInfo()
    {
        Assert.True(MusicSource.Supports(null));
        Assert.True(MusicSource.Supports(""));
        Assert.True(MusicSource.Supports("http://www.4c44.com"));
        Assert.True(MusicSource.Supports("http://www.4c44.com/base"));
        Assert.False(MusicSource.Supports("http://www.4c44.com?a=1"));
        Assert.False(MusicSource.Supports("http://www.4c44.com#frag"));
        Assert.False(MusicSource.Supports("file:///tmp"));
    }

    [Fact]
    public async Task DoubaoLiveListDetailAndPlaybackMatchTheFixedProtocol()
    {
        const string schedule = """
            {"data":{"days":[{"live":[{"id":"11","teamA":"甲队","teamB":"乙队","teamAImage":"https://img/a.png","teamAscore":0,"teamBscore":0,"matchTime":"2026-01-01T00:00:00Z","league":"联赛","status":1,"playbackId":0}]}]}}
            """;
        const string playbacks = """
            {"data":{"list":[{"schedule":{"id":"11","teamA":"甲队","teamB":"乙队","teamAImage":"https://img/a.png","teamAscore":2,"teamBscore":1,"matchTime":"2026-01-01T00:00:00Z","league":"联赛","status":2},"playback":{"id":"77","lines":[{"title":"上半场","proxyUrl":"/play/h1.m3u8"}]}}],"page":1,"pageSize":20,"total":1}}
            """;
        const string detail = """
            {"data":{"title":"直播间","online":"128","cover":"https://img/c.png","category":"游戏","nickname":"主播","siteId":"huya","url":"https://live.example","stream":[{"name":"高清","lines":[{"name":"线路1","url":"//cdn.example/live.m3u8"}]}]}}
            """;
        const string play = """<html><body><script>var u="https:\u002F\u002Fcdn.example/live.m3u8";</script></body></html>""";
        var rec = new SyntheticSourceHelpers.Recorder((url, _) => Task.FromResult(
            url.Contains("/play/", StringComparison.Ordinal) ? play :
            url.Contains("getRoomDetail", StringComparison.Ordinal) ? detail :
            url.Contains("/playbacks", StringComparison.Ordinal) ? playbacks :
            url.Contains("schedules/public/local", StringComparison.Ordinal) ? schedule : "{}"));
        var source = new DoubaoSource(SyntheticSourceHelpers.Site("doubao", "csp_DoubaoGuard"), (url, headers, ct) => rec.Fetch(url, headers, ct));
        var live = await source.GetItemsAsync("live", 1, null, TestContext.Current.CancellationToken);
        Assert.Equal("live###11", Assert.Single(live.Items).Id);
        Assert.Contains("直播中", live.Items[0].Remarks);
        var replay = await source.GetItemsAsync("replay_football", 2, null, TestContext.Current.CancellationToken);
        Assert.Equal("replay###77", Assert.Single(replay.Items).Id);
        Assert.Contains("/playbacks?page=2&pageSize=20&dataType=football", rec.Urls[^1]);
        Assert.EndsWith("doubaozhibo.com/api/v1/schedules/public/local", rec.Urls[0], StringComparison.Ordinal);
        Assert.Equal("application/json", rec.Calls[0].Headers!["Accept"]);
    }

    [Fact]
    public async Task DoubaoDetailDistinguishesLiveAndReplayLines()
    {
        const string schedule = """
            {"data":{"days":[{"live":[{"id":"11","teamA":"甲队","teamB":"乙队","matchTime":"2026-01-01T00:00:00Z","league":"联赛","status":1,"playbackId":0,"signals":[{"name":"主信号","playId":"pid-1"}]}]}]}}
            """;
        const string playbacks = """
            {"data":{"list":[{"schedule":{"id":"11","teamA":"甲队","teamB":"乙队","matchTime":"2026-01-01T00:00:00Z","league":"联赛","status":2,"teamAscore":1,"teamBscore":0},"playback":{"id":"77","lines":[{"title":"上半场","proxyUrl":"https://cdn.example/h1.m3u8"},{"title":"下半场","proxyUrl":"https://cdn.example/h2.m3u8"}]}}],"page":1,"pageSize":20,"total":1}}
            """;
        const string play = """<html><script>var u="https:\u002F\u002Fcdn.example/live.m3u8";</script></html>""";
        var rec = new SyntheticSourceHelpers.Recorder((url, _) => Task.FromResult(
            url.Contains("/play/", StringComparison.Ordinal) ? play :
            url.Contains("/playbacks", StringComparison.Ordinal) ? playbacks : schedule));
        var source = new DoubaoSource(SyntheticSourceHelpers.Site("doubao", "csp_DoubaoGuard"), (url, headers, ct) => rec.Fetch(url, headers, ct));
        var liveDetail = await source.GetDetailAsync("live###11", TestContext.Current.CancellationToken);
        Assert.Equal("直播线路", liveDetail.Lines[0].Id);
        Assert.Equal("直播线路\u0001pid-1", liveDetail.Lines[0].Episodes[0].Id);
        var livePlay = await source.ResolvePlaybackAsync("live###11", liveDetail.Lines[0].Episodes[0].Id, TestContext.Current.CancellationToken);
        Assert.Equal("https://cdn.example/live.m3u8", livePlay.Uri);
        var replayDetail = await source.GetDetailAsync("replay###77", TestContext.Current.CancellationToken);
        Assert.Equal("回放线路", replayDetail.Lines[0].Id);
        Assert.Equal(2, replayDetail.Lines[0].Episodes.Count);
        Assert.Equal("回放线路\u0001https://cdn.example/h1.m3u8", replayDetail.Lines[0].Episodes[0].Id);
        var direct = await source.ResolvePlaybackAsync("replay###77", replayDetail.Lines[0].Episodes[0].Id, TestContext.Current.CancellationToken);
        Assert.Equal("https://cdn.example/h1.m3u8", direct.Uri);
        Assert.Equal("回放线路", direct.LineId);
    }

    [Fact]
    public async Task DoubaoMalformedJsonAndCancellationStayVisible()
    {
        var malformed = new DoubaoSource(SyntheticSourceHelpers.Site("doubao", "csp_DoubaoGuard"), (_, _, _) => Task.FromResult("<html>captcha</html>"));
        await Assert.ThrowsAnyAsync<JsonException>(() => malformed.GetItemsAsync("live", 1, null, TestContext.Current.CancellationToken));
        var hanging = new DoubaoSource(SyntheticSourceHelpers.Site("doubao", "csp_DoubaoGuard"),
            (_, _, ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct).ContinueWith(_ => "{}", ct));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => hanging.GetItemsAsync("live", 1, null, SyntheticSourceHelpers.CancelSoon()));
        await Assert.ThrowsAsync<InvalidDataException>(() => malformed.GetItemsAsync("unknown", 1, null, TestContext.Current.CancellationToken));
    }
}
