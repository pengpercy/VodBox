using VodBox.Core;
using VodBox.Infrastructure;
using Xunit;

namespace VodBox.Tests;

public sealed class AdditionalJarAdapterTests
{
    private static SourceInfo RjSite => new() { Key="rj", Name="RJ", Api="csp_AppRJ", Runtime=SourceRuntime.NativeSpider, Ext="""{"url":"https://example.invalid"}""" };
    private static SourceInfo YsSite(string path="api.php/app") => new() { Key="ys", Name="YS", Api="csp_AppYs", Runtime=SourceRuntime.NativeSpider, Ext="https://example.invalid/"+path+"/" };

    [Fact]
    public async Task AppRjUsesSignedMultipartContractAndFreshPlayback()
    {
        var requests=new List<(string Url,IReadOnlyDictionary<string,string> Fields)>();
        var details=0;
        var source=new AppRjSource(RjSite,(url,fields,ct)=>
        {
            requests.Add((url,new Dictionary<string,string>(fields)));
            Assert.Equal("1000",fields["timestamp"]);
            Assert.Equal(AppRjSource.Sign(1000),fields["sign"]);
            if(url.EndsWith("/v3/type/top_type"))return Task.FromResult("""{"data":{"list":[{"type_id":1,"type_name":"电影","year":["2025","2026"]}]}}""");
            if(url.EndsWith("/v3/home/vod_details"))return Task.FromResult("{\"data\":{\"vod_id\":\"7\",\"vod_name\":\"测试\",\"vod_play_list\":[{\"name\":\"线路\",\"ua\":\"test-agent\",\"parse_urls\":[],\"urls\":[{\"name\":\"第一集\",\"url\":\"https://cdn.example.invalid/video.mp4?token="+(++details)+"\"}]}]}}");
            return Task.FromResult("""{"data":{"pagecount":3,"list":[{"vod_id":7,"vod_name":"测试","vod_pic_thumb":"https://example.invalid/poster.jpg"}]}}""");
        },()=>1000);
        Assert.Single(await source.GetCategoriesAsync(TestContext.Current.CancellationToken));
        Assert.Equal("year",Assert.Single(await source.GetFiltersAsync("1",TestContext.Current.CancellationToken)).Key);
        var page=await source.GetItemsAsync("1",2,new Dictionary<string,string>{{"year","2026"}},TestContext.Current.CancellationToken);
        Assert.Equal(3,page.PageCount);
        Assert.Equal("2026",requests[^1].Fields["year"]);
        Assert.Equal("7",Assert.Single(page.Items).Id);
        Assert.NotEmpty((await source.SearchAsync("中文",1,TestContext.Current.CancellationToken)).Items);
        Assert.Equal("中文",requests[^1].Fields["keyword"]);
        var detail=await source.GetDetailAsync("7",TestContext.Current.CancellationToken);
        var request=await source.ResolvePlaybackAsync("7",detail.Lines[0].Episodes[0].Id,TestContext.Current.CancellationToken);
        Assert.Contains("token=2",request.Uri);
        Assert.Equal("test-agent",request.Headers["User-Agent"]);
        Assert.Equal("rj:0",request.LineId);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"data\":null}")]
    [InlineData("{\"data\":{\"list\":{}}}")]
    public async Task AppRjMalformedResponsesFailExplicitly(string json)
    {
        var source=new AppRjSource(RjSite,(_,_,_)=>Task.FromResult(json),()=>1000);
        await Assert.ThrowsAsync<InvalidDataException>(()=>source.GetCategoriesAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("api.php/app")]
    [InlineData("xgapp")]
    public async Task AppYsAppBranchesSupportBrowseSearchDetailAndDirectPlayback(string path)
    {
        var requests=new List<string>();
        var source=new AppYsSource(YsSite(path),(url,ct)=>
        {
            requests.Add(url);
            if(url.Contains("/nav?"))return Task.FromResult("""{"data":[{"type_id":1,"type_name":"电影"}]}""");
            if(url.Contains("/video_detail?"))
            {
                const string vod="""{"vod_id":"7","vod_name":"测试","vod_url_with_player":[{"name":"mp4","url":"第一集$https://cdn.example.invalid/video.mp4","parse_api":""}]}""";
                return Task.FromResult(path=="xgapp"?"{\"data\":{\"vod_info\":"+vod+"}}":"{\"data\":"+vod+"}");
            }
            if(url.Contains("/index_video?"))return Task.FromResult("""{"data":[{"vlist":[{"vod_id":7,"vod_name":"推荐"}]}]}""");
            return Task.FromResult("""{"totalpage":4,"data":[{"vod_id":7,"vod_name":"测试"}]}""");
        });
        Assert.Single(await source.GetCategoriesAsync(TestContext.Current.CancellationToken));
        Assert.Single((await source.GetHomeAsync(TestContext.Current.CancellationToken)).Items);
        var page=await source.GetItemsAsync("1",2,new Dictionary<string,string>{{"year","2026"}},TestContext.Current.CancellationToken);
        Assert.Equal(4,page.PageCount);
        Assert.Contains("tid=1",requests[^1]);Assert.Contains("pg=2",requests[^1]);Assert.Contains("year=2026",requests[^1]);
        Assert.Single((await source.SearchAsync("中文",1,TestContext.Current.CancellationToken)).Items);
        Assert.Contains("text="+Uri.EscapeDataString("中文"),requests[^1]);
        var detail=await source.GetDetailAsync("7",TestContext.Current.CancellationToken);
        var request=await source.ResolvePlaybackAsync("7",detail.Lines[0].Episodes[0].Id,TestContext.Current.CancellationToken);
        Assert.Equal(ResolutionKind.Direct,request.Resolution);
        Assert.Equal("https://cdn.example.invalid/video.mp4",request.Uri);
    }

    [Fact]
    public async Task AppRjParserFallbackIsSignedAndPreservesReturnedAgent()
    {
        var calls=new List<string>();
        const string detail="""{"data":{"vod_name":"测试","vod_play_list":[{"name":"线路","parse_urls":["https://parser.example.invalid/bad?url=","https://parser.example.invalid/good?url="],"urls":[{"name":"集","url":"https://media.example.invalid/watch?id=1"}]}]}}""";
        var source=new AppRjSource(RjSite,(_,_,_)=>Task.FromResult(detail),()=>1000,(url,ct)=>
        {
            calls.Add(url);
            return Task.FromResult(url.Contains("/bad?")?"{\"url\":\"javascript:bad\"}":"{\"url\":\"https://cdn.example.invalid/ok.mp4\",\"UA\":\"parsed-agent\"}");
        });
        var request=await source.ResolvePlaybackAsync("7","rj:0:0",TestContext.Current.CancellationToken);
        Assert.Equal(2,calls.Count);
        Assert.All(calls,url=>Assert.Contains("&timestamp=1000",url));
        Assert.Equal("https://cdn.example.invalid/ok.mp4",request.Uri);
        Assert.Equal("parsed-agent",request.Headers["User-Agent"]);
    }

    [Fact]
    public async Task CancellationPreventsBothAdaptersFromCallingTransport()
    {
        var calls=0;
        using var scope=new CancellationTokenSource();scope.Cancel();
        var rj=new AppRjSource(RjSite,(_,_,_)=>{calls++;return Task.FromResult("{}");},()=>1000);
        var ys=new AppYsSource(YsSite(),(_,_)=>{calls++;return Task.FromResult("{}");});
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>rj.GetHomeAsync(scope.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>ys.GetHomeAsync(scope.Token));
        Assert.Equal(0,calls);
    }

    [Fact]
    public async Task AppYsExplicitlyDistinguishesJsonParserFromUnsupportedSniffing()
    {
        var parser="https://parser.example.invalid/?url=";
        var source=new AppYsSource(YsSite(),(_,_)=>Task.FromResult("{\"data\":{\"vod_name\":\"测试\",\"vod_url_with_player\":[{\"name\":\"线路\",\"url\":\"集$https://example.invalid/watch\",\"parse_api\":\""+parser+"\"}]}}"));
        var request=await source.ResolvePlaybackAsync("7","https://example.invalid/watch",TestContext.Current.CancellationToken);
        Assert.Equal(ResolutionKind.Json,request.Resolution);Assert.Equal(parser,request.ParseEndpoint);
        var sniff=new AppYsSource(YsSite(),(_,_)=>Task.FromResult("""{"data":{"vod_name":"测试","vod_url_with_player":[{"name":"线路","url":"集$https://example.invalid/watch"}]}}"""));
        await Assert.ThrowsAsync<NotSupportedException>(()=>sniff.ResolvePlaybackAsync("7","https://example.invalid/watch",TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ThreeExtraProtocolsHaveIndependentFactoriesAndUnimplementedVariantsStayDisabled()
    {
        var config=ConfigLoader.Parse("""{"sites":[{"key":"p","name":"Push","api":"csp_Push"},{"key":"r","name":"RJ","api":"csp_AppRJ","ext":{"url":"https://example.invalid"}},{"key":"y","name":"YS","api":"csp_AppYs","ext":"https://example.invalid/api.php/app/"},{"key":"unknown","name":"Unknown","api":"csp_AppYs","ext":"https://example.invalid/foo.vod"}]}""")!;
        var sources=ConfigLoader.ToSources(config);
        Assert.Equal(3,sources.Count);
        Assert.IsType<PushSource>(NativeSpiders.Create(sources[0]));
        Assert.IsType<AppRjSource>(NativeSpiders.Create(sources[1]));
        Assert.IsType<AppYsSource>(NativeSpiders.Create(sources[2]));
        Assert.Throws<NotSupportedException>(()=>new AppYsSource(YsSite("foo.vod")));
    }
}
