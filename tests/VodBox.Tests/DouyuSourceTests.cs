using System.Net;
using System.Text;
using VodBox.Core;
using VodBox.Infrastructure;
using Xunit;

namespace VodBox.Tests;

/// <summary>斗鱼直播爬虫离线回归：vike_pageContext JSON + api 列表锁定。</summary>
public class DouyuSourceTests
{
    private static SourceInfo Info() => new()
    {
        Key = "斗鱼js", Name = "斗鱼直播", Runtime = SourceRuntime.NativeSpider, Api = "csp_Douyu",
    };

    private sealed class Handler : HttpMessageHandler
    {
        private readonly Func<string, string> _route;
        public Handler(Func<string, string> route) => _route = route;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var key = request.RequestUri!.AbsolutePath.TrimStart('/') + request.RequestUri!.Query;
            var isJson = request.RequestUri!.AbsolutePath.Contains("api/");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_route(key), Encoding.UTF8, isJson ? "application/json" : "text/html"),
                RequestMessage = request,
            });
        }
    }

    private static DouyuSource Create(Func<string, string> route) =>
        new(Info(), new HttpClient(new Handler(route)));

    private static string Esc(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r") + "\"";

    private static string ListJson(string statusKey, params (string id, string name)[] items)
    {
        var arr = string.Join(",", items.Select(i =>
            "{\"" + (statusKey == "error" ? "roomId" : "rid") + "\":" + Esc(i.id) + ",\"roomName\":" + Esc(i.name) + ",\"roomSrc\":" + Esc("https://i.douyu.com/" + i.id + ".jpg") + ",\"nickname\":" + Esc("主播" + i.id) + "}"));
        return "{\"" + statusKey + "\":0,\"data\":{\"list\":[" + arr + "]}}";
    }

    private static string RoomPage(string rid, string name, int isLive)
    {
        // vike_pageContext 里是裸 JSON（不是 JSON 字符串字面量）。
        // 结构：{pageProps:{room:{roomInfo:{roomInfo:{...}}}}} = 5 层 { + 5 层 }（不是 6 层）
        var inner = "{\"rid\":\"" + rid + "\",\"roomName\":\"" + name + "\",\"roomSrc\":\"https://i.douyu.com/" + rid + ".jpg\",\"nickname\":\"主播" + rid + "\",\"isLive\":" + isLive + ",\"notice\":\"公告\"}";
        var ctx = "{\"pageProps\":{\"room\":{\"roomInfo\":{\"roomInfo\":" + inner + "}}}}";
        return "<html><body><script id=\"vike_pageContext\">" + ctx + "</script></body></html>";
    }

    [Fact]
    public async Task Categories_AreEight()
    {
        using var source = Create(_ => ListJson("code"));
        var categories = await source.GetCategoriesAsync();
        Assert.Equal(8, categories.Count);
        Assert.Equal("一起看", categories[0].Name);
        Assert.Equal("正能量", categories[^1].Name);
    }

    [Fact]
    public async Task GetItems_ParsesRoomList()
    {
        using var source = Create(_ => ListJson("code", ("4549169", "港剧间"), ("123", "游戏厅")));
        var page = await source.GetItemsAsync("yqk", 1, null);
        Assert.Equal(2, page.Items.Count);
        Assert.Equal("港剧间", page.Items[0].Title);
        Assert.Equal("4549169", page.Items[0].Id);
        Assert.Equal("https://i.douyu.com/4549169.jpg", page.Items[0].Poster);
        Assert.Equal("主播4549169", page.Items[0].Remarks);
        Assert.Equal(2, page.PageCount); // 非空 → 有下一页
    }

    [Fact]
    public async Task GetItems_EmptyPageStops()
    {
        using var source = Create(_ => ListJson("code"));
        var page = await source.GetItemsAsync("yqk", 2, null);
        Assert.Empty(page.Items);
        Assert.Equal(2, page.PageCount); // 空页 → 保持当前页
    }

    [Fact]
    public async Task GetItems_RejectsInvalidCategoryAndPage()
    {
        using var source = Create(_ => ListJson("code"));
        await Assert.ThrowsAsync<InvalidDataException>(() => source.GetItemsAsync("xxx", 1, null));
        await Assert.ThrowsAsync<InvalidDataException>(() => source.GetItemsAsync("yqk", 0, null));
    }

    [Fact]
    public async Task Search_ParsesLiveRoomResults()
    {
        using var source = Create(_ => ListJson("error", ("777", "英雄联盟"), ("888", "LPL")));
        var page = await source.SearchAsync("英雄联盟", 1);
        Assert.Equal(2, page.Items.Count);
        Assert.Equal("英雄联盟", page.Items[0].Title);
    }

    [Fact]
    public async Task Detail_ExtractsVikePageContext()
    {
        var routes = new Dictionary<string, string>
        {
            ["api/room/list?page=1&type=yqk"] = ListJson("code", ("4549169", "港剧间")),
            ["4549169"] = RoomPage("4549169", "港剧间", 1),
        };
        using var source = Create(path => routes.TryGetValue(path, out var v) ? v : ListJson("code"));
        var detail = await source.GetDetailAsync("4549169");
        Assert.Equal("港剧间", detail.Item.Title);
        Assert.Equal("https://i.douyu.com/4549169.jpg", detail.Item.Poster);
        var line = Assert.Single(detail.Lines);
        Assert.Equal("斗鱼直播", line.Name);
    }

    [Fact]
    public async Task Detail_ThrowsWhenOffline()
    {
        var routes = new Dictionary<string, string>
        {
            ["api/room/list?page=1&type=yqk"] = ListJson("code", ("4549169", "港剧间")),
            ["4549169"] = RoomPage("4549169", "港剧间", 0),
        };
        using var source = Create(path => routes.TryGetValue(path, out var v) ? v : ListJson("code"));
        await Assert.ThrowsAsync<InvalidDataException>(() => source.GetDetailAsync("4549169"));
    }

    [Fact]
    public async Task ResolvePlayback_MarksSniffAndLive()
    {
        var routes = new Dictionary<string, string>
        {
            ["api/room/list?page=1&type=yqk"] = ListJson("code", ("4549169", "港剧间")),
            ["4549169"] = RoomPage("4549169", "港剧间", 1),
        };
        using var source = Create(path => routes.TryGetValue(path, out var v) ? v : ListJson("code"));
        var play = await source.ResolvePlaybackAsync("4549169", "4549169");
        Assert.Equal(ResolutionKind.Sniff, play.Resolution);
        Assert.True(play.IsLive);
        Assert.Equal("斗鱼js", play.SourceKey);
        Assert.Equal("斗鱼直播", play.SourceName);
        Assert.Contains("Referer", play.Headers.Keys);
    }

    [Fact]
    public void Id_ValidatesRoomNumber()
    {
        Assert.Equal("4549169", DouyuSource.Id("4549169"));
        Assert.Throws<InvalidDataException>(() => DouyuSource.Id(""));
        Assert.Throws<InvalidDataException>(() => DouyuSource.Id("abc"));
        Assert.Throws<InvalidDataException>(() => DouyuSource.Id(new string('1', 21)));
    }
}
