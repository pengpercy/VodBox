using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using VodBox.Core;

namespace VodBox.Infrastructure;

/// <summary>豆包直播公开赛事及回放接口，按固定 Java 插件协议重写。</summary>
public sealed class DoubaoSource : IResolvingContentSource
{
    private const string Origin="https://www.doubaozhibo.com";
    private const string Agent="Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36";
    private readonly SourceInfo _site;
    private readonly string _origin;
    private readonly Func<string,IReadOnlyDictionary<string,string>,CancellationToken,Task<string>> _fetch;
    public string Key=>_site.Key;
    public string Name=>_site.Name;
    public DoubaoSource(SourceInfo site):this(site,null){}
    internal DoubaoSource(SourceInfo site,Func<string,IReadOnlyDictionary<string,string>,CancellationToken,Task<string>>? fetch)
    {
        _site=site;_origin=string.IsNullOrWhiteSpace(site.Ext)?Origin:site.Ext.TrimEnd('/');
        if(!Supports(_origin))throw new InvalidDataException("Doubao ext 必须是 HTTP(S) 基地址。");_fetch=fetch??Fetch;
    }
    internal static bool Supports(string? ext)=>string.IsNullOrWhiteSpace(ext)||(Uri.TryCreate(ext,UriKind.Absolute,out var uri)&&(uri.Scheme is "http" or "https")&&uri.UserInfo.Length==0&&uri.Query.Length==0&&uri.Fragment.Length==0);
    private static Dictionary<string,string> Headers()=>new(){{"User-Agent",Agent},{"Accept","application/json"},{"Accept-Language","zh-CN"}};
    private static async Task<string> Fetch(string url,IReadOnlyDictionary<string,string> headers,CancellationToken ct){using var http=new DefaultHttp(false);return DefaultHttp.Decode(await http.GetBoundedAsync(url,8*1024*1024,ct,headers));}
    private async Task<JsonDocument> Request(string path,CancellationToken ct)=>JsonDocument.Parse(await _fetch(_origin+path,Headers(),ct));
    private static string Text(JsonElement value,string key)=>AppRjSource.Text(value,key);
    private static int Number(JsonElement value,string key,int fallback=0)=>int.TryParse(Text(value,key),out var number)?number:fallback;
    private static IEnumerable<JsonElement> Array(JsonElement obj,string key)=>obj.TryGetProperty(key,out var array)&&array.ValueKind==JsonValueKind.Array?array.EnumerateArray():Enumerable.Empty<JsonElement>();
    private static IEnumerable<JsonElement> Schedules(JsonDocument doc)=>Array(doc.RootElement.GetProperty("data"),"days").SelectMany(day=>Array(day,"live").Concat(Array(day,"playback")));
    private static string Date(string value)=>DateTimeOffset.TryParse(value,CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal,out var time)?time.ToOffset(TimeSpan.FromHours(8)).ToString("MM-dd HH:mm",CultureInfo.InvariantCulture):value;
    private static MediaItem Match(JsonElement schedule,string id,bool replay=false)=>new(){Id=id,Title=Text(schedule,"teamA")+(replay?" "+Number(schedule,"teamAscore")+"-"+Number(schedule,"teamBscore")+" ":" vs ")+Text(schedule,"teamB"),Poster=Text(schedule,"teamAImage"),Remarks=Date(Text(schedule,"matchTime"))+" | "+Text(schedule,"league")+" | "+(replay?"回放":Status(Number(schedule,"status")))};
    private static string Status(int status)=>status==1?"直播中":status==2?"已结束":"未开始";
    public Task<IReadOnlyList<Category>> GetCategoriesAsync(CancellationToken ct=default){ct.ThrowIfCancellationRequested();return Task.FromResult<IReadOnlyList<Category>>([new("live","📡 赛事直播"),new("replay_football","⚽ 足球回放"),new("replay_basketball","🏀 篮球回放"),new("replay_all","🎬 全部回放")]);}
    public Task<IReadOnlyList<FilterGroup>> GetFiltersAsync(string categoryId,CancellationToken ct=default){ct.ThrowIfCancellationRequested();return Task.FromResult<IReadOnlyList<FilterGroup>>([]);}
    public Task<MediaPage> GetHomeAsync(CancellationToken ct=default)=>GetItemsAsync("live",1,null,ct);
    public async Task<MediaPage> GetItemsAsync(string categoryId,int page,IReadOnlyDictionary<string,string>? filters,CancellationToken ct=default)
    {
        if(page<1)throw new InvalidDataException("页码无效。");
        if(categoryId.StartsWith("live",StringComparison.Ordinal))
        {
            if(page>1)return new([],page,page);
            using var doc=await Request("/api/v1/schedules/public/local",ct);
            var entries=Array(doc.RootElement.GetProperty("data"),"days").SelectMany(day=>Array(day,"live")).Where(match=>Number(match,"playbackId")<=0).OrderBy(match=>Number(match,"status")==1?0:Number(match,"status")==0?1:2).ThenBy(match=>Text(match,"matchTime"),StringComparer.Ordinal).Select(match=>Match(match,"live###"+Text(match,"id"))).ToArray();
            return new(entries,1,1);
        }
        var type=categoryId switch{"replay_football"=>"football","replay_basketball"=>"basketball","replay_all"=>"all",_=>throw new InvalidDataException("Doubao 分类无效。")};
        using var replay=await Request("/api/v1/playbacks?page="+page.ToString(CultureInfo.InvariantCulture)+"&pageSize=20&dataType="+type,ct);
        var data=replay.RootElement.GetProperty("data");var items=Array(data,"list").Select(value=>Match(value.GetProperty("schedule"),"replay###"+Text(value.GetProperty("playback"),"id"),true)).ToArray();
        return new(items,page,Math.Max(page,(Number(data,"total")+19)/20));
    }
    public Task<MediaPage> SearchAsync(string query,int page,CancellationToken ct=default){ct.ThrowIfCancellationRequested();return Task.FromResult(new MediaPage([],page,page));}
    private async Task<(JsonElement Schedule,JsonElement Playback)> Find(string mediaId,CancellationToken ct)
    {
        var parts=mediaId.Split("###",StringSplitOptions.None);if(parts.Length!=2||parts[1].Length==0)throw new InvalidDataException("Doubao 媒体标识无效。");
        if(parts[0]=="live")
        {
            using var doc=await Request("/api/v1/schedules/public/local",ct);var match=Schedules(doc).FirstOrDefault(value=>Text(value,"id")==parts[1]);if(match.ValueKind==JsonValueKind.Object)return(match.Clone(),default);
        }
        else if(parts[0]=="replay")
        {
            for(var page=1;page<=5;page++)
            {
                using var doc=await Request("/api/v1/playbacks?page="+page.ToString(CultureInfo.InvariantCulture)+"&pageSize=20&dataType=all",ct);var data=doc.RootElement.GetProperty("data");
                var list=Array(data,"list").ToArray();var match=list.FirstOrDefault(value=>Text(value.GetProperty("playback"),"id")==parts[1]);
                if(match.ValueKind==JsonValueKind.Object)return(match.GetProperty("schedule").Clone(),match.GetProperty("playback").Clone());
                if(list.Length==0||page*Math.Max(1,Number(data,"pageSize",20))>=Number(data,"total"))break;
            }
        }
        throw new InvalidDataException("Doubao 赛事已不在公开列表中。");
    }
    public async Task<MediaDetail> GetDetailAsync(string mediaId,CancellationToken ct=default)
    {
        var(schedule,playback)=await Find(mediaId,ct);var replay=playback.ValueKind==JsonValueKind.Object;var flag=replay?"回放线路":"直播线路";
        var episodes=Array(replay?playback:schedule,replay?"lines":"signals").Select(value=>
        {
            var raw=Text(value,replay?"proxyUrl":"playId");if(replay&&raw.StartsWith("/",StringComparison.Ordinal))raw=_origin+raw;
            return new Episode(flag+"\u0001"+raw,Text(value,replay?"title":"name"),raw);
        }).ToArray();
        return new(){Item=Match(schedule,mediaId,replay),Description="请勿相信视频中广告",Lines=episodes.Length>0?[new PlaybackLine(flag,flag,episodes)]:[]};
    }
    public async Task<PlaybackRequest> ResolvePlaybackAsync(string mediaId,string episodeId,CancellationToken ct=default)
    {
        var detail=await GetDetailAsync(mediaId,ct);var line=detail.Lines.FirstOrDefault(value=>value.Episodes.Any(ep=>ep.Id==episodeId));var episode=line?.Episodes.FirstOrDefault(value=>value.Id==episodeId);if(episode?.Uri is not {Length:>0} play)throw new InvalidDataException("Doubao 选集不存在。");
        var url=play;
        if(!play.StartsWith("http",StringComparison.Ordinal))
        {
            var html=(await _fetch(_origin+"/play/"+Uri.EscapeDataString(play),Headers(),ct)).Replace("\\u002F","/",StringComparison.Ordinal);
            var matches=Regex.Matches(html,"(https?://[\\w.\\-/:?=&]+\\.m3u8[^,\"\\s]*)",RegexOptions.CultureInvariant,TimeSpan.FromSeconds(2)).Select(m=>m.Groups[1].Value.TrimEnd(',', '"')).ToArray();
            url=matches.FirstOrDefault(value=>new[]{"rtmpcdn","live.","pul-","gkykp"}.Any(value.Contains))??matches.FirstOrDefault()??throw new InvalidDataException("Doubao 播放页未返回 m3u8。");
        }
        if(!Uri.TryCreate(url,UriKind.Absolute,out var uri)||uri.Scheme is not("http" or "https")||uri.UserInfo.Length>0)throw new InvalidDataException("Doubao 播放地址无效。");
        return new(){Uri=url,SourceKey=Key,SourceName=Name,MediaId=mediaId,LineId=line!.Id,EpisodeId=episodeId,Title=detail.Item.Title+" · "+episode.Title,Headers=Headers()};
    }
}
