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
        var safeHeaders = NormalizeHeaders(headers);
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

    /// <summary>逐跳/帧长度请求头：代理自己计算，不能由调用方注入（避免请求走私/长度错乱）。</summary>
    private static readonly HashSet<string> RestrictedProxyHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization", "Proxy-Connection",
        "TE", "Trailer", "Transfer-Encoding", "Upgrade", "Expect",
        "Content-Length", "Content-Type", "Content-Encoding", "Range",
    };

    /// <summary>
    /// 规范化要转发的媒体请求头：保留 Cookie/Authorization/Referer/UA 等真实播放所需头（完整透传），
    /// 但拒绝逐跳与帧长度头以及非法字符。凭据只绑定到本次注册的句柄，不做全局 Cookie 会话。
    /// </summary>
    private static Dictionary<string, string> NormalizeHeaders(IReadOnlyDictionary<string, string>? headers)
    {
        var safe = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in headers ?? new Dictionary<string, string>())
        {
            if (key.Length == 0 || key.Length > 256 || !IsHeaderToken(key) || RestrictedProxyHeaders.Contains(key)) continue;
            if (value.Length > 8192 || value.IndexOfAny(['\r', '\n', '\0']) >= 0) throw new InvalidDataException("代理请求头非法。");
            safe[key] = value;
        }
        return safe;
    }

    /// <summary>RFC 7230 token 字符：非 token 的头名（含空格、冒号等）会被丢弃而非原样转发。</summary>
    private static bool IsHeaderToken(string name)
    {
        foreach (var c in name)
            if (!char.IsAsciiLetterOrDigit(c) && !"!#$%&'*+-.^_`|~".Contains(c)) return false;
        return true;
    }

    /// <summary>
    /// 跨源请求只保留 UA：Cookie/Authorization/Referer 等凭据绝不跟随重定向或清单子资源到别的源站。
    /// </summary>
    private static Dictionary<string, string> CrossOriginHeaders(IReadOnlyDictionary<string, string> headers) =>
        headers.Where(header => header.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(header => header.Key, header => header.Value, StringComparer.OrdinalIgnoreCase);

    private static bool SameOrigin(Uri left, Uri right) =>
        left.GetLeftPart(UriPartial.Authority).Equals(right.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase);

    public async Task ServeCachedAsync(HttpContext context)
    {
        var id=HandleId(context);
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
        var id = HandleId(context);
        if (!_entries.TryGetValue(id, out var entry) || entry.Expires <= DateTimeOffset.UtcNow) { context.Response.StatusCode = 404; return; }
        if(context.Request.Headers.TryGetValue("Range",out var requestedRange)&&!System.Net.Http.Headers.RangeHeaderValue.TryParse(requestedRange.ToString(),out _))
        {context.Response.StatusCode=400;return;}
        // FFmpeg 默认带 "Range: bytes=0-" 打开片段；这等同整段请求，按整段取回才能检查伪装前缀。
        var whole = !context.Request.Headers.TryGetValue("Range", out var rangeHeader) || rangeHeader.ToString().Trim() == "bytes=0-";
        using var response = await FetchAsync(entry, context, forwardRange: !whole);
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
                // 清单指向别的源站时只保留 UA，不携带 Cookie/Authorization/Referer，避免跨源泄露凭据。
                var headers = SameOrigin(target, entry.Uri) ? entry.Headers : CrossOriginHeaders(entry.Headers);
                return Register(url, headers);
            }
            var rewritten = dash ? RewriteDash(text.ToString(), effectiveUri, RegisterChild) : RewriteHls(text.ToString(), effectiveUri, RegisterChild);
            await context.Response.WriteAsync(rewritten, context.RequestAborted);
            return;
        }
        if (response.StatusCode == HttpStatusCode.OK && whole)
        {
            // 部分站点把 TS 片段伪装成图片（前置一张极小 PNG 等）；只在确认后面紧跟 TS 同步字节时才剥离前缀。
            await using var body = await response.Content.ReadAsStreamAsync(context.RequestAborted);
            var head = new byte[DisguiseScanLimit];
            var read = await body.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, context.RequestAborted);
            var skip = DisguisedPrefixLength(head.AsSpan(0, read));
            context.Response.ContentType = skip > 0 ? "video/mp2t" : type;
            if (response.Content.Headers.ContentLength is { } total) context.Response.ContentLength = total - skip;
            await context.Response.Body.WriteAsync(head.AsMemory(skip, read - skip), context.RequestAborted);
            await body.CopyToAsync(context.Response.Body, context.RequestAborted);
            return;
        }
        context.Response.ContentType = type;
        if (response.Content.Headers.ContentLength is { } length) context.Response.ContentLength = length;
        if (response.Content.Headers.ContentRange is { } contentRange) context.Response.Headers.ContentRange = contentRange.ToString();
        if (response.Headers.AcceptRanges.Count > 0) context.Response.Headers.AcceptRanges = string.Join(",", response.Headers.AcceptRanges);
        await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
    }

    private const int DisguiseScanLimit = 16 * 1024;

    /// <summary>句柄可带媒体扩展名（<c>/proxy/{id}.ts</c>），查表只用扩展名前的随机部分。</summary>
    private static string HandleId(HttpContext context)
    {
        var id = context.Request.RouteValues["id"]?.ToString() ?? "";
        var dot = id.IndexOf('.');
        return dot < 0 ? id : id[..dot];
    }

    /// <summary>
    /// 伪装成图片的 TS 片段：以 PNG/JPEG/GIF/BMP 签名开头，且其后出现连续三个间隔 188 字节的 TS 同步字节。
    /// 返回需要跳过的前缀长度；不是这种形态（含真正的图片）返回 0，原样转发。
    /// </summary>
    internal static int DisguisedPrefixLength(ReadOnlySpan<byte> head)
    {
        var image = head.StartsWith((ReadOnlySpan<byte>)[0x89, (byte)'P', (byte)'N', (byte)'G']) ||
            head.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]) || head.StartsWith("GIF8"u8) || head.StartsWith("BM"u8);
        if (!image) return 0;
        for (var offset = 1; offset + 376 < head.Length; offset++)
            if (head[offset] == 0x47 && head[offset + 188] == 0x47 && head[offset + 376] == 0x47) return offset;
        return 0;
    }

    /// <summary>
    /// 给代理句柄补上 FFmpeg 认可的媒体扩展名：新版 HLS 解复用器（extension_picky）拒绝无扩展名或
    /// 图片扩展名的片段地址。原地址是清单则用 .m3u8/.mpd，常见媒体扩展名原样保留，其余一律按 TS。
    /// </summary>
    public static string WithMediaExtension(string handle, string originalUrl)
    {
        var extension = Uri.TryCreate(originalUrl, UriKind.Absolute, out var uri) ? Path.GetExtension(uri.AbsolutePath).ToLowerInvariant() : "";
        return handle + (extension is ".m3u8" or ".mpd" or ".ts" or ".m4s" or ".mp4" or ".m4a" or ".m4v" or ".aac" or ".mp3" or ".vtt" or ".webvtt" ? extension : ".ts");
    }

    private async Task<HttpResponseMessage> FetchAsync(Entry entry,HttpContext context,bool forwardRange=true)
    {
        var target=entry.Uri;
        for(var attempt=0;attempt<=5;attempt++)
        {
            using var request=new HttpRequestMessage(HttpMethod.Get,target);
            // 重定向到别的源站时只带 UA：Cookie/Authorization/Referer 等凭据不跟随跨源跳转。
            foreach(var (key,value) in SameOrigin(target,entry.Uri)?entry.Headers:CrossOriginHeaders(entry.Headers))
                request.Headers.TryAddWithoutValidation(key,value);
            if(forwardRange&&context.Request.Headers.TryGetValue("Range",out var range))
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
            if (!line.StartsWith('#') && !string.IsNullOrWhiteSpace(line))
            {
                var target = new Uri(baseUri, line.Trim()).AbsoluteUri;
                line = WithMediaExtension(register(target), target);
            }
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
