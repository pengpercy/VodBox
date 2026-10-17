using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VodBox.Core;

namespace VodBox.Infrastructure;

/// <summary>Appgz 原生匿名设备认证及 RSA/AES 内容协议；与 GuaziTY 无关。</summary>
public sealed class AppGzSource : IResolvingContentSource
{
    private const string Origin="https://api.gjjwgp3n.com";
    private readonly SourceInfo _site;
    private readonly Func<string,IReadOnlyDictionary<string,string>,IReadOnlyDictionary<string,string>,CancellationToken,Task<string>> _post;
    private readonly Func<long> _clock;
    private readonly string _key;
    private readonly string _iv;
    private readonly string _origin;
    private readonly string _identityFile;
    private readonly SemaphoreSlim _authentication=new(1,1);
    private string _token="",_oldKey="",_newKey="";
    public string Key=>_site.Key;
    public string Name=>_site.Name;
    public AppGzSource(SourceInfo site):this(site,null,null,null,null,null){}
    internal AppGzSource(SourceInfo site,Func<string,IReadOnlyDictionary<string,string>,IReadOnlyDictionary<string,string>,CancellationToken,Task<string>>? post,Func<long>? clock=null,string? key=null,string? iv=null,string? identityFile=null,string origin=Origin)
    {
        _site=site;_post=post??Post;_clock=clock??(()=>DateTimeOffset.UtcNow.ToUnixTimeSeconds());_key=key??AppGzCrypto.RandomKey();_iv=iv??AppGzCrypto.RandomKey();_identityFile=identityFile??Path.Combine(AppPaths.DataDirectory,"appgz-device.json");_origin=origin.TrimEnd('/');
    }
    // Java Appgz 没有 init(context,ext) 覆盖：不把其它端点规则冒充 Appgz。
    internal static bool Supports(string? ext)=>string.IsNullOrWhiteSpace(ext)||ext=="null";
    private static Dictionary<string,string> Headers()=>new(){{"User-Agent","okhttp/3.12.0"},{"Version","2603002"},{"PackageName","com.r3ac892153.q1ed27199a.wf930fc6bf20260401"},{"Ver","3.0.4.0"},{"api-ver","3.0.4.0"},{"code","GZ0001"},{"Referer",Origin}};
    private static async Task<string> Post(string url,IReadOnlyDictionary<string,string> fields,IReadOnlyDictionary<string,string> headers,CancellationToken ct)
    {
        using var http=new HttpClient{Timeout=TimeSpan.FromSeconds(20)};using var request=new HttpRequestMessage(HttpMethod.Post,url){Content=new FormUrlEncodedContent(fields)};foreach(var pair in headers)request.Headers.TryAddWithoutValidation(pair.Key,pair.Value);
        using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);response.EnsureSuccessStatusCode();await using var stream=await response.Content.ReadAsStreamAsync(ct);return DefaultHttp.Decode(await BoundedContent.ReadAsync(stream,8*1024*1024,ct));
    }
    private async Task<JsonDocument> Raw(string path,Dictionary<string,object?> payload,string token,CancellationToken ct)=>AppGzCrypto.Unwrap(await _post(_origin+path,AppGzCrypto.Wrap(AppGzCrypto.Json(payload),token,_clock(),_key,_iv),Headers(),ct));
    private static string Text(JsonElement item,string key)=>AppRjSource.Text(item,key);
    private async Task Authenticate(CancellationToken ct,bool refresh=false)
    {
        await _authentication.WaitAsync(ct);
        try
        {
            if(!refresh&&_token.Length>0)return;
            if(_newKey.Length==0&&File.Exists(_identityFile))
            {
                using var identity=JsonDocument.Parse(await File.ReadAllTextAsync(_identityFile,ct));_oldKey=Text(identity.RootElement,"old_key");_newKey=Text(identity.RootElement,"new_key");_token=Text(identity.RootElement,"token");
                if(!refresh&&_token.Length>0)return;
            }
            if(_newKey.Length==0)
            {
                // Android Build/Settings 无桌面对等项：生成持久桌面 androidId，沿用 Java SHA1 device-key 规则。
                var androidId=Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
                var fingerprint="3883756"+string.Concat(new[]{"vodbox","vodbox","desktop","desktop","vodbox","desktop","vodbox",""}.Select(value=>(value.Length%10).ToString(CultureInfo.InvariantCulture)));
                var hash=unchecked((ulong)(long)JavaHash(fingerprint));var uuid=hash.ToString("x16",CultureInfo.InvariantCulture)+hash.ToString("x16",CultureInfo.InvariantCulture);
                _newKey=Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(androidId+"|"+uuid)));_oldKey=DeviceId(androidId,_clock(),RandomNumberGenerator.GetInt32(int.MaxValue));
            }
            foreach(var signup in new[]{false,true,false})
            {
                var fields=new Dictionary<string,object?>{{"old_key",_oldKey},{"new_key",_newKey}};if(signup){fields["phone_type"]=1;fields["code"]="";}
                using var doc=await Raw("/App/Authentication/Device/"+(signup?"signUp":"signIn"),fields,"",ct);_token=Text(doc.RootElement,"token");if(_token.Length>0)break;
            }
            if(_token.Length==0)throw new InvalidDataException("Appgz 匿名设备注册或登录失败。");
            await AtomicFile.WriteAsync(_identityFile,AppGzCrypto.Json(new Dictionary<string,string>{{"old_key",_oldKey},{"new_key",_newKey},{"token",_token}}),ct);
        }
        finally{_authentication.Release();}
    }
    internal static int JavaHash(string value){var hash=0;foreach(var c in value)hash=unchecked(hash*31+c);return hash;}
    internal static string DeviceId(string androidId,long timestamp,int random)
    {
        var data=new byte[18];BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(0,4),unchecked((int)timestamp));BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(4,4),random);data[8]=3;data[9]=0;BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(10,4),JavaHash(androidId));
        using var hmac=new HMACSHA1(Encoding.UTF8.GetBytes("d6fc3a4a06adbde89223bvefedc24fecde188aaa9161"));var hash=JavaHash(Convert.ToBase64String(hmac.ComputeHash(data,0,14)));BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(14,4),hash);return Convert.ToBase64String(data);
    }
    private async Task<JsonDocument> Request(string path,Dictionary<string,object?> fields,CancellationToken ct)
    {
        await Authenticate(ct);return await Raw(path,fields,_token,ct);
    }
    private static MediaItem Item(JsonElement item)=>new(){Id=Text(item,"vod_id"),Title=Text(item,"vod_name"),Poster=Text(item,"vod_pic"),Remarks=Text(item,"vod_scroe"),Year=Text(item,"vod_year"),Area=Text(item,"vod_area")};
    public Task<IReadOnlyList<Category>> GetCategoriesAsync(CancellationToken ct=default){ct.ThrowIfCancellationRequested();return Task.FromResult<IReadOnlyList<Category>>([new("1","电影"),new("2","电视剧"),new("4","动漫"),new("3","综艺")]);}
    public Task<IReadOnlyList<FilterGroup>> GetFiltersAsync(string categoryId,CancellationToken ct=default)=>new JpysSource(_site with{Ext=null},(address,headers,token)=>Task.FromResult("{}"),deviceId:"00000000-0000-0000-0000-000000000000").GetFiltersAsync(categoryId,ct);
    public async Task<MediaPage> GetHomeAsync(CancellationToken ct=default)
    {
        using var doc=await Request("/App/IndexList/index",new(){{"pid","1"}},ct);var items=new List<MediaItem>();
        foreach(var group in doc.RootElement.GetProperty("list").EnumerateArray())if(Text(group,"type").Contains("热门推荐",StringComparison.Ordinal))foreach(var value in group.GetProperty("list").EnumerateArray())items.Add(new(){Id=Text(value,"vod_id"),Title=Text(value,"c_name"),Poster=Text(value,"c_pic"),Remarks=Text(value,"new_continue")});return new(items,1,1);
    }
    public async Task<MediaPage> GetItemsAsync(string categoryId,int page,IReadOnlyDictionary<string,string>? filters,CancellationToken ct=default)
    {
        if(page<1)throw new InvalidDataException("页码无效。");string Value(string key)=>filters?.TryGetValue(key,out var value)==true&&value!="全部"&&value.Length>0?value:"0";
        var sub=categoryId switch{"1"=>"5","2"=>"12","3"=>"22",_=>"30"};
        using var doc=await Request("/App/IndexList/indexList",new(){{"area",Value("area")},{"sub",sub},{"year",Value("year")},{"pageSize","30"},{"sort","d_id"},{"page",page.ToString(CultureInfo.InvariantCulture)},{"tid",categoryId}},ct);var items=doc.RootElement.GetProperty("list").EnumerateArray().Select(Item).ToArray();return new(items,page,items.Length>=30?page+1:page);
    }
    public async Task<MediaPage> SearchAsync(string query,int page,CancellationToken ct=default)
    {
        if(page<1)throw new InvalidDataException("页码无效。");if(page>1)return new([],page,page);
        using var doc=await Request("/App/Index/findMoreVod",new(){{"keywords",query},{"order_val","1"}},ct);return new(doc.RootElement.GetProperty("list").EnumerateArray().Select(Item).ToArray(),1,1);
    }
    private async Task<(MediaItem Item,string Actor,string Director,IReadOnlyList<Episode> Episodes)> Detail(string mediaId,CancellationToken ct)
    {
        await Authenticate(ct);using var doc=await Request("/App/IndexPlay/playInfo",new(){{"token_id",""},{"vod_id",mediaId},{"mobile_time",_clock().ToString(CultureInfo.InvariantCulture)},{"token",_token}},ct);var info=doc.RootElement.GetProperty("vodInfo");
        var item=Item(info) with{Id=mediaId,Remarks=Text(info,"vod_title")};var actor=Text(info,"vod_actor");var director=Text(info,"vod_director");
        using var urls=await Request("/App/Resource/Vurl/show",new(){{"vurl_cloud_id","2"},{"vod_d_id",mediaId}},ct);var episodes=new List<Episode>();
        foreach(var value in urls.RootElement.GetProperty("list").EnumerateArray())
        {
            var play=value.GetProperty("play");var param="";foreach(var quality in new[]{"1080","720","480"})if(play.TryGetProperty(quality,out var qualityItem)&&Text(qualityItem,"param") is {Length:>0} selected){param=selected;break;}
            if(param.Length>0)episodes.Add(new("专线播放\u0001"+param,Text(value,"title"),param));
        }
        return(item,actor,director,episodes);
    }
    public async Task<MediaDetail> GetDetailAsync(string mediaId,CancellationToken ct=default)
    {
        var data=await Detail(mediaId,ct);return new(){Item=data.Item,Description="内详",Actor=data.Actor,Director=data.Director,Lines=data.Episodes.Count>0?[new PlaybackLine("专线播放","专线播放",data.Episodes)]:[]};
    }
    public async Task<PlaybackRequest> ResolvePlaybackAsync(string mediaId,string episodeId,CancellationToken ct=default)
    {
        var detail=await Detail(mediaId,ct);var episode=detail.Episodes.FirstOrDefault(value=>value.Id==episodeId)??throw new InvalidDataException("Appgz 选集不存在。");var param=episode.Uri??"";
        var values=param.TrimStart('?').Split('&').Select(value=>value.Split('=',2)).Where(value=>value.Length==2).ToDictionary(value=>value[0],value=>System.Net.WebUtility.UrlDecode(value[1]),StringComparer.Ordinal);
        string Value(string name)=>values.TryGetValue(name,out var value)?value:throw new InvalidDataException("Appgz 播放参数缺少 "+name);
        var fields=new Dictionary<string,object?>{{"domain_type",Value("domain_type")},{"vod_id",Value("vod_d_id")},{"type",Value("type")},{"resolution",Value("resolution")},{"vurl_id",Value("vurl_id")}};
        using var doc=await Request("/App/Resource/VurlDetail/showOne",fields,ct);var url=Text(doc.RootElement,"url");
        if(Uri.TryCreate(url,UriKind.Absolute,out var candidate)&&candidate.AbsolutePath.TrimEnd('/').EndsWith("/hls/index.m3u8",StringComparison.OrdinalIgnoreCase))
        {
            await Authenticate(ct,true);using var retried=await Request("/App/Resource/VurlDetail/showOne",fields,ct);url=Text(retried.RootElement,"url");
        }
        if(!Uri.TryCreate(url,UriKind.Absolute,out var uri)||uri.Scheme is not("http" or "https")||uri.UserInfo.Length>0)throw new InvalidDataException("Appgz 返回无效播放地址。");
        return new(){Uri=url,SourceKey=Key,SourceName=Name,MediaId=mediaId,LineId="专线播放",EpisodeId=episodeId,Title=detail.Item.Title+" · "+episode.Title,Headers=new(){{"User-Agent","Lavf/57.83.100"}}};
    }
}
