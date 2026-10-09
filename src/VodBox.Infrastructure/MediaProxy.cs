using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Http;

namespace VodBox.Infrastructure;

/// <summary>仅代理应用主动注册的媒体，随机句柄不可枚举；支持HLS相对片段/密钥与Range。</summary>
public sealed class MediaProxy : IDisposable
{
    private sealed record Entry(Uri Uri, IReadOnlyDictionary<string, string> Headers, DateTimeOffset Expires);
    private readonly ConcurrentDictionary<string, Entry> _entries = new();
    private readonly Dictionary<string,string> _registrations = new(StringComparer.Ordinal);
    private readonly MediaCache? _cache;
    public MediaProxy(string? cacheDirectory=null) { if(cacheDirectory is not null)_cache=new MediaCache(cacheDirectory); }
    private readonly object _registrationGate = new();
    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
    public string Register(string url, IReadOnlyDictionary<string, string>? headers = null)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) throw new InvalidDataException("代理地址必须为HTTP(S)。");
        var safeHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in headers ?? new Dictionary<string, string>())
        {
            if (key is not ("User-Agent" or "Referer")) continue;
            if (value.IndexOfAny(['\r', '\n', '\0']) >= 0) throw new InvalidDataException("代理请求头非法。");
            safeHeaders[key] = value;
        }
        var registrationKey=uri.AbsoluteUri+"\n"+string.Join("\n",safeHeaders.OrderBy(header=>header.Key,StringComparer.OrdinalIgnoreCase).Select(header=>header.Key.ToLowerInvariant()+":"+header.Value));
        lock(_registrationGate)
        {
            if(_registrations.TryGetValue(registrationKey,out var existing)&&_entries.TryGetValue(existing,out var entry)&&entry.Expires>DateTimeOffset.UtcNow)
            {
                _entries[existing]=entry with { Expires=DateTimeOffset.UtcNow.AddHours(4) };
                return "/proxy/"+existing;
            }
            foreach(var expired in _entries.Where(item=>item.Value.Expires<=DateTimeOffset.UtcNow).ToArray())_entries.TryRemove(expired.Key,out _);
            foreach(var stale in _registrations.Where(item=>!_entries.ContainsKey(item.Value)).Select(item=>item.Key).ToArray())_registrations.Remove(stale);
            if(_entries.Count>=4096)throw new InvalidOperationException("代理媒体注册已达上限。");
            var id=Guid.NewGuid().ToString("N");
            _entries[id]=new Entry(uri,safeHeaders,DateTimeOffset.UtcNow.AddHours(4));
            _registrations[registrationKey]=id;
            return "/proxy/"+id;
        }
    }

    public async Task ServeCachedAsync(HttpContext context)
    {
        var id=context.Request.RouteValues["id"]?.ToString()??"";
        if(_cache is null||!_entries.TryGetValue(id,out var entry)||entry.Expires<=DateTimeOffset.UtcNow){context.Response.StatusCode=404;return;}
        // 清单保持实时重写，不缓存直播清单；Range走原始流式代理。
        if(context.Request.Headers.ContainsKey("Range")||entry.Uri.AbsolutePath.EndsWith(".m3u8",StringComparison.OrdinalIgnoreCase)||entry.Uri.AbsolutePath.EndsWith(".mpd",StringComparison.OrdinalIgnoreCase))
        {await ServeAsync(context);return;}
        var path=await _cache.GetAsync(entry.Uri.AbsoluteUri,entry.Headers,context.RequestAborted);
        context.Response.ContentType="application/octet-stream";context.Response.ContentLength=new FileInfo(path).Length;
        await context.Response.SendFileAsync(path,context.RequestAborted);
    }

    public async Task ServeAsync(HttpContext context)
    {
        var id = context.Request.RouteValues["id"]?.ToString() ?? "";
        if (!_entries.TryGetValue(id, out var entry) || entry.Expires <= DateTimeOffset.UtcNow) { context.Response.StatusCode = 404; return; }
        if(context.Request.Headers.TryGetValue("Range",out var requestedRange)&&!System.Net.Http.Headers.RangeHeaderValue.TryParse(requestedRange.ToString(),out _))
        {context.Response.StatusCode=400;return;}
        using var response = await FetchAsync(entry, context);
        if ((int)response.StatusCode is >= 300 and < 400) { context.Response.StatusCode = 502; return; }
        context.Response.StatusCode = (int)response.StatusCode;
        if (!response.IsSuccessStatusCode) return;
        var type = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
        var effectiveUri=response.RequestMessage?.RequestUri??entry.Uri;
        var hls = effectiveUri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) || type.Contains("mpegurl", StringComparison.OrdinalIgnoreCase);
        var dash = effectiveUri.AbsolutePath.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase) || type.Contains("dash+xml", StringComparison.OrdinalIgnoreCase);
        if (hls || dash)
        {
            if (response.Content.Headers.ContentLength > 2 * 1024 * 1024) { context.Response.StatusCode = 502; return; }
            await using var stream = await response.Content.ReadAsStreamAsync(context.RequestAborted);
            using var reader = new StreamReader(stream);
            var text = new System.Text.StringBuilder(); var buffer = new char[8192]; int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory(), context.RequestAborted)) > 0)
            {
                if (text.Length + count > 2 * 1024 * 1024) { context.Response.StatusCode = 502; return; }
                text.Append(buffer, 0, count);
            }
            context.Response.ContentType = dash ? "application/dash+xml" : "application/vnd.apple.mpegurl";
            string RegisterChild(string url)
            {
                var target = new Uri(url);
                // 清单指向别的源站时不继承原站Referer，避免跨源泄露。
                var headers = target.GetLeftPart(UriPartial.Authority) == entry.Uri.GetLeftPart(UriPartial.Authority)
                    ? entry.Headers : entry.Headers.Where(header => header.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase)).ToDictionary(header => header.Key, header => header.Value);
                return Register(url, headers);
            }
            var rewritten = dash ? RewriteDash(text.ToString(), effectiveUri, RegisterChild) : RewriteHls(text.ToString(), effectiveUri, RegisterChild);
            await context.Response.WriteAsync(rewritten, context.RequestAborted);
            return;
        }
        context.Response.ContentType = type;
        if (response.Content.Headers.ContentLength is { } length) context.Response.ContentLength = length;
        if (response.Content.Headers.ContentRange is { } contentRange) context.Response.Headers.ContentRange = contentRange.ToString();
        if (response.Headers.AcceptRanges.Count > 0) context.Response.Headers.AcceptRanges = string.Join(",", response.Headers.AcceptRanges);
        await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
    }

    private async Task<HttpResponseMessage> FetchAsync(Entry entry,HttpContext context)
    {
        var target=entry.Uri;
        for(var attempt=0;attempt<=5;attempt++)
        {
            using var request=new HttpRequestMessage(HttpMethod.Get,target);
            foreach(var (key,value) in entry.Headers)
                if(!key.Equals("Referer",StringComparison.OrdinalIgnoreCase)||target.GetLeftPart(UriPartial.Authority)==entry.Uri.GetLeftPart(UriPartial.Authority))
                    request.Headers.TryAddWithoutValidation(key,value);
            if(context.Request.Headers.TryGetValue("Range",out var range))
            {
                if(!System.Net.Http.Headers.RangeHeaderValue.TryParse(range.ToString(),out var parsed))throw new InvalidDataException("Range请求无效。");
                request.Headers.Range=parsed;
            }
            var response=await _http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,context.RequestAborted);
            if((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308)||response.Headers.Location is null)return response;
            var location=response.Headers.Location;
            var next=location.IsAbsoluteUri?location:new Uri(target,location);
            response.Dispose();
            if(next.Scheme is not ("http" or "https"))throw new InvalidDataException("媒体重定向使用不支持的协议。");
            if(attempt==5)throw new InvalidDataException("媒体重定向超过上限。");
            target=next;
        }
        throw new InvalidDataException("媒体重定向失败。");
    }

    internal static string RewriteHls(string playlist, Uri baseUri, Func<string, string> register)
    {
        if (!playlist.TrimStart().StartsWith("#EXTM3U", StringComparison.Ordinal)) throw new InvalidDataException("响应不是HLS清单。");
        var result = new List<string>();
        foreach (var raw in playlist.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (!line.StartsWith('#') && !string.IsNullOrWhiteSpace(line)) line = register(new Uri(baseUri, line.Trim()).AbsoluteUri);
            else
            {
                var cursor = 0;
                while (true)
                {
                    var marker = line.IndexOf("URI=\"", cursor, StringComparison.Ordinal);
                    if (marker < 0) break;
                    var start = marker + 5;
                    var end = line.IndexOf('"', start);
                    if (end <= start) throw new InvalidDataException("HLS URI属性缺少结束引号。");
                    var original = line[start..end];
                    if (original.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) { cursor = end + 1; continue; }
                    var replacement = register(new Uri(baseUri, original).AbsoluteUri);
                    line = line[..start] + replacement + line[end..];
                    cursor = start + replacement.Length + 1;
                }
            }
            result.Add(line);
        }
        return string.Join("\n", result);
    }

    /// <summary>静态SegmentList/SegmentBase清单；动态SegmentTemplate不能直接使用随机句柄，明确拒绝。</summary>
    internal static string RewriteDash(string manifest, Uri baseUri, Func<string, string> register)
    {
        using var input = new StringReader(manifest);
        using var reader = System.Xml.XmlReader.Create(input, new System.Xml.XmlReaderSettings
        {
            DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 2 * 1024 * 1024,
        });
        var document = System.Xml.Linq.XDocument.Load(reader);
        if (document.Root?.Name.LocalName != "MPD") throw new InvalidDataException("响应不是DASH清单。");
        ExpandStaticTemplates(document.Root);
        void Walk(System.Xml.Linq.XElement element, Uri inherited)
        {
            var localBase = element.Elements().FirstOrDefault(child => child.Name.LocalName == "BaseURL");
            var resolved = localBase is null ? inherited : new Uri(inherited, localBase.Value.Trim());
            foreach (var attribute in element.Attributes().Where(attribute => attribute.Name.LocalName is "media" or "sourceURL"))
                attribute.Value = register(new Uri(resolved, attribute.Value).AbsoluteUri);
            var children = element.Elements().Where(child => child.Name.LocalName != "BaseURL").ToArray();
            foreach (var child in children) Walk(child, resolved);
            if (localBase is not null)
            {
                if (element.Name.LocalName == "Representation" && !element.Descendants().Any(child => child.Name.LocalName == "SegmentURL"))
                    localBase.Value = register(resolved.AbsoluteUri);
                else localBase.Remove();
            }
        }
        Walk(document.Root, baseUri);
        return document.ToString(System.Xml.Linq.SaveOptions.DisableFormatting);
    }

    private static void ExpandStaticTemplates(System.Xml.Linq.XElement root)
    {
        if (root.Descendants().All(element => element.Name.LocalName != "SegmentTemplate")) return;
        var dynamicManifest=(string?)root.Attribute("type")=="dynamic";
        if (!root.Descendants().Any(element => element.Name.LocalName == "Representation")) throw new NotSupportedException("DASH模板缺少Representation。");
        double duration = 0;
        if (root.Attribute("mediaPresentationDuration") is { } durationValue)
            duration = System.Xml.XmlConvert.ToTimeSpan(durationValue.Value).TotalSeconds;
        foreach (var representation in root.Descendants().Where(element => element.Name.LocalName == "Representation").ToArray())
        {
            var inherited = representation.AncestorsAndSelf().SelectMany(element => element.Elements().Where(child => child.Name.LocalName == "SegmentTemplate")).ToArray();
            if (inherited.Length == 0) continue;
            var attributes = new Dictionary<string, string>();
            foreach (var template in inherited.Reverse())
                foreach (var attribute in template.Attributes()) attributes[attribute.Name.LocalName] = attribute.Value;
            if (!attributes.TryGetValue("media", out var media)) throw new InvalidDataException("DASH模板缺少media。");
            var period=representation.Ancestors().FirstOrDefault(element=>element.Name.LocalName=="Period");
            var periodDuration=duration;
            if(period?.Attribute("duration") is {} explicitPeriodDuration)periodDuration=System.Xml.XmlConvert.ToTimeSpan(explicitPeriodDuration.Value).TotalSeconds;
            long timescale = attributes.TryGetValue("timescale", out var scaleText) && long.TryParse(scaleText, out var scale) && scale > 0 ? scale : 1;
            long number = attributes.TryGetValue("startNumber", out var numberText) && long.TryParse(numberText, out var first) && first >= 0 ? first : 1;
            var list = new System.Xml.Linq.XElement(representation.Name.Namespace + "SegmentList", new System.Xml.Linq.XAttribute("timescale", timescale));
            long offset=0;
            if(attributes.TryGetValue("presentationTimeOffset",out var offsetText))
            {
                if(!long.TryParse(offsetText,out offset)||offset<0)throw new InvalidDataException("DASH时间偏移无效。");
                list.SetAttributeValue("presentationTimeOffset",offset);
            }
            list.SetAttributeValue("startNumber",number);
            string Expand(string template, long currentNumber, long time)
            {
                var result = template.Replace("$RepresentationID$", (string?)representation.Attribute("id") ?? "")
                    .Replace("$Bandwidth$", (string?)representation.Attribute("bandwidth") ?? "");
                foreach (var token in new[] { "Number", "Time" })
                {
                    var value = token == "Number" ? currentNumber : time;
                    result = result.Replace("$" + token + "$", value.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    var prefix = "$" + token + "%0";
                    var start = result.IndexOf(prefix, StringComparison.Ordinal);
                    if (start >= 0)
                    {
                        var end = result.IndexOf("d$", start, StringComparison.Ordinal);
                        if (end < 0 || !int.TryParse(result[(start + prefix.Length)..end], out var width) || width is < 1 or > 12)
                            throw new InvalidDataException("DASH模板数字格式无效。");
                        result = result[..start] + value.ToString("D" + width, System.Globalization.CultureInfo.InvariantCulture) + result[(end + 2)..];
                    }
                }
                result = result.Replace("$$", "\u0001");
                if (result.Contains('$')) throw new NotSupportedException("DASH模板含未知变量。");
                return result.Replace("\u0001", "$");
            }
            if (attributes.TryGetValue("initialization", out var initialization))
                list.Add(new System.Xml.Linq.XElement(representation.Name.Namespace + "Initialization", new System.Xml.Linq.XAttribute("sourceURL", Expand(initialization, number, 0))));
            var timeline = inherited.SelectMany(template => template.Elements()).FirstOrDefault(element => element.Name.LocalName == "SegmentTimeline");
            var points = new List<(long Time, long Duration)>();
            if (timeline is not null)
            {
                long time = 0;
                var segments=timeline.Elements().Where(element=>element.Name.LocalName=="S").ToArray();
                var normalized=new System.Xml.Linq.XElement(representation.Name.Namespace+"SegmentTimeline");
                for(var segmentIndex=0;segmentIndex<segments.Length;segmentIndex++)
                {
                    var segment=segments[segmentIndex];
                    if (segment.Attribute("t") is { } timeValue && !long.TryParse(timeValue.Value, out time)) throw new InvalidDataException("DASH时间无效。");
                    if (!long.TryParse((string?)segment.Attribute("d"), out var length) || length <= 0) throw new InvalidDataException("DASH片段时长无效。");
                    var repeat = 0;
                    if (segment.Attribute("r") is { } repeatValue && !int.TryParse(repeatValue.Value, out repeat)) throw new InvalidDataException("DASH重复次数无效。");
                    if(repeat<0)
                    {
                        if(repeat!=-1)throw new InvalidDataException("DASH重复次数无效。");
                        var end=periodDuration>0?periodDuration*timescale+offset:double.NaN;
                        if(segmentIndex+1<segments.Length&&segments[segmentIndex+1].Attribute("t") is {} nextTime&&long.TryParse(nextTime.Value,out var nextStart))end=nextStart;
                        var repeated=Math.Ceiling((end-time)/length)-1;
                        if(!double.IsFinite(repeated)||repeated<0||repeated>10000)throw new NotSupportedException("DASH负重复缺少有限结束时间。");
                        repeat=(int)repeated;
                    }
                    normalized.Add(new System.Xml.Linq.XElement(representation.Name.Namespace+"S",new System.Xml.Linq.XAttribute("t",time),new System.Xml.Linq.XAttribute("d",length),new System.Xml.Linq.XAttribute("r",repeat)));
                    if (repeat > 10000 || points.Count + repeat + 1 > 10000) throw new InvalidDataException("DASH片段数量过大。");
                    for (var index = 0; index <= repeat; index++) { points.Add((time, length)); time = checked(time + length); }
                }
                list.Add(normalized);
            }
            else
            {
                if(dynamicManifest)throw new NotSupportedException("实时DASH必须提供明确的SegmentTimeline。");
                if (!attributes.TryGetValue("duration", out var segmentDurationText) || !long.TryParse(segmentDurationText, out var segmentDuration) || segmentDuration <= 0 || periodDuration <= 0)
                    throw new NotSupportedException("DASH模板缺少有限时间轴或时长。");
                var count = Math.Ceiling(periodDuration * timescale / segmentDuration);
                if (!double.IsFinite(count) || count > 10000) throw new InvalidDataException("DASH片段数量过大。");
                list.SetAttributeValue("duration", segmentDuration);
                for (var index = 0; index < count; index++) points.Add((checked(index * segmentDuration), segmentDuration));
            }
            foreach (var point in points)
                list.Add(new System.Xml.Linq.XElement(representation.Name.Namespace + "SegmentURL", new System.Xml.Linq.XAttribute("media", Expand(media, number++, point.Time))));
            foreach (var old in representation.Elements().Where(element => element.Name.LocalName == "SegmentTemplate").ToArray()) old.Remove();
            representation.Add(list);
        }
        foreach (var old in root.Descendants().Where(element => element.Name.LocalName == "SegmentTemplate").ToArray()) old.Remove();
    }

    public Task ClearCacheAsync(CancellationToken ct=default)=>_cache?.ClearAsync(ct)??Task.CompletedTask;
    public void Clear() { lock(_registrationGate){_entries.Clear();_registrations.Clear();} }
    public void Dispose() { Clear(); _http.Dispose(); }
}
