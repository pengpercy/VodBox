using VodBox.Core;
using VodBox.Infrastructure;
using Xunit;

namespace VodBox.Tests;

public sealed class BttwooSourceTests
{
    private static readonly SourceInfo Site = new() { Key = "比特", Name = "比特", Runtime = SourceRuntime.NativeSpider, Api = "csp_BttwooGuard" };

    private const string Card = """
        <div class="relative group movie-card" data-vod-id="ch4oc6fir"><div class="block">
        <a href="/play/ch4oc6fir" class="block"> <div class="relative w-full aspect-[2/3]"> <img data-src="https://img.example/a.jpg?x=1&amp;y=2" src="/static/p.svg" alt="地狱尖兵" class="lazy"> </div></a>
        <a href="/play/ch4oc6fir" class="block"> <div class="relative w-full aspect-[2/3]"> <img data-src="https://img.example/a.jpg" src="/static/p.svg" alt="地狱尖兵" class="lazy"> </div></a>
        <a href="/play/ch2f3zaqh" class="block"><div class="x"><img data-src="https://img.example/b.jpg" alt="第二部"></div></a>
        <a href="?page=2">2</a>
        """;

    private const string Detail = """
        <meta name="keywords" content="4k,挑情丑闻,2026,剧情,爱情,韩国">
        <meta property="og:image" content="https://img.example/poster.jpg" />
        <meta property="og:description" content="讲述&amp;故事" />
        <h1 class="text-xl font-bold text-white">挑情丑闻</h1>
        <div class="col-span-1 text-gray-500">导演</div> <div class="col-span-2 text-gray-300">郑址宇</div>
        <div class="col-span-1 text-gray-500">主演</div> <div class="col-span-2 text-gray-300">孙艺珍 / 池昌旭</div>
        <div class="col-span-1 text-gray-500">地区</div> <div class="col-span-2 text-gray-300">韩国</div>
        <a href="/play/ch4ku2k2w" @click.prevent="x" data-line="1" data-episode="2" dataid="38967" class="episode-link"><span x-show="!isEpisodeActive(1, 2)"> 2 </span></a>
        <a href="/play/ch4ku2k2g" @click.prevent="x" data-line="1" data-episode="1" dataid="38966" class="episode-link"><div class="playing-bars"></div><span x-show="!isEpisodeActive(1, 1)"> 1 </span></a>
        <div x-data="{isLoggedIn: false,username: '',uid: '0',userInitial: '',userlink:'X1VUXk9bVAEDAw4FCgc7IUleV0Bb',mobileMenuOpen: false}"></div>
        """;

    [Fact]
    public void PlayPathMatchesSiteSignature()
    {
        // 真实站点前端对同一输入生成的签名（含 userlink 混淆值）。
        var path = BttwooSource.BuildPlayPath("38966", "ch4ku2k2g", "1080", "X1VUXk9bVAEDAw4FCgc7IUleV0Bb", 1791878131149);
        Assert.Equal("/video/play?p=38966&v=ch4ku2k2g&q=1080&s=415a3b4b63bfd3080af9bbe33bdf4a94&t=1791878131149&k=" +
            Uri.EscapeDataString("NlM7Oi4CXFBmc3E3JBRGIzcMBk4nNwEKIFknUA=="), path);
        Assert.EndsWith("s=6e40c51ba932c1b87e52b68a2f1ab00a&t=1791878131149&k=0", BttwooSource.BuildPlayPath("38967", "ch4ku2k2g", "720", "0", 1791878131149));
    }

    [Fact]
    public void CardsAreDeduplicatedAndDecoded()
    {
        var items = BttwooSource.ParseCards(Card);
        Assert.Equal(["ch4oc6fir", "ch2f3zaqh"], items.Select(item => item.Id));
        Assert.Equal("地狱尖兵", items[0].Title);
        Assert.Equal("https://img.example/a.jpg?x=1&y=2", items[0].Poster);
    }

    [Fact]
    public async Task CategoryRequestUsesFilterQueryAndDetectsNextPage()
    {
        string? requested = null;
        var source = new BttwooSource(Site, (url, _, _) => { requested = url; return Task.FromResult(Card); }, null);
        var page = await source.GetItemsAsync("2", 1, new Dictionary<string, string> { ["areas"] = "12", ["years"] = "" }, TestContext.Current.CancellationToken);
        Assert.Equal("https://www.bttwoo.com/filter?classify=2&areas=12", requested);
        Assert.Equal(2, page.PageCount);
        await Assert.ThrowsAsync<InvalidDataException>(() => source.GetItemsAsync("2", 1, new Dictionary<string, string> { ["areas"] = "1&x=2" }, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidDataException>(() => source.GetItemsAsync("9", 1, null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FiltersAreReadFromFilterPageLinks()
    {
        const string html = """
            <a href="?" class="a">全部</a>
            <a href="?areas=12" class="px-3 py-1">
                                韩国
                            </a>
            <a href="?types=5" class="px-3">喜剧</a><a href="?years=3">2025</a><a href="?page=2">2</a>
            """;
        var source = new BttwooSource(Site, (_, _, _) => Task.FromResult(html), null);
        var filters = await source.GetFiltersAsync("1", TestContext.Current.CancellationToken);
        Assert.Equal(["areas", "types", "years"], filters.Select(group => group.Key));
        Assert.Equal(new FilterValue("韩国", "12"), filters[0].Values[1]);
    }

    [Fact]
    public void DetailListsOrderedEpisodesWithDataIds()
    {
        var detail = BttwooSource.ParseDetail("ch4ku2k2g", Detail);
        Assert.Equal("挑情丑闻", detail.Item.Title);
        Assert.Equal("2026", detail.Item.Year);
        Assert.Equal("韩国", detail.Item.Area);
        Assert.Equal("讲述&故事", detail.Description);
        Assert.Equal("孙艺珍 / 池昌旭", detail.Actor);
        var line = Assert.Single(detail.Lines);
        Assert.Equal(["38966|ch4ku2k2g", "38967|ch4ku2k2w"], line.Episodes.Select(episode => episode.Id));
        Assert.Equal("第1集", line.Episodes[0].Title);
    }

    [Fact]
    public async Task PlaybackSignsRequestSkipsLockedQualitiesAndForcesProxy()
    {
        var requests = new List<(string Url, string? Referer)>();
        var source = new BttwooSource(Site, (url, referer, _) =>
        {
            requests.Add((url, referer));
            return Task.FromResult(url.Contains("/video/play", StringComparison.Ordinal)
                ? """{"code":200,"data":{"quality_urls":[{"title":"4K","description":"蓝光","locked":true,"url":"1"},{"title":"1080p","description":"超清","locked":false,"url":"https://cdn.example/m3u8/a.m3u8"}]}}"""
                : Detail);
        }, () => 1791878131149);
        var request = await source.ResolvePlaybackAsync("ch4ku2k2g", "38966|ch4ku2k2g", TestContext.Current.CancellationToken);
        Assert.Equal("https://cdn.example/m3u8/a.m3u8", request.Uri);
        Assert.Empty(request.Choices);
        Assert.True(request.ProxyMedia);
        Assert.Equal("line1", request.LineId);
        Assert.Equal("https://www.bttwoo.com/play/ch4ku2k2g", requests[1].Referer);
        Assert.Contains("s=415a3b4b63bfd3080af9bbe33bdf4a94", requests[1].Url);
        await Assert.ThrowsAsync<InvalidDataException>(() => source.ResolvePlaybackAsync("ch4ku2k2g", "38966|../x", TestContext.Current.CancellationToken));
    }

    [Fact]
    public void GuardAndPlainApiNamesAreRegistered()
    {
        Assert.IsType<BttwooSource>(NativeSpiders.Create(Site));
        Assert.True(NativeSpiders.IsSupported("csp_Bttwoo"));
    }
}
