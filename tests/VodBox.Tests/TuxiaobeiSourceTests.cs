using System.Net;
using System.Text;
using VodBox.Core;
using VodBox.Infrastructure;
using Xunit;

namespace VodBox.Tests;

/// <summary>兔小贝儿童启蒙爬虫离线回归：MIP JSON + HTML 结构锁定。</summary>
public class TuxiaobeiSourceTests
{
    private static SourceInfo Info() => new()
    {
        Key = "dr_兔小贝", Name = "兔小贝", Runtime = SourceRuntime.NativeSpider, Api = "csp_Tuxiaobei",
    };

    private sealed class Handler : HttpMessageHandler
    {
        private readonly Func<string, string> _route;
        public Handler(Func<string, string> route) => _route = route;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var key = request.RequestUri!.AbsolutePath.TrimStart('/') + request.RequestUri!.Query;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_route(key), Encoding.UTF8, request.RequestUri!.AbsolutePath.Contains("play/") ? "text/html" : "application/json"),
                RequestMessage = request,
            });
        }
    }

    private static TuxiaobeiSource Create(Func<string, string> route) =>
        new(Info(), new HttpClient(new Handler(route)));

    private static string Esc(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r") + "\"";

    private static string ListJson(int status, params (string id, string name, string duration)[] items)
    {
        var arr = string.Join(",", items.Select(i =>
            "{\"video_id\":" + i.id + ",\"name\":" + Esc(i.name) + ",\"image\":" + Esc("https://i.tuxiaobei.com/" + i.id + ".jpg") + ",\"duration_string\":" + Esc(i.duration) + "}"));
        return "({\"status\":" + status + ",\"data\":{\"items\":[" + arr + "]}});";
    }

    private static string PlayPage(string title, string videoSrc) => $"""
        <html><head><title>{title}</title></head>
        <body><mip-search-video id="videoWrap" video-src="{videoSrc}"></mip-search-video></body></html>
        """;

    [Fact]
    public async Task Categories_AreFour()
    {
        using var source = Create(_ => ListJson(0));
        var categories = await source.GetCategoriesAsync();
        Assert.Equal(4, categories.Count);
        Assert.Equal(["儿歌", "故事", "国学", "启蒙"], categories.Select(c => c.Name).ToArray());
    }

    [Fact]
    public async Task GetItems_ParsesMipJsonp()
    {
        using var source = Create(_ => ListJson(0, ("2662", "快乐小屋", "02:30"), ("2663", "小星星", "01:45")));
        var page = await source.GetItemsAsync("2", 1, null);
        Assert.Equal(2, page.Items.Count);
        Assert.Equal("快乐小屋", page.Items[0].Title);
        Assert.Equal("2662", page.Items[0].Id);
        Assert.Equal("02:30", page.Items[0].Remarks);
        Assert.Equal("https://i.tuxiaobei.com/2662.jpg", page.Items[0].Poster);
    }

    [Fact]
    public async Task GetItems_FullPageOf30OffersNext()
    {
        var items = Enumerable.Range(1, 30).Select(i => (i.ToString(), $"第{i}集", "01:00")).ToArray();
        using var source = Create(_ => ListJson(0, items));
        var page = await source.GetItemsAsync("2", 1, null);
        Assert.Equal(30, page.Items.Count);
        Assert.Equal(2, page.PageCount); // 满 30 → 可能有下一页
    }

    [Fact]
    public async Task GetItems_RejectsInvalidCategoryAndPage()
    {
        using var source = Create(_ => ListJson(0));
        await Assert.ThrowsAsync<InvalidDataException>(() => source.GetItemsAsync("99", 1, null));
        await Assert.ThrowsAsync<InvalidDataException>(() => source.GetItemsAsync("2", 0, null));
    }

    [Fact]
    public async Task Detail_ExtractsMipSearchVideoSrc()
    {
        var routes = new Dictionary<string, string>
        {
            ["list/mip-data?typeId=2&page=1&callback="] = ListJson(0, ("2662", "快乐小屋", "02:30")),
            ["play/2662"] = PlayPage("快乐小屋", "https://resource-cdn.tuxiaobei.com/video/x.mp4"),
        };
        using var source = Create(path => routes.TryGetValue(path, out var v) ? v : ListJson(0));
        var detail = await source.GetDetailAsync("2662");
        Assert.Equal("快乐小屋", detail.Item.Title);
        var line = Assert.Single(detail.Lines);
        Assert.Equal("兔小贝", line.Name);
    }

    [Fact]
    public async Task ResolvePlayback_ReturnsVideoWithHeaders()
    {
        var routes = new Dictionary<string, string>
        {
            ["list/mip-data?typeId=2&page=1&callback="] = ListJson(0, ("2662", "快乐小屋", "02:30")),
            ["play/2662"] = PlayPage("快乐小屋", "https://resource-cdn.tuxiaobei.com/video/x.mp4"),
        };
        using var source = Create(path => routes.TryGetValue(path, out var v) ? v : ListJson(0));
        var play = await source.ResolvePlaybackAsync("2662", "2662");
        Assert.Equal("https://resource-cdn.tuxiaobei.com/video/x.mp4", play.Uri);
        Assert.Equal("dr_兔小贝", play.SourceKey);
        Assert.Equal("兔小贝", play.SourceName);
        Assert.Contains("Referer", play.Headers.Keys);
    }

    [Fact]
    public async Task ResolvePlayback_RejectsMismatchedEpisode()
    {
        using var source = Create(_ => ListJson(0));
        await Assert.ThrowsAsync<InvalidDataException>(() => source.ResolvePlaybackAsync("2662", "2663"));
    }

    [Fact]
    public async Task Search_ReturnsEmptyWhenNoCards()
    {
        const string html = "<html><body><div class=\"list-con\"></div></body></html>";
        using var source = Create(_ => html);
        var page = await source.SearchAsync("儿歌", 1);
        Assert.Empty(page.Items);
    }

    [Fact]
    public void Id_ValidatesVideoNumber()
    {
        Assert.Equal("2662", TuxiaobeiSource.Id("2662"));
        Assert.Throws<InvalidDataException>(() => TuxiaobeiSource.Id(""));
        Assert.Throws<InvalidDataException>(() => TuxiaobeiSource.Id("abc"));
        Assert.Throws<InvalidDataException>(() => TuxiaobeiSource.Id(new string('1', 13)));
    }
}
