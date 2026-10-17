using VodBox.Core;
using VodBox.Infrastructure;
using Xunit;

namespace VodBox.Tests;

/// <summary>Auete / Libvio / NewCz 独立 HTML 端口离线协议回归。</summary>
public sealed class HtmlPortSourceTests
{
    [Fact]
    public void AueteSupportsAcceptsOnlyKnownFields()
    {
        Assert.True(AueteSource.Supports(null));
        Assert.True(AueteSource.Supports("""{"siteUrl":"https://a.example","filterSpeedUp":true}"""));
        Assert.False(AueteSource.Supports("""{"siteUrl":"https://a.example","unknown":1}"""));
        Assert.False(AueteSource.Supports("""{"siteUrl":"file:///tmp"}"""));
        Assert.False(AueteSource.Supports("not-json"));
    }

    [Fact]
    public void LibvioSupportsAcceptsCloudDriveAndSiteUrlKeys()
    {
        Assert.True(LibvioSource.Supports("""{"Cloud-drive":"https://cloud.example","siteUrl":"https://lib.example"}"""));
        Assert.True(LibvioSource.Supports("""{"siteUrl":"https://lib.example"}"""));
        Assert.False(LibvioSource.Supports("""{"siteUrl":"https://lib.example","token":"x"}"""));
        Assert.False(LibvioSource.Supports("""{"Cloud-drive":"https://cloud.example","siteUrl":"https://u:{{PASSWORD_x}}@lib.example"}"""));
    }

    [Fact]
    public void NewCzSupportsOnlyEmptyLogin()
    {
        Assert.True(NewCzSource.Supports("""{"login":""}"""));
        Assert.False(NewCzSource.Supports("""{"login":"user"}"""));
        Assert.False(NewCzSource.Supports("""{"other":"x"}"""));
    }

    [Fact]
    public async Task AueteCategoriesListDetailAndPlaybackMatchTheFixedSelectors()
    {
        const string home = """
            <ul class="nav"><a href="/Movie/index.html">电影</a><a href="/Tv/index.html">电视剧</a><a href="/Other/index.html">其它</a></ul>
            """;
        const string list = """
            <ul class="threadlist">
              <li><h2><a href="/Movie/xjp/abc123/" title="动作片甲">动作片甲</a></h2><a class="pic"><img src="/p1.jpg"></a><span class="hdtag">HD</span></li>
              <li><h2><a href="/Movie/dzp/def456/" title="剧情片乙">剧情片乙</a></h2><a class="pic"><img src="/p2.jpg"></a><span class="hdtag">更新</span></li>
            </ul>
            <ul class="pagination"><a href="/Movie/index3.html">3</a></ul>
            """;
        const string detail = """
            <h1 class="detail-title">《动作片甲》</h1>
            <div class="detail-main-card" data-pname="动作片甲" data-ppic="/p1.jpg"></div>
            <div class="play-card" id="player_list">
              <div class="card-header"><span>线路：<b>云播B线</b></span></div>
              <ul class="episode-list"><a href="/Movie/xjp/abc123/play-1-1.html">第一集</a><a href="/Movie/xjp/abc123/play-1-2.html">第二集</a></ul>
            </div>
            """;
        const string play = """<script>var now="https://cdn.example/a.m3u8";</script>""";
        var rec = new SyntheticSourceHelpers.Recorder((url, _) => Task.FromResult(
            url.EndsWith("/au.example/", StringComparison.Ordinal) ? home :
            url.EndsWith("/Movie/xjp/abc123/", StringComparison.Ordinal) ? detail :
            url.Contains("play-1-1.html", StringComparison.Ordinal) ? play : list));
        var source = new AueteSource(SyntheticSourceHelpers.Site("auete", "csp_AueteGuard", """{"siteUrl":"https://au.example"}"""), (url, ct) => rec.Fetch(url, ct));
        Assert.Equal(new[] { "Movie", "Tv" }, (await source.GetCategoriesAsync(TestContext.Current.CancellationToken)).Select(c => c.Id));
        var page = await source.GetItemsAsync("Movie", 1, null, TestContext.Current.CancellationToken);
        Assert.Equal(2, page.Items.Count);
        Assert.Equal("HD", page.Items[0].Remarks);
        Assert.Equal(3, page.PageCount);
        Assert.EndsWith("/Movie/index.html", rec.Urls[^1], StringComparison.Ordinal);
        var detailResult = await source.GetDetailAsync("Movie/xjp/abc123", TestContext.Current.CancellationToken);
        Assert.Equal(new[] { "云播B线" }, detailResult.Lines.Select(l => l.Name));
        var episode = detailResult.Lines[0].Episodes[0];
        Assert.Equal("byun\u0001/Movie/xjp/abc123/play-1-1.html", episode.Id);
        var playback = await source.ResolvePlaybackAsync("Movie/xjp/abc123", episode.Id, TestContext.Current.CancellationToken);
        Assert.Equal("https://cdn.example/a.m3u8", playback.Uri);
        Assert.True(playback.ProxyMedia);
        Assert.Equal("byun", playback.LineId);
    }

    [Fact]
    public async Task AueteSearchEncodesQueryAndRejectsUnknownExt()
    {
        var rec = new SyntheticSourceHelpers.Recorder((_, _) => Task.FromResult("""<ul class="threadlist"><li><h2><a href="/Movie/xjp/abc123/" title="中文电影">中文</a></h2><img src="/p.jpg"></li></ul>"""));
        var source = new AueteSource(SyntheticSourceHelpers.Site("auete", "csp_AueteGuard", """{"siteUrl":"https://au.example"}"""), (url, ct) => rec.Fetch(url, ct));
        Assert.Equal("中文电影", Assert.Single((await source.SearchAsync("中文", 1, TestContext.Current.CancellationToken)).Items).Title);
        Assert.Contains("auete4so.php?searchword=%E4%B8%AD%E6%96%87", rec.Urls[^1]);
        Assert.Throws<NotSupportedException>(() => new AueteSource(SyntheticSourceHelpers.Site("auete", "csp_AueteGuard", """{"siteUrl":"https://au.example","unknown":1}"""), (url, ct) => rec.Fetch(url, ct)));
    }

    [Fact]
    public async Task LibvioCardsDetailPlaybackAndSearchMatchTheAnonymousSubset()
    {
        const string root = """
            <a href="/type/1.html">电影</a><a href="/type/2.html">电视剧</a>
            <div class="stui-vodlist__box">
              <a href="/detail/7.html" title="中文电影" data-original="/cover.jpg"><span class="pic-text">更新</span></a>
            </div>
            <ul class="stui-page__item"><a href="/show/1--------2---.html">2</a></ul>
            """;
        const string list = """
            <div class="stui-vodlist__box"><a href="/detail/8.html" title="列表电影" data-original="/c8.jpg"></a></div>
            """;
        const string detail = """
            <div class="stui-content__thumb"><img data-original="/d.jpg"></div>
            <h1>中文电影</h1><span>年份：2026</span><span>地区：大陆</span>
            <div class="playlist-panel">
              <div class="panel-head">直连线路</div>
              <a href="/play/7-1-1.html">第1集</a>
              <a href="/play/7-1-2.html">第2集</a>
            </div>
            <div class="playlist-panel">
              <div class="panel-head">夸克网盘</div>
              <a href="/play/7-9-1.html">网盘</a>
            </div>
            <span class="detail-content">简介</span>
            """;
        const string play = """
            <script>var player_data={"url":"https://cdn.example/7-1-1.m3u8","from":"direct","nid":"1","id":"7"};</script>
            """;
        const string search = """{"list":[{"id":7,"name":"中文电影","pic":"/s.jpg"}]}""";
        var rec = new SyntheticSourceHelpers.Recorder((url, _) => Task.FromResult(
            url.Contains("ajax/suggest", StringComparison.Ordinal) ? search :
            url.Contains("/play/7-1-1.html", StringComparison.Ordinal) ? play :
            url.EndsWith("/detail/7.html", StringComparison.Ordinal) ? detail :
            url.Contains("/show/", StringComparison.Ordinal) ? list : root));
        var source = new LibvioSource(SyntheticSourceHelpers.Site("lib", "csp_LibvioGuard", """{"siteUrl":"https://lib.example","Cloud-drive":"https://cloud.example"}"""), (url, ct) => rec.Fetch(url, ct));
        Assert.Equal(new[] { "电影", "电视剧" }, (await source.GetCategoriesAsync(TestContext.Current.CancellationToken)).Select(c => c.Name));
        var home = await source.GetHomeAsync(TestContext.Current.CancellationToken);
        Assert.Equal("https://lib.example/detail/7.html", Assert.Single(home.Items).Id);
        Assert.Equal(2, home.PageCount);
        var filtered = await source.GetItemsAsync("1", 2, new Dictionary<string, string> { ["3"] = "动作" }, TestContext.Current.CancellationToken);
        Assert.Equal("列表电影", Assert.Single(filtered.Items).Title);
        Assert.Contains(rec.Urls, url => url.Contains("/show/1---%E5%8A%A8%E4%BD%9C-----2---.html", StringComparison.Ordinal));
        Assert.Equal("中文电影", Assert.Single((await source.SearchAsync("中文", 1, TestContext.Current.CancellationToken)).Items).Title);
        Assert.Contains("limit=10", rec.Urls[^1]);
        var detailResult = await source.GetDetailAsync("https://lib.example/detail/7.html", TestContext.Current.CancellationToken);
        Assert.Single(detailResult.Lines);
        Assert.Equal("直连线路", detailResult.Lines[0].Id);
        Assert.Equal(2, detailResult.Lines[0].Episodes.Count);
        var episode = detailResult.Lines[0].Episodes[0];
        Assert.Equal("直连线路\u0001https://lib.example/play/7-1-1.html", episode.Id);
        var playback = await source.ResolvePlaybackAsync("https://lib.example/detail/7.html", episode.Id, TestContext.Current.CancellationToken);
        Assert.Equal("https://cdn.example/7-1-1.m3u8", playback.Uri);
        Assert.Equal("https://lib.example/", playback.Headers["Referer"]);
    }

    [Fact]
    public async Task LibvioCloudOnlyLineIsNotExposedAndNeedsAccountIsReported()
    {
        // 只有网盘线路的详情：不暴露任何可播线路，也不假装网盘线路是站内直连。
        const string cloudOnly = """
            <h1>仅网盘</h1>
            <div class="playlist-panel"><div class="panel-head">夸克</div><a href="/play/9-9-1.html">夸克</a></div>
            """;
        var rec = new SyntheticSourceHelpers.Recorder((_, _) => Task.FromResult(cloudOnly));
        var source = new LibvioSource(SyntheticSourceHelpers.Site("lib", "csp_LibvioGuard", """{"siteUrl":"https://lib.example"}"""), (url, ct) => rec.Fetch(url, ct));
        Assert.Empty((await source.GetDetailAsync("https://lib.example/detail/9.html", TestContext.Current.CancellationToken)).Lines);

        // 直连线路 + 播放页要求登录：详情可解析，播放解析显式报「需要账号」，不静默失败。
        const string connectDetail = """
            <h1>直连片</h1>
            <div class="playlist-panel"><div class="panel-head">直连</div><a href="/play/9-1-1.html">第1集</a></div>
            """;
        var locked = new SyntheticSourceHelpers.Recorder((url, _) => Task.FromResult(
            url.Contains("/play/9-1-1.html", StringComparison.Ordinal) ? "登录后观看" : connectDetail));
        var lockedSource = new LibvioSource(SyntheticSourceHelpers.Site("lib", "csp_LibvioGuard", """{"siteUrl":"https://lib.example"}"""), (url, ct) => locked.Fetch(url, ct));
        var line = (await lockedSource.GetDetailAsync("https://lib.example/detail/9.html", TestContext.Current.CancellationToken)).Lines[0];
        await Assert.ThrowsAsync<NotSupportedException>(() => lockedSource.ResolvePlaybackAsync("https://lib.example/detail/9.html", line.Episodes[0].Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task NewCzListSearchDetailAndReverseHexDecode()
    {
        const string list = """
            <div class="mi_ne_kd"><ul>
              <li><a href="/movie/1.html"><img src="/m1.jpg" alt="电影甲" data-original="/m1o.jpg"></a><div class="hdinfo">HD</div></li>
            </ul></div>
            """;
        const string search = "1|电影甲|/p1.jpg|HD$$$2|电影乙|/p2.jpg|更新";
        var rec = new SyntheticSourceHelpers.Recorder((url, _) => Task.FromResult(
            url.Contains("czzysearch.php", StringComparison.Ordinal) ? search : list));
        var source = new NewCzSource(SyntheticSourceHelpers.Site("newcz", "csp_NewCzGuard"), (url, ct) => rec.Fetch(url, ct));
        var home = await source.GetHomeAsync(TestContext.Current.CancellationToken);
        Assert.Equal("https://www.4kcz.com/movie/1.html", Assert.Single(home.Items).Id);
        Assert.Equal("HD", home.Items[0].Remarks);
        var searchPage = await source.SearchAsync("电影", 1, TestContext.Current.CancellationToken);
        Assert.Equal(2, searchPage.Items.Count);
        Assert.Equal("电影乙", searchPage.Items[1].Title);
        Assert.EndsWith("movie/2.html", searchPage.Items[1].Id, StringComparison.Ordinal);
        Assert.False(NewCzSource.Supports("""{"login":"x"}"""));
    }

    [Fact]
    public void NewCzReverseHexReversesAndRemovesMiddlePadding()
    {
        // 算法：字面反转十六进制 → 按字节解码 → 删除中间 7 个字符。
        // 构造 15 字符中间态 "ABCDXXXXXXXEFGH"，删除正中 7 个后应为 "ABCDEFGH"。
        const string intermediate = "ABCDXXXXXXXEFGH";
        var hex = string.Concat(intermediate.Select(c => ((int)c).ToString("x2", System.Globalization.CultureInfo.InvariantCulture)));
        var encoded = new string(hex.Reverse().ToArray());
        Assert.Equal("ABCDEFGH", NewCzSource.DecodeReverseHex(encoded));
        Assert.Throws<InvalidDataException>(() => NewCzSource.DecodeReverseHex("ab"));
    }

    [Fact]
    public async Task Non200AndCancellationPropagateFromLibvio()
    {
        var failing = new SyntheticSourceHelpers.Recorder((_, _) => Task.FromResult("<html></html>"), _ => true);
        var source = new LibvioSource(SyntheticSourceHelpers.Site("lib", "csp_LibvioGuard", """{"siteUrl":"https://lib.example"}"""), (url, ct) => failing.Fetch(url, ct));
        await Assert.ThrowsAsync<HttpRequestException>(() => source.GetHomeAsync(TestContext.Current.CancellationToken));
        var hanging = new SyntheticSourceHelpers.Recorder(async (_, _, ct) => { await Task.Delay(Timeout.InfiniteTimeSpan, ct); return ""; });
        var slow = new LibvioSource(SyntheticSourceHelpers.Site("lib", "csp_LibvioGuard", """{"siteUrl":"https://lib.example"}"""), (url, ct) => hanging.Fetch(url, ct));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => slow.GetHomeAsync(SyntheticSourceHelpers.CancelSoon()));
    }
}
