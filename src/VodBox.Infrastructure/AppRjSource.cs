using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VodBox.Core;

namespace VodBox.Infrastructure;

/// <summary>AppRJ v3 multipart 协议，签名与原始 Spider 契约一致；播放时重新读取详情。</summary>
public sealed class AppRjSource : IResolvingContentSource
{
    private readonly SourceInfo _site;
    private readonly Uri _base;
    private readonly Func<string, IReadOnlyDictionary<string,string>, CancellationToken, Task<string>> _post;
    private readonly Func<long> _clock;
    private readonly Func<string,CancellationToken,Task<string>> _parseFetch;
    private static readonly HttpClient Http = new(new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(20) };
    private const string Salt = "7gp0bnd2sr85ydii2j32pcypscoc4w6c7g5spl";
    public string Key => _site.Key;
    public string Name => _site.Name;
    public AppRjSource(SourceInfo site) : this(site, null, null) { }
    internal AppRjSource(SourceInfo site, Func<string,IReadOnlyDictionary<string,string>,CancellationToken,Task<string>>? post, Func<long>? clock, Func<string,CancellationToken,Task<string>>? parseFetch = null)
    {
        _site = site;
        using var config = JsonDocument.Parse(site.Ext ?? "{}");
        if (config.RootElement.ValueKind != JsonValueKind.Object || !config.RootElement.TryGetProperty("url", out var url) || url.ValueKind != JsonValueKind.String || !Uri.TryCreate(url.GetString(), UriKind.Absolute, out var endpoint) || endpoint.Scheme is not ("http" or "https") || endpoint.UserInfo.Length > 0)
            throw new InvalidDataException("AppRJ ext.url 必须是 HTTP(S) 地址。");
        _base = endpoint;
        _post = post ?? PostAsync;
        _parseFetch = parseFetch ?? (async (address, token) =>
        {
            using var http = new DefaultHttp(false);
            return DefaultHttp.Decode(await http.GetBoundedAsync(address, 2 * 1024 * 1024, token));
        });
        _clock = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }
    private static async Task<string> PostAsync(string url, IReadOnlyDictionary<string,string> fields, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        foreach (var pair in fields) form.Add(new StringContent(pair.Value), pair.Key);
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = form };
        request.Headers.TryAddWithoutValidation("User-Agent", "okhttp-okgo/jeasonlzy");
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return DefaultHttp.Decode(await BoundedContent.ReadAsync(stream, 8 * 1024 * 1024, ct));
    }
    internal static string Sign(long timestamp) => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(Salt + timestamp.ToString(CultureInfo.InvariantCulture)))).ToLowerInvariant();
    private async Task<JsonDocument> Request(string path, Dictionary<string,string> fields, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var time = _clock(); fields["timestamp"] = time.ToString(CultureInfo.InvariantCulture); fields["sign"] = Sign(time);
        using var scope = CancellationTokenSource.CreateLinkedTokenSource(ct);
        scope.CancelAfter(TimeSpan.FromSeconds(20));
        var text = await _post(new Uri(_base, path).AbsoluteUri, fields, scope.Token);
        scope.Token.ThrowIfCancellationRequested();
        ct.ThrowIfCancellationRequested();
        return JsonDocument.Parse(text);
    }
    private static JsonElement Data(JsonDocument doc) => doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object ? data : throw new InvalidDataException("AppRJ 响应缺少 data 对象。");
    internal static string Text(JsonElement item, string key)
    {
        if (item.ValueKind != JsonValueKind.Object) throw new InvalidDataException("接口条目不是对象。");
        if (!item.TryGetProperty(key,out var value) || value.ValueKind == JsonValueKind.Null) return "";
        if (value.ValueKind is not (JsonValueKind.String or JsonValueKind.Number)) throw new InvalidDataException("接口字段不是标量：" + key);
        return value.ToString();
    }
    private static JsonElement List(JsonElement data) => data.TryGetProperty("list",out var list) && list.ValueKind == JsonValueKind.Array ? list : throw new InvalidDataException("AppRJ 响应缺少 list 数组。");
    internal static MediaItem Item(JsonElement vod) => new() { Id=Text(vod,"vod_id"), Title=Text(vod,"vod_name"), Poster=Text(vod,"vod_pic") is {Length:>0} pic ? pic : Text(vod,"vod_pic_thumb"), Remarks=Text(vod,"vod_remarks"), Year=Text(vod,"vod_year") };
    public async Task<IReadOnlyList<Category>> GetCategoriesAsync(CancellationToken ct = default)
    {
        using var doc=await Request("/v3/type/top_type",[],ct);
        return List(Data(doc)).EnumerateArray().Select(item=>new Category(Text(item,"type_id"),Text(item,"type_name"))).Where(item=>item.Id.Length>0).ToArray();
    }
    public async Task<IReadOnlyList<FilterGroup>> GetFiltersAsync(string categoryId,CancellationToken ct=default)
    {
        using var doc=await Request("/v3/type/top_type",[],ct);
        var category=List(Data(doc)).EnumerateArray().FirstOrDefault(item=>Text(item,"type_id")==categoryId);
        if(category.ValueKind!=JsonValueKind.Object)return [];
        var groups=new List<FilterGroup>();
        foreach(var (field,label) in new[]{("extend","类型"),("area","地区"),("year","年份"),("lang","语言")})
            if(category.TryGetProperty(field,out var values)&&values.ValueKind==JsonValueKind.Array)
                groups.Add(new FilterGroup(field=="extend"?"class":field,label,values.EnumerateArray().Select(v=>new FilterValue(v.ToString(),v.ToString())).ToList(),""));
        return groups;
    }
    public async Task<MediaPage> GetHomeAsync(CancellationToken ct=default)
    { var categories=await GetCategoriesAsync(ct);return categories.Count==0?new MediaPage([],1,1):await GetItemsAsync(categories[0].Id,1,null,ct); }
    public Task<MediaPage> GetItemsAsync(string categoryId,int page,IReadOnlyDictionary<string,string>? filters,CancellationToken ct=default)
    {
        var fields=new Dictionary<string,string>{{"type_id",categoryId},{"page",page.ToString(CultureInfo.InvariantCulture)},{"limit","12"}};
        foreach(var key in new[]{"area","class","lang","year"})if(filters?.TryGetValue(key,out var value)==true)fields[key]=value;
        return Page("/v3/home/type_search",fields,page,ct);
    }
    public Task<MediaPage> SearchAsync(string query,int page,CancellationToken ct=default)=>Page("/v3/home/search",new(){{"keyword",query},{"page",page.ToString(CultureInfo.InvariantCulture)},{"limit","12"}},page,ct);
    private async Task<MediaPage> Page(string path,Dictionary<string,string> fields,int page,CancellationToken ct)
    {
        if(page<1)throw new InvalidDataException("页码无效。");
        using var doc=await Request(path,fields,ct);var data=Data(doc);
        var items=List(data).EnumerateArray().Select(Item).Where(item=>item.Id.Length>0).ToArray();
        var count=int.TryParse(Text(data,"pagecount"),out var total)?Math.Max(page,total):items.Length==12?page+1:page;
        return new MediaPage(items,page,count);
    }
    private async Task<JsonDocument> Detail(string id,CancellationToken ct)=>await Request("/v3/home/vod_details",new(){{"vod_id",id}},ct);
    private static IReadOnlyList<PlaybackLine> Lines(JsonElement data)
    {
        if(!data.TryGetProperty("vod_play_list",out var lines)||lines.ValueKind!=JsonValueKind.Array)throw new InvalidDataException("AppRJ 缺少播放线路。");
        return lines.EnumerateArray().Select((line,i)=>new PlaybackLine("rj:"+i,Text(line,"name"),EpisodeArray(line).EnumerateArray().Select((ep,j)=>new Episode("rj:"+i+":"+j,Text(ep,"name"))).ToArray())).ToArray();
    }
    private static JsonElement EpisodeArray(JsonElement line)
    {
        if (line.ValueKind != JsonValueKind.Object || !line.TryGetProperty("urls",out var urls) || urls.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("AppRJ 线路缺少 urls 数组。");
        return urls;
    }
    public async Task<MediaDetail> GetDetailAsync(string mediaId,CancellationToken ct=default)
    { using var doc=await Detail(mediaId,ct);var data=Data(doc);return new MediaDetail{Item=Item(data) with{Id=mediaId},Description=Text(data,"vod_content"),Actor=Text(data,"vod_actor"),Director=Text(data,"vod_director"),Lines=Lines(data)}; }
    public async Task<PlaybackRequest> ResolvePlaybackAsync(string mediaId,string episodeId,CancellationToken ct=default)
    {
        using var doc=await Detail(mediaId,ct);var data=Data(doc);var lines=Lines(data);
        var i=-1;var j=-1;
        for(var a=0;a<lines.Count;a++)for(var b=0;b<lines[a].Episodes.Count;b++)if(lines[a].Episodes[b].Id==episodeId){i=a;j=b;}
        if(i<0)throw new InvalidDataException("AppRJ 选集不存在。");
        var line=data.GetProperty("vod_play_list")[i];var ep=line.GetProperty("urls")[j];var url=Text(ep,"url");var ua=Text(line,"ua");
        if(line.TryGetProperty("parse_urls",out var parsers)&&parsers.ValueKind==JsonValueKind.Array&&parsers.GetArrayLength()>0)
        {
            var found=false;
            foreach(var parser in parsers.EnumerateArray().Take(3))
            {
                var prefix=parser.GetString();if(string.IsNullOrWhiteSpace(prefix))continue;
                var time=_clock();var endpoint=prefix+url+"&sign="+Sign(time)+"&timestamp="+time.ToString(CultureInfo.InvariantCulture);
                if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var parserAddress) || parserAddress.Scheme is not ("http" or "https") || parserAddress.UserInfo.Length > 0) continue;
                try { using var result=JsonDocument.Parse(await _parseFetch(endpoint,ct)); ct.ThrowIfCancellationRequested();var parsed=Text(result.RootElement,"url");if(Uri.TryCreate(parsed,UriKind.Absolute,out var uri)&&uri.Scheme is "http" or "https"){url=parsed;ua=Text(result.RootElement,"UA") is {Length:>0} agent?agent:ua;found=true;break;} }
                catch(Exception error)when(error is not OperationCanceledException){ }
            }
            if(!found)throw new InvalidDataException("AppRJ 解析线路均失败。");
        }
        if(!Uri.TryCreate(url,UriKind.Absolute,out var address)||address.Scheme is not("http" or "https")||address.UserInfo.Length>0)throw new InvalidDataException("AppRJ 返回无效播放地址。");
        if(ua.IndexOfAny(['\r','\n','\0'])>=0)throw new InvalidDataException("AppRJ UA 包含非法字符。");
        return new PlaybackRequest{Uri=url,SourceKey=Key,SourceName=Name,MediaId=mediaId,LineId=lines[i].Id,EpisodeId=episodeId,Title=Text(data,"vod_name")+" · "+Text(ep,"name"),Headers=ua.Length>0?new(){{"User-Agent",ua}}:new()};
    }
}
