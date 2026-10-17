using System.Text;
using System.Text.Json;
using VodBox.Core;
using VodBox.Infrastructure;
using Xunit;

namespace VodBox.Tests;

/// <summary>Nmyswv aF / YCyz 签名 / AppGz 信封 / AiDj URL / Alllive LZ-string 的固定向量回归。</summary>
public sealed class NativeCodecSourceTests
{
    private const string NmyswvMovieList = """
        <ul class="resize_list"><li><a href="/index.php?m=vod-detail-id-12.html" title="中文电影"><img data-src="/p.jpg"></a><span class="vod_remarks">HD</span></li></ul>
        """;
    private const string NmyswvSearchList = """
        <ul id="data_list"><li><a href="/index.php?m=vod-detail-id-12.html"><span class="sTit">中文电影</span><img src="/p.jpg"></a></li></ul>
        """;
    private const string NmyswvDetail = """
        <h1 class="title">中文电影</h1>
        <section class="page-hd"><img src="/cover.jpg"></section>
        <article class="detail-con">剧情简介</article>
        <div class="numList" div.id="l1" data-id="l1"><ul>
          <li><a href="/index.php?m=vod-play-id-12-src-1-num-1.html">第1集</a></li>
          <li><a href="/index.php?m=vod-play-id-12-src-1-num-2.html">第2集</a></li>
        </ul></div>
        """;

    [Fact]
    public async Task NmyswvHomeSearchDetailAndAepisodeIdentityStayStable()
    {
        var rec = new SyntheticSourceHelpers.Recorder((url, _) => Task.FromResult(
            url.Contains("vod-detail-id", StringComparison.Ordinal) ? NmyswvDetail :
            url.Contains("vod-search", StringComparison.Ordinal) ? NmyswvSearchList : NmyswvMovieList));
        using var http = new HttpClient(new SyntheticSourceHelpers.StatusHandler(async (req, ct) =>
            new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(await rec.Fetch(req.RequestUri!.AbsoluteUri, ct), Encoding.UTF8, "text/html") }));
        var source = new NmyswvSource(SyntheticSourceHelpers.Site("nmyswv", "csp_NmyswvGuard", "https://vip.example/"), http);
        var home = await source.GetHomeAsync(TestContext.Current.CancellationToken);
        Assert.Equal("/index.php?m=vod-detail-id-12.html", Assert.Single(home.Items).Id);
        Assert.EndsWith("/p.jpg", home.Items[0].Poster, StringComparison.Ordinal);
        var search = await source.SearchAsync("中文", 1, TestContext.Current.CancellationToken);
        Assert.Equal("中文电影", Assert.Single(search.Items).Title);
        var items = await source.GetItemsAsync("1", 1, new Dictionary<string, string> { ["tid"] = "5" }, TestContext.Current.CancellationToken);
        Assert.Equal("中文电影", Assert.Single(items.Items).Title);
        Assert.Contains(rec.Urls, url => url.Contains("vod-list-id-5-pg-1", StringComparison.Ordinal));
        var detail = await source.GetDetailAsync("/index.php?m=vod-detail-id-12.html", TestContext.Current.CancellationToken);
        Assert.Equal(2, detail.Lines[0].Episodes.Count);
        Assert.Equal("线路1\u0001/index.php?m=vod-play-id-12-src-1-num-1.html", detail.Lines[0].Episodes[^1].Id);
        Assert.Equal("线路1", detail.Lines[0].Id);
    }

    [Fact]
    public void NmyswvAfDecoderUsesFixedInputAndOutputVector()
    {
        // 单条线路的单集：name$<aF 编码>，前缀不含空线路分隔符。
        // 真实 aF 字面量以 "aF" 开头；偶数位携带数据，奇数位是填充。
        const string encoded = "第1集$aFHFRF0FcFDFoFvFLF2FNFkFbFiF5FlFeFGFFFtFcFGFxFlFLF2FEFuFbFTFNF1FOFAO0O0OO0O0O";
        Assert.Equal("http://cdn.example/a.m3u8",
            NmyswvSource.DecodePlayback($"<script>mac_url='{encoded}';</script>", "/index.php?m=vod-play-id-9-src-1-num-1.html"));
        Assert.Equal("https://cdn.example/a.m3u8",
            NmyswvSource.DecodePlayback("<script>mac_url='第1集$https://cdn.example/a.m3u8';</script>", "/index.php?m=vod-play-id-9-src-1-num-1.html"));
        Assert.Throws<NotSupportedException>(() => NmyswvSource.DecodePlayback("<script>mac_url='第1集$https://web.example/watch';</script>", "/index.php?m=vod-play-id-9-src-1-num-1.html"));
        Assert.Throws<InvalidDataException>(() => NmyswvSource.DecodePlayback("<html></html>", "/index.php?m=vod-play-id-9-src-1-num-1.html"));
    }

    [Fact]
    public void NmyswvSupportsRequiresSiteRoot()
    {
        Assert.True(NmyswvSource.Supports("https://vip.example/"));
        Assert.True(NmyswvSource.Supports(null));
        Assert.False(NmyswvSource.Supports("https://vip.example/path"));
        Assert.False(NmyswvSource.Supports("file:///tmp"));
    }

    [Fact]
    public async Task YCyzCardsFiltersDetailAndSignedLinesMatchEvidence()
    {
        const string card = """
            <div class="movie-card">
              <a href="/movie/7.html"><img data-src="/p7.jpg"></a>
              <div class="card-info"><h4>中文电影</h4></div>
              <div class="card-meta">更新至第3集</div>
            </div>
            """;
        const string search = "7|中文电影|/p7.jpg|HD";
        const string detail = """
            <h1>中文电影</h1>
            <div class="info-list"><div class="info-item"><span class="info-label">类型</span><a>动作</a><a>剧情</a></div></div>
            <div class="desc">简介</div>
            <div class="play-list"><a href="/movie/7/1-1.html">第1集</a><a href="/movie/7/1-2.html">第2集</a></div>
            """;
        const string playPage = """<script>var pid = 'pid123';</script>""";
        const string lines = """{"data":{"url3":"https://www.bde4.cc/hls/a.m3u8","m3u8":"https://www.bde4.cc/hls/a.m3u8","m3u8_2":""}}""";
        var rec = new SyntheticSourceHelpers.Recorder((url, _) => Task.FromResult(
            url.Contains("bidisearch.php", StringComparison.Ordinal) ? search :
            url.Contains("/lines?", StringComparison.Ordinal) ? lines :
            url.Contains("/movie/7/1-1.html", StringComparison.Ordinal) ? playPage :
            url.Contains("/movie/7.html", StringComparison.Ordinal) ? detail : card));
        var source = new YCyzSource(SyntheticSourceHelpers.Site("ycyz", "csp_YCyzGuard"), (url, ct) => rec.Fetch(url, ct), () => 1792260000000L);
        var page = await source.GetItemsAsync("1#中国大陆", 2, null, TestContext.Current.CancellationToken);
        Assert.Equal("movie/7.html", Assert.Single(page.Items).Id);
        Assert.Contains("/s/all/2?&type=1&area=%E4%B8%AD%E5%9B%BD%E5%A4%A7%E9%99%86&order=0", rec.Urls[^1]);
        Assert.Equal("中文电影", Assert.Single((await source.SearchAsync("中文", 1, TestContext.Current.CancellationToken)).Items).Title);
        var detailResult = await source.GetDetailAsync("movie/7.html", TestContext.Current.CancellationToken);
        Assert.Equal("动作/剧情", detailResult.Item.TypeName);
        Assert.Equal("自动", detailResult.Lines[0].Id);
        Assert.Equal("自动\u0001movie/7/1-1.html", detailResult.Lines[0].Episodes[0].Id);
        var playback = await source.ResolvePlaybackAsync("movie/7.html", "自动\u0001movie/7/1-1.html", TestContext.Current.CancellationToken);
        Assert.Equal("https://v.xl01.eu.cc/hls/a.m3u8", playback.Uri);
        Assert.True(playback.ProxyMedia);
        Assert.Equal("自动", playback.LineId);
        Assert.Contains("pid=pid123", rec.Urls[^1]);
        Assert.Contains("t=1792260000000", rec.Urls[^1]);
    }

    [Fact]
    public void YCyzSignIsDeterministicUppercaseHexAndSupportsRejectsNonObject()
    {
        var first = YCyzSource.Sign("pid123", 1792260000000L);
        // AES-ECB + 十六进制编码：一个 16 字节分组加密后是 32 字节 /64 个十六进制字符。
        Assert.Equal(64, first.Length);
        Assert.Matches("^[0-9A-F]{64}$", first);
        Assert.Equal(first, YCyzSource.Sign("pid123", 1792260000000L));
        Assert.NotEqual(first, YCyzSource.Sign("pid123", 1792260000001L));
        Assert.NotEqual(first, YCyzSource.Sign("pid124", 1792260000000L));
        Assert.True(YCyzSource.Supports(null));
        Assert.True(YCyzSource.Supports("{}"));
        Assert.False(YCyzSource.Supports("""{"a":1}"""));
        Assert.False(YCyzSource.Supports("not-json"));
    }

    [Fact]
    public void AppGzWrapSignatureAndDeviceIdentityAreDeterministic()
    {
        var request = AppGzCrypto.Wrap("{\"a\":1}", "tok", 1792260000L, "KEYKEYKEYKEYKEYK", "IVIVIVIVIVIVIVIV", "vodbox", _ => "RSA");
        var canonical = "token_id=,token=tok,phone_type=1,request_key=" + request["request_key"] + ",app_id=1,time=1792260000,keys=RSA*&zvdvdvddbfikkkumtmdwqppp?|4Y!s!2br";
        var expected = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes(canonical)));
        Assert.Equal(expected, request["signature"]);
        Assert.Matches("^[0-9A-F]{32}$", request["signature"]);
        Assert.Equal("1", request["app_id"]);
        Assert.Equal("vodbox", request["phone_model"]);
        Assert.True(AppGzSource.Supports(null));
        Assert.True(AppGzSource.Supports(""));
        Assert.True(AppGzSource.Supports("null"));
        Assert.False(AppGzSource.Supports("""{"user":"x"}"""));
        Assert.Equal(0, AppGzSource.JavaHash(""));
        Assert.Equal(97, AppGzSource.JavaHash("a"));
        var device = AppGzSource.DeviceId("abc", 1792260000L, 42);
        Assert.Equal(device, AppGzSource.DeviceId("abc", 1792260000L, 42));
        Assert.NotEqual(device, AppGzSource.DeviceId("abc", 1792260001L, 42));
        Assert.Equal(24, device.Length);
    }

    [Fact]
    public void AiDjSupportsAndMediaUrlDecoding()
    {
        Assert.True(AiDjSource.Supports(null));
        Assert.True(AiDjSource.Supports("{}"));
        Assert.False(AiDjSource.Supports("""{"modulePath":"http://x"}"""));
        Assert.False(AiDjSource.Supports("not-json"));
        Assert.Equal("https://cdn.example/plain.mp4", AiDjSource.DecodeMediaUrl("https://cdn.example/plain.mp4", []));
        Assert.Throws<InvalidDataException>(() => AiDjSource.DecodeMediaUrl("###", []));
    }

    [Fact]
    public void FtyLzStringDecodesBase64FramesAndEmpty()
    {
        Assert.Equal("", FtyLzString.Decode(""));
        // 16-bit 首字符：flag=1、低位字节=0x2D、高位字节=0x4E => U+4E2D '中'，随后结束码。
        Assert.Equal("中", FtyLzString.Decode(WriteStream(
            (2, 1), (8, 0x2D), (8, 0x4E), (2, 2))));
        // 非法字母表与截断帧。
        Assert.Throws<InvalidDataException>(() => FtyLzString.Decode("!!!"));
        Assert.Throws<InvalidDataException>(() => FtyLzString.Decode("++"));
    }

    /// <summary>按解码器 Bits() 的 LSB 语义写入字段，用于构造确定性帧。</summary>
    private static string WriteStream(params (int Count, int Field)[] fields)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/=";
        var outBytes = new List<char>();
        int value = 0, mask = 32;
        foreach (var (count, field) in fields)
        {
            for (var bit = 0; bit < count; bit++)
            {
                if (((field >> bit) & 1) != 0) value |= mask;
                mask >>= 1;
                if (mask == 0) { outBytes.Add(alphabet[value]); value = 0; mask = 32; }
            }
        }
        if (mask != 32) outBytes.Add(alphabet[value]);
        return new string(outBytes.ToArray());
    }
}
