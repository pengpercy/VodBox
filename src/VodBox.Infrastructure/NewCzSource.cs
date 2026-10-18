using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using VodBox.Core;

namespace VodBox.Infrastructure;

/// <summary>NewCz anonymous HTML and dncry AES playback. Account and LAN parser branches are intentionally excluded.</summary>
public sealed class NewCzSource : IResolvingContentSource
{
    // Java yq() 的实际取值：Android Chromecast(CrKey) UA + Accept-language，而非通用桌面 UA。
    private const string Agent="Mozilla/5.0 (Linux; Android) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/148.0.0.0 Safari/537.36 CrKey/1.54.248666";
    // Java 播放头单独使用桌面 UA（见 playerContent 的 user-agent）。
    private const string CzzyAgent="Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/148.0.0.0 Safari/537.36";
    private const string PlayerAgent="Mozilla/5.0 (X11; Linux aarch64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/148.0.0.0 Safari/537.36";
    // 站点前置雷池(SafeLine) WAF：403 响应会下发 sl-session Cookie，Java 插件将其持久化为 czCookie 复用，
    // 故这里共用带 CookieContainer 的客户端，让 WAF 会话 Cookie 跨请求保留。
    private static readonly CookieContainer Cookies=new();
    private static readonly HttpClient Http=new(new HttpClientHandler {AutomaticDecompression=DecompressionMethods.All,CookieContainer=Cookies}) {Timeout=TimeSpan.FromSeconds(20)};
    // 与 Java yq() 返回的请求头一一对应（省略 Accept-Encoding/deflate，交给 AutomaticDecompression）。
    private static readonly (string Name,string Value)[] NavHeaders=
    {
        ("accept","text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8,application/signed-exchange;v=b3;q=0.7"),
        ("accept-language","zh-CN,zh;q=0.9"),
        ("sec-fetch-dest","document"),
        ("sec-fetch-mode","navigate"),
        ("upgrade-insecure-requests","1"),
        ("cache-control","no-cache"),
        ("pragma","no-cache"),
        ("X-Requested-With","XMLHttpRequset"),
    };
    private readonly SourceInfo _site;
    private readonly Uri _host;
    private readonly Func<string,CancellationToken,Task<string>> _fetch;
    internal static bool Supports(string? ext)
    {
        if (string.IsNullOrWhiteSpace(ext)) return true;
        try
        {
            string value = Uri.TryCreate(ext, UriKind.Absolute, out var rawUri) && rawUri.Scheme is "http" or "https" ? ext : FtyExtCodec.JsonConfig(ext);
            if (Uri.TryCreate(value, UriKind.Absolute, out var direct) && direct.Scheme is "http" or "https") return true;
            using var json = System.Text.Json.JsonDocument.Parse(value);
            if (json.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return false;
            foreach (var property in json.RootElement.EnumerateObject())
            {
                if (property.Name is not ("login") || property.Value.ValueKind != System.Text.Json.JsonValueKind.String) return false;
                if (property.Value.GetString() is { Length: > 0 }) return false;
            }
            return true;
        }
        catch (Exception error) when (error is System.Text.Json.JsonException or InvalidDataException or FormatException or ArgumentException) { return false; }
    }
    public string Key=>_site.Key;
    public string Name=>_site.Name;
    private static readonly Category[] Categories=[new("movie_bt","全部"),new("dbtop250","豆瓣电影Top250"),new("movie_bt_series/dyy","电影"),new("movie_bt_series/guochanju","国产剧"),new("movie_bt_series/mj","美剧"),new("movie_bt_series/hj","韩剧"),new("movie_bt_series/rj","日剧"),new("movie_bt_series/huayudianying","华语电影"),new("movie_bt_series/meiguodianying","欧美电影"),new("movie_bt_series/hanguodianying","韩国电影"),new("movie_bt_series/ribendianying","日本电影")];
    public NewCzSource(SourceInfo site):this(site,null) { }
    internal NewCzSource(SourceInfo site,Func<string,CancellationToken,Task<string>>? fetch)
    {
        _site=site;
        if (!Supports(site.Ext)) throw new NotSupportedException("NewCz ext 配置不受支持。");string host="https://www.4kcz.com/";
        // The plugin also maintains an alternate mirror. No mirror is silently substituted on errors.
        if(Uri.TryCreate(site.Ext,UriKind.Absolute,out var uri)&&uri.Scheme is "http" or "https") host=site.Ext!;
        _host=HttpUri(host.TrimEnd('/')+"/");_fetch=fetch??Fetch;
    }
    private static Regex Rx(string expression)=>HtmlRegex.Get(expression);
    private static string Text(string value)=>WebUtility.HtmlDecode(Rx("<[^>]+>").Replace(value," ")).Trim();
    private static string Attr(string value,string key)=>WebUtility.HtmlDecode(Rx("\\b"+Regex.Escape(key)+"\\s*=\\s*([\"'])(.*?)\\1").Match(value).Groups[2].Value);
    private static string Body(string html, string tag, string css)
    {
        var opening = Rx("<" + tag + "\\b[^>]*class=[\"'][^\"']*\\b" + Regex.Escape(css) + "\\b[^\"']*[\"'][^>]*>").Match(html);
        if (!opening.Success) return "";
        int start = opening.Index + opening.Length, depth = 1;
        foreach (Match token in Rx("</?" + tag + "\\b[^>]*>").Matches(html, start))
        {
            if (token.Value.StartsWith("</", StringComparison.Ordinal)) depth--;
            else if (!token.Value.EndsWith("/>", StringComparison.Ordinal)) depth++;
            if (depth == 0) return html[start..token.Index];
        }
        return "";
    }
    private string Url(string path)=>new Uri(_host,path).AbsoluteUri;
    private async Task<string> Fetch(string url,CancellationToken ct)
    {
        using var request=new HttpRequestMessage(HttpMethod.Get,HttpUri(url));
        foreach(var (name,value) in NavHeaders)request.Headers.TryAddWithoutValidation(name,value);
        request.Headers.TryAddWithoutValidation("User-Agent",Agent);
        request.Headers.TryAddWithoutValidation("Referer",_host.AbsoluteUri);
        using var response=await Http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct).ConfigureAwait(false);response.EnsureSuccessStatusCode();
        await using var stream=await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return Encoding.UTF8.GetString(await BoundedContent.ReadAsync(stream,4*1024*1024,ct).ConfigureAwait(false));
    }
    private Task<string> Page(string path,CancellationToken ct)=>_fetch(Url(path),ct);
    public Task<IReadOnlyList<Category>> GetCategoriesAsync(CancellationToken ct=default) {ct.ThrowIfCancellationRequested();return Task.FromResult<IReadOnlyList<Category>>(Categories);}
    public Task<IReadOnlyList<FilterGroup>> GetFiltersAsync(string categoryId,CancellationToken ct=default) {ct.ThrowIfCancellationRequested();return Task.FromResult<IReadOnlyList<FilterGroup>>([]);}
    public async Task<MediaPage> GetHomeAsync(CancellationToken ct=default)=>Cards(await Page("",ct).ConfigureAwait(false),1);
    public async Task<MediaPage> GetItemsAsync(string categoryId,int page,IReadOnlyDictionary<string,string>? filters,CancellationToken ct=default)
    {
        ValidatePage(page);if(!Categories.Any(c=>c.Id==categoryId))throw new InvalidDataException("NewCz 分类无效。");
        if(filters?.Any(p=>p.Value.Length>0)==true)throw new NotSupportedException("NewCz 没有分类筛选。");
        return Cards(await Page(categoryId+"/page/"+page.ToString(System.Globalization.CultureInfo.InvariantCulture),ct).ConfigureAwait(false),page);
    }
    private MediaPage Cards(string html,int page)
    {
        var items=new List<MediaItem>();
        foreach(Match li in Rx("<li\\b[^>]*>(.*?)</li>").Matches(Body(html,"div","mi_ne_kd")))
        {
            string body=li.Groups[1].Value,anchor=Rx("<a\\b[^>]*>").Match(body).Value,image=Rx("<img\\b[^>]*>").Match(body).Value;
            string href=Attr(anchor,"href"),title=Attr(image,"alt"),pic=Attr(image,"data-original");
            if(title.Length==0||!Rx("(?:^|/)movie/[^/?#]+\\.html$").IsMatch(href))continue;
            items.Add(new() {Id=Url(href),Title=title,Poster=pic.Length>0?Url(pic):null,Remarks=Text(Body(body,"div","hdinfo"))});
        }
        // U7.q advertises unbounded pages for every nonempty result; expose one next page.
        var unique=items.DistinctBy(i=>i.Id).ToArray();return new(unique,page,unique.Length>0?page+1:page);
    }
    public async Task<MediaPage> SearchAsync(string query,int page,CancellationToken ct=default)
    {
        ValidatePage(page);if(page>1||string.IsNullOrWhiteSpace(query))return new([],page,page);
        if(query.Length>256)throw new ArgumentException("搜索词过长。",nameof(query));
        string text=await _fetch("http://czzy.xn--t75a.eu.org/czzysearch.php?wd="+Uri.EscapeDataString(query),ct).ConfigureAwait(false);
        var items=new List<MediaItem>();
        foreach(string row in text.Split("$$$",StringSplitOptions.RemoveEmptyEntries))
        {
            var fields=row.Split('|');if(fields.Length<4||fields[1].Length==0||!Rx("^[\\w-]+$").IsMatch(fields[0]))continue;
            items.Add(new() {Id=Url("movie/"+fields[0]+".html"),Title=fields[1],Poster=fields[2],Remarks=fields[3]});
        }
        return new(items,1,1);
    }
    private string DetailUrl(string id)
    {
        var uri=HttpUri(Url(id));if(uri.Authority!=_host.Authority||!Rx("^/movie/[^/?#]+\\.html$").IsMatch(uri.AbsolutePath)||uri.Query.Length>0)throw new InvalidDataException("NewCz 影片标识无效。");return uri.AbsoluteUri;
    }
    private readonly DetailCache _details=new(TimeSpan.FromMinutes(5));
    public Task<MediaDetail> GetDetailAsync(string mediaId,CancellationToken ct=default)=>_details.GetAsync(mediaId,LoadDetailAsync,ct);
    private async Task<MediaDetail> LoadDetailAsync(string mediaId,CancellationToken ct)
    {
        string html=await _fetch(DetailUrl(mediaId),ct).ConfigureAwait(false);
        string title=Text(Rx("<h1\\b[^>]*>(.*?)</h1>").Match(Body(html,"div","moviedteail_tt")).Groups[1].Value);
        if(title.Length==0)throw new InvalidDataException("NewCz 详情缺少片名。");
        string poster=Attr(Rx("<img\\b[^>]*>").Match(Body(html,"div","dyimg")).Value,"src");
        string facts=Body(html,"ul","moviedteail_list");
        string Fact(string label)
        {
            foreach(Match li in Rx("<li\\b[^>]*>(.*?)</li>").Matches(facts))
            {string text=Text(li.Groups[1].Value);if(text.StartsWith(label,StringComparison.Ordinal))return text[(text.IndexOf('：')+1)..].Trim();}return "";
        }
        var episodes=Rx("<a\\b[^>]*href=[\"']([^\"']+)[\"'][^>]*>(.*?)</a>").Matches(Body(html,"div","paly_list_btn"))
            .Where(m=>Uri.TryCreate(_host,m.Groups[1].Value,out var uri) && uri.Authority==_host.Authority && uri.AbsolutePath.EndsWith(".html",StringComparison.OrdinalIgnoreCase))
            .Select(m=>new Episode("专线\u0001"+(m.Groups[1].Value.StartsWith("http",StringComparison.Ordinal) ? m.Groups[1].Value.Replace(_host.AbsoluteUri,"",StringComparison.Ordinal) : m.Groups[1].Value),Text(m.Groups[2].Value))).DistinctBy(e=>e.Id).ToArray();
        return new() {Item=new() {Id=mediaId,Title=title,Poster=poster.Length>0?Url(poster):null,Year=Fact("年份"),Area=Fact("地区"),TypeName=Fact("类型"),Remarks=Fact("豆瓣")},Actor=Fact("主演"),Director=Fact("导演"),Description=Text(Body(html,"div","yp_context")),Lines=episodes.Length>0?[new("专线","专线",episodes)]:[]};
    }
    public async Task<PlaybackRequest> ResolvePlaybackAsync(string mediaId,string episodeId,CancellationToken ct=default)
    {
        var detail=await GetDetailAsync(mediaId,ct).ConfigureAwait(false);
        if(!detail.Lines.Any(l=>l.Episodes.Any(e=>e.Id==episodeId)))throw new InvalidDataException("NewCz 选集已失效。");
        string html=await Page(episodeId[(episodeId.IndexOf('\u0001')+1)..],ct).ConfigureAwait(false);
        if(html.Contains("登录后即可观看",StringComparison.Ordinal))throw new NotSupportedException("NewCz 此线路需要账号。");
        var cipher=Rx("\"([^\"]+)\";var [\\d\\w]+=function dncry.*?md5.enc.Utf8.parse\\(\"([\\d\\w]+)\".*?md5.enc.Utf8.parse\\(([\\d]+)\\)").Match(html);
        string script=html;
        if(cipher.Success)script=CatVodAes.Decrypt(cipher.Groups[1].Value,cipher.Groups[2].Value,cipher.Groups[3].Value);
        var media=Rx("video:\\s*\\{url:\\s*\"([^\"]+)\"").Match(script);
        if(!media.Success)
        {
            // 原插件在 iframe 解析之前识别 t0o1.czzy.fun 播放器：url= 后即媒体地址，需固定 Origin 与桌面 Chrome UA。
            var direct=Rx("t0o1\\.czzy\\.fun:.*?url=(.*?)\"").Match(html);
            if(direct.Success)
                return new() {Uri=HttpUri(direct.Groups[1].Value).AbsoluteUri,Title=detail.Item.Title,SourceKey=Key,SourceName=Name,MediaId=mediaId,EpisodeId=episodeId,LineId="专线",Headers=new Dictionary<string,string>{{"Origin","https://t0o1.czzy.fun:321"},{"User-Agent",CzzyAgent}}};
        }
        string resolved=media.Success?media.Groups[1].Value:await ResolveFrame(html,ct).ConfigureAwait(false);
        string url=HttpUri(resolved).AbsoluteUri;
        return new() {Uri=url,Title=detail.Item.Title,SourceKey=Key,SourceName=Name,MediaId=mediaId,EpisodeId=episodeId,LineId="专线",Headers=new Dictionary<string,string>{{"Origin",_host.GetLeftPart(UriPartial.Authority)},{"User-Agent",PlayerAgent}}};
    }
    private async Task<string> ResolveFrame(string html,CancellationToken ct)
    {
        var frame=Rx("<iframe\\b[^>]*allowfullscreen=[\"']true[\"'][^>]*>").Match(html);
        if(!frame.Success) frame=Rx("<iframe\\b[^>]*>").Match(html);
        string src=Attr(frame.Value,"src");
        if(src.Length==0)throw new NotSupportedException("NewCz 未识别的匿名播放器。");
        string parser=await _fetch(HttpUri(Url(src)).AbsoluteUri,ct).ConfigureAwait(false);
        var encrypted=Rx("var\\s+player\\s*=\\s*\"([^\"]+)\"").Match(parser);
        if(encrypted.Success)
        {
            string iv=Rx("var\\s+rand\\s*=\\s*\"([^\"]+)\"").Match(parser).Groups[1].Value;
            using var json=System.Text.Json.JsonDocument.Parse(CatVodAes.Decrypt(encrypted.Groups[1].Value,"VFBTzdujpR9FWBhe",iv));
            string value=json.RootElement.GetProperty("url").GetString()??"";
            return Uri.TryCreate(value,UriKind.Absolute,out _)?value:"https://t02.cz01.org"+value;
        }
        var hex=Rx("var\\s+url\\s*=\\s*'([^']+)'").Match(parser);
        if(!hex.Success) hex=Rx("var\\s+result_v2\\s*=\\s*\\{\"data\":\"([^\"]+)\"").Match(parser);
        if(hex.Success) return DecodeReverseHex(hex.Groups[1].Value);
        throw new NotSupportedException("NewCz 未识别的 iframe 播放器响应。");
    }
    internal static string DecodeReverseHex(string encoded)
    {
        string reversed=new(encoded.Reverse().ToArray());
        if(reversed.Length%2!=0||reversed.Length<14)throw new InvalidDataException("NewCz reverse-hex 数据无效。");
        var output=new StringBuilder(reversed.Length/2);
        for(int i=0;i<reversed.Length;i+=2) output.Append((char)Convert.ToInt32(reversed.Substring(i,2),16));
        int middle=(output.Length-7)/2;
        return output.ToString().Remove(middle,7);
    }
    private static Uri HttpUri(string value)=>Uri.TryCreate(value,UriKind.Absolute,out var uri)&&uri.Scheme is "http" or "https"&&uri.UserInfo.Length==0?uri:throw new InvalidDataException("NewCz HTTP(S)地址无效。");
    private static void ValidatePage(int page) {if(page<1||page==int.MaxValue)throw new ArgumentOutOfRangeException(nameof(page));}
}
