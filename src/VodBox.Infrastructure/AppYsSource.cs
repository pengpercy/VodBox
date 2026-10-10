using System.Globalization;
using System.Text.Json;
using VodBox.Core;

namespace VodBox.Infrastructure;

/// <summary>AppYs api.php/app 与 xgapp JSON 分支；不实现 .vod 与站点特定网页嗅探。</summary>
public sealed class AppYsSource : IResolvingContentSource
{
    private readonly SourceInfo _site;
    private readonly Uri _base;
    private readonly Func<string,CancellationToken,Task<string>> _fetch;
    public string Key=>_site.Key;
    public string Name=>_site.Name;
    public AppYsSource(SourceInfo site):this(site,null){}
    internal AppYsSource(SourceInfo site,Func<string,CancellationToken,Task<string>>? fetch)
    {
        _site=site;
        if(!Uri.TryCreate(site.Ext,UriKind.Absolute,out var endpoint)||endpoint.Scheme is not("http" or "https")||endpoint.UserInfo.Length>0||endpoint.Query.Length>0||!(endpoint.AbsolutePath.Contains("api.php/app",StringComparison.Ordinal)||endpoint.AbsolutePath.Contains("xgapp",StringComparison.Ordinal)))
            throw new NotSupportedException("AppYs 当前仅支持 ext 为 api.php/app 或 xgapp HTTP(S) 接口地址。");
        _base=new Uri(endpoint.AbsoluteUri.TrimEnd('/')+"/");
        _fetch=fetch??Fetch;
    }
    private static async Task<string> Fetch(string url,CancellationToken ct)
    {using var http=new DefaultHttp();return DefaultHttp.Decode(await http.GetBoundedAsync(url,8*1024*1024,ct));}
    private async Task<JsonDocument> Request(string path,IReadOnlyDictionary<string,string> args,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var url=new Uri(_base,path).AbsoluteUri+"?"+string.Join("&",args.Select(p=>Uri.EscapeDataString(p.Key)+"="+Uri.EscapeDataString(p.Value)));
        using var scope=CancellationTokenSource.CreateLinkedTokenSource(ct);scope.CancelAfter(TimeSpan.FromSeconds(20));
        var text=await _fetch(url,scope.Token);scope.Token.ThrowIfCancellationRequested();return JsonDocument.Parse(text);
    }
    private static JsonElement Payload(JsonDocument doc)=>doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("data",out var data)?data:doc.RootElement;
    private static JsonElement List(JsonElement root)
    {
        if(root.ValueKind==JsonValueKind.Array)return root;
        if(root.ValueKind==JsonValueKind.Object&&root.TryGetProperty("list",out var list)&&list.ValueKind==JsonValueKind.Array)return list;
        throw new InvalidDataException("AppYs 响应缺少列表数组。");
    }
    public async Task<IReadOnlyList<Category>> GetCategoriesAsync(CancellationToken ct=default)
    {
        using var doc=await Request("nav",new Dictionary<string,string>{{"token",""}},ct);
        return List(Payload(doc)).EnumerateArray().Select(item=>new Category(AppRjSource.Text(item,"type_id"),AppRjSource.Text(item,"type_name"))).Where(c=>c.Id.Length>0).ToArray();
    }
    public Task<MediaPage> GetHomeAsync(CancellationToken ct=default)=>Page("index_video",new(){{"token",""}},1,ct);
    public Task<MediaPage> GetItemsAsync(string categoryId,int page,IReadOnlyDictionary<string,string>? filters,CancellationToken ct=default)
    {
        var args=new Dictionary<string,string>{{"tid",categoryId},{"pg",page.ToString(CultureInfo.InvariantCulture)},{"limit","18"}};
        foreach(var key in new[]{"class","area","lang","year"})if(filters?.TryGetValue(key,out var value)==true)args[key]=value;
        return Page("video",args,page,ct);
    }
    public Task<MediaPage> SearchAsync(string query,int page,CancellationToken ct=default)=>Page("search",new(){{"text",query},{"pg",page.ToString(CultureInfo.InvariantCulture)}},page,ct);
    private async Task<MediaPage> Page(string path,Dictionary<string,string> args,int page,CancellationToken ct)
    {
        if(page<1)throw new InvalidDataException("AppYs 页码无效。");
        using var doc=await Request(path,args,ct);var data=Payload(doc);
        var list=List(data);var items=new List<MediaItem>();
        foreach(var item in list.EnumerateArray())
        {
            // index_video 可返回分组的 vlist，其余端点为视频条目。
            if(item.ValueKind != JsonValueKind.Object) throw new InvalidDataException("AppYs 列表条目不是对象。");
            if(item.TryGetProperty("vlist",out var nested)&&nested.ValueKind==JsonValueKind.Array)items.AddRange(nested.EnumerateArray().Select(AppRjSource.Item));
            else items.Add(AppRjSource.Item(item));
        }
        var count=doc.RootElement.TryGetProperty("totalpage",out var total)&&int.TryParse(total.ToString(),out var parsed)?parsed:items.Count>=18?page+1:page;
        return new MediaPage(items.Where(i=>i.Id.Length>0).ToArray(),page,Math.Max(page,count));
    }
    private async Task<JsonDocument> Detail(string id,CancellationToken ct)=>await Request("video_detail",new Dictionary<string,string>{{"id",id}},ct);
    private JsonElement Vod(JsonDocument doc)
    {
        var root=Payload(doc);
        if(_base.AbsolutePath.Contains("xgapp",StringComparison.Ordinal)&&root.TryGetProperty("vod_info",out var nested))root=nested;
        if(root.ValueKind!=JsonValueKind.Object)throw new InvalidDataException("AppYs 详情为空。");
        return root;
    }
    private static MediaDetail Map(JsonElement vod,string id)
    {
        if(!vod.TryGetProperty("vod_url_with_player",out var raw)||raw.ValueKind!=JsonValueKind.Array)throw new InvalidDataException("AppYs 详情缺少播放线路。");
        var from=new List<string>();var urls=new List<string>();
        foreach(var line in raw.EnumerateArray()){from.Add(AppRjSource.Text(line,"name"));urls.Add(AppRjSource.Text(line,"url"));}
        return MacCmsSource.ToDetail(new MacCmsVod{VodId=id,VodName=AppRjSource.Text(vod,"vod_name"),VodPic=AppRjSource.Text(vod,"vod_pic"),VodRemarks=AppRjSource.Text(vod,"vod_remarks"),VodContent=AppRjSource.Text(vod,"vod_content"),VodActor=AppRjSource.Text(vod,"vod_actor"),VodDirector=AppRjSource.Text(vod,"vod_director"),VodPlayFrom=string.Join("$$$",from),VodPlayUrl=string.Join("$$$",urls)});
    }
    public async Task<MediaDetail> GetDetailAsync(string mediaId,CancellationToken ct=default)
    {using var doc=await Detail(mediaId,ct);return Map(Vod(doc),mediaId);}
    public async Task<PlaybackRequest> ResolvePlaybackAsync(string mediaId,string episodeId,CancellationToken ct=default)
    {
        using var doc=await Detail(mediaId,ct);var vod=Vod(doc);var detail=Map(vod,mediaId);
        foreach(var line in detail.Lines)
        foreach(var episode in line.Episodes)
            if(episode.Id==episodeId)
            {
                var raw=vod.GetProperty("vod_url_with_player").EnumerateArray().FirstOrDefault(item=>AppRjSource.Text(item,"name")==line.Name);
                var parser=raw.ValueKind==JsonValueKind.Object?AppRjSource.Text(raw,"parse_api"):"";
                if(parser.Length==0&&(!Uri.TryCreate(episode.Uri,UriKind.Absolute,out var address)||!new[]{".mp4",".m3u8",".mpd",".flv",".mp3",".m4a",".mkv",".webm"}.Any(ext=>address.AbsolutePath.EndsWith(ext,StringComparison.OrdinalIgnoreCase))))
                    throw new NotSupportedException("AppYs 该线路需要网页嗅探，桌面端尚未支持。");
                return new PlaybackRequest{Uri=episode.Uri!,Title=detail.Item.Title+" · "+episode.Title,SourceKey=Key,SourceName=Name,MediaId=mediaId,LineId=line.Id,EpisodeId=episode.Id,Resolution=parser.Length>0?ResolutionKind.Json:ResolutionKind.Direct,ParseEndpoint=parser.Length>0?parser:null};
            }
        throw new InvalidDataException("AppYs 选集不存在，请刷新详情。");
    }
}
