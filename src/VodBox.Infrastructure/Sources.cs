using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Net;
using System.Text.RegularExpressions;
using VodBox.Core;

namespace VodBox.Infrastructure;

/// <summary>直播源工厂：把 m3u/txt 播放列表或 LunaTV 配置里的 lives 转为 LiveGroup 集合。</summary>
public sealed class LiveSources
{
    private readonly Func<string, IReadOnlyDictionary<string, string>?, CancellationToken, Task<string>> _fetch;
    public IReadOnlyList<string> Warnings { get; private set; } = [];
    public LiveSources(DefaultHttp http) : this(async (url, headers, ct) => DefaultHttp.Decode(await http.GetBoundedAsync(url, 8 * 1024 * 1024, ct, headers))) { }
    public LiveSources(Func<string, CancellationToken, Task<string>> fetch) : this((url, _, ct) => fetch(url, ct)) { }
    public LiveSources(Func<string, IReadOnlyDictionary<string, string>?, CancellationToken, Task<string>> fetch) => _fetch = fetch;

    /// <summary>加载单个地址：m3u/txt 直接解析；LunaTV 配置 JSON 则展开其中的 lives。</summary>
    public async Task<List<LiveGroup>> LoadAsync(string url, CancellationToken ct = default)
    {
        Warnings = [];
        var text = LocalMedia.TryResolveFile(url, out var localFile)
            ? await File.ReadAllTextAsync(localFile, ct)
            : await _fetch(url, null, ct);
        if (!text.TrimStart().StartsWith('{')) return Parse(text);
        var config = SourceConfigParser.Parse(text);
        var sites = (config.Lives ?? []).Select(pair => new LiveSite { Key = pair.Key, Name = pair.Value.Name, Url = pair.Value.Url, Ua = pair.Value.Ua, Epg = pair.Value.Epg }).ToArray();
        return await LoadSitesAsync(sites, url, ct);
    }

    /// <summary>并列加载多个直播源（LunaTV LiveConfig）；单个失败只记警告，全部失败才抛出。</summary>
    public async Task<List<LiveGroup>> LoadSitesAsync(IReadOnlyList<LiveSite> sites, string? baseAddress = null, CancellationToken ct = default)
    {
        Warnings = [];
        var groups = new List<LiveGroup>();
        var failures = new List<string>();
        foreach (var source in sites.Where(site => !site.Disabled).Take(32))
        {
            if (string.IsNullOrWhiteSpace(source.Url)) continue;
            var address = source.Url;
            var name = source.Name is { Length: > 0 } n ? n : "直播源";
            // 本地播放列表（绝对路径或 file:// URI）直接读盘，远程地址才走 HTTP。
            if (LocalMedia.TryResolveFile(address, out var localPath))
            {
                try
                {
                    var localGroups = Parse(await File.ReadAllTextAsync(localPath, ct));
                    if (localGroups.Count > 0) groups.AddRange(localGroups);
                    else failures.Add(name + ": 没有可播放频道");
                }
                catch (Exception error) when (error is not OperationCanceledException) { failures.Add(name + ": " + error.Message); }
                continue;
            }
            if (!Uri.TryCreate(address, UriKind.Absolute, out _) && baseAddress is not null)
                address = Uri.TryCreate(baseAddress, UriKind.Absolute, out var baseUri) && baseUri.Scheme is "http" or "https"
                    ? new Uri(baseUri, address).AbsoluteUri : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(baseAddress))!, address);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(12));
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (!string.IsNullOrWhiteSpace(source.Ua)) headers["User-Agent"] = source.Ua;
                var content = await _fetch(address, headers, timeout.Token);
                foreach (var group in Parse(content))
                {
                    var channels = group.Channels.Select(channel =>
                    {
                        var playbackHeaders = new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase);
                        foreach (var (key, value) in channel.Headers) playbackHeaders[key] = value;
                        return channel with { Headers = playbackHeaders };
                    }).ToArray();
                    groups.Add(group with { Name = sites.Count > 1 ? name + " · " + group.Name : group.Name, Channels = channels });
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { failures.Add(name + ": 加载超时"); }
            catch (Exception error) when (error is not OperationCanceledException) { failures.Add(name + ": " + error.Message); }
        }
        if (sites.Count > 32) failures.Add("直播源超过32个，只加载前32个");
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
