using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using VodBox.Core;

namespace VodBox.Infrastructure;

/// <summary>Libvio anonymous HTML and direct player_ media subset. Cloud-drive lines are excluded.</summary>
public sealed class LibvioSource : IResolvingContentSource
{
    private const string Agent="Mozilla/5.0 (X11; Linux aarch64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/148.0.0.0 Safari/537.36";
    private static readonly HttpClient Http=new(new HttpClientHandler { AutomaticDecompression=DecompressionMethods.All }) { Timeout=TimeSpan.FromSeconds(20) };
    private readonly SourceInfo _site;
    private readonly Uri _host;
    private readonly Func<string,CancellationToken,Task<string>> _fetch;
    private readonly Func<string,string,CancellationToken,Task<string>> _post;
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
                // siteUrl 决定匿名站内直连基地址；Cloud-drive 是需账号的网盘线路（按用户要求跳过），
                // 仅接受其存在但不据此提升能力，其余未知字段一律拒绝。
                if (property.Name is not ("siteUrl" or "Cloud-drive") || property.Value.ValueKind != System.Text.Json.JsonValueKind.String) return false;
                if (property.Name == "siteUrl" && property.Value.GetString() is { Length: > 0 } host && (!Uri.TryCreate(host, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0)) return false;
            }
            return true;
        }
        catch (Exception error) when (error is System.Text.Json.JsonException or InvalidDataException or FormatException or ArgumentException) { return false; }
    }
    public string Key=>_site.Key;
    public string Name=>_site.Name;
    public LibvioSource(SourceInfo site):this(site,null) { }
    internal LibvioSource(SourceInfo site,Func<string,CancellationToken,Task<string>>? fetch,Func<string,string,CancellationToken,Task<string>>? post=null)
    {
        _site=site;
        if (!Supports(site.Ext)) throw new NotSupportedException("Libvio ext 配置不受支持。");
        string host="https://www.libvio.io/";
        if (!string.IsNullOrWhiteSpace(site.Ext))
        {
            string ext=Uri.TryCreate(site.Ext,UriKind.Absolute,out var raw) && raw.Scheme is "http" or "https" ? site.Ext : FtyExtCodec.JsonConfig(site.Ext);
            if (ext.TrimStart().StartsWith('{'))
            {
                using var json=JsonDocument.Parse(ext);
                if(json.RootElement.TryGetProperty("siteUrl",out var value) && value.ValueKind==JsonValueKind.String && value.GetString() is { Length:>0 } configured) host=configured;
            }
            else if(Uri.TryCreate(ext,UriKind.Absolute,out var uri) && uri.Scheme is "http" or "https") host=ext;
        }
        _host=HttpUri(host.TrimEnd('/')+"/");_fetch=fetch??Fetch;_post=post??Post;
    }
    private static Regex Rx(string expression)=>new(expression,RegexOptions.Singleline|RegexOptions.IgnoreCase|RegexOptions.CultureInvariant,TimeSpan.FromSeconds(2));
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
    private string Url(string value)=>new Uri(_host,value).AbsoluteUri;
    private async Task<string> Fetch(string url,CancellationToken ct)
    {
        using var request=new HttpRequestMessage(HttpMethod.Get,HttpUri(url));request.Headers.TryAddWithoutValidation("User-Agent",Agent);request.Headers.TryAddWithoutValidation("Referer",_host.AbsoluteUri);
        using var response=await Http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct).ConfigureAwait(false);response.EnsureSuccessStatusCode();
        await using var stream=await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return Encoding.UTF8.GetString(await BoundedContent.ReadAsync(stream,4*1024*1024,ct).ConfigureAwait(false));
    }
    private Task<string> Page(string value,CancellationToken ct)=>_fetch(Url(value),ct);
    public async Task<IReadOnlyList<Category>> GetCategoriesAsync(CancellationToken ct=default)
    {
        string html=await Page("",ct).ConfigureAwait(false);
        return Rx("<a\\b[^>]*href=[\"']/type/(\\d+)\\.html[\"'][^>]*>(.*?)</a>").Matches(html)
            .Select(m=>new Category(m.Groups[1].Value,Text(m.Groups[2].Value))).Where(c=>c.Name is "电影" or "电视剧" or "综艺" or "动漫").DistinctBy(c=>c.Id).ToArray();
    }
    public Task<IReadOnlyList<FilterGroup>> GetFiltersAsync(string categoryId,CancellationToken ct=default)
    {
        ct.ThrowIfCancellationRequested();
        using var json=JsonDocument.Parse(FilterJson);
        if(!json.RootElement.TryGetProperty(categoryId,out var groups))return Task.FromResult<IReadOnlyList<FilterGroup>>([]);
        return Task.FromResult<IReadOnlyList<FilterGroup>>(groups.EnumerateArray().Select(g=>new FilterGroup(Field(g,"key"),Field(g,"name"),g.GetProperty("value").EnumerateArray().Select(v=>new FilterValue(Field(v,"n"),Field(v,"v"))).ToList(),g.GetProperty("value")[0].GetProperty("v").GetString()??"")).ToArray());
    }
    public async Task<MediaPage> GetHomeAsync(CancellationToken ct=default)=>Cards(await Page("",ct).ConfigureAwait(false),1);
    public async Task<MediaPage> GetItemsAsync(string categoryId,int page,IReadOnlyDictionary<string,string>? filters,CancellationToken ct=default)
    {
        ValidatePage(page);if(!Rx("^\\d+$").IsMatch(categoryId)) throw new InvalidDataException("Libvio 分类无效。");
        var parts=new string[12];Array.Fill(parts,"");parts[0]=categoryId;parts[8]=page.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if(filters is not null) foreach(var pair in filters)
        {
            if(!int.TryParse(pair.Key,out int position) || position is <=0 or >=12 or 8) throw new InvalidDataException("Libvio 筛选无效。");
            if(pair.Value.IndexOfAny(['-','/','?','#'])>=0) throw new InvalidDataException("Libvio 筛选无效。");
            parts[position]=Uri.EscapeDataString(pair.Value);
        }
        return Cards(await Page("show/"+string.Join('-',parts)+".html",ct).ConfigureAwait(false),page);
    }
    public async Task<MediaPage> SearchAsync(string query,int page,CancellationToken ct=default)
    {
        ValidatePage(page);if(page>1 || string.IsNullOrWhiteSpace(query)) return new([],page,page);
        if(query.Length>256) throw new ArgumentException("搜索词过长。",nameof(query));
        using var json=JsonDocument.Parse(await Page("index.php/ajax/suggest?mid=1&wd="+Uri.EscapeDataString(query)+"&limit=10&timestamp="+DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),ct).ConfigureAwait(false));
        var list=json.RootElement.GetProperty("list");
        using var nested=list.ValueKind==JsonValueKind.String ? JsonDocument.Parse(list.GetString()!):null;
        var array=nested?.RootElement??list;
        if(array.ValueKind!=JsonValueKind.Array) throw new InvalidDataException("Libvio 搜索响应不是数组。");
        return new(array.EnumerateArray().Select(v=>new MediaItem {Id=Url("detail/"+Field(v,"id")+".html"),Title=Field(v,"name"),Poster=Url(Field(v,"pic"))}).ToArray(),1,1);
    }
    private MediaPage Cards(string html,int page)
    {
        var items=new List<MediaItem>();
        foreach(Match box in Rx("<div\\b[^>]*class=[\"'][^\"']*stui-vodlist__box[^\"']*[\"'][^>]*>(.*?)</div>").Matches(html))
        {
            string anchor=Rx("<a\\b[^>]*>").Match(box.Groups[1].Value).Value;
            string href=Attr(anchor,"href"),title=Attr(anchor,"title"),pic=Attr(anchor,"data-original");
            if(!Rx("(?:^|/)detail/\\d+\\.html$").IsMatch(href)||title.Length==0) continue;
            items.Add(new() {Id=Url(href),Title=title,Poster=pic.Length>0?Url(pic):null,Remarks=Text(Body(box.Groups[1].Value,"span","pic-text"))});
        }
        int count=page;
        foreach(Match link in Rx("/show/([^\"']+)\\.html").Matches(Body(html,"ul","stui-page__item")))
        {var parts=link.Groups[1].Value.Split('-');if(parts.Length==12&&int.TryParse(parts[8],out int n)) count=Math.Max(count,n);}
        return new(items.DistinctBy(i=>i.Id).ToArray(),page,count);
    }
    private string DetailUrl(string id)
    {
        var uri=HttpUri(Url(id));if(uri.Authority!=_host.Authority || !Rx("^/detail/\\d+\\.html$").IsMatch(uri.AbsolutePath)||uri.Query.Length>0) throw new InvalidDataException("Libvio 影片标识无效。");return uri.AbsoluteUri;
    }
    public async Task<MediaDetail> GetDetailAsync(string mediaId,CancellationToken ct=default)
    {
        string html=await _fetch(DetailUrl(mediaId),ct).ConfigureAwait(false);
        string title=Text(Rx("<h1\\b[^>]*>(.*?)</h1>").Match(html).Groups[1].Value);
        if(title.Length==0) throw new InvalidDataException("Libvio 详情缺少片名。");
        string poster=Attr(Rx("<img\\b[^>]*data-original=[\"'][^\"']+[\"'][^>]*>").Match(Body(html,"div","stui-content__thumb")).Value,"data-original");
        string Fact(string label)=>Rx(Regex.Escape(label)+"：\\s*(\\S+)").Match(Text(html)).Groups[1].Value;
        var markers=Rx("<div\\b[^>]*class=[\"'][^\"']*\\bplaylist-panel\\b[^\"']*[\"'][^>]*>").Matches(html);
        var lines=new List<PlaybackLine>();
        for(int i=0;i<markers.Count;i++)
        {
            string body=html[(markers[i].Index+markers[i].Length)..(i+1<markers.Count?markers[i+1].Index:html.Length)];
            string label=Text(Body(body,"div","panel-head"));
            if(new[]{"夸克","优汐","阿里","百度","UC","嘟嘟"}.Any(s=>label.Contains(s,StringComparison.OrdinalIgnoreCase))) continue;
            var episodes=Rx("<a\\b[^>]*href=[\"']([^\"']+)[\"'][^>]*>(.*?)</a>").Matches(body)
                .Where(m=>Rx("(?:^|/)play/\\d+-\\d+-\\d+\\.html$").IsMatch(m.Groups[1].Value))
                .Select(m=>new Episode(label+'\u0001'+Url(m.Groups[1].Value),Text(m.Groups[2].Value))).DistinctBy(e=>e.Id).ToArray();
            if(episodes.Length>0) lines.Add(new(label,label,episodes));
        }
        return new() {Item=new() {Id=mediaId,Title=title,Poster=poster.Length>0?Url(poster):null,Year=Fact("年份"),Area=Fact("地区"),TypeName=Fact("类型"),Remarks=Fact("更新")},Actor=Fact("主演"),Director=Fact("导演"),Description=Text(Body(html,"span","detail-content")),Lines=lines};
    }
    public async Task<PlaybackRequest> ResolvePlaybackAsync(string mediaId,string episodeId,CancellationToken ct=default)
    {
        var detail=await GetDetailAsync(mediaId,ct).ConfigureAwait(false);
        var line=detail.Lines.FirstOrDefault(l=>l.Episodes.Any(e=>e.Id==episodeId))??throw new InvalidDataException("Libvio 选集已失效。");
        string html=await _fetch(episodeId[(episodeId.IndexOf('\u0001')+1)..],ct).ConfigureAwait(false);
        if(html.Contains("登录后",StringComparison.Ordinal)) throw new NotSupportedException("Libvio 此线路需要账号。");
        var player=Rx("\\bvar\\s+player_\\w+\\s*=\\s*(\\{.*?\\})\\s*(?:;|</script>)").Match(html);
        if(!player.Success) throw new InvalidDataException("Libvio 缺少 player 配置。");
        using var json=JsonDocument.Parse(player.Groups[1].Value);
        string url=Field(json.RootElement,"url");
        // The fixed Java plugin checks literal m3u8/mp4 before parser-specific branches.
        string from=Field(json.RootElement,"from");
        if(!DirectMedia(url))
        {
            string endpoint=from switch
            {
                "vr2"=>"vid/plyr/vr2.php?url=",
                "ty_new1"=>"vid/ty4.php?url=",
                "cczz" or "zxyy" or "gwj" or "lbyy" or "NBY" or "03yy"=>"vid/yd.php?url=",
                _=>throw new NotSupportedException("Libvio 未识别的匿名播放器："+from)
            };
            string parser=await Page(endpoint+url+"&nid="+Field(json.RootElement,"nid")+"&id="+Field(json.RootElement,"id"),ct).ConfigureAwait(false);
            if(from=="ty_new1")
            {
                string api=Unescape(Rx("parseUrl[\"']\\s*:\\s*[\"']((?:\\\\.|[^\"'])*)[\"']").Match(parser).Groups[1].Value);
                string sid=Unescape(Rx("sid[\"']\\s*:\\s*[\"']((?:\\\\.|[^\"'])*)[\"']").Match(parser).Groups[1].Value);
                if(api.Length>0) parser=await _post(Url(api),JsonSerializer.Serialize(new Dictionary<string,string>{{"url",url},{"sid",sid}},LibvioJson.Default.DictionaryStringString),ct).ConfigureAwait(false);
            }
            else
            {
                var fetch=Rx("fetch\\(\\s*['\"]([^'\"]+)['\"]").Match(parser);
                if(fetch.Success) parser=await Page(fetch.Groups[1].Value,ct).ConfigureAwait(false);
            }
            url=ParsedUrl(parser);
        }
        var uri=HttpUri(url);
        return new() {Uri=uri.AbsoluteUri,Title=detail.Item.Title,SourceKey=Key,SourceName=Name,MediaId=mediaId,EpisodeId=episodeId,LineId=line.Id,Headers=new Dictionary<string,string>{{"User-Agent",Agent},{"Referer",_host.AbsoluteUri}}};
    }
    private static bool DirectMedia(string value)=>Uri.TryCreate(value,UriKind.Absolute,out var uri) && uri.Scheme is "http" or "https" && (uri.AbsolutePath.EndsWith(".m3u8",StringComparison.OrdinalIgnoreCase)||uri.AbsolutePath.EndsWith(".mp4",StringComparison.OrdinalIgnoreCase));
    private static string Unescape(string text)
    {
        try { using var json=JsonDocument.Parse("\""+text+"\""); return json.RootElement.GetString()??""; }
        catch(JsonException) { return text.Replace("\\/","/",StringComparison.Ordinal); }
    }
    private static string ParsedUrl(string parser)
    {
        try
        {
            using var json=JsonDocument.Parse(parser);
            string url=Field(json.RootElement,"url");
            if(url.Length>0)return url;
        }
        catch(JsonException) { }
        var match=Rx("(?:urls\\s*:|var\\s+vid\\s*=)\\s*([\"'])(.*?)\\1").Match(parser);
        if(!match.Success)throw new InvalidDataException("Libvio 解析器没有返回媒体地址。");
        return Unescape(match.Groups[2].Value);
    }
    private async Task<string> Post(string url,string body,CancellationToken ct)
    {
        using var request=new HttpRequestMessage(HttpMethod.Post,HttpUri(url)) {Content=new StringContent(body,Encoding.UTF8,"application/json")};
        request.Headers.TryAddWithoutValidation("User-Agent",Agent);request.Headers.TryAddWithoutValidation("Referer",_host.AbsoluteUri);
        using var response=await Http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct).ConfigureAwait(false);response.EnsureSuccessStatusCode();
        await using var stream=await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return Encoding.UTF8.GetString(await BoundedContent.ReadAsync(stream,4*1024*1024,ct).ConfigureAwait(false));
    }
    private const string FilterJson="{\"1\":[{\"key\":\"3\",\"name\":\"类型\",\"value\":[{\"n\":\"全部\",\"v\":\"\"},{\"n\":\"喜剧\",\"v\":\"喜剧\"},{\"n\":\"爱情\",\"v\":\"爱情\"},{\"n\":\"恐怖\",\"v\":\"恐怖\"},{\"n\":\"动作\",\"v\":\"动作\"},{\"n\":\"科幻\",\"v\":\"科幻\"},{\"n\":\"剧情\",\"v\":\"剧情\"},{\"n\":\"战争\",\"v\":\"战争\"},{\"n\":\"警匪\",\"v\":\"警匪\"},{\"n\":\"犯罪\",\"v\":\"犯罪\"},{\"n\":\"动画\",\"v\":\"动画\"},{\"n\":\"奇幻\",\"v\":\"奇幻\"},{\"n\":\"武侠\",\"v\":\"武侠\"},{\"n\":\"冒险\",\"v\":\"冒险\"},{\"n\":\"枪战\",\"v\":\"枪战\"},{\"n\":\"恐怖\",\"v\":\"恐怖\"},{\"n\":\"悬疑\",\"v\":\"悬疑\"},{\"n\":\"惊悚\",\"v\":\"惊悚\"},{\"n\":\"经典\",\"v\":\"经典\"},{\"n\":\"青春\",\"v\":\"青春\"},{\"n\":\"文艺\",\"v\":\"文艺\"},{\"n\":\"微电影\",\"v\":\"微电影\"},{\"n\":\"古装\",\"v\":\"古装\"},{\"n\":\"历史\",\"v\":\"历史\"},{\"n\":\"运动\",\"v\":\"运动\"},{\"n\":\"农村\",\"v\":\"农村\"},{\"n\":\"儿童\",\"v\":\"儿童\"}]},{\"key\":\"1\",\"name\":\"地区\",\"value\":[{\"n\":\"全部\",\"v\":\"\"},{\"n\":\"中国大陆\",\"v\":\"中国大陆\"},{\"n\":\"中国香港\",\"v\":\"中国香港\"},{\"n\":\"中国台湾\",\"v\":\"中国台湾\"},{\"n\":\"美国\",\"v\":\"美国\"},{\"n\":\"法国\",\"v\":\"法国\"},{\"n\":\"英国\",\"v\":\"英国\"},{\"n\":\"日本\",\"v\":\"日本\"},{\"n\":\"韩国\",\"v\":\"韩国\"},{\"n\":\"德国\",\"v\":\"德国\"},{\"n\":\"泰国\",\"v\":\"泰国\"},{\"n\":\"印度\",\"v\":\"印度\"},{\"n\":\"意大利\",\"v\":\"意大利\"},{\"n\":\"西班牙\",\"v\":\"西班牙\"},{\"n\":\"加拿大\",\"v\":\"加拿大\"},{\"n\":\"其他\",\"v\":\"其他\"}]},{\"key\":\"11\",\"name\":\"年份\",\"value\":[{\"n\":\"全部\",\"v\":\"\"},{\"n\":\"2023\",\"v\":\"2023\"},{\"n\":\"2022\",\"v\":\"2022\"},{\"n\":\"2021\",\"v\":\"2021\"},{\"n\":\"2020\",\"v\":\"2020\"},{\"n\":\"2019\",\"v\":\"2019\"},{\"n\":\"2018\",\"v\":\"2018\"},{\"n\":\"2017\",\"v\":\"2017\"},{\"n\":\"2016\",\"v\":\"2016\"},{\"n\":\"2015\",\"v\":\"2015\"},{\"n\":\"2014\",\"v\":\"2014\"},{\"n\":\"2013\",\"v\":\"2013\"},{\"n\":\"2012\",\"v\":\"2012\"},{\"n\":\"2011\",\"v\":\"2011\"},{\"n\":\"2010\",\"v\":\"2010\"},{\"n\":\"2009\",\"v\":\"2009\"},{\"n\":\"2008\",\"v\":\"2008\"},{\"n\":\"2007\",\"v\":\"2007\"},{\"n\":\"2006\",\"v\":\"2006\"},{\"n\":\"2005\",\"v\":\"2005\"},{\"n\":\"2004\",\"v\":\"2004\"},{\"n\":\"2003\",\"v\":\"2003\"},{\"n\":\"2002\",\"v\":\"2002\"},{\"n\":\"2001\",\"v\":\"2001\"},{\"n\":\"2000\",\"v\":\"2000\"}]},{\"key\":\"4\",\"name\":\"语言\",\"value\":[{\"n\":\"全部\",\"v\":\"\"},{\"n\":\"英语\",\"v\":\"英语\"},{\"n\":\"韩语\",\"v\":\"韩语\"},{\"n\":\"日语\",\"v\":\"日语\"},{\"n\":\"法语\",\"v\":\"法语\"},{\"n\":\"泰语\",\"v\":\"泰语\"},{\"n\":\"德语\",\"v\":\"德语\"},{\"n\":\"印度语\",\"v\":\"印度语\"},{\"n\":\"国语\",\"v\":\"国语\"},{\"n\":\"粤语\",\"v\":\"粤语\"},{\"n\":\"俄语\",\"v\":\"俄语\"},{\"n\":\"西班牙语\",\"v\":\"西班牙语\"},{\"n\":\"意大利语\",\"v\":\"意大利语\"},{\"n\":\"其它\",\"v\":\"其它\"}]},{\"key\":\"2\",\"name\":\"排序\",\"value\":[{\"n\":\"时间\",\"v\":\"time\"},{\"n\":\"人气\",\"v\":\"hits\"},{\"n\":\"评分\",\"v\":\"score\"}]}],\"2\":[{\"key\":\"3\",\"name\":\"类型\",\"value\":[{\"n\":\"全部\",\"v\":\"\"},{\"n\":\"古装\",\"v\":\"古装\"},{\"n\":\"战争\",\"v\":\"战争\"},{\"n\":\"青春偶像\",\"v\":\"青春偶像\"},{\"n\":\"喜剧\",\"v\":\"喜剧\"},{\"n\":\"家庭\",\"v\":\"家庭\"},{\"n\":\"犯罪\",\"v\":\"犯罪\"},{\"n\":\"动作\",\"v\":\"动作\"},{\"n\":\"奇幻\",\"v\":\"奇幻\"},{\"n\":\"剧情\",\"v\":\"剧情\"},{\"n\":\"历史\",\"v\":\"历史\"},{\"n\":\"经典\",\"v\":\"经典\"},{\"n\":\"乡村\",\"v\":\"乡村\"},{\"n\":\"情景\",\"v\":\"情景\"},{\"n\":\"商战\",\"v\":\"商战\"},{\"n\":\"网剧\",\"v\":\"网剧\"},{\"n\":\"其他\",\"v\":\"其他\"}]},{\"key\":\"1\",\"name\":\"地区\",\"value\":[{\"n\":\"全部\",\"v\":\"\"},{\"n\":\"中国大陆\",\"v\":\"中国大陆\"},{\"n\":\"中国台湾\",\"v\":\"中国台湾\"},{\"n\":\"中国香港\",\"v\":\"中国香港\"},{\"n\":\"韩国\",\"v\":\"韩国\"},{\"n\":\"日本\",\"v\":\"日本\"},{\"n\":\"美国\",\"v\":\"美国\"},{\"n\":\"泰国\",\"v\":\"泰国\"},{\"n\":\"英国\",\"v\":\"英国\"},{\"n\":\"新加坡\",\"v\":\"新加坡\"},{\"n\":\"其他\",\"v\":\"其他\"}]},{\"key\":\"4\",\"name\":\"语言\",\"value\":[{\"n\":\"全部\",\"v\":\"\"},{\"n\":\"英语\",\"v\":\"英语\"},{\"n\":\"韩语\",\"v\":\"韩语\"},{\"n\":\"日语\",\"v\":\"日语\"},{\"n\":\"法语\",\"v\":\"法语\"},{\"n\":\"泰语\",\"v\":\"泰语\"},{\"n\":\"德语\",\"v\":\"德语\"},{\"n\":\"印度语\",\"v\":\"印度语\"},{\"n\":\"国语\",\"v\":\"国语\"},{\"n\":\"粤语\",\"v\":\"粤语\"},{\"n\":\"俄语\",\"v\":\"俄语\"},{\"n\":\"西班牙语\",\"v\":\"西班牙语\"},{\"n\":\"意大利语\",\"v\":\"意大利语\"},{\"n\":\"其它\",\"v\":\"其它\"}]},{\"key\":\"11\",\"name\":\"年份\",\"value\":[{\"n\":\"全部\",\"v\":\"\"},{\"n\":\"2023\",\"v\":\"2023\"},{\"n\":\"2022\",\"v\":\"2022\"},{\"n\":\"2021\",\"v\":\"2021\"},{\"n\":\"2020\",\"v\":\"2020\"},{\"n\":\"2019\",\"v\":\"2019\"},{\"n\":\"2018\",\"v\":\"2018\"},{\"n\":\"2017\",\"v\":\"2017\"},{\"n\":\"2016\",\"v\":\"2016\"},{\"n\":\"2015\",\"v\":\"2015\"},{\"n\":\"2014\",\"v\":\"2014\"},{\"n\":\"2013\",\"v\":\"2013\"},{\"n\":\"2012\",\"v\":\"2012\"},{\"n\":\"2011\",\"v\":\"2011\"},{\"n\":\"2010\",\"v\":\"2010\"},{\"n\":\"2009\",\"v\":\"2009\"},{\"n\":\"2008\",\"v\":\"2008\"},{\"n\":\"2007\",\"v\":\"2007\"},{\"n\":\"2006\",\"v\":\"2006\"},{\"n\":\"2005\",\"v\":\"2005\"},{\"n\":\"2004\",\"v\":\"2004\"},{\"n\":\"2003\",\"v\":\"2003\"},{\"n\":\"2002\",\"v\":\"2002\"},{\"n\":\"2001\",\"v\":\"2001\"},{\"n\":\"2000\",\"v\":\"2000\"}]},{\"key\":\"2\",\"name\":\"排序\",\"value\":[{\"n\":\"时间\",\"v\":\"time\"},{\"n\":\"人气\",\"v\":\"hits\"},{\"n\":\"评分\",\"v\":\"score\"}]}],\"4\":[{\"key\":\"3\",\"name\":\"类型\",\"value\":[{\"n\":\"全部\",\"v\":\"\"},{\"n\":\"情感\",\"v\":\"情感\"},{\"n\":\"科幻\",\"v\":\"科幻\"},{\"n\":\"热血\",\"v\":\"热血\"},{\"n\":\"推理\",\"v\":\"推理\"},{\"n\":\"搞笑\",\"v\":\"搞笑\"},{\"n\":\"冒险\",\"v\":\"冒险\"},{\"n\":\"萝莉\",\"v\":\"萝莉\"},{\"n\":\"校园\",\"v\":\"校园\"},{\"n\":\"动作\",\"v\":\"动作\"},{\"n\":\"机战\",\"v\":\"机战\"},{\"n\":\"运动\",\"v\":\"运动\"},{\"n\":\"战争\",\"v\":\"战争\"},{\"n\":\"少年\",\"v\":\"少年\"},{\"n\":\"少女\",\"v\":\"少女\"},{\"n\":\"社会\",\"v\":\"社会\"},{\"n\":\"原创\",\"v\":\"原创\"},{\"n\":\"亲子\",\"v\":\"亲子\"},{\"n\":\"益智\",\"v\":\"益智\"},{\"n\":\"励志\",\"v\":\"励志\"},{\"n\":\"其他\",\"v\":\"其他\"}]},{\"key\":\"1\",\"name\":\"地区\",\"value\":[{\"n\":\"全部\",\"v\":\"\"},{\"n\":\"中国\",\"v\":\"中国\"},{\"n\":\"日本\",\"v\":\"日本\"},{\"n\":\"欧美\",\"v\":\"欧美\"},{\"n\":\"其他\",\"v\":\"其他\"}]},{\"key\":\"11\",\"name\":\"年份\",\"value\":[{\"n\":\"全部\",\"v\":\"\"},{\"n\":\"2023\",\"v\":\"2023\"},{\"n\":\"2022\",\"v\":\"2022\"},{\"n\":\"2021\",\"v\":\"2021\"},{\"n\":\"2020\",\"v\":\"2020\"},{\"n\":\"2019\",\"v\":\"2019\"},{\"n\":\"2018\",\"v\":\"2018\"},{\"n\":\"2017\",\"v\":\"2017\"},{\"n\":\"2016\",\"v\":\"2016\"},{\"n\":\"2015\",\"v\":\"2015\"},{\"n\":\"2014\",\"v\":\"2014\"},{\"n\":\"2013\",\"v\":\"2013\"},{\"n\":\"2012\",\"v\":\"2012\"},{\"n\":\"2011\",\"v\":\"2011\"},{\"n\":\"2010\",\"v\":\"2010\"},{\"n\":\"2009\",\"v\":\"2009\"},{\"n\":\"2008\",\"v\":\"2008\"},{\"n\":\"2007\",\"v\":\"2007\"},{\"n\":\"2006\",\"v\":\"2006\"},{\"n\":\"2005\",\"v\":\"2005\"},{\"n\":\"2004\",\"v\":\"2004\"},{\"n\":\"2003\",\"v\":\"2003\"},{\"n\":\"2002\",\"v\":\"2002\"},{\"n\":\"2001\",\"v\":\"2001\"},{\"n\":\"2000\",\"v\":\"2000\"}]},{\"key\":\"2\",\"name\":\"排序\",\"value\":[{\"n\":\"时间\",\"v\":\"time\"},{\"n\":\"人气\",\"v\":\"hits\"},{\"n\":\"评分\",\"v\":\"score\"}]}],\"15\":[{\"key\":\"3\",\"name\":\"类型\",\"value\":[{\"n\":\"全部\",\"v\":\"\"},{\"n\":\"古装\",\"v\":\"古装\"},{\"n\":\"战争\",\"v\":\"战争\"},{\"n\":\"青春偶像\",\"v\":\"青春偶像\"},{\"n\":\"喜剧\",\"v\":\"喜剧\"},{\"n\":\"家庭\",\"v\":\"家庭\"},{\"n\":\"犯罪\",\"v\":\"犯罪\"},{\"n\":\"动作\",\"v\":\"动作\"},{\"n\":\"奇幻\",\"v\":\"奇幻\"},{\"n\":\"剧情\",\"v\":\"剧情\"},{\"n\":\"历史\",\"v\":\"历史\"},{\"n\":\"经典\",\"v\":\"经典\"},{\"n\":\"乡村\",\"v\":\"乡村\"},{\"n\":\"情景\",\"v\":\"情景\"},{\"n\":\"商战\",\"v\":\"商战\"},{\"n\":\"网剧\",\"v\":\"网剧\"},{\"n\":\"惊悚\",\"v\":\"惊悚\"},{\"n\":\"其他\",\"v\":\"其他\"}]},{\"key\":\"1\",\"name\":\"地区\",\"value\":[{\"n\":\"全部\",\"v\":\"\"},{\"n\":\"日本\",\"v\":\"日本\"},{\"n\":\"韩国\",\"v\":\"韩国\"}]},{\"key\":\"11\",\"name\":\"年份\",\"value\":[{\"n\":\"全部\",\"v\":\"\"},{\"n\":\"2023\",\"v\":\"2023\"},{\"n\":\"2022\",\"v\":\"2022\"},{\"n\":\"2021\",\"v\":\"2021\"},{\"n\":\"2020\",\"v\":\"2020\"},{\"n\":\"2019\",\"v\":\"2019\"},{\"n\":\"2018\",\"v\":\"2018\"},{\"n\":\"2017\",\"v\":\"2017\"},{\"n\":\"2016\",\"v\":\"2016\"},{\"n\":\"2015\",\"v\":\"2015\"},{\"n\":\"2014\",\"v\":\"2014\"},{\"n\":\"2013\",\"v\":\"2013\"},{\"n\":\"2012\",\"v\":\"2012\"},{\"n\":\"2011\",\"v\":\"2011\"},{\"n\":\"2010\",\"v\":\"2010\"},{\"n\":\"2009\",\"v\":\"2009\"},{\"n\":\"2008\",\"v\":\"2008\"},{\"n\":\"2007\",\"v\":\"2007\"},{\"n\":\"2006\",\"v\":\"2006\"},{\"n\":\"2005\",\"v\":\"2005\"},{\"n\":\"2004\",\"v\":\"2004\"},{\"n\":\"2003\",\"v\":\"2003\"},{\"n\":\"2002\",\"v\":\"2002\"},{\"n\":\"2001\",\"v\":\"2001\"},{\"n\":\"2000\",\"v\":\"2000\"}]},{\"key\":\"2\",\"name\":\"排序\",\"value\":[{\"n\":\"时间\",\"v\":\"time\"},{\"n\":\"人气\",\"v\":\"hits\"},{\"n\":\"评分\",\"v\":\"score\"}]}],\"16\":[{\"key\":\"3\",\"name\":\"类型\",\"value\":[{\"n\":\"全部\",\"v\":\"\"},{\"n\":\"古装\",\"v\":\"古装\"},{\"n\":\"战争\",\"v\":\"战争\"},{\"n\":\"青春偶像\",\"v\":\"青春偶像\"},{\"n\":\"喜剧\",\"v\":\"喜剧\"},{\"n\":\"家庭\",\"v\":\"家庭\"},{\"n\":\"犯罪\",\"v\":\"犯罪\"},{\"n\":\"动作\",\"v\":\"动作\"},{\"n\":\"奇幻\",\"v\":\"奇幻\"},{\"n\":\"剧情\",\"v\":\"剧情\"},{\"n\":\"历史\",\"v\":\"历史\"},{\"n\":\"经典\",\"v\":\"经典\"},{\"n\":\"乡村\",\"v\":\"乡村\"},{\"n\":\"情景\",\"v\":\"情景\"},{\"n\":\"商战\",\"v\":\"商战\"},{\"n\":\"网剧\",\"v\":\"网剧\"},{\"n\":\"其他\",\"v\":\"其他\"}]},{\"key\":\"1\",\"name\":\"地区\",\"value\":[{\"n\":\"全部\",\"v\":\"\"},{\"n\":\"美国\",\"v\":\"美国\"},{\"n\":\"英国\",\"v\":\"英国\"},{\"n\":\"德国\",\"v\":\"德国\"},{\"n\":\"加拿大\",\"v\":\"加拿大\"},{\"n\":\"其他\",\"v\":\"其他\"}]},{\"key\":\"11\",\"name\":\"年份\",\"value\":[{\"n\":\"全部\",\"v\":\"\"},{\"n\":\"2023\",\"v\":\"2023\"},{\"n\":\"2022\",\"v\":\"2022\"},{\"n\":\"2021\",\"v\":\"2021\"},{\"n\":\"2020\",\"v\":\"2020\"},{\"n\":\"2019\",\"v\":\"2019\"},{\"n\":\"2018\",\"v\":\"2018\"},{\"n\":\"2017\",\"v\":\"2017\"},{\"n\":\"2016\",\"v\":\"2016\"},{\"n\":\"2015\",\"v\":\"2015\"},{\"n\":\"2014\",\"v\":\"2014\"},{\"n\":\"2013\",\"v\":\"2013\"},{\"n\":\"2012\",\"v\":\"2012\"},{\"n\":\"2011\",\"v\":\"2011\"},{\"n\":\"2010\",\"v\":\"2010\"},{\"n\":\"2009\",\"v\":\"2009\"},{\"n\":\"2008\",\"v\":\"2008\"},{\"n\":\"2007\",\"v\":\"2007\"},{\"n\":\"2006\",\"v\":\"2006\"},{\"n\":\"2005\",\"v\":\"2005\"},{\"n\":\"2004\",\"v\":\"2004\"},{\"n\":\"2003\",\"v\":\"2003\"},{\"n\":\"2002\",\"v\":\"2002\"},{\"n\":\"2001\",\"v\":\"2001\"},{\"n\":\"2000\",\"v\":\"2000\"}]},{\"key\":\"4\",\"name\":\"语言\",\"value\":[{\"n\":\"全部\",\"v\":\"\"},{\"n\":\"国语\",\"v\":\"国语\"},{\"n\":\"日语\",\"v\":\"日语\"},{\"n\":\"英语\",\"v\":\"英语\"},{\"n\":\"其他\",\"v\":\"其他\"}]},{\"key\":\"2\",\"name\":\"排序\",\"value\":[{\"n\":\"时间\",\"v\":\"time\"},{\"n\":\"人气\",\"v\":\"hits\"},{\"n\":\"评分\",\"v\":\"score\"}]}]}";
    private static string Field(JsonElement value,string name)=>value.TryGetProperty(name,out var p)?p.ValueKind==JsonValueKind.String?p.GetString()??"":p.ToString():"";
    private static Uri HttpUri(string value)=>Uri.TryCreate(value,UriKind.Absolute,out var uri)&&uri.Scheme is "http" or "https"&&uri.UserInfo.Length==0?uri:throw new InvalidDataException("Libvio HTTP(S)地址无效。");
    private static void ValidatePage(int page) {if(page<1||page==int.MaxValue)throw new ArgumentOutOfRangeException(nameof(page));}
}

[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string,string>))]
internal sealed partial class LibvioJson : System.Text.Json.Serialization.JsonSerializerContext;
