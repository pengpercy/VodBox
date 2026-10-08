using System.Diagnostics;
using System.Text.Json;
using VodBox.Core;
using VodBox.Infrastructure;
using VodBox.PluginHost;

var passed = 0;
void Require(bool value, string message)
{
    if (!value) throw new InvalidOperationException(message);
    Console.WriteLine($"PASS {++passed}: {message}");
}
using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
var ct = deadline.Token;

// 离线真实 VM：内联 JS、完整字符串转义、JSON rule、筛选对象和 opaque play id。
using (var runtime = new DrpyRuntime())
{
    await runtime.InitAsync("""
        var rule = {
          title:'fixture', host:'https://example.org', url:'/category/fyclass/fypage', searchUrl:'/search/**',
          class_name:'测试', class_url:'2',
          推荐:'js:setResult([{url:"home",title:"首页"}]);',
          一级:'js:setResult([{url:JSON.stringify(MY_FL),title:MY_CATE}]);',
          搜索:'js:setResult([{url:KEY,title:String(MY_PAGE)}]);',
          二级:'js:VOD={vod_id:input,vod_name:"详情",vod_play_from:"线路",vod_play_url:"第1集$opaque"};',
          play_parse:true, lazy:'js:input={parse:0,jx:0,url:input,header:{Referer:MY_FLAG}};', play_json:[]
        };
        """, ct);
    var query = "C#\t\0\b\f\n\r\"\\中文\u2028\u2029";
    using var search = JsonDocument.Parse(await runtime.SearchAsync(query, 2, ct));
    Require(search.RootElement.GetProperty("list")[0].GetProperty("vod_id").GetString() == query, "JS literal control/unicode roundtrip");
    using var home = JsonDocument.Parse(await runtime.HomeVodAsync(ct));
    Require(home.RootElement.GetProperty("list")[0].GetProperty("vod_id").GetString() == "home", "real HomeVod export");
    using var category = JsonDocument.Parse(await runtime.CategoryAsync("2", 3, new Dictionary<string, string> { ["year"] = "2026\t" }, ct));
    using var filter = JsonDocument.Parse(category.RootElement.GetProperty("list")[0].GetProperty("vod_id").GetString()!);
    Require(filter.RootElement.GetProperty("year").GetString() == "2026\t", "real category filters");
    using var specialCategory = JsonDocument.Parse(await runtime.CategoryAsync("2", 1,
        new Dictionary<string, string> { ["__proto__"] = "own-value", ["controls"] = query }, ct));
    using var specialFilter = JsonDocument.Parse(specialCategory.RootElement.GetProperty("list")[0].GetProperty("vod_id").GetString()!);
    Require(specialFilter.RootElement.TryGetProperty("__proto__", out var own) && own.GetString() == "own-value" &&
        specialFilter.RootElement.GetProperty("controls").GetString() == query, "filter object preserves own __proto__ and controls");
    using var play = JsonDocument.Parse(await runtime.PlayAsync("线路\t\u2028", "https://cdn.example/a?x=\t\u2028", ["线路"], ct));
    Require(play.RootElement.GetProperty("header").GetProperty("Referer").GetString() == "线路\t\u2028", "real play flag escaping");
    await runtime.InitAsync("""{"title":"JSON rule","host":"https://example.org","class_name":"JSON分类","class_url":"json","一级":"","二级":"*"}""", ct);
    using var classes = JsonDocument.Parse(await runtime.HomeAsync(ct));
    Require(classes.RootElement.GetProperty("class")[0].GetProperty("type_id").GetString() == "json", "JSON rule init object");
}

// 真网站点：显式 DrpySource，不经 ConfigLoader 的 native-key promotion。
if (!args.Contains("--offline"))
{
    using var source = new DrpySource(new SourceInfo
    {
        Key = "dr_兔小贝", Name = "兔小贝 · drpy smoke", Runtime = SourceRuntime.QuickJs,
        Api = "drpy2.min.js",
        Ext = "https://raw.githubusercontent.com/fantaiying7/EXT/refs/heads/main/%E5%85%94%E5%B0%8F%E8%B4%9D.js"
    });
    var sw = Stopwatch.StartNew();
    var classes = await source.GetCategoriesAsync(ct);
    Require(classes.Count == 4, $"real-site categories={classes.Count}");
    var home = await source.GetHomeAsync(ct);
    if (home.Items.Count > 0) Require(true, $"real-site HomeVod={home.Items.Count}");
    else Console.WriteLine("LIMITATION: real-site HomeVod=0; upstream recommendation selector .pic-list.list-box no longer matches the homepage. No native/category fallback used.");
    var category = await source.GetItemsAsync("2", 1, null, ct);
    Require(category.Items.Count > 0, $"real-site category={category.Items.Count}");
    var search = await source.SearchAsync("小猪", 1, ct);
    Require(search.Items.Count > 0, $"real-site search={search.Items.Count}");
    var detail = await source.GetDetailAsync(search.Items[0].Id, ct);
    var line = detail.Lines.First();
    var episode = line.Episodes.First();
    var again = await source.GetDetailAsync(search.Items[0].Id, ct);
    Require(again.Lines[0].Episodes[0].Id == episode.Id, $"real-site detail={detail.Lines.Count} lines, stable resume");
    var request = await source.ResolvePlaybackAsync(search.Items[0].Id, episode.Id, ct);
    Require(request.Uri.Contains(".mp4") && request.Resolution == ResolutionKind.Direct && request.LineId == line.Id,
        $"real-site playback Direct: {request.Uri}");
    using var http = new HttpClient();
    using var probe = new HttpRequestMessage(HttpMethod.Get, request.Uri);
    probe.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 1023);
    foreach (var header in request.Headers) probe.Headers.TryAddWithoutValidation(header.Key, header.Value);
    using var response = await http.SendAsync(probe, HttpCompletionOption.ResponseHeadersRead, ct);
    response.EnsureSuccessStatusCode();
    await using var stream = await response.Content.ReadAsStreamAsync(ct);
    var bytes = new byte[32];
    var count = await stream.ReadAsync(bytes, ct);
    Require(count >= 8 && System.Text.Encoding.ASCII.GetString(bytes, 4, 4) == "ftyp", $"actual CDN MP4 bytes, HTTP {(int)response.StatusCode}");
    Console.WriteLine($"Real-site chain {sw.ElapsedMilliseconds}ms");
}
Console.WriteLine($"DRPY SMOKE {passed}/{passed}");
