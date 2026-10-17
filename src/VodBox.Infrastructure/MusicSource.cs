using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using VodBox.Core;

namespace VodBox.Infrastructure;

/// <summary>4c44 易听音乐网页、csrf 会话和 play.php 音频协议。</summary>
public sealed class MusicSource : IResolvingContentSource,IDisposable
{
    private const string DefaultOrigin="http://www.4c44.com";
    private const string Agent="Mozilla/5.0 (Windows NT 10.0; WOW64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/86.0.4240.198 Safari/537.36";
    private readonly SourceInfo _site;
    private readonly string _origin;
    private readonly HttpClient _http;
    private readonly Func<string,IReadOnlyDictionary<string,string>?,CancellationToken,Task<string>> _send;
    private readonly Func<DateTimeOffset> _clock;
    public string Key=>_site.Key;
    public string Name=>_site.Name;
    public MusicSource(SourceInfo site):this(site,null,null){}
    internal MusicSource(SourceInfo site,Func<string,IReadOnlyDictionary<string,string>?,CancellationToken,Task<string>>? send,Func<DateTimeOffset>? clock=null)
    {
        _site=site;_origin=string.IsNullOrWhiteSpace(site.Ext)?DefaultOrigin:site.Ext.TrimEnd('/');if(!Supports(_origin))throw new InvalidDataException("Music ext 必须是 HTTP(S) 基地址。");
        _http=new HttpClient(new HttpClientHandler{AutomaticDecompression=DecompressionMethods.All,UseCookies=true,CookieContainer=new CookieContainer()}){Timeout=TimeSpan.FromSeconds(20)};
        _send=send??Send;_clock=clock??(()=>DateTimeOffset.Now);
    }
    internal static bool Supports(string? ext)=>string.IsNullOrWhiteSpace(ext)||(Uri.TryCreate(ext,UriKind.Absolute,out var uri)&&(uri.Scheme is "http" or "https")&&uri.UserInfo.Length==0&&uri.Query.Length==0&&uri.Fragment.Length==0);
    private async Task<string> Send(string url,IReadOnlyDictionary<string,string>? fields,CancellationToken ct)
    {
        using var request=new HttpRequestMessage(fields==null?HttpMethod.Get:HttpMethod.Post,url);
        request.Headers.TryAddWithoutValidation("User-Agent",Agent);request.Headers.TryAddWithoutValidation("Referer",_origin);request.Headers.TryAddWithoutValidation("Accept","text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8,application/signed-exchange;v=b3;q=0.7");
        if(fields!=null)request.Content=new FormUrlEncodedContent(fields);
        using var response=await _http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);response.EnsureSuccessStatusCode();await using var stream=await response.Content.ReadAsStreamAsync(ct);return DefaultHttp.Decode(await BoundedContent.ReadAsync(stream,8*1024*1024,ct));
    }
    private string Address(string path)
    {
        if(!Uri.TryCreate(new Uri(_origin+"/"),path,out var uri)||uri.Scheme is not("http" or "https")||uri.UserInfo.Length>0||uri.Authority!=new Uri(_origin).Authority)throw new InvalidDataException("Music 页面地址无效。");return uri.AbsoluteUri;
    }
    private async Task<string> Page(string path,CancellationToken ct)
    {
        var address=Address(path);var html=await _send(address,null,ct);
        if(html.Contains("csrf_token",StringComparison.Ordinal)&&html.Contains("human_check",StringComparison.Ordinal))
        {
            var root=Html.Parse(html);var token=root.All("input").FirstOrDefault(node=>node.Attribute("name")=="csrf_token")?.Attribute("value");
            if(string.IsNullOrEmpty(token))throw new InvalidDataException("Music 人机确认缺少 csrf_token。");
            await _send(_origin,new Dictionary<string,string>{{"csrf_token",token},{"human_check","on"}},ct);
            html=await _send(address,null,ct);if(html.Contains("csrf_token",StringComparison.Ordinal)&&html.Contains("human_check",StringComparison.Ordinal))throw new InvalidDataException("Music 人机确认未通过。");
        }
        return html;
    }
    public async Task<IReadOnlyList<Category>> GetCategoriesAsync(CancellationToken ct=default)
    {
        var root=Html.Parse(await Page("/",ct));var result=new List<Category>();
        foreach(var container in root.Class("pull-right"))foreach(var link in container.All("a"))
        {
            var href=link.Attribute("href");if(!href.EndsWith(".html",StringComparison.Ordinal))continue;
            var name=link.Attribute("title");if(name.Length==0)name=link.Text;
            result.Add(new(href.Replace(".html","",StringComparison.Ordinal),name));
        }
        return result.DistinctBy(value=>value.Id).ToArray();
    }
    public Task<IReadOnlyList<FilterGroup>> GetFiltersAsync(string categoryId,CancellationToken ct=default){ct.ThrowIfCancellationRequested();return Task.FromResult<IReadOnlyList<FilterGroup>>([]);}
    public async Task<MediaPage> GetHomeAsync(CancellationToken ct=default)=>new(List(Html.Parse(await Page("/",ct))),1,1);
    public async Task<MediaPage> GetItemsAsync(string categoryId,int page,IReadOnlyDictionary<string,string>? filters,CancellationToken ct=default)
    {
        if(page<1)throw new InvalidDataException("页码无效。");var items=List(Html.Parse(await Page(categoryId+"/"+page.ToString(CultureInfo.InvariantCulture)+".html",ct)));return new(items,page,items.Count>0?page+1:page);
    }
    public Task<MediaPage> SearchAsync(string query,int page,CancellationToken ct=default)
    {
        ct.ThrowIfCancellationRequested();if(page<1)throw new InvalidDataException("页码无效。");
        return Task.FromResult(new MediaPage(page==1?[new MediaItem{Id="/so/"+Uri.EscapeDataString(query)+".html",Title=query,Remarks="歌星/歌名搜索"}]:[],page,page));
    }
    private IReadOnlyList<MediaItem> List(Html root)
    {
        var result=new List<MediaItem>();
        foreach(var name in new[]{"lksinger_list","singer_list","video_list","ilingku_fl"})
            foreach(var container in root.Class(name))foreach(var item in container.All("li"))
            {
                var anchor=item.Class("name").SelectMany(node=>node.All("a")).FirstOrDefault()??item.All("a").FirstOrDefault();if(anchor==null)continue;
                var href=anchor.Attribute("href");if(href.Length==0)continue;var title=anchor.Text;
                result.Add(new(){Id=href,Title=title,Poster=item.All("img").FirstOrDefault()?.Attribute("src")});
            }
        return result.DistinctBy(item=>item.Id).ToArray();
    }
    public async Task<MediaDetail> GetDetailAsync(string mediaId,CancellationToken ct=default)
    {
        var root=Html.Parse(await Page(mediaId,ct));var container=root.Class("play_list").FirstOrDefault()??throw new InvalidDataException("Music 页面缺少 play_list。");
        var title=container.All("h1").FirstOrDefault()?.Text.Split(',')[0]??Name;var episodes=new List<Episode>();
        foreach(var item in container.All("li"))
        {
            var anchor=item.Class("name").SelectMany(node=>node.All("a")).FirstOrDefault();if(anchor==null)continue;
            var href=anchor.Attribute("href");if(href.Length>0)episodes.Add(new("易听\u0001"+href,anchor.Text,href));
            var video=item.Class("mv").SelectMany(node=>node.All("a")).FirstOrDefault();if(video!=null&&video.Attribute("href") is {Length:>0} path)episodes.Add(new("易听\u0001"+path,anchor.Text+"-MTV",path));
        }
        var poster=root.Class("play_left").SelectMany(node=>node.All("img")).FirstOrDefault()?.Attribute("src");
        return new(){Item=new(){Id=mediaId,Title=title,Poster=poster},Actor="易听",Director="易听",Lines=episodes.Count>0?[new PlaybackLine("易听","易听",episodes)]:[]};
    }
    public async Task<PlaybackRequest> ResolvePlaybackAsync(string mediaId,string episodeId,CancellationToken ct=default)
    {
        var detail=await GetDetailAsync(mediaId,ct);var episode=detail.Lines.SelectMany(line=>line.Episodes).FirstOrDefault(value=>value.Id==episodeId)??throw new InvalidDataException("Music 选集不存在。");var play=episode.Uri??"";
        var file=play[(play.LastIndexOf('/')+1)..];if(!file.EndsWith(".html",StringComparison.Ordinal))throw new InvalidDataException("Music 歌曲页标识无效。");var id=file[..^5];string url;
        if(play.Contains("mp4",StringComparison.Ordinal))url="http://www.4c44.com/data/down.php?ac=vplay&id="+Uri.EscapeDataString(id)+"&q=1080";
        else
        {
            if(play.Contains("radio",StringComparison.Ordinal))id=Regex.Match(await Page(play,ct),"\"music\",\"(.*?)\"",RegexOptions.CultureInvariant,TimeSpan.FromSeconds(2)).Groups[1].Value;
            if(id.Length==0)throw new InvalidDataException("Music 歌曲 ID 无效。");
            var key=_clock().ToString("yyyyMMddHHmm",CultureInfo.InvariantCulture)+"4c44";
            // Java SN 在字段初始化时绑定默认域名，init(ext) 不会重建 SN；保留默认端点。
            using var doc=JsonDocument.Parse(await _send(DefaultOrigin+"/js/play.php",new Dictionary<string,string>{{"id",id},{"type","music"},{"key",key}},ct));url=AppRjSource.Text(doc.RootElement,"url");
        }
        if(!Uri.TryCreate(url,UriKind.Absolute,out var uri)||uri.Scheme is not("http" or "https")||uri.UserInfo.Length>0)throw new InvalidDataException("Music 播放地址无效。");
        return new(){Uri=url,SourceKey=Key,SourceName=Name,MediaId=mediaId,LineId="易听",EpisodeId=episodeId,Title=detail.Item.Title+" · "+episode.Title,Headers=new(){{"User-Agent",Agent},{"Referer",_origin}}};
    }
    public void Dispose()=>_http.Dispose();

    // 本站只需有限的标签/属性遍历；不执行页面脚本。
    private sealed class Html
    {
        public string Tag="";public string Content="";public Dictionary<string,string> Attributes=new(StringComparer.OrdinalIgnoreCase);public List<Html> Children=[];
        public string Attribute(string key)=>Attributes.TryGetValue(key,out var value)?value:"";
        public string Text=>Regex.Replace(WebUtility.HtmlDecode(Content+string.Concat(Children.Select(child=>child.Text))),"\\s+"," ",RegexOptions.CultureInvariant,TimeSpan.FromSeconds(1)).Trim();
        public IEnumerable<Html> All(string tag)=>Children.SelectMany(child=>new[]{child}.Concat(child.All(tag))).Where(child=>child.Tag.Equals(tag,StringComparison.OrdinalIgnoreCase));
        public IEnumerable<Html> Class(string name)=>Children.SelectMany(child=>new[]{child}.Concat(child.Descendants())).Where(child=>child.Attribute("class").Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries).Contains(name,StringComparer.Ordinal));
        private IEnumerable<Html> Descendants()=>Children.SelectMany(child=>new[]{child}.Concat(child.Descendants()));
        public static Html Parse(string html)
        {
            var root=new Html();var stack=new Stack<Html>();stack.Push(root);
            var tokens=Regex.Matches(html,"<!--.*?-->|<[^>]+>|[^<]+",RegexOptions.Singleline|RegexOptions.CultureInvariant,TimeSpan.FromSeconds(3));
            foreach(Match token in tokens)
            {
                var text=token.Value;if(text.StartsWith("<!",StringComparison.Ordinal)||text.StartsWith("<?",StringComparison.Ordinal))continue;
                if(!text.StartsWith('<')){stack.Peek().Content+=text;continue;}
                var tag=Regex.Match(text,"^</?\\s*([A-Za-z0-9]+)",RegexOptions.CultureInvariant,TimeSpan.FromSeconds(1)).Groups[1].Value.ToLowerInvariant();if(tag.Length==0)continue;
                if(text.StartsWith("</",StringComparison.Ordinal)){if(stack.Any(node=>node.Tag==tag)){while(stack.Count>1){if(stack.Pop().Tag==tag)break;}}continue;}
                if(tag=="li"&&stack.Peek().Tag=="li")stack.Pop();
                var node=new Html{Tag=tag};
                foreach(Match attr in Regex.Matches(text,"([\\w:-]+)\\s*=\\s*(?:\"([^\"]*)\"|'([^']*)'|([^\\s>]+))",RegexOptions.CultureInvariant,TimeSpan.FromSeconds(1)))node.Attributes[attr.Groups[1].Value]=WebUtility.HtmlDecode(attr.Groups[2].Success?attr.Groups[2].Value:attr.Groups[3].Success?attr.Groups[3].Value:attr.Groups[4].Value);
                stack.Peek().Children.Add(node);if(!text.EndsWith("/>",StringComparison.Ordinal)&&!new[]{"img","input","br","hr","meta","link","source","area","base","embed","wbr"}.Contains(tag,StringComparer.Ordinal))stack.Push(node);
            }
            return root;
        }
    }
}
