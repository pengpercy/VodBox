using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using VodBox.Core;

namespace VodBox.Infrastructure;

/// <summary>JPJ API, DNS discovery and HTTP media subset; proprietary tvbox-xg FTP is explicitly classified.</summary>
public sealed class JPJSource : IResolvingContentSource
{
    private const string Agent="Mozilla/5.0 (Linux; Android 9; V2196A Build/PQ3A.190705.08211809; wv) AppleWebKit/537.36 (KHTML, like Gecko) Version/4.0 Chrome/91.0.4472.114 Mobile Safari/537.36;webank/h5face;webank/1.0;netType:NETWORK_WIFI;appVersion:416;packageName:com.jp3.xg3";
    private static readonly HttpClient Http=new(new HttpClientHandler {AutomaticDecompression=DecompressionMethods.All}) {Timeout=TimeSpan.FromSeconds(20)};
    private readonly SourceInfo _site;
    private readonly Func<string,CancellationToken,Task<string>> _fetch;
    private readonly SemaphoreSlim _gate=new(1,1);
    private Uri _host=new("https://ev5356.970xw.com");
    private string _imageDomain="";
    private bool _initialized;
    public string Key=>_site.Key;
    public string Name=>_site.Name;
    private static readonly Category[] Categories=[new("1","电影"),new("2","电视剧"),new("3","动漫"),new("4","综艺"),new("50","纪录片"),new("99","Netflix")];
    public JPJSource(SourceInfo site):this(site,null) { }
    internal JPJSource(SourceInfo site,Func<string,CancellationToken,Task<string>>? fetch)
    {
        if(!Supports(site.Ext))throw new NotSupportedException("JPJ ext 配置不受支持。");
        _site=site;_fetch=fetch??Fetch;
    }
    internal static bool Supports(string? ext)
    {
        if(string.IsNullOrWhiteSpace(ext))return true;
        try {using var json=JsonDocument.Parse(FtyExtCodec.JsonConfig(ext));return json.RootElement.ValueKind==JsonValueKind.Object&&!json.RootElement.EnumerateObject().Any();}
        catch(Exception e) when(e is JsonException or InvalidDataException or FormatException){return false;}
    }
    private async Task<string> Fetch(string url,CancellationToken ct)
    {
        using var request=new HttpRequestMessage(HttpMethod.Get,HttpUri(url));request.Headers.TryAddWithoutValidation("User-Agent",Agent);request.Headers.TryAddWithoutValidation("Referer",_host.AbsoluteUri.TrimEnd('/'));
        using var response=await Http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct).ConfigureAwait(false);response.EnsureSuccessStatusCode();
        await using var stream=await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return Encoding.UTF8.GetString(await BoundedContent.ReadAsync(stream,4*1024*1024,ct).ConfigureAwait(false));
    }
    private async Task Init(CancellationToken ct)
    {
        if(_initialized)return;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if(_initialized)return;
            try
            {
                using var dns=JsonDocument.Parse(await _fetch("https://dns.alidns.com/resolve?name=swrdsfeiujo25sw.cc&type=TXT",ct).ConfigureAwait(false));
                var answer=dns.RootElement.GetProperty("Answer")[0];
                string[] suffixes=Field(answer,"data").Replace("\"","",StringComparison.Ordinal).Split(',',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries);
                foreach(string suffix in suffixes)
                {
                    if(Uri.CheckHostName("fan123."+suffix)!=UriHostNameType.Dns||suffix.IndexOfAny(['/','?','#',':','@'])>=0)continue;
                    var candidate=new Uri("https://fan123."+suffix);
                    try
                    {
                        using var settings=JsonDocument.Parse(await _fetch(new Uri(candidate,"/api/v2/settings/resourceDomainConfig").AbsoluteUri,ct).ConfigureAwait(false));
                        _host=candidate;
                        SetImageDomain(settings.RootElement);
                        break;
                    }
                    catch(Exception error) when(error is HttpRequestException or JsonException or InvalidDataException or KeyNotFoundException) { }
                }
            }
            catch(Exception error) when(error is HttpRequestException or JsonException or InvalidDataException or KeyNotFoundException or ArgumentOutOfRangeException)
            {
                // Java keeps its fixed default host when DNS/bootstrap fails.
            }
            if(_imageDomain.Length==0)
            {
                using var settings=JsonDocument.Parse(await _fetch(new Uri(_host,"/api/v2/settings/resourceDomainConfig").AbsoluteUri,ct).ConfigureAwait(false));
                SetImageDomain(settings.RootElement);
            }
            _initialized=true;
        }
        finally {_gate.Release();}
    }
    private void SetImageDomain(JsonElement root)
    {
        var data=root.GetProperty("data");
        foreach(string raw in Field(data,"imgDomain").Split(',',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries))
        {
            string candidate=raw.TrimEnd('/');
            if(Uri.CheckHostName(candidate)==UriHostNameType.Dns){_imageDomain=candidate;return;}
        }
    }
    private async Task<JsonDocument> Request(string path,CancellationToken ct)
    {
        await Init(ct).ConfigureAwait(false);
        return JsonDocument.Parse(await _fetch(new Uri(_host,path).AbsoluteUri,ct).ConfigureAwait(false));
    }
    public Task<IReadOnlyList<Category>> GetCategoriesAsync(CancellationToken ct=default){ct.ThrowIfCancellationRequested();return Task.FromResult<IReadOnlyList<Category>>(Categories);}
    public async Task<MediaPage> GetHomeAsync(CancellationToken ct=default)
    {
        using var json=await Request("/api/slide/list?pos_id=88",ct).ConfigureAwait(false);
        return new(Array(json.RootElement,"data").EnumerateArray().Select(v=>Item(v,true)).Where(v=>v.Id.Length>0&&v.Title.Length>0).ToArray(),1,1);
    }
    public Task<IReadOnlyList<FilterGroup>> GetFiltersAsync(string categoryId,CancellationToken ct=default){ct.ThrowIfCancellationRequested();return Task.FromResult<IReadOnlyList<FilterGroup>>([]);}
    public async Task<MediaPage> GetItemsAsync(string categoryId,int page,IReadOnlyDictionary<string,string>? filters,CancellationToken ct=default)
    {
        ValidatePage(page);if(!Categories.Any(c=>c.Id==categoryId))throw new InvalidDataException("JPJ 分类无效。");
        string Value(string key,string fallback)=>filters?.TryGetValue(key,out var value)==true&&value.Length>0?Uri.EscapeDataString(value):fallback;
        if(filters?.Keys.Any(k=>k is not ("area" or "year" or "by"))==true)throw new InvalidDataException("JPJ 筛选无效。");
        bool tag=categoryId is "50" or "99" or "111";
        string path=tag?"/api/dyTag/list?category_id="+categoryId+"&page="+page.ToString(CultureInfo.InvariantCulture):"/api/crumb/list?fcate_pid="+categoryId+"&area="+Value("area","0")+"&year="+Value("year","0")+"&type=0&sort="+Value("by","updata")+"&page="+page.ToString(CultureInfo.InvariantCulture)+"&category_id=";
        using var json=await Request(path,ct).ConfigureAwait(false);
        var data=Array(json.RootElement,"data");
        var items=tag?data.EnumerateArray().SelectMany(v=>Array(v,"dataList").EnumerateArray()).Select(v=>Item(v)).ToArray():data.EnumerateArray().Select(v=>Item(v)).ToArray();
        items=items.Where(v=>v.Title.Length>0).ToArray();return new(items,page,tag?page:items.Length>0?page+1:page);
    }
    public async Task<MediaPage> SearchAsync(string query,int page,CancellationToken ct=default)
    {
        ValidatePage(page);if(string.IsNullOrWhiteSpace(query))return new([],page,page);if(query.Length>256)throw new ArgumentException("搜索词过长。",nameof(query));
        using var json=await Request("/api/v2/search/videoV2?key="+Uri.EscapeDataString(query)+"&category_id=88&page="+page.ToString(CultureInfo.InvariantCulture)+"&pageSize=20",ct).ConfigureAwait(false);
        var items=Array(json.RootElement,"data").EnumerateArray().Select(v=>Item(v)).Where(v=>v.Title.Length>0).ToArray();return new(items,page,items.Length>0?page+1:page);
    }
    private MediaItem Item(JsonElement value,bool slide=false)
    {
        string pic=Field(value,"thumbnail");if(pic.Length==0)pic=Field(value,"path");
        string remarks=Field(value,"mask");if(remarks.Length==0&&value.TryGetProperty("playlist",out var playlist)&&playlist.ValueKind==JsonValueKind.Object)remarks=Field(playlist,"title");
        return new() {Id=Field(value,slide?"jump_id":"id"),Title=Field(value,"title"),Poster=pic.Length>0&&_imageDomain.Length>0?"http://"+_imageDomain+pic:null,Remarks=remarks,Year=Field(value,"year"),Area=Field(value,"area"),TypeName=Names(value,"types")};
    }
    public async Task<MediaDetail> GetDetailAsync(string mediaId,CancellationToken ct=default)
    {
        if(mediaId.Length is 0 or >128||mediaId.Any(char.IsControl))throw new InvalidDataException("JPJ 影片标识无效。");
        using var json=await Request("/api/video/detailv2?id="+Uri.EscapeDataString(mediaId),ct).ConfigureAwait(false);
        var value=json.RootElement.GetProperty("data");
        var lines=new List<PlaybackLine>();
        foreach(var group in Array(value,"source_list_source").EnumerateArray())
        {
            // 备用 CDN 分组只有 name/source_key（如 {"name":"极速蓝光","source_key":"back_source_list_cdn"}），
            // 没有 source_list，不是可播放线路；跳过而不是当成缺失数组报错。
            if(!group.TryGetProperty("source_list",out var sourceList)||sourceList.ValueKind!=JsonValueKind.Array)continue;
            string flag=Field(group,"name");if(flag.Length==0||flag.Contains('\u0001'))throw new InvalidDataException("JPJ 线路名无效。");
            var episodes=sourceList.EnumerateArray().Select(v=>new Episode(flag+'\u0001'+Field(v,"url").Replace("ftp","tvbox-xg:ftp",StringComparison.Ordinal),Field(v,"source_name"))).ToArray();
            if(episodes.Length>0)lines.Add(new(flag,flag,episodes));
        }
        return new() {Item=Item(value) with {Id=mediaId},Description=Field(value,"description").Replace("　","",StringComparison.Ordinal),Actor=Names(value,"actors"),Director=Names(value,"directors"),Lines=lines};
    }
    public async Task<PlaybackRequest> ResolvePlaybackAsync(string mediaId,string episodeId,CancellationToken ct=default)
    {
        var detail=await GetDetailAsync(mediaId,ct).ConfigureAwait(false);
        var line=detail.Lines.FirstOrDefault(l=>l.Episodes.Any(e=>e.Id==episodeId))??throw new InvalidDataException("JPJ 选集已失效。");
        string url=episodeId[(episodeId.IndexOf('\u0001')+1)..];
        if(url.StartsWith("tvbox-xg:",StringComparison.OrdinalIgnoreCase))throw new NotSupportedException("荐片此线路使用 tvbox-xg/FTP 私有传输，需要原宿主的 PCDN 播放组件；请选择 HTTP 直连线路。");
        var uri=HttpUri(url);
        return new() {Uri=uri.AbsoluteUri,Title=detail.Item.Title,SourceKey=Key,SourceName=Name,MediaId=mediaId,EpisodeId=episodeId,LineId=line.Id,ProxyMedia=url.Contains("m3u8",StringComparison.OrdinalIgnoreCase),Headers=new Dictionary<string,string>{{"User-Agent",Agent},{"Referer",_host.AbsoluteUri.TrimEnd('/')}}};
    }
    private static string Names(JsonElement value,string key)=>value.TryGetProperty(key,out var array)&&array.ValueKind==JsonValueKind.Array?string.Join(' ',array.EnumerateArray().Select(v=>Field(v,"title") is {Length:>0} title?title:Field(v,"name"))):"";
    private static JsonElement Array(JsonElement value,string key)=>value.TryGetProperty(key,out var array)&&array.ValueKind==JsonValueKind.Array?array:throw new InvalidDataException("JPJ 缺少数组："+key);
    private static string Field(JsonElement value,string key)=>value.ValueKind==JsonValueKind.Object&&value.TryGetProperty(key,out var property)?property.ValueKind==JsonValueKind.String?property.GetString()??"":property.ToString():"";
    private static Uri HttpUri(string value)=>Uri.TryCreate(value,UriKind.Absolute,out var uri)&&uri.Scheme is "http" or "https"&&uri.UserInfo.Length==0?uri:throw new InvalidDataException("JPJ 线路不是可直接播放的 HTTP(S) 地址。");
    private static void ValidatePage(int page){if(page<1||page==int.MaxValue)throw new ArgumentOutOfRangeException(nameof(page));}
}
