using System.Net;
using System.Text;
using VodBox.Core;
using VodBox.Infrastructure;
using Xunit;

namespace VodBox.Tests;

/// <summary>虎牙直播爬虫离线回归：FLV 签名向量 + 合成 JSON 锁定协议契约。</summary>
public class HuyaSourceTests
{
    private static SourceInfo Info() => new()
    {
        Key = "虎牙js", Name = "虎牙直播", Runtime = SourceRuntime.NativeSpider, Api = "csp_Huya",
    };

    private sealed class JsonHandler : HttpMessageHandler
    {
        private readonly Func<string, string> _route;
        public JsonHandler(Func<string, string> route) => _route = route;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var key = request.RequestUri!.AbsolutePath.TrimStart('/') + request.RequestUri!.Query;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_route(key), Encoding.UTF8, "application/json"),
                RequestMessage = request,
            });
        }
    }

    private static HuyaSource Create(Func<string, string> route) =>
        new(Info(), new HttpClient(new JsonHandler(route)));

    /// <summary>JSON 字符串字面量转义（不走 JsonSerializer，避免反射在 AOT 测试设置下被禁）。</summary>
    private static string Esc(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r") + "\"";

    private static string ListPage(int page, int totalPage, params (string room, string title)[] rooms)
    {
        var datas = string.Join(",", rooms.Select(r =>
            "{"
            + "\"profileRoom\":" + Esc(r.room)
            + ",\"introduction\":" + Esc(r.title)
            + ",\"screenshot\":" + Esc("https://i.huya.com/" + r.room + ".jpg")
            + ",\"nick\":" + Esc("主播" + r.room) + "}"));
        return "{\"status\":200,\"data\":{\"totalPage\":" + totalPage + ",\"datas\":[" + datas + "]}}";
    }

    private static string RoomPage(string id, string name, bool live = true, params (string cdn, string url)[] lines)
    {
        var multiLine = string.Join(",", lines.Select(l =>
            "{\"cdnType\":" + Esc(l.cdn) + ",\"url\":" + Esc(l.url) + "}"));
        return "{\"status\":200,\"data\":{\"liveStatus\":" + Esc(live ? "ON" : "OFF")
            + ",\"liveData\":{\"roomName\":" + Esc(name)
            + ",\"screenshot\":" + Esc("https://i.huya.com/" + id + ".jpg")
            + ",\"contentIntro\":\"简介\"}"
            + ",\"stream\":{\"flv\":{\"multiLine\":[" + multiLine + "]}}}}";
    }
    // ---------- FLV 签名（旧实现的 OpenSSL 向量等价校验）----------

    [Fact]
    public void Sign_ProducesDeterministicWsSecret()
    {
        // fm 模板 base64 解出 "hls_0_xxxx..."；已知输入产生确定的 MD5 wsSecret
        // fm = base64("hls_0_0_0&wsSecret=xxx") 取前缀 "hls"
        var fm = Convert.ToBase64String(Encoding.UTF8.GetBytes("hls_0_0_0"));
        var url = $"https://al.flv.huya.com/src/11995132.flv?fm={Uri.EscapeDataString(fm)}&wsTime=abc123&ctype=huya_webh5&fs=bgct";
        var result = HuyaSource.Sign(url, 1700000000);

        // 重排字段：移除 fm/wsTime/u/seqid，注入 wsSecret/wsTime/u/seqid，保留 ctype/fs
        Assert.Contains("wsSecret=", result);
        Assert.Contains("wsTime=abc123", result);
        Assert.Contains("u=0", result);
        Assert.Contains("seqid=17000000000000", result);
        Assert.Contains("ctype=huya_webh5", result);
        Assert.Contains("fs=bgct", result);
        Assert.DoesNotContain("fm=", result);
        // wsSecret 是 32 位小写十六进制
        var secret = result.Split("wsSecret=")[1].Split('&')[0];
        Assert.Equal(32, secret.Length);
        Assert.All(secret, c => Assert.True(Uri.IsHexDigit(c) && (!char.IsLetter(c) || char.IsLower(c))));
    }

    [Fact]
    public void Sign_IsDeterministicForSameInput()
    {
        var fm = Convert.ToBase64String(Encoding.UTF8.GetBytes("hls_0_0_0"));
        var url = $"https://al.flv.huya.com/src/11995132.flv?fm={Uri.EscapeDataString(fm)}&wsTime=abc123&ctype=huya_webh5";
        Assert.Equal(HuyaSource.Sign(url, 1700000000), HuyaSource.Sign(url, 1700000000));
    }

    [Fact]
    public void Sign_RejectsMissingFields()
    {
        Assert.Throws<InvalidDataException>(() =>
            HuyaSource.Sign("https://al.flv.huya.com/src/1.flv?wsTime=abc", 1));
    }

    // ---------- 列表 / 详情 / 播放 ----------

    [Fact]
    public async Task GetItems_ParsesListWithPagination()
    {
        using var source = Create(_ => ListPage(1, 3, ("1001", "一起看《武林外传》"), ("1002", "一起看《西游记》")));
        var page = await source.GetItemsAsync("2135", 1, null, ct: TestContext.Current.CancellationToken);
        Assert.Equal(2, page.Items.Count);
        Assert.Equal("一起看《武林外传》", page.Items[0].Title);
        Assert.Equal("1001", page.Items[0].Id);
        Assert.Equal("https://i.huya.com/1001.jpg", page.Items[0].Poster);
        Assert.Equal(2, page.PageCount); // totalPage=3 → 有下一页
    }

    [Fact]
    public async Task GetItems_LastPageKeepsCurrent()
    {
        using var source = Create(_ => ListPage(3, 3, ("1001", "末页")));
        var page = await source.GetItemsAsync("2135", 3, null, ct: TestContext.Current.CancellationToken);
        Assert.Equal(3, page.PageCount); // 末页保持当前页
    }

    [Fact]
    public async Task Detail_ExtractsMultiCdnLines()
    {
        var fm = Convert.ToBase64String(Encoding.UTF8.GetBytes("hls_0_0_0"));
        var liveUrl = $"https://al.flv.huya.com/src/11995132.flv?fm={Uri.EscapeDataString(fm)}&wsTime=abc&ctype=huya_webh5";
        var routes = new Dictionary<string, string>
        {
            ["cache.php?m=Live&do=profileRoom&roomid=11995132"] = RoomPage("11995132", "一起看《武林外传》", true, ("AL", liveUrl), ("TX", liveUrl)),
        };
        using var source = Create(key => routes.TryGetValue(key, out var v) ? v : throw new InvalidDataException("未命中路由 " + key));
        var detail = await source.GetDetailAsync("11995132", ct: TestContext.Current.CancellationToken);

        Assert.Equal("一起看《武林外传》", detail.Item.Title);
        Assert.Equal(2, detail.Lines.Count);
        Assert.Equal("AL", detail.Lines[0].Name);
        Assert.Equal("TX", detail.Lines[1].Name);
    }

    [Fact]
    public async Task Detail_ThrowsWhenOffline()
    {
        var routes = new Dictionary<string, string>
        {
            ["cache.php?m=Live&do=profileRoom&roomid=11995132"] = RoomPage("11995132", "x", live: false, ("AL", "https://x/v.flv?fm=YQ==&wsTime=t&ctype=c")),
        };
        using var source = Create(key => routes[key]);
        await Assert.ThrowsAsync<InvalidDataException>(() => source.GetDetailAsync("11995132", ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ResolvePlayback_SignsUrlAndMarksLive()
    {
        var fm = Convert.ToBase64String(Encoding.UTF8.GetBytes("hls_0_0_0"));
        var liveUrl = $"https://al.flv.huya.com/src/11995132.flv?fm={Uri.EscapeDataString(fm)}&wsTime=abc&ctype=huya_webh5";
        var routes = new Dictionary<string, string>
        {
            ["cache.php?m=Live&do=profileRoom&roomid=11995132"] = RoomPage("11995132", "一起看《武林外传》", true, ("AL", liveUrl)),
        };
        using var source = Create(key => routes[key]);
        var play = await source.ResolvePlaybackAsync("11995132", "0", ct: TestContext.Current.CancellationToken);

        Assert.True(play.IsLive);
        Assert.Contains("wsSecret=", play.Uri);
        Assert.DoesNotContain("fm=", play.Uri);
        Assert.Equal("一起看《武林外传》", play.Title);
        Assert.Equal("虎牙js", play.SourceKey);
        Assert.Contains("Referer", play.Headers.Keys);
    }

    /// <summary>
    /// 实测发现：部分虎牙房间的 liveData.roomName 是空字符串（直播标题未设）。
    /// 此前用 Required(roomName) 会在这种房间直接报错，导致整个列表无法打开。
    /// 空标题应回退到房间号兜底，而不是报错（Gson 宽容行为对齐）。
    /// </summary>
    [Fact]
    public async Task Detail_ToleratesEmptyRoomName()
    {
        var fm = Convert.ToBase64String(Encoding.UTF8.GetBytes("hls_0_0_0"));
        var liveUrl = $"https://al.flv.huya.com/src/11995132.flv?fm={Uri.EscapeDataString(fm)}&wsTime=abc&ctype=huya_webh5";
        var routes = new Dictionary<string, string>
        {
            ["cache.php?m=Live&do=profileRoom&roomid=11995132"] = RoomPage("11995132", "", true, ("AL", liveUrl)),
        };
        using var source = Create(key => routes[key]);
        var detail = await source.GetDetailAsync("11995132", ct: TestContext.Current.CancellationToken);

        Assert.False(string.IsNullOrWhiteSpace(detail.Item.Title), "空标题应回退为房间号兜底");
        Assert.Contains("11995132", detail.Item.Title);
        Assert.NotEmpty(detail.Lines);
    }

    [Fact]
    public async Task ResolvePlayback_ToleratesEmptyRoomName()
    {
        var fm = Convert.ToBase64String(Encoding.UTF8.GetBytes("hls_0_0_0"));
        var liveUrl = $"https://al.flv.huya.com/src/11995132.flv?fm={Uri.EscapeDataString(fm)}&wsTime=abc&ctype=huya_webh5";
        var routes = new Dictionary<string, string>
        {
            ["cache.php?m=Live&do=profileRoom&roomid=11995132"] = RoomPage("11995132", "", true, ("AL", liveUrl)),
        };
        using var source = Create(key => routes[key]);
        var play = await source.ResolvePlaybackAsync("11995132", "0", ct: TestContext.Current.CancellationToken);
        Assert.Contains("11995132", play.Title);
    }

    [Fact]
    public void Id_ValidatesRoomNumber()
    {
        Assert.Equal("11995132", HuyaSource.Id("11995132"));
        Assert.Throws<InvalidDataException>(() => HuyaSource.Id(""));
        Assert.Throws<InvalidDataException>(() => HuyaSource.Id("abc"));
        Assert.Throws<InvalidDataException>(() => HuyaSource.Id(new string('1', 21)));
    }
}
