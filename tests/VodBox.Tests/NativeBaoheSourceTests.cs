using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using VodBox.Core;
using VodBox.Infrastructure;
using Xunit;

namespace VodBox.Tests;

/// <summary>宝盒 bh0822.jar 首批原生迁移（Wwys/AppV6/TingBook/HeMa/Feiyu）的离线协议回归。</summary>
public sealed class NativeBaoheSourceTests
{
    private const string WwysHome = """
        <div id="topnav" class="wrap"><ul class="swiper-wrapper" style="width:100%"><li ><a href="/vod-type-id-1-pg-1.html">电影</a></li><li ><a href="/vod-type-id-2-pg-1.html">连续剧</a></li></ul></div>
        <section class="mod"><div class="mod-hd"></div><div class="mod-bd">
        <ul class="resize_list"><li><a href="/vod-detail-id-96319.html" title="伟大的长征"><div class="pic"><img data-src="https://img/1.jpg"><span class="sBottom"><span>更新至18集<em>0.0</em></span></span></div></a></li></ul>
        <ul class="resize_list"><li><a href="/vod-detail-id-96476.html" title="嫁金钗"><div class="pic"><img data-src="https://img/2.jpg"><span class="sBottom"><span>更新至08集<em>0.0</em></span></span></div></a></li></ul>
        </div></section>
        """;

    private const string WwysList = """
        <ul class="resize_list"><li><a href="/vod-detail-id-94741.html" title="抓特务"><div class="pic"style=" height: 150px;"><img src="https://img/9.jpg"><span class="sBottom"><span><em>0.0</em></span></span></div></a></li></ul>
        <div class="page"><a class="pagelink_a" href="/vod-list-id-1-pg-691-order--by-time-class-0-year-0-letter--area--lang-.html">尾页</a><input type="button" value="跳 转" onclick="pagego('/vod-list-id-1-pg-{pg}-order--by-time-class-0-year-0-letter--area--lang-.html',691)" class="pagebtn" /></div>
        """;

    private const string WwysDetail = """
        <section class="page-hd"><a href="/vod-detail-id-96476.html" title="嫁金钗"><img src="https://img/2.jpg" alt="嫁金钗"></a></section>
        <h1 class="title"><a href="/vod-detail-id-96476.html" title="嫁金钗">嫁金钗</a></h1>
        <div class="desc_item"><span>状态:&nbsp;</span><font size="2" color="red">更新至08集</font></div>
        <div class="desc_item"><span>主演:&nbsp;</span><a href='/a'>田曦薇</a>&nbsp;<a href='/b'>闫桉</a></div>
        <div class="desc_item"><span>导演:&nbsp;</span><a href='/c'>杨龙</a></div>
        <article class="detail-con"><p>简&nbsp;&nbsp;介：阿丑原是街上倒夜香的低贱少女。</p></article>
        <div id="monday" class="numList"><ul><li><a href="/vod-play-id-96476-src-1-num-1.html">第01集</a></li><li><a href="/vod-play-id-96476-src-1-num-2.html">第02集</a></li></ul></div>
        <div id="lzm3u8" class="numList"><ul><li><a href="/vod-play-id-96476-src-2-num-1.html">第01集</a></li></ul></div>
        """;

    private const string WwysPlay = """
        <script>var player_aaaa={"url":"/vod-play-id-96476-src-{src}-num-{num}.html",mac_name='嫁金钗',mac_from='monday$$$lzm3u8',mac_server='no$$$no',mac_note='$$$',mac_url='第01集$https://cdn/one.m3u8#第02集$https://cdn/two.m3u8$$$第01集$https://other/one.m3u8'; </script>
        """;

    [Fact]
    public async Task WwysHomeCategoriesListDetailAndPlayerFollowPluginSelectors()
    {
        var rec = new SyntheticSourceHelpers.Recorder((url, _) => Task.FromResult(
            url.Contains("vod-list-id", StringComparison.Ordinal) ? WwysList :
            url.Contains("vod-detail-id", StringComparison.Ordinal) ? WwysDetail :
            url.Contains("player/monday.js", StringComparison.Ordinal)
                ? "MacPlayer.Html = '<iframe src=\"https://api.wwgz.cn:520/player/?url='+MacPlayer.PlayUrl+'\" ></iframe>';"
            : url.Contains("api.wwgz.cn:520/player/", StringComparison.Ordinal)
                ? """{"url": "https://svip.example/index.m3u8"}"""
            : url.Contains("vod-play-id", StringComparison.Ordinal) ? WwysPlay : WwysHome));
        var source = new WwysSource(SyntheticSourceHelpers.Site("wwys", "csp_Wwys", "https://vip.wwgz.cn:5200"),
            (url, ct) => rec.Fetch(url, ct));
        var cancellation = TestContext.Current.CancellationToken;

        var categories = await source.GetCategoriesAsync(cancellation);
        Assert.Equal(new[] { ("1", "电影"), ("2", "连续剧") }, categories.Select(c => (c.Id, c.Name)));
        Assert.Equal(new[] { "类型" }, (await source.GetFiltersAsync("1", cancellation)).Select(g => g.Name));
        Assert.Empty(await source.GetFiltersAsync("4", cancellation));

        var home = await source.GetHomeAsync(cancellation);
        Assert.Equal(new[] { "伟大的长征", "嫁金钗" }, home.Items.Select(i => i.Title));
        Assert.Equal("https://img/1.jpg", home.Items[0].Poster);
        Assert.Equal("更新至18集0.0", home.Items[0].Remarks);

        var list = await source.GetItemsAsync("1", 2, null, cancellation);
        Assert.Equal("抓特务", Assert.Single(list.Items).Title);
        Assert.Equal(2, list.Page);
        Assert.Equal(691, list.PageCount);
        Assert.Contains("/vod-list-id-1-pg-2-order--by-time-class-0-year-0-letter--area--lang-.html", rec.Urls[^1]);

        var filtered = await source.GetItemsAsync("1", 1, new Dictionary<string, string> { ["cateId"] = "5" }, cancellation);
        Assert.Contains("/vod-list-id-5-pg-1-", rec.Urls[^1]);
        Assert.NotNull(filtered);

        var detail = await source.GetDetailAsync("96476", cancellation);
        Assert.Equal("嫁金钗", detail.Item.Title);
        Assert.Equal("更新至08集", detail.Item.Remarks);
        Assert.Equal("https://img/2.jpg", detail.Item.Poster);
        Assert.Equal("田曦薇 闫桉", detail.Actor);
        Assert.Equal("杨龙", detail.Director);
        Assert.Contains("阿丑原是街上倒夜香的低贱少女", detail.Description);
        Assert.Equal(new[] { "高清无广", "量子有广" }, detail.Lines.Select(l => l.Name));
        // 原插件在每条线路内 Collections.reverse，第一集在末尾。
        Assert.Equal(new[] { "第02集", "第01集" }, detail.Lines[0].Episodes.Select(e => e.Title));
        Assert.Equal("/vod-play-id-96476-src-1-num-2.html", detail.Lines[0].Episodes[0].Id);

        var playback = await source.ResolvePlaybackAsync("96476", "/vod-play-id-96476-src-1-num-2.html", cancellation);
        Assert.Equal("https://svip.example/index.m3u8", playback.Uri);
        Assert.Contains("https://api.wwgz.cn:520/player/?url=https://cdn/two.m3u8", rec.Urls[^1]);
        Assert.Equal("Mozilla/5.0 (Linux; Android 10; SM-G975F) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.120 Mobile Safari/537.36", playback.Headers["User-Agent"]);
    }

    [Fact]
    public async Task WwysPlayerFallsBackToMacUrlWhenThePlayerScriptIsUnavailable()
    {
        var rec = new SyntheticSourceHelpers.Recorder(
            (url, _) => Task.FromResult(url.Contains("vod-play-id", StringComparison.Ordinal) ? WwysPlay : ""),
            url => url.Contains("player/monday.js", StringComparison.Ordinal));
        var source = new WwysSource(SyntheticSourceHelpers.Site("wwys", "csp_Wwys", "https://vip.wwgz.cn:5200"),
            (url, ct) => rec.Fetch(url, ct));
        var playback = await source.ResolvePlaybackAsync("96476", "/vod-play-id-96476-src-2-num-1.html", TestContext.Current.CancellationToken);
        Assert.Equal("https://other/one.m3u8", playback.Uri);
    }

    [Fact]
    public void WwysSourceOnlyAcceptsHttpBaseExt()
    {
        Assert.True(WwysSource.Supports("https://vip.wwgz.cn:5200"));
        Assert.True(WwysSource.Supports("https://vip.wwgz.cn:5200/"));
        Assert.False(WwysSource.Supports(null));
        Assert.False(WwysSource.Supports("https://vip.wwgz.cn:5200/path"));
        Assert.False(WwysSource.Supports("https://vip.wwgz.cn:5200/?q=1"));
    }

    private const string AppV6Nav = """
        {"code":1,"msg":"导航列表","data":[{"type_id":1,"type_name":"电影","type_extend":{"class":"科幻,剧情,惊悚","area":"大陆,香港","year":"2026,2025","lang":"国语"}}]}
        """;

    private const string AppV6List = """
        {"code":1,"data":[{"vod_id":11,"vod_name":"列表片","vod_pic":"https://img/11.jpg","vod_remarks":"更新中"}]}
        """;

    private const string AppV6Detail = """
        {"code":1,"data":{"vod_info":{"vod_name":"列表片","vod_pic":"https://img/11.jpg","vod_class":"动作","vod_remarks":"全12集","vod_content":" 简介  ","vod_actor":"甲","vod_director":"乙","vod_url_with_player":[{"name":"线路一","url":"第1集$https://cdn/1.m3u8#第2集$https://cdn/2.m3u8","parse_api":"https://jx.example/?url="}]}}}
        """;

    [Fact]
    public async Task AppV6UsesExtAsApiRootAndKeepsRequestShapes()
    {
        var rec = new SyntheticSourceHelpers.Recorder((url, _) => Task.FromResult(
            url.Contains("/v6/nav", StringComparison.Ordinal) ? AppV6Nav :
            url.Contains("/v6/index_video", StringComparison.Ordinal) ? """{"code":1,"data":[{"vlist":[{"vod_id":1,"vod_name":"首页片","vod_pic":"p","vod_remarks":"r"}]}]}""" :
            url.Contains("/v6/video_detail", StringComparison.Ordinal) ? AppV6Detail :
            url.Contains("api.example/play", StringComparison.Ordinal) ? """{"url":"https://cdn/1.m3u8"}""" :
            url.Contains("/v6/search", StringComparison.Ordinal) ? """{"code":1,"data":[{"vod_id":5,"vod_name":"搜到","vod_pic":"p","vod_remarks":"r"}]}""" :
            url.Contains("/v6/video?", StringComparison.Ordinal) ? AppV6List : ""));
        var source = new AppV6Source(SyntheticSourceHelpers.Site("v6", "csp_AppV6", "http://172.247.227.203:25321/mtys.php"),
            (url, ct) => rec.Fetch(url, ct));
        var cancellation = TestContext.Current.CancellationToken;

        Assert.Equal(new[] { ("1", "电影") }, (await source.GetCategoriesAsync(cancellation)).Select(c => (c.Id, c.Name)));
        var filters = await source.GetFiltersAsync("1", cancellation);
        Assert.Equal(new[] { "类型", "地区", "年份" }, filters.Select(g => g.Name));
        Assert.Equal(3, filters[0].Values.Count);
        Assert.DoesNotContain(filters, g => g.Key == "lang");

        var home = await source.GetHomeAsync(cancellation);
        Assert.Equal("首页片", Assert.Single(home.Items).Title);
        Assert.EndsWith("/mtys.php/v6/index_video", rec.Urls[^1]);

        var list = await source.GetItemsAsync("1", 2, new Dictionary<string, string> { ["class"] = "科幻", ["area"] = "大陆", ["lang"] = "国语", ["year"] = "2026" }, cancellation);
        Assert.Equal("列表片", Assert.Single(list.Items).Title);
        Assert.EndsWith("/v6/video?pg=2&tid=1&class=%E7%A7%91%E5%B9%BB&area=%E5%A4%A7%E9%99%86&lang=%E5%9B%BD%E8%AF%AD&year=2026&order=time&token=&", rec.Urls[^1]);

        Assert.Equal("搜到", Assert.Single((await source.SearchAsync("列表", 1, cancellation)).Items).Title);

        var detail = await source.GetDetailAsync("11", cancellation);
        Assert.Equal("简介", detail.Description);
        Assert.Equal("甲", detail.Actor);
        Assert.Equal("线路一", Assert.Single(detail.Lines).Id);
        Assert.Equal("https://jx.example/?url=https://cdn/1.m3u8", detail.Lines[0].Episodes[0].Id);

        var direct = await source.ResolvePlaybackAsync("11", "https://cdn/1.m3u8", cancellation);
        Assert.Equal("https://cdn/1.m3u8", direct.Uri);
        Assert.Equal("okhttp/4.9.3", direct.Headers["User-Agent"]);

        var indirect = await source.ResolvePlaybackAsync("11", "https://api.example/play?url=abc", cancellation);
        Assert.Equal("https://cdn/1.m3u8", indirect.Uri);

        await Assert.ThrowsAsync<InvalidDataException>(() => source.ResolvePlaybackAsync("11", "https://cdn/only.mp3", cancellation));
    }

    [Fact]
    public void AppV6SourceRequiresHttpExt()
    {
        Assert.True(AppV6Source.Supports("http://172.247.227.203:25321/mtys.php"));
        Assert.False(AppV6Source.Supports(""));
        Assert.False(AppV6Source.Supports("ftp://x/y"));
    }

    private const string TingBooks = """
        <ul>
        <a href="/book/39351.html" class="item-link" title="御廷谣有声全集">
          <li class="item"><div class="icon"><img src="https://img/t1.jpg" class="list-img"></div>
          <div class="text"><h2 class="name"><span class="status">连载中 | </span>御廷谣</h2><div class="desc">权谋</div></div></li>
        </a>
        <a href="/book/1000.html" class="item-link" title="穿越">
          <li class="item"><div class="icon"><img src="//img/t2.jpg" class="list-img"></div>
          <div class="text"><h2 class="name">四合院：这个穿越有点早</h2><div class="desc">年代</div></div></li>
        </a>
        </ul>
        """;

    private const string TingDetail = """
        <div class="book-detail"><h1 class="title">四合院：这个穿越有点早</h1></div>
        <div class="book-detail-content"><div class="detail-top"><div class="book clearfix">
        <img src="https://app.365ting.com/uploads/bookcover.png" alt="四合院" class="img lf">
        <div class="desc"><h2 class="book-title">四合院：这个穿越有点早</h2>
        <div class="extra">类型:<span class="text"><a href="/category/13.html" title="重生穿越小说">重生穿越</a></span></div>
        <div class="extra">作者:<span class="text">佚名</span></div>
        <div class="extra">播音:<span class="text">咖啡Coffee</span></div>
        <div class="book-intro">简介正文</div>
        </div></div></div>
        <div class="book-list clearfix"><a href="/play/1000/1691794.html" title="第0001集" class="list-item">1</a><a href="/play/1000/1691795.html" title="第0002集" class="list-item">2</a></div>
        <ul class="pagination"><li><a href="/book/1000.html?page=3">3</a></li></ul>
        </div>
        """;

    [Fact]
    public async Task TingBookParsesBooksDetailChaptersAndPlayerEndpoint()
    {
        var rec = new SyntheticSourceHelpers.Recorder((url, _) => Task.FromResult(
            url.Contains("getchapterurl", StringComparison.Ordinal) ? """{"jsjm":0,"src":"//cdn.example/a.mp3"}""" : TingBooks));
        var source = new TingBookSource(SyntheticSourceHelpers.Site("ting", "csp_TingBook", "https://app.365ting.com"),
            (url, ct) => rec.Fetch(url, ct));
        var cancellation = TestContext.Current.CancellationToken;

        var categories = await source.GetCategoriesAsync(cancellation);
        Assert.Equal(16, categories.Count);
        Assert.Equal(("6", "玄幻奇幻"), (categories[0].Id, categories[0].Name));

        var home = await source.GetHomeAsync(cancellation);
        Assert.Equal(new[] { "连载中", "四合院：这个穿越有点早" }, home.Items.Select(i => i.Title));
        Assert.Equal("御廷谣", home.Items[0].Remarks);
        Assert.Equal("https://img/t1.jpg", home.Items[0].Poster);
        Assert.Equal("https://img/t2.jpg", home.Items[1].Poster);

        var search = await source.SearchAsync("四合院", 1, cancellation);
        Assert.Equal(2, search.Items.Count);
        Assert.Contains("/pc/index/search/keyword/%E5%9B%9B%E5%90%88%E9%99%A2.html", rec.Urls[^1]);

        var detailRec = new SyntheticSourceHelpers.Recorder((url, _) => Task.FromResult(
            url.Contains("getchapterurl", StringComparison.Ordinal) ? """{"jsjm":0,"src":"//cdn.example/a.mp3"}""" : TingDetail));
        var detailSource = new TingBookSource(SyntheticSourceHelpers.Site("ting", "csp_TingBook", "https://app.365ting.com"),
            (url, ct) => detailRec.Fetch(url, ct));
        var detail = await detailSource.GetDetailAsync("/book/1000.html", cancellation);
        Assert.Equal("四合院：这个穿越有点早", detail.Item.Title);
        Assert.Equal("https://app.365ting.com/uploads/bookcover.png", detail.Item.Poster);
        Assert.Equal("重生穿越", detail.Item.TypeName);
        Assert.Equal("佚名", detail.Actor);
        Assert.Equal("咖啡Coffee", detail.Director);
        Assert.Equal("六月", Assert.Single(detail.Lines).Id);
        Assert.Equal("第0001集", detail.Lines[0].Episodes[0].Title);
        Assert.Equal("/play/1000/1691794.html", detail.Lines[0].Episodes[0].Id);
        Assert.Contains("https://app.365ting.com/book/1000.html?page=2", detailRec.Urls);

        var playback = await detailSource.ResolvePlaybackAsync("/book/1000.html", "/play/1000/1691794.html", cancellation);
        Assert.Equal("https://cdn.example/a.mp3", playback.Uri);
        Assert.Contains("/pc/index/getchapterurl/bookId/1000/chapterId/1691794/timestamp/", detailRec.Urls[^1]);
        Assert.Equal("https://app.365ting.com/", playback.Headers["Referer"]);
    }

    [Fact]
    public void TingBookDecodesCharCodesAndValidatesExt()
    {
        Assert.Equal("https://cdn.example/x.mp3", TingBookSource.Decode(1, "47*47*99*100*110*46*101*120*97*109*112*108*101*47*120*46*109*112*51"));
        Assert.Equal("https://cdn/x.mp3", TingBookSource.Decode(0, "//cdn/x.mp3"));
        Assert.Equal("", TingBookSource.Decode(0, ""));
        Assert.True(TingBookSource.Supports(null));
        Assert.True(TingBookSource.Supports("https://app.365ting.com"));
        Assert.True(TingBookSource.Supports("""{"site":"https://app.365ting.com"}"""));
        Assert.False(TingBookSource.Supports("app.365ting.com"));
        Assert.False(TingBookSource.Supports("""{"other":"https://app.365ting.com"}"""));
    }

    private const string HeMaList = """
        {"code":0,"data":{"list":[{"bookId":"1001","bookName":"短剧A","cover":"https://img/a.jpg","chapterInfo":"更新至2集"}]}}
        """;

    private const string HeMaDetail = """
        {"code":0,"data":{"bookId":"1001","bookName":"短剧A","cover":"https://img/a.jpg","chapterInfo":"更新至2集","intro":"简介","chapterList":[{"chapterId":"101","chapterName":"第1集","preChapterId":"101"},{"chapterId":"102","chapterName":"第2集","preChapterId":"101"}]}}
        """;

    private const string HeMaPlay = """
        {"code":0,"data":{"list":[{"chapterId":"102","content":{"mp4Url":"https://cdn/hm/2.mp4"}}]}}
        """;

    [Fact]
    public async Task HeMaWrapsRequestsInThePortalEnvelopeAndResolvesPlayback()
    {
        var bodies = new List<string>();
        var source = new HeMaSource(SyntheticSourceHelpers.Site("hema", "csp_HeMa", null),
            (url, body, _) =>
            {
                bodies.Add(body);
                return Task.FromResult(body.Contains("\"1131\"", StringComparison.Ordinal) ? HeMaDetail
                    : body.Contains("\"1139\"", StringComparison.Ordinal) ? HeMaPlay
                    : body.Contains("\"1802\"", StringComparison.Ordinal) ? HeMaList : HeMaList);
            });
        var cancellation = TestContext.Current.CancellationToken;

        Assert.Equal(new[] { "短剧", "漫剧", "热播榜", "飙升榜", "新剧榜", "热搜榜" }, (await source.GetCategoriesAsync(cancellation)).Select(c => c.Name));
        Assert.Equal("分类", Assert.Single(await source.GetFiltersAsync("drama_all", cancellation)).Name);

        var list = await source.GetItemsAsync("drama_all", 1, null, cancellation);
        Assert.Equal("短剧A", Assert.Single(list.Items).Title);
        Assert.Contains("\"apiId\":\"1125\"", bodies[^1]);
        Assert.Contains("\"url\":\"https://freevideo.zqqds.cn/free-video-portal/portal/1125\"", bodies[^1]);
        Assert.Contains("BnCMa2+Kbcl9D1ctCsHNdkR0b6tNUb4MWkCbJX2wXsVtqTKVONaSzTI3b+ulP+SEOFULO5vnXXaMNkE6Gcy7K0g==", bodies[^1]);
        Assert.Contains("\\\"channelGroupId\\\":\\\"157\\\"", bodies[^1]);

        var ranked = await source.GetItemsAsync("rank_rise", 1, null, cancellation);
        Assert.Contains("\\\"channelGroupId\\\":\\\"14\\\"", bodies[^1]);
        Assert.Contains("\\\"channelId\\\":69", bodies[^1]);
        Assert.NotNull(ranked);

        var search = await source.SearchAsync("短剧", 1, cancellation);
        Assert.Contains("\\\"keyword\\\":\\\"短剧\\\"", bodies[^1]);
        Assert.Single(search.Items);

        var detail = await source.GetDetailAsync("1001", cancellation);
        Assert.Equal("短剧A", detail.Item.Title);
        Assert.Equal("简介", detail.Description);
        Assert.Equal("更新至2集", detail.Item.Remarks);
        Assert.Equal(2, Assert.Single(detail.Lines).Episodes.Count);
        Assert.Equal("1001|||101|||101", detail.Lines[0].Episodes[0].Id);

        var playback = await source.ResolvePlaybackAsync("1001", "1001|||102|||101", cancellation);
        Assert.Equal("https://cdn/hm/2.mp4", playback.Uri);
        Assert.Equal("https://freevideo.zqqds.cn/", playback.Headers["Referer"]);
    }

    [Fact]
    public void HeMaSourceAcceptsOnlyItsConfigShape()
    {
        Assert.True(HeMaSource.Supports(null));
        Assert.True(HeMaSource.Supports("""{"version":"3.7.1","installTime":1784189517540}"""));
        Assert.False(HeMaSource.Supports("https://example"));
        Assert.False(HeMaSource.Supports("""{"unknown":1}"""));
        Assert.False(HeMaSource.Supports("""{"version":123}"""));
    }

    private const string FeiyuCategories = """
        {"code":0,"data":[{"id":1,"name":"电影"},{"id":2,"name":"电视剧"}]}
        """;

    private const string FeiyuList = """
        {"code":0,"data":{"list":[{"id":21,"name":"飞鱼片","pic":"https://img/f.jpg","remarks":"HD"}]}}
        """;

    private const string FeiyuDetail = """
        {"code":0,"data":{"id":21,"name":"飞鱼片","pic":"https://img/f.jpg","categoryName":"电影","year":"2026","area":"大陆","actor":"甲","director":"乙","content":"简介","playGroups":[{"name":"m3u8","code":"m3u8","parseApi":"","playUrls":[{"name":"第1集","url":"https://cdn/f.m3u8"}]},{"name":"4K高清","code":"4k","parseApi":"https://jx.example/json?url=","playUrls":[{"name":"第1集","url":"https://cdn/4k.m3u8"}]}]}}
        """;

    [Fact]
    public async Task FeiyuSignsWithThePluginAlgorithmAndPortsPlayback()
    {
        var nonce = "AAAAAAAAAAAAAAAAAAAAAA==";
        const long clock = 1792260000;
        var requestHeaders = new List<IReadOnlyDictionary<string, string>>();
        var urls = new List<string>();
        var source = new FeiyuSource(SyntheticSourceHelpers.Site("feiyu", "csp_Feiyu", null),
            (url, headers, _) =>
            {
                urls.Add(url);
                requestHeaders.Add(headers);
                return Task.FromResult(url.StartsWith("https://jx.example/json", StringComparison.Ordinal) ? """{"url":"https://cdn/resolved.m3u8"}"""
                    : url.Contains("/categories/1/videos", StringComparison.Ordinal) ? FeiyuList
                    : url.Contains("/videos/search", StringComparison.Ordinal) ? FeiyuList
                    : url.Contains("/videos/21", StringComparison.Ordinal) ? FeiyuDetail
                    : url.Contains("/ranking/list", StringComparison.Ordinal) ? FeiyuList
                    : FeiyuCategories);
            },
            () => nonce, () => clock, null);
        var cancellation = TestContext.Current.CancellationToken;

        Assert.Equal("007dbfc8184236587aa00d40c9a624fc5b76b04538e817761a4a1d3d44983412", FeiyuSource.SignatureKey());
        Assert.Equal(new[] { ("1", "电影"), ("2", "电视剧") }, (await source.GetCategoriesAsync(cancellation)).Select(c => (c.Id, c.Name)));
        Assert.Equal("https://4kyszx.top/api/app/categories", urls[0]);
        Assert.Equal(ExpectedSignature("/api/app/categories", "", clock, nonce), requestHeaders[0]["x-signature"]);
        Assert.Equal("4kyszx.top", requestHeaders[0]["host"]);
        Assert.Equal(nonce, requestHeaders[0]["x-nonce"]);

        var list = await source.GetItemsAsync("1", 2, new Dictionary<string, string> { ["class"] = "科幻" }, cancellation);
        Assert.Equal("飞鱼片", Assert.Single(list.Items).Title);
        Assert.Equal("https://4kyszx.top/api/app/categories/1/videos?page=2&page_size=20&class=%E7%A7%91%E5%B9%BB", urls[^1]);
        Assert.Equal(ExpectedSignature("/api/app/categories/1/videos", "page=2&page_size=20&class=科幻", clock, nonce), requestHeaders[^1]["x-signature"]);

        var detail = await source.GetDetailAsync("21", cancellation);
        Assert.Equal(new[] { "4K高清", "m3u8" }, detail.Lines.Select(l => l.Id));
        Assert.Equal("https://jx.example/json?url=||https://cdn/4k.m3u8", detail.Lines[0].Episodes[0].Id);
        Assert.Equal("||https://cdn/f.m3u8", detail.Lines[1].Episodes[0].Id);

        var direct = await source.ResolvePlaybackAsync("21", "||https://cdn/f.m3u8", cancellation);
        Assert.Equal("https://cdn/f.m3u8", direct.Uri);
        Assert.Null(direct.ParseEndpoint);

        var parsed = await source.ResolvePlaybackAsync("21", "https://jx.example/json?url=||https://cdn/4k.m3u8", cancellation);
        Assert.Equal("https://cdn/resolved.m3u8", parsed.Uri);
        Assert.Null(parsed.ParseEndpoint);

        var delegated = await source.ResolvePlaybackAsync("21", "https://openapi/jx/||https://cdn/4k.m3u8", cancellation);
        Assert.Equal("https://cdn/4k.m3u8", delegated.Uri);
        Assert.Equal("https://openapi/jx/", delegated.ParseEndpoint);
    }

    [Fact]
    public void FeiyuUrlEncodeAndLineRankMatchThePlugin()
    {
        Assert.Equal("keyword=%E4%B8%AD%E6%96%87+%E5%A4%AA%E7%A9%BA", FeiyuSource.JavaUrlEncode([("keyword", "中文 太空")]));
        Assert.Equal("a=1&b=%2B%2F*", FeiyuSource.JavaUrlEncode([("a", "1"), ("b", "+/*")]));
        Assert.Equal(100, FeiyuSource.LineRank("4K高清"));
        Assert.Equal(90, FeiyuSource.LineRank("藍光"));
        Assert.Equal(10, FeiyuSource.LineRank("m3u8"));
        Assert.Equal(50, FeiyuSource.LineRank("其它"));
        Assert.True(FeiyuSource.Supports(null));
        Assert.True(FeiyuSource.Supports("   "));
        Assert.False(FeiyuSource.Supports("https://4kyszx.top"));
    }

    private static string ExpectedSignature(string path, string canonicalParameters, long clock, string nonce)
    {
        var key = Hex(HMACSHA256.HashData(Encoding.UTF8.GetBytes("cms_device_salt_v1_2024cms_app_sign_key_v1_2024_secure"), Encoding.UTF8.GetBytes("f45a775875e2e004adbcea78e3312218")));
        var message = "GET\n" + path + "\n" + canonicalParameters + "\n" + clock.ToString(CultureInfo.InvariantCulture) + "\n" + nonce + "\n2.6.8+1";
        return Hex(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(message)));
    }

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
}
