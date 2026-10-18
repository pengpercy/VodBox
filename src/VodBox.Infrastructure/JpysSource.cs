using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VodBox.Core;

namespace VodBox.Infrastructure;

/// <summary>精品影视 mw-movie 协议，按 Java Spider 的参数顺序及双摘要签名重写。</summary>
public sealed class JpysSource : IResolvingContentSource
{
    // 站点在轮换域名：www.ghw9zwp5.com 已被前置 WAF 直接 403，而非 www 的裸域会 302 到当前可用域名
    // （实测 https://ghw9zwp5.com/ -> https://0bv77aw.com/）。用裸域作基地址并跟随重定向，避免硬编码失效镜像。
    private const string DefaultOrigin = "https://ghw9zwp5.com";
    private const string Api = "/api/mw-movie/anonymous/";
    private const string Secret = "cb808529bae6b6be45ecfab29a4889bc";
    private const string Agent = "Mozilla/5.0 (X11; Linux aarch64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/148.0.0.0 Safari/537.36";
    private static readonly Lazy<string> Device = new(LoadDevice);
    // 原插件播放请求头的 Origin/Referer 固定为 www 主域；ext 覆盖过域名时用该域名，不退回默认镜像。
    private string PlayerOrigin=>_origin==DefaultOrigin?"https://www.ghw9zwp5.com":_origin;
    private readonly SourceInfo _site;
    private readonly string _origin;
    private readonly string _device;
    private readonly Func<long> _clock;
    private readonly Func<string,IReadOnlyDictionary<string,string>,CancellationToken,Task<string>> _fetch;
    public string Key => _site.Key;
    public string Name => _site.Name;
    public JpysSource(SourceInfo site) : this(site, null, null, null) { }
    internal JpysSource(SourceInfo site, Func<string,IReadOnlyDictionary<string,string>,CancellationToken,Task<string>>? fetch, Func<long>? clock = null, string? deviceId = null)
    {
        _site=site;
        var endpoint=string.IsNullOrWhiteSpace(site.Ext)?DefaultOrigin:site.Ext.Trim();
        if(!Uri.TryCreate(endpoint,UriKind.Absolute,out var uri)||uri.Scheme is not("http" or "https")||uri.UserInfo.Length>0||uri.Query.Length>0||uri.Fragment.Length>0)
            throw new InvalidDataException("Jpys ext 必须是 HTTP(S) 基地址。");
        _origin=endpoint.TrimEnd('/'); _device=deviceId??Device.Value;
        if(_device.Length==0||_device.Any(c=>!char.IsAsciiLetterOrDigit(c)&&c!='-'))throw new InvalidDataException("Jpys deviceid 无效。");
        _clock=clock??(()=>DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()); _fetch=fetch??Fetch;
    }
    internal static bool Supports(string? ext)
    {
        var endpoint=string.IsNullOrWhiteSpace(ext)?DefaultOrigin:ext.Trim();
        return Uri.TryCreate(endpoint,UriKind.Absolute,out var uri)&&(uri.Scheme is "http" or "https")&&uri.UserInfo.Length==0&&uri.Query.Length==0&&uri.Fragment.Length==0;
    }
    private static string LoadDevice()
    {
        var path=Path.Combine(AppPaths.DataDirectory,"jpDeviceId");
        Directory.CreateDirectory(AppPaths.DataDirectory);
        if(File.Exists(path)){var value=File.ReadAllText(path).Trim();if(Guid.TryParse(value,out _))return value;}
        var id=Guid.NewGuid().ToString();AtomicFile.WriteAsync(path,id).GetAwaiter().GetResult();return id;
    }
    private static async Task<string> Fetch(string url,IReadOnlyDictionary<string,string> headers,CancellationToken ct)
    {
        // 裸域会 302 到轮换后的当前域名，必须允许跟随重定向，否则拿到的是空的 302 响应体。
        return DefaultHttp.Decode(await DefaultHttp.Shared.GetBoundedAsync(url,8*1024*1024,ct,headers));
    }
    internal static string Sign(string canonical,long timestamp)
    {
        var input=canonical+(canonical.Length>0?"&":"")+"key="+Secret+"&t="+timestamp.ToString(CultureInfo.InvariantCulture);
        var md5=Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
        return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(md5))).ToLowerInvariant();
    }
    private async Task<JsonDocument> Request(string path,string query,string canonical,CancellationToken ct,bool detail=false,bool player=false)
    {
        var time=_clock();var headers=new Dictionary<string,string>{{"sign",Sign(canonical,time)},{"T",time.ToString(CultureInfo.InvariantCulture)}};
        // 原插件 detailContent 的 map.put(SN,SN) 是真实异常，保留其行为并单独记录证据。
        headers[detail?_device:"deviceid"]=_device;
        if(player)headers["User-Agent"]=Agent;
        return JsonDocument.Parse(await _fetch(_origin+Api+path+(query.Length>0?"?"+query:""),headers,ct));
    }
    private static string Text(JsonElement item,string key)=>AppRjSource.Text(item,key);
    private static MediaItem Item(JsonElement item,string remarks="vodVersion")=>new(){Id=Text(item,"vodId"),Title=Text(item,"vodName"),Poster=Text(item,"vodPic"),Remarks=Text(item,remarks),Year=Text(item,"vodYear")};
    private static bool Visible(JsonElement item)=>!new[]{"其它","伦理","情色","剧情,爱情"}.Any(term=>Text(item,"vodClass").Contains(term,StringComparison.Ordinal));
    private static MediaItem[] Items(JsonElement list,string remarks="vodVersion",string? query=null)=>list.EnumerateArray().Where(Visible).Where(v=>query==null||Text(v,"vodName").Contains(query,StringComparison.Ordinal)).Select(v=>Item(v,remarks)).ToArray();
    public Task<IReadOnlyList<Category>> GetCategoriesAsync(CancellationToken ct=default){ct.ThrowIfCancellationRequested();return Task.FromResult<IReadOnlyList<Category>>([new("1","电影"),new("2","电视剧"),new("4","动漫"),new("3","综艺")]);}
    public Task<IReadOnlyList<FilterGroup>> GetFiltersAsync(string categoryId,CancellationToken ct=default)
    {
        ct.ThrowIfCancellationRequested();return Task.FromResult(Filters(categoryId));
    }
    /// <summary>mw-movie 系的固定地区/年份筛选（Appgz 沿用同一份）。</summary>
    internal static IReadOnlyList<FilterGroup> Filters(string categoryId)
    {
        var areas=categoryId=="4"?new[]{"中国大陆","美国","日本","其他"}:new[]{"中国大陆","中国香港","中国台湾","美国","日本","韩国","泰国","印度","其他"};
        var years=Enumerable.Range(2020,7).Reverse().Select(y=>y.ToString(CultureInfo.InvariantCulture)).ToList();
        if(categoryId!="3"){years.AddRange(Enumerable.Range(2010,10).Reverse().Select(y=>y.ToString(CultureInfo.InvariantCulture)));years.AddRange(["2009~2000","90年代","80年代"]);}
        if(categoryId is "3" or "4")years.Add("更早");
        return [new("area","地区",new[]{new FilterValue("全部","")}.Concat(areas.Select(v=>new FilterValue(v,v))).ToList(),""),new("year","年份",new[]{new FilterValue("全部","")}.Concat(years.Select(v=>new FilterValue(v,v))).ToList(),"")];
    }
    public async Task<MediaPage> GetHomeAsync(CancellationToken ct=default){using var doc=await Request("home/hotSearch","","",ct);return new(Items(doc.RootElement.GetProperty("data")),1,1);}
    public async Task<MediaPage> GetItemsAsync(string categoryId,int page,IReadOnlyDictionary<string,string>? filters,CancellationToken ct=default)
    {
        if(page<1)throw new InvalidDataException("页码无效。");
        string Value(string key)=>filters?.TryGetValue(key,out var value)==true&&value!="全部"?value:"";
        var area=Value("area");var year=Value("year");var p=page.ToString(CultureInfo.InvariantCulture);
        using var doc=await Request("video/list","type1="+Uri.EscapeDataString(categoryId)+"&pageNum="+p+"&area="+Uri.EscapeDataString(area)+"&year="+Uri.EscapeDataString(year),"area="+area+"&pageNum="+p+"&type1="+categoryId+"&year="+year,ct);
        var list=doc.RootElement.GetProperty("data").GetProperty("list");return new(Items(list),page,list.GetArrayLength()>0?page+1:page);
    }
    public async Task<MediaPage> SearchAsync(string query,int page,CancellationToken ct=default)
    {
        if(page<1)throw new InvalidDataException("页码无效。");if(page>1)return new([],page,page);
        using var doc=await Request("video/searchByWord","keyword="+System.Net.WebUtility.UrlEncode(query)+"&pageNum=1&pageSize=8","keyword="+query+"&pageNum=1&pageSize=8",ct);
        return new(Items(doc.RootElement.GetProperty("data").GetProperty("result").GetProperty("list"),"vodRemarks",query),1,1);
    }
    public async Task<MediaDetail> GetDetailAsync(string mediaId,CancellationToken ct=default)
    {
        using var doc=await Request("video/detail","id="+Uri.EscapeDataString(mediaId),"id="+mediaId,ct,detail:true);var data=doc.RootElement.GetProperty("data");
        var episodes=data.GetProperty("episodeList").EnumerateArray().Select(ep=>new Episode("爱电影\u0001"+mediaId+"@"+Text(ep,"nid"),Text(ep,"name"))).ToArray();
        return new(){Item=Item(data,"vodRemarks") with{Id=mediaId,Area=Text(data,"vodArea"),TypeName=Text(data,"vodClass")},Description=data.TryGetProperty("vodBlurb",out _)?Text(data,"vodBlurb"):Text(data,"vodContent"),Actor=Text(data,"vodActor"),Director=Text(data,"vodDirector"),Lines=episodes.Length>0?[new PlaybackLine("爱电影","爱电影",episodes)]:[]};
    }
    public async Task<PlaybackRequest> ResolvePlaybackAsync(string mediaId,string episodeId,CancellationToken ct=default)
    {
        // 原插件 playerContent 直接用 id@nid 请求播放地址，不再抓详情；归属由下面的 id 比对保证。
        const string prefix="爱电影\u0001";
        if(!episodeId.StartsWith(prefix,StringComparison.Ordinal))throw new InvalidDataException("Jpys 线路标识无效。");
        var parts=episodeId[prefix.Length..].Split('@');if(parts.Length!=2||parts[0]!=mediaId||parts[1].Length==0)throw new InvalidDataException("Jpys 选集标识无效。");
        var canonical="clientType=3&id="+mediaId+"&nid="+parts[1];
        using var doc=await Request("v2/video/episode/url","clientType=3&id="+Uri.EscapeDataString(mediaId)+"&nid="+Uri.EscapeDataString(parts[1]),canonical,ct,player:true);
        var url=Text(doc.RootElement.GetProperty("data").GetProperty("list")[0],"url");
        if(!Uri.TryCreate(url,UriKind.Absolute,out var uri)||uri.Scheme is not("http" or "https")||uri.UserInfo.Length>0)throw new InvalidDataException("Jpys 播放地址无效。");
        return new(){Uri=url,SourceKey=Key,SourceName=Name,MediaId=mediaId,EpisodeId=episodeId,LineId="爱电影",Title=Name,Headers=new(){{"User-Agent",Agent},{"Origin",PlayerOrigin},{"Referer",PlayerOrigin}}};
    }
}
