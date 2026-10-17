using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VodBox.Core;

namespace VodBox.Infrastructure;

/// <summary>Alllive 聚合接口。响应按 Java zE.N 作 LZ-string Base64 解压。</summary>
public sealed class AllliveSource : IResolvingContentSource
{
    private readonly SourceInfo _site;
    private readonly string _origin;
    private readonly Func<string,CancellationToken,Task<string>> _fetch;
    private readonly Func<CancellationToken,Task<string>> _uid;
    private readonly Func<long> _clock;
    private string _cookie="";
    public string Key=>_site.Key;
    public string Name=>_site.Name;
    public AllliveSource(SourceInfo site):this(site,null,null,null){}
    internal AllliveSource(SourceInfo site,Func<string,CancellationToken,Task<string>>? fetch,Func<CancellationToken,Task<string>>? uid=null,Func<long>? clock=null)
    {
        _site=site;var ext=(site.Ext??"").Trim();
        if(!ext.StartsWith("https://",StringComparison.Ordinal)&&!ext.StartsWith("http://",StringComparison.Ordinal))ext=FtyExtCodec.Decode(ext);
        if(!Uri.TryCreate(ext,UriKind.Absolute,out var uri)||uri.Scheme is not("http" or "https")||uri.UserInfo.Length>0||uri.Query.Length>0||uri.Fragment.Length>0)throw new InvalidDataException("Alllive ext 解密后必须是 HTTP(S) 基地址。");
        _origin=ext.TrimEnd('/');_fetch=fetch??Fetch;_uid=uid??FetchUid;_clock=clock??(()=>DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }
    internal static bool Supports(string? ext)
    {
        try
        {
            var endpoint=(ext??"").Trim();
            if(!endpoint.StartsWith("https://",StringComparison.Ordinal)&&!endpoint.StartsWith("http://",StringComparison.Ordinal))endpoint=FtyExtCodec.Decode(endpoint);
            return Uri.TryCreate(endpoint,UriKind.Absolute,out var uri)&&(uri.Scheme is "http" or "https")&&uri.UserInfo.Length==0&&uri.Query.Length==0&&uri.Fragment.Length==0;
        }
        catch(InvalidDataException){return false;}
    }
    private static async Task<string> Fetch(string url,CancellationToken ct)
    {
        using var http=new DefaultHttp(false);return DefaultHttp.Decode(await http.GetBoundedAsync(url,8*1024*1024,ct,new Dictionary<string,string>{{"sec-fetch-site","same-origin"}}));
    }
    private static async Task<string> FetchUid(CancellationToken ct)
    {
        using var http=new HttpClient{Timeout=TimeSpan.FromSeconds(20)};
        using var response=await http.PostAsync("https://udblgn.huya.com/web/anonymousLogin",new StringContent("{\"appId\":5002,\"byPass\":3,\"context\":\"\",\"version\":\"2.4\",\"data\":{}}",Encoding.UTF8,"application/json"),ct);
        response.EnsureSuccessStatusCode();await using var stream=await response.Content.ReadAsStreamAsync(ct);
        using var doc=JsonDocument.Parse(await BoundedContent.ReadAsync(stream,1024*1024,ct));return Text(doc.RootElement.GetProperty("data"),"uid");
    }
    private async Task<JsonDocument> Request(string platform,string path,CancellationToken ct,bool cookie=false)
    {
        if(platform is not("huya" or "douyu" or "bilibili"))throw new InvalidDataException("Alllive 平台无效。");
        var address=_origin+"/api/"+platform+"/"+path;
        if(cookie&&_cookie.Length>0)address+="&cookie="+Uri.EscapeDataString(System.Net.WebUtility.UrlDecode(_cookie));
        var doc=JsonDocument.Parse(FtyLzString.Decode(await _fetch(address,ct)));
        if(doc.RootElement.TryGetProperty("data",out var data)&&data.ValueKind==JsonValueKind.Object&&Text(data,"cookie") is {Length:>0} value)_cookie=value;
        return doc;
    }
    private static string Text(JsonElement data,string key)=>AppRjSource.Text(data,key);
    private static (string Platform,string Id) Parts(string id)
    {
        var split=id.IndexOf('_');if(split<=0||split==id.Length-1)throw new InvalidDataException("Alllive 标识无效。");
        var platform=id[..split];if(platform is not("huya" or "douyu" or "bilibili"))throw new InvalidDataException("Alllive 平台无效。");return(platform,id[(split+1)..]);
    }
    public async Task<IReadOnlyList<Category>> GetCategoriesAsync(CancellationToken ct=default)
    {
        // TVBox 中平台页面返回文件夹卡片；桌面模型无文件夹，展开为可直接浏览的分类。
        var result=new List<Category>{new("bilibili","哔哩 · 推荐")};
        foreach(var (platform,label) in new[]{("huya","虎牙"),("douyu","斗鱼")})
        {
            using var doc=await Request(platform,"getCategories",ct);
            foreach(var group in doc.RootElement.GetProperty("data").EnumerateArray())
                foreach(var item in group.GetProperty("list").EnumerateArray())
                    result.Add(new(platform+"_"+Text(item,"cid"),label+" · "+Text(group,"name")+" · "+Text(item,"name")));
        }
        return result;
    }
    public Task<IReadOnlyList<FilterGroup>> GetFiltersAsync(string categoryId,CancellationToken ct=default){ct.ThrowIfCancellationRequested();return Task.FromResult<IReadOnlyList<FilterGroup>>([]);}
    public Task<MediaPage> GetHomeAsync(CancellationToken ct=default)=>GetItemsAsync("bilibili",1,null,ct);
    public async Task<MediaPage> GetItemsAsync(string categoryId,int page,IReadOnlyDictionary<string,string>? filters,CancellationToken ct=default)
    {
        if(page<1)throw new InvalidDataException("页码无效。");var number=page.ToString(CultureInfo.InvariantCulture);
        var (platform,id)=categoryId=="bilibili"?("bilibili",""):Parts(categoryId);
        using var doc=await Request(platform,id.Length==0?"getRecommendRooms?page="+number:"getCategoryRooms?id="+Uri.EscapeDataString(id)+"&pid=1&page="+number,ct,true);
        var array=doc.RootElement.GetProperty("data").GetProperty("list");return new(Rooms(array,platform),page,array.GetArrayLength()>0?page+1:page);
    }
    private static MediaItem[] Rooms(JsonElement list,string platform)=>list.EnumerateArray().Select(room=>new MediaItem{Id=platform+"_"+Text(room,"roomId"),Title=Text(room,"title"),Poster=Text(room,"cover"),Remarks=Text(room,"nickname")}).ToArray();
    public async Task<MediaPage> SearchAsync(string query,int page,CancellationToken ct=default)
    {
        if(page<1)throw new InvalidDataException("页码无效。");var result=new List<MediaItem>();var more=false;
        foreach(var platform in new[]{"huya","douyu"})
        {
            using var doc=await Request(platform,"searchRooms?page="+page.ToString(CultureInfo.InvariantCulture)+"&kw="+Uri.EscapeDataString(query),ct);
            var array=doc.RootElement.GetProperty("data").GetProperty("list");more|=array.GetArrayLength()>0;result.AddRange(Rooms(array,platform));
        }
        return new(result,page,more?page+1:page);
    }
    private async Task<JsonDocument> Detail(string mediaId,CancellationToken ct){var(platform,id)=Parts(mediaId);return await Request(platform,"getRoomDetail?id="+Uri.EscapeDataString(id),ct);}
    private static IReadOnlyList<PlaybackLine> Lines(JsonElement data,string suffix="")=>data.GetProperty("stream").EnumerateArray().Select(stream=>new PlaybackLine(Text(stream,"name"),Text(stream,"name"),stream.GetProperty("lines").EnumerateArray().Select(line=>new Episode(Text(stream,"name")+"\u0001"+Text(line,"url")+suffix,Text(line,"name"),Text(line,"url")+suffix)).ToArray())).ToArray();
    public async Task<MediaDetail> GetDetailAsync(string mediaId,CancellationToken ct=default)
    {
        using var doc=await Detail(mediaId,ct);var data=doc.RootElement.GetProperty("data");
        return new(){Item=new(){Id=mediaId,Title=Text(data,"title"),Poster=Text(data,"cover"),Remarks=Text(data,"online"),TypeName=Text(data,"category")},Actor=Text(data,"nickname"),Director=Text(data,"siteId"),Description=Text(data,"url"),Lines=Lines(data,data.TryGetProperty("info",out var info)&&info.ValueKind==JsonValueKind.Object?HuyaSuffix(info,await _uid(ct),_clock()):"")};
    }
    private static string Md5(string input)=>Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
    internal static string HuyaSuffix(JsonElement info,string uid,long milliseconds)
    {
        if(!long.TryParse(uid,NumberStyles.None,CultureInfo.InvariantCulture,out var user)||user<0)throw new InvalidDataException("Alllive 虎牙匿名 UID 无效。");
        var values=Text(info,"code").Split('&').Select(v=>v.Split('=',2)).Where(v=>v.Length==2).ToDictionary(v=>v[0],v=>System.Net.WebUtility.UrlDecode(v[1]),StringComparer.Ordinal);
        string Value(string key)=>values.TryGetValue(key,out var v)?v:throw new InvalidDataException("Alllive 虎牙签名缺少 "+key);
        var fm=Encoding.UTF8.GetString(Convert.FromBase64String(Value("fm"))).Split('_')[0];var ctype=Value("ctype");
        var time=(milliseconds/1000+21600).ToString("x",CultureInfo.InvariantCulture);var seq=checked(milliseconds*1000+user).ToString(CultureInfo.InvariantCulture);
        var hash=Md5(seq+"|"+ctype+"|"+ctype);var secret=Md5(fm+"_"+uid+"_"+Text(info,"name")+"_"+hash+"_"+time);
        return "&wsSecret="+secret+"&wsTime="+time+"&seqid="+seq+"&ctype="+ctype+"&ver=1&fs="+Value("fs")+"&t="+ctype+"&u="+uid;
    }
    public async Task<PlaybackRequest> ResolvePlaybackAsync(string mediaId,string episodeId,CancellationToken ct=default)
    {
        using var doc=await Detail(mediaId,ct);var data=doc.RootElement.GetProperty("data");var lines=Lines(data);
        var separator=episodeId.IndexOf('\u0001');
        if(separator<1)throw new InvalidDataException("Alllive 选集标识无效。");
        var originalLine=episodeId[..separator];var originalUrl=episodeId[(separator+1)..];
        static string StreamIdentity(string url)=>Uri.TryCreate(url.StartsWith("//",StringComparison.Ordinal)?"https:"+url:url,UriKind.Absolute,out var uri)?uri.GetLeftPart(UriPartial.Path):url.Split('?')[0];
        var candidates=lines.SelectMany((line,i)=>line.Episodes.Select((episode,j)=>(line,episode,i,j))).Where(v=>v.line.Id==originalLine).ToArray();
        var matches=candidates.Where(v=>StreamIdentity(v.episode.Uri??"")==StreamIdentity(originalUrl)).ToArray();
        var selected=matches.Length==1?matches[0]:candidates.Length==1?candidates[0]:default;
        if(selected.episode is null)throw new InvalidDataException("Alllive 选集不存在。");
        var url=Text(data.GetProperty("stream")[selected.i].GetProperty("lines")[selected.j],"url");
        if(data.TryGetProperty("info",out var info)&&info.ValueKind==JsonValueKind.Object)url+=HuyaSuffix(info,await _uid(ct),_clock());
        if(url.StartsWith("//",StringComparison.Ordinal))url="https:"+url;
        if(!Uri.TryCreate(url,UriKind.Absolute,out var uri)||uri.Scheme is not("http" or "https")||uri.UserInfo.Length>0)throw new InvalidDataException("Alllive 播放地址无效。");
        return new(){Uri=url,SourceKey=Key,SourceName=Name,MediaId=mediaId,LineId=selected.line.Id,EpisodeId=episodeId,Title=Text(data,"title")+" · "+selected.episode.Title};
    }
}
