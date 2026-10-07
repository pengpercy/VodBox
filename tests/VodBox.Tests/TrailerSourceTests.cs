using System.Net;
using System.Text;
using VodBox.Core;
using VodBox.Infrastructure;
using Xunit;

namespace VodBox.Tests;

/// <summary>6huo 荐影预告片爬虫离线回归：合成 fixture 锁定 HTML 结构与解析契约。</summary>
public class TrailerSourceTests
{
    private static SourceInfo Info() => new()
    {
        Key = "csp_YGP", Name = "荐影预告片", Runtime = SourceRuntime.NativeSpider, Api = "csp_YGP",
    };

    private sealed class PageHandler : HttpMessageHandler
    {
        private readonly Func<string, string> _route;
        public PageHandler(Func<string, string> route) => _route = route;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath.TrimStart('/');
            var query = request.RequestUri!.Query;
            var key = string.IsNullOrEmpty(path) && query.Contains("keyword=") ? "?search" : path;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_route(key), Encoding.UTF8, "text/html"),
                RequestMessage = request,
            });
        }
    }

    private static string ListPage(string body, bool hasNext = false, int next = 2) => $"""
        <html><body>
          <div class="movlist"><ul>{body}</ul></div>
          {(hasNext ? $"<a href=\"/movlist/____{next}\">下一页</a>" : "")}
        </body></html>
        """;

    private static string Card(int id, string title, string img) => $"""
        <a href="/movie/{id}"><span class="item-title">{title}</span><img src="{img}" /></a>
        """;

    private static string DetailPage(string title, string lineName, params (int id, string ep)[] episodes)
    {
        var eps = string.Join("", episodes.Select(e => $"<a class=\"tlist-bbs-tdtitle\" href=\"/show/{e.id}\">{e.ep}</a>"));
        return $"""
            <html><body>
              <h1 class="movie-name">{title}</h1>
              <table class="tlist"><tr><th>{lineName}</th></tr><tr><td>{eps}</td></tr></table>
            </body></html>
            """;
    }

    private static string ShowPage(string videoUrl) =>
        "<html><body><script>var videoObject = {video: '" + videoUrl + "', poster: ''};</script></body></html>";

    private static TrailerSource Create(Func<string, string> route) =>
        new(Info(), new HttpClient(new PageHandler(route)));

    [Fact]
    public async Task Categories_IsSingleTrailerWorld()
    {
        using var source = Create(_ => ListPage(Card(1, "x", "https://i/p.jpg")));
        var categories = await source.GetCategoriesAsync();
        var category = Assert.Single(categories);
        Assert.Equal("trailers", category.Id);
        Assert.Equal("预告片世界", category.Name);
    }

    [Fact]
    public async Task GetHome_ParsesCardsWithPoster()
    {
        using var source = Create(path => ListPage(Card(1, "流浪地球", "https://i.example.com/p.jpg") + Card(2, "满江红", "https://i.example.com/q.jpg")));
        var page = await source.GetHomeAsync();
        Assert.Equal(2, page.Items.Count);
        Assert.Equal("流浪地球", page.Items[0].Title);
        Assert.Equal("/movie/1", page.Items[0].Id);
        Assert.Equal("https://i.example.com/p.jpg", page.Items[0].Poster);
    }

    [Fact]
    public async Task GetItems_PaginationFollowsNextLink()
    {
        var routes = new Dictionary<string, string>
        {
            ["movlist/____1"] = ListPage(Card(1, "A", "https://i/a.jpg"), hasNext: true, next: 2),
            ["movlist/____2"] = ListPage(Card(2, "B", "https://i/b.jpg")), // 无下一页
        };
        using var source = Create(path => routes.TryGetValue(path, out var p) ? p : ListPage(""));
        var p1 = await source.GetItemsAsync("trailers", 1, null);
        Assert.Equal(2, p1.PageCount);          // 有「下一页」→ 给下一页
        var p2 = await source.GetItemsAsync("trailers", 2, null);
        Assert.Equal(2, p2.PageCount);          // 末页保持当前页（不再伪造页数）
    }

    [Fact]
    public async Task GetItems_RejectsInvalidCategoryAndPage()
    {
        using var source = Create(_ => ListPage(""));
        await Assert.ThrowsAsync<InvalidDataException>(() => source.GetItemsAsync("movies", 1, null));
        await Assert.ThrowsAsync<InvalidDataException>(() => source.GetItemsAsync("trailers", 0, null));
    }

    [Fact]
    public async Task Detail_ExtractsLinesAndEpisodes()
    {
        var routes = new Dictionary<string, string>
        {
            ["movlist/____1"] = ListPage(Card(1, "流浪地球", "https://i/p.jpg")),
            ["movie/1"] = DetailPage("流浪地球", "预告片", (10, "先导预告"), (11, "终极预告")),
        };
        using var source = Create(path => routes.TryGetValue(path, out var p) ? p : ListPage(""));
        var detail = await source.GetDetailAsync("/movie/1");

        Assert.Equal("流浪地球", detail.Item.Title);
        var line = Assert.Single(detail.Lines);
        Assert.Equal("预告片", line.Name);
        Assert.Equal(["/show/10", "/show/11"], line.Episodes.Select(e => e.Id).ToArray());
        Assert.All(line.Episodes, e => Assert.Null(e.Uri)); // 地址由 ResolvePlayback 现取
    }

    [Fact]
    public async Task Detail_RejectsInvalidMovieId()
    {
        using var source = Create(_ => ListPage(""));
        await Assert.ThrowsAsync<InvalidDataException>(() => source.GetDetailAsync("/show/1"));
        await Assert.ThrowsAsync<InvalidDataException>(() => source.GetDetailAsync("任意"));
    }

    [Fact]
    public async Task ResolvePlayback_ParsesCkplayerVideoObject()
    {
        var routes = new Dictionary<string, string>
        {
            ["movlist/____1"] = ListPage(Card(1, "流浪地球", "https://i/p.jpg")),
            ["movie/1"] = DetailPage("流浪地球", "预告片", (10, "先导预告")),
            ["show/10"] = ShowPage("https://vod.pipi.cn/x/v.mp4"),
        };
        using var source = Create(path => routes.TryGetValue(path, out var p) ? p : ListPage(""));
        var play = await source.ResolvePlaybackAsync("/movie/1", "/show/10");

        Assert.Equal("https://vod.pipi.cn/x/v.mp4", play.Uri);
        Assert.Equal("流浪地球 · 先导预告", play.Title);
        Assert.Equal("csp_YGP", play.SourceKey);
        Assert.Contains("User-Agent", play.Headers.Keys);
    }

    [Fact]
    public async Task ResolvePlayback_ThrowsWhenNoCkplayerDeclaration()
    {
        var routes = new Dictionary<string, string>
        {
            ["movlist/____1"] = ListPage(Card(1, "流浪地球", "https://i/p.jpg")),
            ["movie/1"] = DetailPage("流浪地球", "预告片", (10, "先导预告")),
            ["show/10"] = "<html><body><p>no player</p></body></html>",
        };
        using var source = Create(path => routes.TryGetValue(path, out var p) ? p : ListPage(""));
        await Assert.ThrowsAsync<NotSupportedException>(() => source.ResolvePlaybackAsync("/movie/1", "/show/10"));
    }

    [Fact]
    public async Task ResolvePlayback_RejectsEpisodeNotInMovie()
    {
        var routes = new Dictionary<string, string>
        {
            ["movlist/____1"] = ListPage(Card(1, "流浪地球", "https://i/p.jpg")),
            ["movie/1"] = DetailPage("流浪地球", "预告片", (10, "先导预告")),
        };
        using var source = Create(path => routes.TryGetValue(path, out var p) ? p : ListPage(""));
        await Assert.ThrowsAsync<InvalidDataException>(() => source.ResolvePlaybackAsync("/movie/1", "/show/99"));
    }

    // ---------- 解析辅助 ----------

    [Fact]
    public void Url_RejectsUserInfoAndNonHttp()
    {
        Assert.Equal("https://www.6huo.com/x.mp4", TrailerSource.Url("/x.mp4"));
        Assert.Throws<InvalidDataException>(() => TrailerSource.Url("https://user:pass@x/a.mp4"));
        Assert.Throws<InvalidDataException>(() => TrailerSource.Url("ftp://x/a.mp4"));
        Assert.Throws<InvalidDataException>(() => TrailerSource.Url(""));
    }
}
