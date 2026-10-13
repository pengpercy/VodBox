using System.Text;
using VodBox.Core;
using VodBox.Infrastructure;
using Xunit;

namespace VodBox.Tests;

public sealed class KanqiuSourceTests
{
    private static readonly SourceInfo Site = new() { Key = "看球", Name = "看球", Runtime = SourceRuntime.NativeSpider, Api = "csp_KanqiuGuard" };

    private const string Schedule = """
        <li class=""> <a style="padding: 0 15px;" href='/match/1/live'> <img class="lazyload" alt="NBA" data-src="x.png">NBA </a> </li>
        <ul class="list-group"><li class="list-group-item live-list-group-time"> <span class="date"></span> 2026-10-13 </li>
        <li class="list-group-item group-game-item"><div class="col-md-1 category-game-time">15:00 </div><span class="game-type">菲NCAA</span>
        <span class="team-name"
              title="LPU海盗">LPU海盗</span> VS <span class="team-name" title="UPHSD&amp;阿尔塔斯"> x</span>
        <div data-id="129575" class="pay-btn"><a class="btn" href='/live/129575/play' role="button">直播中<i></i></a></div></li>
        <li class="list-group-item live-list-group-time"> 2026-10-14 </li>
        <li class="list-group-item group-game-item"><div class="col-md-1 category-game-time">07:00</div><span class="game-type">NBA</span>
        <span class="team-name" title="魔术">魔术</span><span class="team-name" title="骑士">骑士</span>
        <div data-id="127956" class="pay-btn"><a class="btn btn-disabled" href='/live/127956/play' role="button"> 暂无 </a></div></li></ul>
        """;

    private static string Source(string payload) =>
        "{\"data\":\"Kdhbai" + Convert.ToBase64String(Encoding.UTF8.GetBytes(payload)) + "wE\"}";

    [Fact]
    public void ScheduleCarriesDateTimeLeagueAndLiveStatus()
    {
        var items = KanqiuSource.ParseSchedule(Schedule);
        Assert.Equal(["129575", "127956"], items.Select(item => item.Id));
        Assert.Equal("LPU海盗 VS UPHSD&阿尔塔斯", items[0].Title);
        Assert.Equal("2026-10-13 15:00 菲NCAA 直播中", items[0].Remarks);
        Assert.Equal("2026-10-14 07:00 NBA", items[1].Remarks);
    }

    [Fact]
    public void LinesUnwrapPlayerPagesAndNumberDuplicateNames()
    {
        var lines = KanqiuSource.ParseLines(Source("""
            {"links":[{"url":"https://play.example/m3u8.html?url=https%3A%2F%2Fcdn.example%2Fa.m3u8%3Fauth%3D1","name":"线路C4"},
                      {"url":"https://cdn.example/b.m3u8","name":"线路C4"},{"url":"javascript:alert(1)","name":"坏"},{"url":"https://cdn.example/c.flv","name":""}]}
            """));
        Assert.Equal(["线路C4", "线路C4 (2)", "线路4"], lines.Select(line => line.Title));
        Assert.Equal("https://cdn.example/a.m3u8?auth=1", lines[0].Uri);
        Assert.Empty(KanqiuSource.ParseLines(Source("{\"links\":null}")));
        Assert.Throws<InvalidDataException>(() => KanqiuSource.ParseLines("{\"data\":1}"));
    }

    [Fact]
    public async Task PlaybackPutsChosenLineFirstAndReportsNotStarted()
    {
        var links = Source("""{"links":[{"url":"https://cdn.example/1.m3u8","name":"Q1"},{"url":"https://cdn.example/2.m3u8","name":"Q2"}]}""");
        var referers = new List<string?>();
        var source = new KanqiuSource(Site, (url, referer, _) =>
        {
            referers.Add(referer);
            return Task.FromResult(url.EndsWith("/source", StringComparison.Ordinal)
                ? url.Contains("/1/", StringComparison.Ordinal) ? Source("{\"links\":null}") : links
                : "<title>魔术 vs 骑士_在线直播_NBA</title>");
        });
        var request = await source.ResolvePlaybackAsync("129575", "1", TestContext.Current.CancellationToken);
        Assert.Equal(("https://cdn.example/2.m3u8", "魔术 vs 骑士", true), (request.Uri, request.Title, request.IsLive));
        Assert.Equal(["Q2", "Q1"], request.Choices.Select(choice => choice.Name));
        Assert.Equal("https://www.88kanqiu.one/live/129575/play", referers[1]);
        var notStarted = await Assert.ThrowsAsync<InvalidDataException>(() => source.GetDetailAsync("1", TestContext.Current.CancellationToken));
        Assert.Contains("尚未开始", notStarted.Message);
        await Assert.ThrowsAsync<InvalidDataException>(() => source.GetDetailAsync("1/../2", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CategoriesComeFromLeagueNav()
    {
        var source = new KanqiuSource(Site, (_, _, _) => Task.FromResult(Schedule));
        var categories = await source.GetCategoriesAsync(TestContext.Current.CancellationToken);
        Assert.Equal([new Category("0", "全部直播"), new Category("1", "NBA")], categories);
        Assert.IsType<KanqiuSource>(NativeSpiders.Create(Site));
    }
}
