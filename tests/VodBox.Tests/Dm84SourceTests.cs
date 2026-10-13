using VodBox.Core;
using VodBox.Infrastructure;
using Xunit;

namespace VodBox.Tests;

public sealed class Dm84SourceTests
{
    private static readonly SourceInfo Site = new() { Key = "Dm84", Name = "巴士", Runtime = SourceRuntime.NativeSpider, Api = "csp_Dm84Guard" };

    private const string List = """
        <a href="/show-2---%E5%86%92%E9%99%A9---.html">冒险</a><a href="/show-2-----2025-.html">2025</a>
        <a rel="nofllow" href="/show-2--time----.html" class='current'>按时间</a><a rel="nofllow" href="/show-2--hits----.html">按人气</a>
        <ul class="v_list"><li><div class="item"><a href="/v/6266.html" class="cover lazy" data-bg="https://img.example/a.jpg?w=1&amp;h=2" title="永夜之王在线观看">永夜之王在线观看</a><a class="title" href="/v/6266.html" title="永夜之王">永夜之王</a><span class="desc">第20话</span></div></li>
        <li><div class="item"><a href="/v/6232.html" class="cover lazy" data-bg="https://img.example/b.jpg" title="山海经"></a><a class="title" href="/v/6232.html" title="山海经">山海经</a></div></li></ul>
        <a href="/show-2--hits-%E5%86%92%E9%99%A9---3.html">下一页</a>
        """;

    private const string Detail = """
        <h1 class="v_title"><a href="/v/4183.html" title="x">斗罗大陆2</a></h1>
        <p class="v_desc"><span class="desc">第174话</span><em class="hr">|</em>2023<em class="hr">|</em>大陆<em class="hr">|</em><a href="/show">奇幻</a>,<a>热血</a></p>
        <div class="v_content" id="v_content"><div class="cover"><img src="https://img.example/c.jpg" /></div><div class="content" id="intro_content"><div id="intro"><p>导演：沈乐平</p><p>主演：霍雨浩,唐舞桐</p><p>又名：绝世唐门</p><p>剧情：这里没有魔法。</p><div class="show_more" id="show_more"></div></div></div></div>
        <ul class="tab_control play_from"><li class="current">线路1</li><li>备用</li></ul>
        <ul class="play_list current"><li><a href="/p/4183-1-2.html">2</a></li><li><a href="/p/4183-1-1.html">1</a></li></ul>
        <ul class="play_list"><li><a href="/p/4183-2-1.html">HD</a></li><li><a href="/p/9999-2-1.html">外站</a></li></ul>
        """;

    [Fact]
    public void CardsKeepPosterTitleAndRemarks()
    {
        var items = Dm84Source.ParseCards(List);
        Assert.Equal(["6266", "6232"], items.Select(item => item.Id));
        Assert.Equal("永夜之王", items[0].Title);
        Assert.Equal("https://img.example/a.jpg?w=1&h=2", items[0].Poster);
        Assert.Equal("第20话", items[0].Remarks);
        Assert.Null(items[1].Remarks);
    }

    [Fact]
    public async Task FiltersAndListUrlFollowTheShowLayout()
    {
        var urls = new List<string>();
        var source = new Dm84Source(Site, (url, _, _, _) => { urls.Add(url); return Task.FromResult(List); });
        var filters = await source.GetFiltersAsync("2", TestContext.Current.CancellationToken);
        Assert.Equal(["class", "year", "by"], filters.Select(group => group.Key));
        Assert.Equal(new FilterValue("冒险", "冒险"), filters[0].Values[1]);
        Assert.Equal("time", filters[2].Init);
        var page = await source.GetItemsAsync("2", 2, new Dictionary<string, string> { ["class"] = "冒险", ["by"] = "hits" }, TestContext.Current.CancellationToken);
        Assert.Equal("https://dm84.net/show-2--hits-%E5%86%92%E9%99%A9---2.html", urls[^1]);
        Assert.Equal(3, page.PageCount);
        await Assert.ThrowsAsync<InvalidDataException>(() => source.GetItemsAsync("2", 1, new Dictionary<string, string> { ["year"] = "2025-x" }, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidDataException>(() => source.GetItemsAsync("5", 1, null, TestContext.Current.CancellationToken));
        await source.SearchAsync("斗罗", 1, TestContext.Current.CancellationToken);
        Assert.Equal("https://dm84.net/s-%E6%96%97%E7%BD%97---------.html", urls[^1]);
    }

    [Fact]
    public void DetailReadsLabeledIntroAndOrdersEpisodesPerLine()
    {
        var detail = Dm84Source.ParseDetail("4183", Detail);
        Assert.Equal("斗罗大陆2", detail.Item.Title);
        Assert.Equal(("第174话", "2023", "大陆", "奇幻,热血"), (detail.Item.Remarks, detail.Item.Year, detail.Item.Area, detail.Item.TypeName));
        Assert.Equal(("沈乐平", "霍雨浩,唐舞桐", "这里没有魔法。"), (detail.Director, detail.Actor, detail.Description));
        Assert.Equal(["线路1", "备用"], detail.Lines.Select(line => line.Name));
        Assert.Equal(["4183-1-1", "4183-1-2"], detail.Lines[0].Episodes.Select(episode => episode.Id));
        Assert.Equal("第1集", detail.Lines[0].Episodes[0].Title);
        Assert.Equal("HD", Assert.Single(detail.Lines[1].Episodes).Title);
        var plain = Dm84Source.ParseDetail("4183", Detail.Replace("<p>导演：沈乐平</p><p>主演：霍雨浩,唐舞桐</p><p>又名：绝世唐门</p><p>剧情：这里没有魔法。</p>", "<p>只有简介。</p>"));
        Assert.Equal("只有简介。", plain.Description);
    }

    [Fact]
    public async Task PlaybackPostsBootstrapToParserWithClientFallback()
    {
        var calls = new List<(string Url, string? Referer, string? Body)>();
        var source = new Dm84Source(Site, (url, referer, body, _) =>
        {
            calls.Add((url, referer, body));
            return Task.FromResult(url switch
            {
                "https://dm84.net/p/4183-1-1.html" => "<h1>斗罗 - 第1集</h1><iframe src=\"https://hh.example/?url=ABCDEF\" width=\"100%\"></iframe>",
                "https://hh.example/?url=ABCDEF" => "<script>window.__HHJX_BOOTSTRAP__={\"url\":\"ABCDEF\",\"t\":1791880476,\"key\":\"k1\",\"ts_key\":\"k2\"};</script>",
                "https://hh.example/api/parse" => "{\"code\":200,\"url\":\"https://cdn.example/v.m3u8?a=1\"}",
                _ => throw new InvalidOperationException(url),
            });
        });
        var request = await source.ResolvePlaybackAsync("4183", "4183-1-1", TestContext.Current.CancellationToken);
        Assert.Equal("https://cdn.example/v.m3u8?a=1", request.Uri);
        Assert.Equal(("斗罗 - 第1集", "line1"), (request.Title, request.LineId));
        Assert.False(request.ProxyMedia);
        var parse = calls[^1];
        Assert.Equal("https://hh.example/?url=ABCDEF", parse.Referer);
        Assert.Equal("{\"url\":\"ABCDEF\",\"t\":1791880476,\"key\":\"k1\",\"client_fallback\":true}", parse.Body);
        await Assert.ThrowsAsync<InvalidDataException>(() => source.ResolvePlaybackAsync("4183", "9999-1-1", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RewrittenHlsGoesThroughParserRelayAndBrowserOnlyLinesFail()
    {
        string parse = "{\"code\":200,\"url\":\"https://cdn.example/a.m3u8\",\"ext\":\"hls_rewrite\"}";
        var source = new Dm84Source(Site, (url, _, _, _) => Task.FromResult(url switch
        {
            "https://dm84.net/p/1-1-1.html" => "<iframe src=\"https://hh.example/?url=AA\"></iframe>",
            "https://hh.example/?url=AA" => "__HHJX_BOOTSTRAP__={\"url\":\"AA\",\"t\":5,\"key\":\"k\",\"ts_key\":\"t k\"};",
            _ => parse,
        }));
        var request = await source.ResolvePlaybackAsync("1", "1-1-1", TestContext.Current.CancellationToken);
        Assert.Equal("https://hh.example/getts?url=https%3A%2F%2Fcdn.example%2Fa.m3u8&t=5&key=t%20k", request.Uri);
        parse = "{\"code\":200,\"url\":\"XNjU=\",\"ext\":\"youku\"}";
        await Assert.ThrowsAsync<InvalidDataException>(() => source.ResolvePlaybackAsync("1", "1-1-1", TestContext.Current.CancellationToken));
    }

    [Fact]
    public void GuardAndPlainApiNamesAreRegistered()
    {
        Assert.IsType<Dm84Source>(NativeSpiders.Create(Site));
        Assert.True(NativeSpiders.IsSupported("csp_Dm84"));
    }
}
