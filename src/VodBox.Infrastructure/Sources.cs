using System.Globalization;
using System.Text;
using System.Text.Json;
using VodBox.Core;

namespace VodBox.Infrastructure;

/// <summary>苹果 CMS V10 JSON 采集站点源（site.type=0，api 形如 https://xx/api.php/provide/vod）。</summary>
public sealed class MacCmsSource : IContentSource
{
    private readonly DefaultHttp _http;
    private readonly SourceInfo _site;

    public string Key => _site.Key;
    public string Name => _site.Name;
    public string? ParseEndpoint=>_site.PlayUrl;

    public MacCmsSource(SourceInfo site, DefaultHttp http)
    {
        _site = site;
        _http = http;
    }

    private string Api(string action, IReadOnlyDictionary<string, string> args)
    {
        var query = string.Join("&", args.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));
        var api = _site.Api!;
        if (!api.Contains('?')) api += "?";
        else api += "&";
        return $"{api}ac={action}&{query}";
    }

    public async Task<IReadOnlyList<Category>> GetCategoriesAsync(CancellationToken ct = default)
    {
        var url = Api("list", new Dictionary<string, string>());
        var response = await _http.GetStringAsync(url, ct: ct);
        var parsed = JsonSerializer.Deserialize(response, Json.TypeInfo<MacCmsCategoryResponse>())
                     ?? throw new InvalidDataException("分类响应解析失败");
        return parsed.Classes
            .Where(c => !string.IsNullOrWhiteSpace(c.TypeId))
            .Select(c => new Category(c.TypeId, c.TypeName))
            .ToList();
    }

    public async Task<MediaPage> GetHomeAsync(CancellationToken ct = default) => await GetItemsAsync("1", 1, null, ct);

    public async Task<MediaPage> GetItemsAsync(string categoryId, int page, IReadOnlyDictionary<string, string>? filters, CancellationToken ct = default)
    {
        var args = new Dictionary<string, string> { ["pg"] = page.ToString(CultureInfo.InvariantCulture) };
        if (categoryId != "0" && !string.IsNullOrEmpty(categoryId)) args["t"] = categoryId;
        if (filters is not null)
        {
            if (filters.TryGetValue("year", out var year) && !string.IsNullOrEmpty(year)) args["year"] = year;
            if (filters.TryGetValue("area", out var area) && !string.IsNullOrEmpty(area)) args["area"] = area;
            if (filters.TryGetValue("lang", out var lang) && !string.IsNullOrEmpty(lang)) args["lang"] = lang;
            if (filters.TryGetValue("class", out var cls) && !string.IsNullOrEmpty(cls)) args["class"] = cls;
        }
        var response = await _http.GetStringAsync(Api("videolist", args), ct: ct);
        var parsed = JsonSerializer.Deserialize(response, Json.TypeInfo<MacCmsListResponse>())
                     ?? throw new InvalidDataException("列表响应解析失败");
        var page_ = parsed.Page <= 0 ? page : parsed.Page;
        return new MediaPage(parsed.List.Select(ToItem).ToList(), page_, parsed.PageCount);
    }

    public async Task<MediaDetail> GetDetailAsync(string mediaId, CancellationToken ct = default)
    {
        var response = await _http.GetStringAsync(Api("videolist", new Dictionary<string, string> { ["ids"] = mediaId }), ct: ct);
        var parsed = JsonSerializer.Deserialize(response, Json.TypeInfo<MacCmsDetailResponse>())
                     ?? throw new InvalidDataException("详情响应解析失败");
        var vod = parsed.List.FirstOrDefault() ?? throw new InvalidDataException($"未找到条目 {mediaId}");
        return ToDetail(vod);
    }

    public async Task<MediaPage> SearchAsync(string query, int page, CancellationToken ct = default)
    {
        var args = new Dictionary<string, string> { ["wd"] = query, ["pg"] = page.ToString(CultureInfo.InvariantCulture) };
        var response = await _http.GetStringAsync(Api("videolist", args), ct: ct);
        var parsed = JsonSerializer.Deserialize(response, Json.TypeInfo<MacCmsSearchResponse>())
                     ?? throw new InvalidDataException("搜索响应解析失败");
        return new MediaPage(parsed.List.Select(ToItem).ToList(), page, page <= 1 ? 1 : page);
    }

    internal static MediaItem ToItem(MacCmsVod vod) => new()
    {
        Id = vod.VodId,
        Title = string.IsNullOrWhiteSpace(vod.VodName) ? "(未命名)" : vod.VodName,
        Poster = vod.VodPic,
        Remarks = vod.VodRemarks,
        Year = vod.VodYear,
        Area = vod.VodArea,
        TypeName = vod.TypeName,
    };

    /// <summary>拆分 vod_play_from / vod_play_url（$$$ 分隔线路、# 分隔集、$ 分隔名与地址）。</summary>
    internal static MediaDetail ToDetail(MacCmsVod vod)
    {
        var lines = new List<PlaybackLine>();
        var froms = (vod.VodPlayFrom ?? "").Split("$$$", StringSplitOptions.RemoveEmptyEntries);
        var urls = (vod.VodPlayUrl ?? "").Split("$$$", StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < froms.Length && i < urls.Length; i++)
        {
            var episodes = new List<Episode>();
            var pairs = urls[i].Split('#', StringSplitOptions.RemoveEmptyEntries);
            foreach (var pair in pairs)
            {
                var sep = pair.IndexOf('$');
                if (sep <= 0) continue;
                var title = pair[..sep].Trim();
                var uri = pair[(sep + 1)..].Trim();
                if (uri.Length > 0) episodes.Add(new Episode(uri, title, uri));
            }
            if (episodes.Count > 0) lines.Add(new PlaybackLine(froms[i].Trim(), froms[i].Trim(), episodes));
        }
        var content = (vod.VodContent ?? "").Trim();
        if (content.StartsWith("<") || content.Contains("<p>")) content = StripTags(content);
        return new MediaDetail
        {
            Item = ToItem(vod),
            Description = content,
            Director = vod.VodDirector,
            Actor = vod.VodActor,
            Lines = lines,
        };
    }

    private static string StripTags(string html) => html
        .Replace("<br>", "\n", StringComparison.OrdinalIgnoreCase)
        .Replace("<br/>", "\n", StringComparison.OrdinalIgnoreCase)
        .Replace("<p>", "\n", StringComparison.OrdinalIgnoreCase)
        .Replace("</p>", "", StringComparison.OrdinalIgnoreCase)
        .Split('<')[0].Trim();
}

/// <summary>直播源工厂：把 TVBox lives 配置转为 LiveGroup 集合。</summary>
public sealed class LiveSources
{
    private readonly Func<string, IReadOnlyDictionary<string, string>?, CancellationToken, Task<string>> _fetch;
    public IReadOnlyList<string> Warnings { get; private set; } = [];
    public LiveSources(DefaultHttp http) : this(async (url, headers, ct) => DefaultHttp.Decode(await http.GetBoundedAsync(url, 8 * 1024 * 1024, ct, headers))) { }
    public LiveSources(Func<string, CancellationToken, Task<string>> fetch) : this((url, _, ct) => fetch(url, ct)) { }
    public LiveSources(Func<string, IReadOnlyDictionary<string, string>?, CancellationToken, Task<string>> fetch) => _fetch = fetch;

    public async Task<List<LiveGroup>> LoadAsync(string url, CancellationToken ct = default)
    {
        Warnings = [];
        var text = LocalMedia.TryResolveFile(url, out var localFile)
            ? await File.ReadAllTextAsync(localFile, ct)
            : await _fetch(url, null, ct);
        if (!text.TrimStart().StartsWith('{')) return Parse(text);
        var config = ConfigLoader.Parse(text) ?? throw new InvalidDataException("直播配置解析失败。");
        var groups = new List<LiveGroup>();
        var failures = new List<string>();
        foreach (var source in config.Lives.Take(32))
        {
            if (string.IsNullOrWhiteSpace(source.Url)) continue;
            var address = source.Url;
            // 本地播放列表（绝对路径或 file:// URI）直接读盘，远程地址才走 HTTP。
            if (LocalMedia.TryResolveFile(address, out var localPath))
            {
                try
                {
                    var localGroups = Parse(await File.ReadAllTextAsync(localPath, ct));
                    if (localGroups.Count > 0) groups.AddRange(localGroups);
                    else failures.Add((source.Name is { Length: > 0 } n ? n : "本地播放列表") + ": 没有可播放频道");
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    failures.Add((source.Name is { Length: > 0 } n2 ? n2 : "本地播放列表") + ": " + error.Message);
                }
                continue;
            }
            if (!Uri.TryCreate(address, UriKind.Absolute, out _))
                address = Uri.TryCreate(url, UriKind.Absolute, out var baseUri) && baseUri.Scheme is "http" or "https"
                    ? new Uri(baseUri, address).AbsoluteUri : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(url))!, address);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromMilliseconds(source.Timeout > 0 ? Math.Clamp(source.Timeout, 100, 60000) : 12000));
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (!string.IsNullOrWhiteSpace(source.Ua)) headers["User-Agent"] = source.Ua;
                var content = File.Exists(address) ? await File.ReadAllTextAsync(address, timeout.Token) : await _fetch(address, headers, timeout.Token);
                foreach (var group in Parse(content))
                {
                    var channels=group.Channels.Select(channel=>
                    {
                        var playbackHeaders=new Dictionary<string,string>(headers,StringComparer.OrdinalIgnoreCase);
                        foreach(var (key,value) in channel.Headers)playbackHeaders[key]=value;
                        return channel with {Headers=playbackHeaders};
                    }).ToArray();
                    groups.Add(group with { Name = string.IsNullOrWhiteSpace(source.Name) ? group.Name : source.Name + " · " + group.Name,Channels=channels });
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { failures.Add(source.Name + ": 加载超时"); }
            catch (Exception error) when (error is not OperationCanceledException) { failures.Add(source.Name + ": " + error.Message); }
        }
        if (config.Lives.Count > 32) failures.Add("直播源超过32个，只加载前32个");
        Warnings = failures.ToArray();
        if (groups.Count == 0 && failures.Count > 0) throw new InvalidDataException("全部直播源失败：" + string.Join("；", failures));
        var number = 0;
        return groups.Select(group => group with { Channels = group.Channels.Select(channel => channel with { Number = ++number }).ToArray() }).ToList();
    }

    private static List<LiveGroup> Parse(string text) => M3uParser.IsM3u(text) ? M3uParser.ParseGroups(text) : TxtLiveParser.Parse(text);
}

/// <summary>M3U 直播列表解析（#EXTINF + group-title + tvg-logo / tvg-id）。
/// 支持 EXT VLC 的 http-user-agent/http-referrer、Kodi stream_headers、#EXTGRP 分组，
/// 并还原 HTML 实体（<c>&amp;amp;</c>）；其余指令和 catchup 尚未消费。</summary>
public static class M3uParser
{
    public static bool IsM3u(string text) => text.Contains("#EXTM3U", StringComparison.OrdinalIgnoreCase);

    public static List<LiveGroup> ParseGroups(string text)
    {
        var channels = new List<LiveChannel>();
        string? pendingCatchup = null;
        int pendingDays = 0;
        var pendingHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? pendingName = null, pendingGroup = "未分组", pendingLogo = null, pendingTvg = null;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim().TrimEnd('\r');
            if (line.Length == 0) continue;
            if (line.StartsWith("#EXTINF", StringComparison.OrdinalIgnoreCase))
            {
                pendingHeaders.Clear();
                pendingCatchup = Attr(line, "catchup-source");
                pendingDays = int.TryParse(Attr(line, "catchup-days"), out var days) ? Math.Clamp(days, 0, 30) : 0;
                pendingName = null; pendingGroup = "未分组"; pendingLogo = null; pendingTvg = null;
                var comma = line.IndexOf(',');
                var attrs = comma > 0 ? line[..comma] : line;
                if (comma > 0 && comma + 1 < line.Length) pendingName = line[(comma + 1)..].Trim();
                pendingGroup = Attr(attrs, "group-title") ?? pendingGroup;
                pendingLogo = UnescapeOrNull(Attr(attrs, "tvg-logo"));
                pendingTvg = Attr(attrs, "tvg-id");
            }
            else if (line.StartsWith("#EXTVLCOPT:", StringComparison.OrdinalIgnoreCase))
            {
                var option = line[11..];
                var equals = option.IndexOf('=');
                if (equals > 0) AddHeader(pendingHeaders, option[..equals], option[(equals + 1)..]);
            }
            else if (line.StartsWith("#EXTGRP:", StringComparison.OrdinalIgnoreCase))
            {
                var group = line[8..].Trim();
                if (group.Length > 0) pendingGroup = group;
            }
            else if (line.StartsWith("#KODIPROP:inputstream.adaptive.stream_headers=", StringComparison.OrdinalIgnoreCase))
            {
                var equals = line.IndexOf('=');
                foreach (var option in line[(equals + 1)..].Split('&'))
                {
                    var split = option.IndexOf('=');
                    if (split > 0) AddHeader(pendingHeaders, option[..split], Uri.UnescapeDataString(option[(split + 1)..]));
                }
            }
            else if (!line.StartsWith('#'))
            {
                var name = string.IsNullOrWhiteSpace(pendingName) ? GuessName(line) : pendingName;
                channels.Add(new LiveChannel
                {
                    Name = name,
                    Uris = [Unescape(line)],
                    Headers = new Dictionary<string, string>(pendingHeaders, StringComparer.OrdinalIgnoreCase),
                    Logo = pendingLogo,
                    Group = pendingGroup,
                    TvgId = pendingTvg,
                    CatchupSource = pendingCatchup, CatchupDays = pendingDays,
                });
                pendingName = null; pendingLogo = null; pendingTvg = null; pendingHeaders.Clear();
            }
        }
        return Group(channels);
    }

    /// <summary>
    /// 还原网页导出的 m3u 中的 HTML 实体（<c>&amp;amp;</c> → <c>&amp;</c>）。
    /// 直播地址常带查询串，未还原会让播放请求落到错误路径。
    /// </summary>
    private static string Unescape(string value) =>
        value.Contains('&') ? System.Net.WebUtility.HtmlDecode(value) : value;

    /// <summary>可空版本：null / 空串原样返回。</summary>
    private static string? UnescapeOrNull(string? value) =>
        string.IsNullOrEmpty(value) ? value : Unescape(value);

    private static void AddHeader(Dictionary<string, string> headers, string key, string value)
    {
        if (value.IndexOfAny(['\r', '\n', '\0']) >= 0) throw new InvalidDataException("直播请求头包含非法控制字符。");
        var name = key.Trim().ToLowerInvariant() switch
        {
            "http-user-agent" or "user-agent" => "User-Agent",
            "http-referrer" or "http-referer" or "referer" or "referrer" => "Referer",
            _ => null,
        };
        if (name is not null) headers[name] = value.Trim();
    }

    private static string? Attr(string attrs, string key)
    {
        var marker = key + "=\"";
        var start = attrs.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return null;
        start += marker.Length;
        var end = attrs.IndexOf('"', start);
        return end > start ? attrs[start..end] : null;
    }

    private static string GuessName(string url)
    {
        var tail = url.Split('/').LastOrDefault() ?? url;
        var dot = tail.IndexOf('.');
        return dot > 0 ? tail[..dot] : tail;
    }

    /// <summary>
    /// 台标占位文字（台标图片缺失或 404 时显示）。
    /// CCTV-N 系列取“台号”（CCTV-5体育 → 5、CCTV5+体育赛事 → 5+），否则十几个央视频道
    /// 会全部显示成同一个 “CCT”；其余取前 2 个汉字或前 3 个拉丁字符。
    /// </summary>
    internal static string BadgeFor(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0) return "TV";
        var cctv = System.Text.RegularExpressions.Regex.Match(trimmed, @"^(?:CCTV|CGTN)[-\s]?(\d+)(\+)?", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (cctv.Success) return cctv.Groups[1].Value + cctv.Groups[2].Value;
        if (char.IsAscii(trimmed[0]))
        {
            var ascii = new string(trimmed.Where(c => char.IsAsciiLetterOrDigit(c)).Take(3).ToArray());
            return ascii.Length > 0 ? ascii.ToUpperInvariant() : "TV";
        }
        return trimmed[..Math.Min(2, trimmed.Length)];
    }

    /// <summary>铭牌底色：按名称稳定取色，同一频道每次都得到同一种颜色。</summary>
    internal static string BadgeColorFor(string name)
    {
        string[] palette = ["#C0392B", "#1F6FB2", "#2E7D5B", "#7B4FA8", "#C77C1E", "#2C6E7F", "#A6406B", "#4A6FA5"];
        var hash = 17;
        foreach (var ch in name) hash = unchecked(hash * 31 + ch);
        return palette[(hash & 0x7fffffff) % palette.Length];
    }

    internal static List<LiveGroup> Group(IReadOnlyList<LiveChannel> channels)
    {
        var number = 0;
        return channels
            .GroupBy(c => c.Group)
            .OrderBy(g => g.Key == "未分组" ? 1 : 0)
            .Select(g =>
            {
                var name=g.Key;string? hash=null;
                // TVBox TXT惯例：分组名_密码。只保存摘要，不在展示名保留密码。
                var separator=name.LastIndexOf('_');
                if(separator>0&&separator<name.Length-1)
                {
                    hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name[(separator+1)..])));
                    name=name[..separator];
                }
                return new LiveGroup(name,g.Select(c=>c with
                {
                    Number = ++number,
                    Group = name,
                    Badge = BadgeFor(c.Name),
                    BadgeColor = BadgeColorFor(c.Name),
                }).ToList(),hash is not null){PasswordHash=hash};
            })
            .ToList();
    }
}

/// <summary>TXT 直播列表解析（「分组,#genre#」+「频道名,地址」格式，FongMi LiveParser 等价）。</summary>
public static class TxtLiveParser
{
    public static List<LiveGroup> Parse(string text)
    {
        var channels = new List<LiveChannel>();
        string group = "未分组";
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim().TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith("//") || line.StartsWith(";")) continue;
            var comma = line.IndexOf(',');
            if (comma <= 0) continue;
            var left = line[..comma].Trim();
            var right = line[(comma + 1)..].Trim();
            if (right.Contains("#genre#", StringComparison.OrdinalIgnoreCase))
            {
                group = string.IsNullOrWhiteSpace(left) ? "未分组" : left;
                continue;
            }
            if (right.Length == 0) continue;
            var uris = right.Split('$').Where(u => !string.IsNullOrWhiteSpace(u) && u.Contains("://")).Select(u => u.Trim()).ToArray();
            if (uris.Length == 0) continue;
            channels.Add(new LiveChannel
            {
                Name = left, Uris = uris, Group = group,
                Badge = M3uParser.BadgeFor(left), BadgeColor = M3uParser.BadgeColorFor(left),
            });
        }
        return M3uParser.Group(channels);
    }
}
