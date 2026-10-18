using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using VodBox.Core;

namespace VodBox.Infrastructure;

/// <summary>YCyz HTML catalogue and signed anonymous /lines protocol.</summary>
public sealed class YCyzSource : IResolvingContentSource
{
    private const string Agent="Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/148.0.0.0 Safari/537.36";
    private static readonly HttpClient Http=new(new HttpClientHandler {AutomaticDecompression=DecompressionMethods.All}) {Timeout=TimeSpan.FromSeconds(60)};
    private readonly SourceInfo _site;
    private readonly Uri _host;
    private readonly Func<string,CancellationToken,Task<string>> _fetch;
    private readonly Func<long> _clock;
    private readonly Func<string,IReadOnlyDictionary<string,string>,CancellationToken,Task<string>> _post;
    public string Key=>_site.Key;
    public string Name=>_site.Name;
    private static readonly Category[] Categories=[new("0","电影"),new("1","电视剧"),new("1#中国大陆","大陆剧"),new("1@gangtaiju","港台剧"),new("1@hanju","韩剧"),new("1@meiju","美剧"),new("1@riju","日剧"),new("1@donghua","动画"),new("1@zongyi","综艺")];
    public YCyzSource(SourceInfo site):this(site,null,null) { }
    internal YCyzSource(SourceInfo site,Func<string,CancellationToken,Task<string>>? fetch,Func<long>? clock=null,Func<string,IReadOnlyDictionary<string,string>,CancellationToken,Task<string>>? post=null)
    {
        if(!Supports(site.Ext))throw new NotSupportedException("YCyz ext 配置不受支持。");
        _site=site;_host=new("https://v.xl01.cc.ua/");_fetch=fetch??Fetch;_post=post??Post;_clock=clock??(()=>DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }
    internal static bool Supports(string? ext)
    {
        if(string.IsNullOrWhiteSpace(ext))return true;
        try {using var json=JsonDocument.Parse(FtyExtCodec.JsonConfig(ext));return json.RootElement.ValueKind==JsonValueKind.Object&&!json.RootElement.EnumerateObject().Any();}
        catch(Exception e) when(e is JsonException or InvalidDataException or FormatException) {return false;}
    }
    private static Regex Rx(string pattern)=>HtmlRegex.Get(pattern);
    private static string Text(string html)=>WebUtility.HtmlDecode(Rx("<[^>]+>").Replace(html," ")).Trim();
    private static string Attr(string html,string name)=>WebUtility.HtmlDecode(Rx("\\b"+Regex.Escape(name)+"\\s*=\\s*([\"'])(.*?)\\1").Match(html).Groups[2].Value);
    private static IEnumerable<string> Bodies(string html,string tag,string css)
    {
        var openings=Rx("<"+tag+"\\b[^>]*class=[\"'][^\"']*\\b"+Regex.Escape(css)+"\\b[^\"']*[\"'][^>]*>").Matches(html);
        foreach(Match opening in openings)
        {
            int start=opening.Index+opening.Length,depth=1;
            foreach(Match token in Rx("</?"+tag+"\\b[^>]*>").Matches(html,start))
            {
                if(token.Value.StartsWith("</",StringComparison.Ordinal))depth--;else if(!token.Value.EndsWith("/>",StringComparison.Ordinal))depth++;
                if(depth==0){yield return html[start..token.Index];break;}
            }
        }
    }
    private static string Body(string html,string tag,string css)=>Bodies(html,tag,css).FirstOrDefault()??"";
    private string Url(string path)=>new Uri(_host,path).AbsoluteUri;
    private async Task<string> Post(string url,IReadOnlyDictionary<string,string> fields,CancellationToken ct)
    {
        using var request=new HttpRequestMessage(HttpMethod.Post,HttpUri(url)){Content=new FormUrlEncodedContent(fields)};
        request.Headers.TryAddWithoutValidation("User-Agent",Agent);request.Headers.TryAddWithoutValidation("Origin",_host.GetLeftPart(UriPartial.Authority));request.Headers.TryAddWithoutValidation("Referer",_host.AbsoluteUri);
        using var response=await Http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct).ConfigureAwait(false);response.EnsureSuccessStatusCode();
        await using var stream=await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return Encoding.UTF8.GetString(await BoundedContent.ReadAsync(stream,4*1024*1024,ct).ConfigureAwait(false));
    }
    private async Task<string> Fetch(string url,CancellationToken ct)
    {
        using var request=new HttpRequestMessage(HttpMethod.Get,HttpUri(url));
        request.Headers.TryAddWithoutValidation("User-Agent",Agent);request.Headers.TryAddWithoutValidation("Origin",_host.GetLeftPart(UriPartial.Authority));request.Headers.TryAddWithoutValidation("Referer",_host.AbsoluteUri);
        using var response=await Http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct).ConfigureAwait(false);response.EnsureSuccessStatusCode();
        await using var stream=await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return Encoding.UTF8.GetString(await BoundedContent.ReadAsync(stream,4*1024*1024,ct).ConfigureAwait(false));
    }
    public Task<IReadOnlyList<Category>> GetCategoriesAsync(CancellationToken ct=default) {ct.ThrowIfCancellationRequested();return Task.FromResult<IReadOnlyList<Category>>(Categories);}
    public Task<MediaPage> GetHomeAsync(CancellationToken ct=default) {ct.ThrowIfCancellationRequested();return Task.FromResult(new MediaPage([],1,1));}
    public Task<IReadOnlyList<FilterGroup>> GetFiltersAsync(string categoryId,CancellationToken ct=default)
    {
        ct.ThrowIfCancellationRequested();
        if(categoryId is not ("0" or "1"))return Task.FromResult<IReadOnlyList<FilterGroup>>([]);
        using var document=JsonDocument.Parse(FilterJson);
        return Task.FromResult<IReadOnlyList<FilterGroup>>(document.RootElement.GetProperty(categoryId).EnumerateArray().Select(g=>new FilterGroup(Field(g,"key"),Field(g,"name"),g.GetProperty("value").EnumerateArray().Select(v=>new FilterValue(Field(v,"n"),Field(v,"v"))).ToList(),g.GetProperty("value")[0].GetProperty("v").GetString()??"")).ToArray());
    }
    public async Task<MediaPage> GetItemsAsync(string categoryId,int page,IReadOnlyDictionary<string,string>? filters,CancellationToken ct=default)
    {
        ValidatePage(page);if(!Categories.Any(c=>c.Id==categoryId))throw new InvalidDataException("YCyz 分类无效。");
        string type=categoryId.Split('@','#')[0],subtype=categoryId.Contains('@')?categoryId.Split('@')[1]:"all",area=categoryId.Contains('#')?categoryId.Split('#')[1]:"",year="",order="0";
        if(filters is not null)foreach(var pair in filters)
        {
            if(pair.Value.Length>128||pair.Value.Any(char.IsControl))throw new InvalidDataException("YCyz 筛选无效。");
            switch(pair.Key){case "s":subtype=pair.Value.Length>0?pair.Value:"all";break;case "area":area=pair.Value;break;case "year":year=pair.Value;break;case "order":order=pair.Value;break;default:throw new InvalidDataException("YCyz 筛选无效。");}
        }
        // 站点现在的列表地址是 /s/{s}[/{page}]?type=…（第 1 页不带页码），并且不再使用多余的 “?&” 前缀。
        string path="s/"+Uri.EscapeDataString(subtype)+(page>1?"/"+page.ToString(CultureInfo.InvariantCulture):"")+"?type="+type+(area.Length>0?"&area="+Uri.EscapeDataString(area):"")+(year.Length>0?"&year="+Uri.EscapeDataString(year):"")+(order.Length>0&&order!="0"?"&order="+Uri.EscapeDataString(order):"");
        var cards=ParseCards(await _fetch(Url(path),ct).ConfigureAwait(false));return new(cards,page,cards.Count>0?page+1:page);
    }
    private static IReadOnlyList<MediaItem> ParseCards(string html)
    {
        var result=new List<MediaItem>();
        foreach(string card in Bodies(html,"div","movie-card"))
        {
            string anchor=Rx("<a\\b[^>]*href=[\"'][^\"']*htm[^\"']*[\"'][^>]*>").Match(card).Value,href=Attr(anchor,"href");
            string title=Text(Rx("<h4\\b[^>]*>(.*?)</h4>").Match(Body(card,"div","card-info")).Groups[1].Value);
            if(!href.StartsWith('/')||title.Length==0)continue;
            string image=Attr(Rx("<img\\b[^>]*data-src=[\"'][^\"']*[\"'][^>]*>").Match(card).Value,"data-src");
            result.Add(new() {Id=href[1..],Title=title,Poster=image,Remarks=Text(Body(card,"div","card-meta"))});
        }
        return result.DistinctBy(v=>v.Id).ToArray();
    }
    public async Task<MediaPage> SearchAsync(string query,int page,CancellationToken ct=default)
    {
        ValidatePage(page);if(page>1||string.IsNullOrWhiteSpace(query))return new([],page,page);
        if(query.Length>256)throw new ArgumentException("搜索词过长。",nameof(query));
        string text=await _fetch("http://czzy.xn--t75a.eu.org/bidisearch.php?wd="+Uri.EscapeDataString(query),ct).ConfigureAwait(false);
        return new(text.Split("$$$",StringSplitOptions.RemoveEmptyEntries).Select(v=>v.Split('|')).Where(v=>v.Length>=4&&v[1].Length>0).Select(v=>new MediaItem {Id=v[0],Title=v[1],Poster=v[2],Remarks=v[3]}).ToArray(),1,1);
    }
    private readonly DetailCache _details=new(TimeSpan.FromMinutes(5));
    public Task<MediaDetail> GetDetailAsync(string mediaId,CancellationToken ct=default)=>_details.GetAsync(mediaId,LoadDetailAsync,ct);
    private async Task<MediaDetail> LoadDetailAsync(string mediaId,CancellationToken ct)
    {
        ValidateId(mediaId);string html=await _fetch(Url(mediaId),ct).ConfigureAwait(false);
        string facts=Body(html,"div","info-list");
        string Fact(string label)
        {
            foreach(string item in Bodies(facts,"div","info-item"))
            {
                string heading=Text(Body(item,"span","info-label"));if(!heading.Contains(label,StringComparison.Ordinal))continue;
                return string.Join('/',Rx("<a\\b[^>]*>(.*?)</a>").Matches(item).Select(m=>Text(m.Groups[1].Value)));
            }return "";
        }
        var episodes=Rx("<a\\b[^>]*href=[\"']([^\"']*htm[^\"']*)[\"'][^>]*>(.*?)</a>").Matches(Body(html,"div","play-list")).Where(m=>m.Groups[1].Value.StartsWith('/'))
            .Select(m=>new Episode("自动\u0001"+m.Groups[1].Value[1..],Text(m.Groups[2].Value))).ToArray();
        string title=Text(Rx("<h1\\b[^>]*>(.*?)</h1>").Match(html).Groups[1].Value);
        return new() {Item=new() {Id=mediaId,Title=title.Length>0?title:mediaId,TypeName=Fact("类型")},Actor=Fact("主演"),Director=Fact("导演"),Description=Text(Body(html,"div","desc")),Lines=episodes.Length>0?[new("自动","自动",episodes)]:[]};
    }
    public async Task<PlaybackRequest> ResolvePlaybackAsync(string mediaId,string episodeId,CancellationToken ct=default)
    {
        var detail=await GetDetailAsync(mediaId,ct).ConfigureAwait(false);
        if(!detail.Lines.Any(l=>l.Episodes.Any(e=>e.Id==episodeId)))throw new InvalidDataException("YCyz 选集已失效。");
        string html=await _fetch(Url(episodeId[(episodeId.IndexOf('\u0001')+1)..]),ct).ConfigureAwait(false);
        var pid=Rx("\\bvar\\s+pid\\s*=\\s*['\"]?([^;'\"\\s]+)['\"]?\\s*;").Match(html);
        if(!pid.Success)throw new InvalidDataException("YCyz 播放页缺少 pid。");
        long time=_clock();string signature=Sign(pid.Groups[1].Value,time);
        using var json=JsonDocument.Parse(await _fetch(Url("lines?t="+time.ToString(CultureInfo.InvariantCulture)+"&sg="+signature+"&pid="+Uri.EscapeDataString(pid.Groups[1].Value)),ct).ConfigureAwait(false));
        var data=json.RootElement.GetProperty("data");
        var candidates=new[]{Field(data,"url3"),Field(data,"m3u8"),Field(data,"m3u8_2")}.SelectMany(v=>v.Split(',',StringSplitOptions.RemoveEmptyEntries)).Select(v=>v.Replace("www.bde4.cc",_host.Host,StringComparison.Ordinal)).Where(v=>Uri.TryCreate(v,UriKind.Absolute,out var uri)&&uri.Scheme is "http" or "https").Distinct(StringComparer.Ordinal).ToArray();
        if(candidates.Length==0&&Field(data,"ptoken")=="888")
        {
            // 原插件：lines 没给出地址且 ptoken=888 时，用同一组 t/sg 向 /god/{pid} 换取地址（参考实现按 SN+"/god/" 拼接，保留双斜杠）。
            string god=await _post(_host.AbsoluteUri+"/god/"+Uri.EscapeDataString(pid.Groups[1].Value),new Dictionary<string,string>{{"t",time.ToString(CultureInfo.InvariantCulture)},{"sg",signature},{"verifyCode","888"}},ct).ConfigureAwait(false);
            using var answer=JsonDocument.Parse(god);
            string value=GodUrl(Field(answer.RootElement,"url"));
            if(Uri.TryCreate(value,UriKind.Absolute,out var uri)&&uri.Scheme is "http" or "https")candidates=[value];
        }
        if(candidates.Length==0)throw new InvalidDataException("YCyz lines 没有媒体地址。");
        return new() {Uri=candidates[0],Title=detail.Item.Title,SourceKey=Key,SourceName=Name,MediaId=mediaId,EpisodeId=episodeId,LineId="自动",ProxyMedia=true,Headers=new Dictionary<string,string>{{"User-Agent",Agent},{"Referer",_host.AbsoluteUri}},Choices=candidates.Select((url,index)=>new PlaybackChoice("线路"+(index+1),url)).ToArray()};
    }
    private static readonly string[] BlockedCdn=["-f19","-f77","lf16-capcut.faceueditorv.com","lf16-secsdk.bitssec.com","lf16-data.bytepluscdn.com","lf16-fdm.fdmings.com","lf16-cc-everphoto.faceulv.com","lf16-faceulv.com","lf16-geckocdn-offline.g-p-static.com"];
    /// <summary>原插件对 /god 返回地址的 CDN 改写：tiktokcdn→akamaized；tos 对象按是否命中受限 CDN 换到镜像前缀。</summary>
    internal static string GodUrl(string url)
    {
        if(url.Length==0)return url;
        url=url.Replace("v16m-default.tiktokcdn.com","v16m-default.akamaized.net",StringComparison.Ordinal);
        bool blocked=BlockedCdn.Any(value=>url.Contains(value,StringComparison.Ordinal));
        const string mirror="https://p16-hera-sign-va.ibyteimg.com/obj/";
        int alisg=url.IndexOf("tos-alisg-v-0000",StringComparison.Ordinal);
        if(alisg>-1)return (blocked?"http://lf77-geckocdn.bytegecko-i18n.com/obj/":mirror)+url[alisg..];
        int maliva=url.IndexOf("tos-maliva-o-0000-us",StringComparison.Ordinal);
        if(maliva>-1)return (blocked?"http://lf16-cdn-solve.gauthmath.com/obj/":mirror)+url[maliva..];
        return url;
    }
    internal static string Sign(string pid,long time)
    {
        string input=pid+"-"+time.ToString(CultureInfo.InvariantCulture);
        string key=Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant()[..16];
        using var aes=Aes.Create();aes.Key=Encoding.UTF8.GetBytes(key);
        return Convert.ToHexString(aes.EncryptEcb(Encoding.UTF8.GetBytes(input),PaddingMode.PKCS7));
    }
    private static string Field(JsonElement value,string name)=>value.TryGetProperty(name,out var p)?p.ValueKind==JsonValueKind.String?p.GetString()??"":p.ToString():"";
    private static Uri HttpUri(string value)=>Uri.TryCreate(value,UriKind.Absolute,out var uri)&&uri.Scheme is "http" or "https"&&uri.UserInfo.Length==0?uri:throw new InvalidDataException("YCyz HTTP(S)地址无效。");
    private static void ValidateId(string value) {if(!Rx("^[\\w/-]+\\.html?$").IsMatch(value)||value.Contains("..",StringComparison.Ordinal))throw new InvalidDataException("YCyz 影片标识无效。");}
    private static void ValidatePage(int page) {if(page<1||page==int.MaxValue)throw new ArgumentOutOfRangeException(nameof(page));}
    private const string FilterJson="{\"0\":[{\"key\":\"s\",\"name\":\"类型\",\"value\":[{\"n\":\"全部\",\"v\":\"all\"},{\"n\":\"动作\",\"v\":\"dongzuo\"},{\"n\":\"爱情\",\"v\":\"aiqing\"},{\"n\":\"喜剧\",\"v\":\"xiju\"},{\"n\":\"科幻\",\"v\":\"kehuan\"},{\"n\":\"恐怖\",\"v\":\"kongbu\"},{\"n\":\"战争\",\"v\":\"zhanzheng\"},{\"n\":\"武侠\",\"v\":\"wuxia\"},{\"n\":\"魔幻\",\"v\":\"mohuan\"},{\"n\":\"剧情\",\"v\":\"juqing\"},{\"n\":\"动画\",\"v\":\"donghua\"},{\"n\":\"惊悚\",\"v\":\"jingsong\"},{\"n\":\"3D\",\"v\":\"3D\"},{\"n\":\"灾难\",\"v\":\"zainan\"},{\"n\":\"悬疑\",\"v\":\"xuanyi\"},{\"n\":\"警匪\",\"v\":\"jingfei\"},{\"n\":\"文艺\",\"v\":\"wenyi\"},{\"n\":\"青春\",\"v\":\"qingchun\"},{\"n\":\"冒险\",\"v\":\"maoxian\"},{\"n\":\"犯罪\",\"v\":\"fanzui\"},{\"n\":\"纪录\",\"v\":\"jilu\"},{\"n\":\"古装\",\"v\":\"guzhuang\"},{\"n\":\"奇幻\",\"v\":\"qihuan\"},{\"n\":\"国语\",\"v\":\"guoyu\"},{\"n\":\"综艺\",\"v\":\"zongyi\"},{\"n\":\"历史\",\"v\":\"lishi\"},{\"n\":\"运动\",\"v\":\"yundong\"},{\"n\":\"原创压制\",\"v\":\"yuanchuang\"},{\"n\":\"美剧\",\"v\":\"meiju\"},{\"n\":\"韩剧\",\"v\":\"hanju\"},{\"n\":\"国产电视剧\",\"v\":\"guoju\"},{\"n\":\"日剧\",\"v\":\"riju\"},{\"n\":\"英剧\",\"v\":\"yingju\"},{\"n\":\"德剧\",\"v\":\"deju\"},{\"n\":\"俄剧\",\"v\":\"eju\"},{\"n\":\"巴剧\",\"v\":\"baju\"},{\"n\":\"加剧\",\"v\":\"jiaju\"},{\"n\":\"西剧\",\"v\":\"anish\"},{\"n\":\"意大利剧\",\"v\":\"yidaliju\"},{\"n\":\"泰剧\",\"v\":\"taiju\"},{\"n\":\"港台剧\",\"v\":\"gangtaiju\"},{\"n\":\"法剧\",\"v\":\"faju\"},{\"n\":\"澳剧\",\"v\":\"aoju\"}]},{\"key\":\"area\",\"name\":\"地区\",\"value\":[{\"n\":\"不限\",\"v\":\"\"},{\"n\":\"中国大陆\",\"v\":\"中国大陆\"},{\"n\":\"中国香港\",\"v\":\"中国香港\"},{\"n\":\"中国台湾\",\"v\":\"中国台湾\"},{\"n\":\"美国\",\"v\":\"美国\"},{\"n\":\"英国\",\"v\":\"英国\"},{\"n\":\"日本\",\"v\":\"日本\"},{\"n\":\"韩国\",\"v\":\"韩国\"},{\"n\":\"法国\",\"v\":\"法国\"},{\"n\":\"印度\",\"v\":\"印度\"},{\"n\":\"德国\",\"v\":\"德国\"},{\"n\":\"西班牙\",\"v\":\"西班牙\"},{\"n\":\"意大利\",\"v\":\"意大利\"},{\"n\":\"澳大利亚\",\"v\":\"澳大利亚\"},{\"n\":\"比利时\",\"v\":\"比利时\"},{\"n\":\"瑞典\",\"v\":\"瑞典\"},{\"n\":\"荷兰\",\"v\":\"荷兰\"},{\"n\":\"丹麦\",\"v\":\"丹麦\"},{\"n\":\"加拿大\",\"v\":\"加拿大\"},{\"n\":\"俄罗斯\",\"v\":\"俄罗斯\"}]},{\"key\":\"year\",\"name\":\"年份\",\"value\":[{\"n\":\"全部\",\"v\":\"\"},{\"n\":\"2026\",\"v\":\"2026\"},{\"n\":\"2025\",\"v\":\"2025\"},{\"n\":\"2024\",\"v\":\"2024\"},{\"n\":\"2023\",\"v\":\"2023\"},{\"n\":\"2022\",\"v\":\"2022\"},{\"n\":\"2021\",\"v\":\"2021\"},{\"n\":\"2020\",\"v\":\"2020\"},{\"n\":\"2019\",\"v\":\"2019\"},{\"n\":\"2018\",\"v\":\"2018\"},{\"n\":\"2017\",\"v\":\"2017\"},{\"n\":\"2016\",\"v\":\"2016\"},{\"n\":\"2015\",\"v\":\"2015\"},{\"n\":\"2014\",\"v\":\"2014\"},{\"n\":\"2013\",\"v\":\"2013\"},{\"n\":\"2012\",\"v\":\"2012\"},{\"n\":\"2011\",\"v\":\"2011\"},{\"n\":\"2010\",\"v\":\"2010\"},{\"n\":\"2009\",\"v\":\"2009\"},{\"n\":\"2008\",\"v\":\"2008\"},{\"n\":\"2007\",\"v\":\"2007\"},{\"n\":\"2006\",\"v\":\"2006\"},{\"n\":\"2005\",\"v\":\"2005\"},{\"n\":\"2004\",\"v\":\"2004\"},{\"n\":\"2003\",\"v\":\"2003\"},{\"n\":\"2002\",\"v\":\"2002\"},{\"n\":\"2001\",\"v\":\"2001\"},{\"n\":\"2000\",\"v\":\"2000\"}]},{\"key\":\"order\",\"name\":\"排序\",\"value\":[{\"n\":\"更新时间\",\"v\":\"0\"},{\"n\":\"豆瓣评分\",\"v\":\"1\"}]}],\"1\":[{\"key\":\"s\",\"name\":\"类型\",\"value\":[{\"n\":\"全部\",\"v\":\"all\"},{\"n\":\"动作\",\"v\":\"dongzuo\"},{\"n\":\"爱情\",\"v\":\"aiqing\"},{\"n\":\"喜剧\",\"v\":\"xiju\"},{\"n\":\"科幻\",\"v\":\"kehuan\"},{\"n\":\"恐怖\",\"v\":\"kongbu\"},{\"n\":\"战争\",\"v\":\"zhanzheng\"},{\"n\":\"武侠\",\"v\":\"wuxia\"},{\"n\":\"魔幻\",\"v\":\"mohuan\"},{\"n\":\"剧情\",\"v\":\"juqing\"},{\"n\":\"动画\",\"v\":\"donghua\"},{\"n\":\"惊悚\",\"v\":\"jingsong\"},{\"n\":\"3D\",\"v\":\"3D\"},{\"n\":\"灾难\",\"v\":\"zainan\"},{\"n\":\"悬疑\",\"v\":\"xuanyi\"},{\"n\":\"警匪\",\"v\":\"jingfei\"},{\"n\":\"文艺\",\"v\":\"wenyi\"},{\"n\":\"青春\",\"v\":\"qingchun\"},{\"n\":\"冒险\",\"v\":\"maoxian\"},{\"n\":\"犯罪\",\"v\":\"fanzui\"},{\"n\":\"纪录\",\"v\":\"jilu\"},{\"n\":\"古装\",\"v\":\"guzhuang\"},{\"n\":\"奇幻\",\"v\":\"qihuan\"},{\"n\":\"国语\",\"v\":\"guoyu\"},{\"n\":\"综艺\",\"v\":\"zongyi\"},{\"n\":\"历史\",\"v\":\"lishi\"},{\"n\":\"运动\",\"v\":\"yundong\"},{\"n\":\"原创压制\",\"v\":\"yuanchuang\"},{\"n\":\"美剧\",\"v\":\"meiju\"},{\"n\":\"韩剧\",\"v\":\"hanju\"},{\"n\":\"国产电视剧\",\"v\":\"guoju\"},{\"n\":\"日剧\",\"v\":\"riju\"},{\"n\":\"英剧\",\"v\":\"yingju\"},{\"n\":\"德剧\",\"v\":\"deju\"},{\"n\":\"俄剧\",\"v\":\"eju\"},{\"n\":\"巴剧\",\"v\":\"baju\"},{\"n\":\"加剧\",\"v\":\"jiaju\"},{\"n\":\"西剧\",\"v\":\"anish\"},{\"n\":\"意大利剧\",\"v\":\"yidaliju\"},{\"n\":\"泰剧\",\"v\":\"taiju\"},{\"n\":\"港台剧\",\"v\":\"gangtaiju\"},{\"n\":\"法剧\",\"v\":\"faju\"},{\"n\":\"澳剧\",\"v\":\"aoju\"}]},{\"key\":\"area\",\"name\":\"地区\",\"value\":[{\"n\":\"不限\",\"v\":\"\"},{\"n\":\"中国大陆\",\"v\":\"中国大陆\"},{\"n\":\"中国香港\",\"v\":\"中国香港\"},{\"n\":\"中国台湾\",\"v\":\"中国台湾\"},{\"n\":\"美国\",\"v\":\"美国\"},{\"n\":\"英国\",\"v\":\"英国\"},{\"n\":\"日本\",\"v\":\"日本\"},{\"n\":\"韩国\",\"v\":\"韩国\"},{\"n\":\"法国\",\"v\":\"法国\"},{\"n\":\"印度\",\"v\":\"印度\"},{\"n\":\"德国\",\"v\":\"德国\"},{\"n\":\"西班牙\",\"v\":\"西班牙\"},{\"n\":\"意大利\",\"v\":\"意大利\"},{\"n\":\"澳大利亚\",\"v\":\"澳大利亚\"},{\"n\":\"比利时\",\"v\":\"比利时\"},{\"n\":\"瑞典\",\"v\":\"瑞典\"},{\"n\":\"荷兰\",\"v\":\"荷兰\"},{\"n\":\"丹麦\",\"v\":\"丹麦\"},{\"n\":\"加拿大\",\"v\":\"加拿大\"},{\"n\":\"俄罗斯\",\"v\":\"俄罗斯\"}]},{\"key\":\"year\",\"name\":\"年份\",\"value\":[{\"n\":\"全部\",\"v\":\"\"},{\"n\":\"2026\",\"v\":\"2026\"},{\"n\":\"2025\",\"v\":\"2025\"},{\"n\":\"2024\",\"v\":\"2024\"},{\"n\":\"2023\",\"v\":\"2023\"},{\"n\":\"2022\",\"v\":\"2022\"},{\"n\":\"2021\",\"v\":\"2021\"},{\"n\":\"2020\",\"v\":\"2020\"},{\"n\":\"2019\",\"v\":\"2019\"},{\"n\":\"2018\",\"v\":\"2018\"},{\"n\":\"2017\",\"v\":\"2017\"},{\"n\":\"2016\",\"v\":\"2016\"},{\"n\":\"2015\",\"v\":\"2015\"},{\"n\":\"2014\",\"v\":\"2014\"},{\"n\":\"2013\",\"v\":\"2013\"},{\"n\":\"2012\",\"v\":\"2012\"},{\"n\":\"2011\",\"v\":\"2011\"},{\"n\":\"2010\",\"v\":\"2010\"},{\"n\":\"2009\",\"v\":\"2009\"},{\"n\":\"2008\",\"v\":\"2008\"},{\"n\":\"2007\",\"v\":\"2007\"},{\"n\":\"2006\",\"v\":\"2006\"},{\"n\":\"2005\",\"v\":\"2005\"},{\"n\":\"2004\",\"v\":\"2004\"},{\"n\":\"2003\",\"v\":\"2003\"},{\"n\":\"2002\",\"v\":\"2002\"},{\"n\":\"2001\",\"v\":\"2001\"},{\"n\":\"2000\",\"v\":\"2000\"}]},{\"key\":\"order\",\"name\":\"排序\",\"value\":[{\"n\":\"更新时间\",\"v\":\"0\"},{\"n\":\"豆瓣评分\",\"v\":\"1\"}]}],\"a\":[{\"key\":\"s\",\"name\":\"类型\",\"value\":[{\"n\":\"全部\",\"v\":\"all\"},{\"n\":\"动作\",\"v\":\"dongzuo\"},{\"n\":\"爱情\",\"v\":\"aiqing\"},{\"n\":\"喜剧\",\"v\":\"xiju\"},{\"n\":\"科幻\",\"v\":\"kehuan\"},{\"n\":\"恐怖\",\"v\":\"kongbu\"},{\"n\":\"战争\",\"v\":\"zhanzheng\"},{\"n\":\"武侠\",\"v\":\"wuxia\"},{\"n\":\"魔幻\",\"v\":\"mohuan\"},{\"n\":\"剧情\",\"v\":\"juqing\"},{\"n\":\"动画\",\"v\":\"donghua\"},{\"n\":\"惊悚\",\"v\":\"jingsong\"},{\"n\":\"3D\",\"v\":\"3D\"},{\"n\":\"灾难\",\"v\":\"zainan\"},{\"n\":\"悬疑\",\"v\":\"xuanyi\"},{\"n\":\"警匪\",\"v\":\"jingfei\"},{\"n\":\"文艺\",\"v\":\"wenyi\"},{\"n\":\"青春\",\"v\":\"qingchun\"},{\"n\":\"冒险\",\"v\":\"maoxian\"},{\"n\":\"犯罪\",\"v\":\"fanzui\"},{\"n\":\"纪录\",\"v\":\"jilu\"},{\"n\":\"古装\",\"v\":\"guzhuang\"},{\"n\":\"奇幻\",\"v\":\"qihuan\"},{\"n\":\"国语\",\"v\":\"guoyu\"},{\"n\":\"综艺\",\"v\":\"zongyi\"},{\"n\":\"历史\",\"v\":\"lishi\"},{\"n\":\"运动\",\"v\":\"yundong\"},{\"n\":\"原创压制\",\"v\":\"yuanchuang\"},{\"n\":\"美剧\",\"v\":\"meiju\"},{\"n\":\"韩剧\",\"v\":\"hanju\"},{\"n\":\"国产电视剧\",\"v\":\"guoju\"},{\"n\":\"日剧\",\"v\":\"riju\"},{\"n\":\"英剧\",\"v\":\"yingju\"},{\"n\":\"德剧\",\"v\":\"deju\"},{\"n\":\"俄剧\",\"v\":\"eju\"},{\"n\":\"巴剧\",\"v\":\"baju\"},{\"n\":\"加剧\",\"v\":\"jiaju\"},{\"n\":\"西剧\",\"v\":\"anish\"},{\"n\":\"意大利剧\",\"v\":\"yidaliju\"},{\"n\":\"泰剧\",\"v\":\"taiju\"},{\"n\":\"港台剧\",\"v\":\"gangtaiju\"},{\"n\":\"法剧\",\"v\":\"faju\"},{\"n\":\"澳剧\",\"v\":\"aoju\"}]},{\"key\":\"area\",\"name\":\"地区\",\"value\":[{\"n\":\"不限\",\"v\":\"\"},{\"n\":\"中国大陆\",\"v\":\"中国大陆\"},{\"n\":\"中国香港\",\"v\":\"中国香港\"},{\"n\":\"中国台湾\",\"v\":\"中国台湾\"},{\"n\":\"美国\",\"v\":\"美国\"},{\"n\":\"英国\",\"v\":\"英国\"},{\"n\":\"日本\",\"v\":\"日本\"},{\"n\":\"韩国\",\"v\":\"韩国\"},{\"n\":\"法国\",\"v\":\"法国\"},{\"n\":\"印度\",\"v\":\"印度\"},{\"n\":\"德国\",\"v\":\"德国\"},{\"n\":\"西班牙\",\"v\":\"西班牙\"},{\"n\":\"意大利\",\"v\":\"意大利\"},{\"n\":\"澳大利亚\",\"v\":\"澳大利亚\"},{\"n\":\"比利时\",\"v\":\"比利时\"},{\"n\":\"瑞典\",\"v\":\"瑞典\"},{\"n\":\"荷兰\",\"v\":\"荷兰\"},{\"n\":\"丹麦\",\"v\":\"丹麦\"},{\"n\":\"加拿大\",\"v\":\"加拿大\"},{\"n\":\"俄罗斯\",\"v\":\"俄罗斯\"}]},{\"key\":\"year\",\"name\":\"年份\",\"value\":[{\"n\":\"全部\",\"v\":\"\"},{\"n\":\"2026\",\"v\":\"2026\"},{\"n\":\"2025\",\"v\":\"2025\"},{\"n\":\"2024\",\"v\":\"2024\"},{\"n\":\"2023\",\"v\":\"2023\"},{\"n\":\"2022\",\"v\":\"2022\"},{\"n\":\"2021\",\"v\":\"2021\"},{\"n\":\"2020\",\"v\":\"2020\"},{\"n\":\"2019\",\"v\":\"2019\"},{\"n\":\"2018\",\"v\":\"2018\"},{\"n\":\"2017\",\"v\":\"2017\"},{\"n\":\"2016\",\"v\":\"2016\"},{\"n\":\"2015\",\"v\":\"2015\"},{\"n\":\"2014\",\"v\":\"2014\"},{\"n\":\"2013\",\"v\":\"2013\"},{\"n\":\"2012\",\"v\":\"2012\"},{\"n\":\"2011\",\"v\":\"2011\"},{\"n\":\"2010\",\"v\":\"2010\"},{\"n\":\"2009\",\"v\":\"2009\"},{\"n\":\"2008\",\"v\":\"2008\"},{\"n\":\"2007\",\"v\":\"2007\"},{\"n\":\"2006\",\"v\":\"2006\"},{\"n\":\"2005\",\"v\":\"2005\"},{\"n\":\"2004\",\"v\":\"2004\"},{\"n\":\"2003\",\"v\":\"2003\"},{\"n\":\"2002\",\"v\":\"2002\"},{\"n\":\"2001\",\"v\":\"2001\"},{\"n\":\"2000\",\"v\":\"2000\"}]},{\"key\":\"order\",\"name\":\"排序\",\"value\":[{\"n\":\"更新时间\",\"v\":\"0\"},{\"n\":\"豆瓣评分\",\"v\":\"1\"}]}]}";
}
