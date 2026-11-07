using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using VodBox.Core;

namespace VodBox.Infrastructure;

/// <summary>
/// 苹果 CMS V10 采集接口客户端（LunaTV downstream.ts 的 C# 实现）。
/// 搜索：<c>?ac=videolist&amp;wd=</c> 首页 + 并发拉取后续页（最多 <see cref="SiteSettings.SearchMaxPage"/> 页），逐页缓存；
/// 详情：<c>?ac=videolist&amp;ids=</c>，失败或无地址时若配置了 detail 站点根则抓取 HTML 详情页里的 m3u8。
/// 与 LunaTV 的差异：mpv 能播 m3u8 以外的媒体，所以不只保留 .m3u8 选集——全部 HTTP(S) 选集与线路都保留，
/// 直链媒体线路排前，明显是网页分享页（.html/share）的线路丢弃。
/// </summary>
public sealed partial class CmsClient : IDisposable
{
    public const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36";
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly SearchPageCache _cache = new();
    private readonly Func<SiteSettings> _settings;

    public static readonly TimeSpan SearchPageTimeout = TimeSpan.FromSeconds(8);
    public static readonly TimeSpan DetailTimeout = TimeSpan.FromSeconds(10);

    public CmsClient(Func<SiteSettings> settings, HttpMessageHandler? handler = null)
    {
        _settings = settings;
        _ownsHttp = true;
        _http = new HttpClient(handler ?? new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(6),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 8,
        }) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
    }

    /// <summary>清空搜索页缓存（换源配置后调用）。</summary>
    public void ClearCache() => _cache.Clear();

    internal static string BuildUrl(string api, IEnumerable<(string Key, string Value)> args)
    {
        var separator = api.Contains('?') ? (api.EndsWith('?') || api.EndsWith('&') ? "" : "&") : "?";
        return api + separator + string.Join("&", args.Select(pair => pair.Key + "=" + Uri.EscapeDataString(pair.Value)));
    }

    public static string SearchUrl(ApiSite site, string query, int page) => page <= 1
        ? BuildUrl(site.Api, [("ac", "videolist"), ("wd", query)])
        : BuildUrl(site.Api, [("ac", "videolist"), ("wd", query), ("pg", page.ToString(CultureInfo.InvariantCulture))]);

    private async Task<(HttpStatusCode Status, string Body)> GetAsync(string url, TimeSpan timeout, CancellationToken ct)
    {
        using var scope = CancellationTokenSource.CreateLinkedTokenSource(ct);
        scope.CancelAfter(timeout);
        try
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, scope.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return (response.StatusCode, "");
            await using var stream = await response.Content.ReadAsStreamAsync(scope.Token).ConfigureAwait(false);
            var bytes = await BoundedContent.ReadAsync(stream, 12 * 1024 * 1024, scope.Token).ConfigureAwait(false);
            return (response.StatusCode, TextEncoding.Decode(bytes));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"请求超时（{timeout.TotalSeconds:0}s）");
        }
    }

    /// <summary>单页搜索（带缓存）。返回该页结果与（首页时）总页数。</summary>
    public async Task<(IReadOnlyList<VodResult> Results, int PageCount)> SearchPageAsync(ApiSite site, string query, int page, CancellationToken ct = default)
    {
        if (_cache.TryGet(site.Key, query, page, out var cached))
        {
            if (cached.Status == SearchPageStatus.Ok) return (cached.Results, cached.PageCount);
            if (cached.Status == SearchPageStatus.Forbidden) throw new HttpRequestException("接口拒绝访问（403）", null, HttpStatusCode.Forbidden);
            throw new TimeoutException("最近一次请求超时，稍后再试");
        }
        HttpStatusCode status;
        string body;
        try
        {
            (status, body) = await GetAsync(SearchUrl(site, query, page), SearchPageTimeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _cache.Set(site.Key, query, page, SearchPageStatus.Timeout, [], 0, _settings().CacheTimeSeconds);
            throw;
        }
        if (status == HttpStatusCode.Forbidden)
        {
            _cache.Set(site.Key, query, page, SearchPageStatus.Forbidden, [], 0, _settings().CacheTimeSeconds);
            throw new HttpRequestException("接口拒绝访问（403）", null, status);
        }
        if (body.Length == 0) throw new HttpRequestException($"HTTP {(int)status}", null, status);
        var list = ParseList(body);
        var results = list.List.Select(vod => ToResult(site, vod)).Where(result => result.Lines.Count > 0).ToArray();
        var pageCount = Math.Max(1, list.PageCount);
        // 空结果不缓存（LunaTV 一致：空可能是站点临时故障）。
        if (list.List.Count > 0) _cache.Set(site.Key, query, page, SearchPageStatus.Ok, results, pageCount, _settings().CacheTimeSeconds);
        return (results, pageCount);
    }

    /// <summary>LunaTV searchFromApi：首页 + 并发补齐后续页（受最大页数限制），单页失败只丢该页。</summary>
    public async Task<IReadOnlyList<VodResult>> SearchAsync(ApiSite site, string query, CancellationToken ct = default)
    {
        var (first, pageCount) = await SearchPageAsync(site, query, 1, ct).ConfigureAwait(false);
        var maxPages = Math.Clamp(_settings().SearchMaxPage, 1, 50);
        var extra = Math.Min(pageCount, maxPages) - 1;
        if (extra <= 0) return first;
        var pages = await Task.WhenAll(Enumerable.Range(2, extra).Select(async page =>
        {
            try { return (await SearchPageAsync(site, query, page, ct).ConfigureAwait(false)).Results; }
            catch (Exception error) when (error is not OperationCanceledException || !ct.IsCancellationRequested) { return []; }
        })).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return first.Concat(pages.SelectMany(page => page)).DistinctBy(result => result.Id).ToArray();
    }

    /// <summary>
    /// 详情：先走 JSON 接口；失败或无可播线路且配置了 detail 站点根时改抓 HTML 详情页（LunaTV handleSpecialSourceDetail）。
    /// </summary>
    public async Task<VodResult> GetDetailAsync(ApiSite site, string id, CancellationToken ct = default)
    {
        Exception? failure = null;
        try
        {
            var (status, body) = await GetAsync(BuildUrl(site.Api, [("ac", "videolist"), ("ids", id)]), DetailTimeout, ct).ConfigureAwait(false);
            if (body.Length == 0) throw new HttpRequestException($"详情请求失败：HTTP {(int)status}", null, status);
            var list = ParseList(body);
            var vod = list.List.FirstOrDefault(item => item.VodId == id) ?? list.List.FirstOrDefault()
                ?? throw new InvalidDataException("获取到的详情内容无效");
            var result = ToResult(site, vod);
            if (result.Lines.Count == 0 && vod.VodContent is { Length: > 0 } content)
            {
                // 播放源为空时尝试从简介里解析 m3u8（LunaTV 同款兜底）。
                var episodes = M3u8InText().Matches(content).Select(match => match.Value.TrimStart('$')).Distinct()
                    .Select((url, index) => new Episode(url, (index + 1).ToString(CultureInfo.InvariantCulture), url)).ToArray();
                if (episodes.Length > 0) result = result with { Lines = [new PlaybackLine("m3u8", "m3u8", episodes)] };
            }
            if (result.Lines.Count > 0 || string.IsNullOrWhiteSpace(site.Detail)) return result;
        }
        catch (Exception error) when (error is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            if (string.IsNullOrWhiteSpace(site.Detail)) throw;
            failure = error;
        }
        try { return await GetHtmlDetailAsync(site, id, ct).ConfigureAwait(false); }
        catch (Exception error) when (failure is not null && error is not OperationCanceledException) { throw failure; }
    }

    private async Task<VodResult> GetHtmlDetailAsync(ApiSite site, string id, CancellationToken ct)
    {
        var url = $"{site.Detail!.TrimEnd('/')}/index.php/vod/detail/id/{Uri.EscapeDataString(id)}.html";
        var (status, html) = await GetAsync(url, DetailTimeout, ct).ConfigureAwait(false);
        if (html.Length == 0) throw new HttpRequestException($"详情页请求失败：HTTP {(int)status}", null, status);
        var matches = (site.Key == "ffzy" ? FfzyM3u8().Matches(html) : null) is { Count: > 0 } special ? special : DollarM3u8().Matches(html);
        var links = matches.Select(match =>
        {
            var link = match.Value[1..];
            var paren = link.IndexOf('(');
            return paren > 0 ? link[..paren] : link;
        }).Distinct().ToArray();
        var episodes = links.Select((link, index) => new Episode(link, (index + 1).ToString(CultureInfo.InvariantCulture), link)).ToArray();
        var title = H1().Match(html) is { Success: true } h1 ? WebUtility.HtmlDecode(h1.Groups[1].Value.Trim()) : "";
        var desc = Sketch().Match(html) is { Success: true } sketch ? CleanHtml(sketch.Groups[1].Value) : "";
        var cover = Jpg().Match(html) is { Success: true } jpg ? jpg.Value.Trim() : "";
        var year = YearTag().Match(html) is { Success: true } y ? y.Groups[1].Value : "unknown";
        return new VodResult
        {
            Id = id, Title = title, Poster = cover, SourceKey = site.Key, SourceName = site.Name,
            Lines = episodes.Length == 0 ? [] : [new PlaybackLine("m3u8", "m3u8", episodes)],
            Year = year, Description = desc,
        };
    }

    // ---------- 解析 ----------

    internal static MacCmsListResponse ParseList(string body)
    {
        var text = body.TrimStart('﻿', ' ', '\r', '\n', '\t');
        if (!text.StartsWith('{')) throw new InvalidDataException("接口没有返回 JSON（可能已失效或被拦截）");
        try { return JsonSerializer.Deserialize(text, Json.TypeInfo<MacCmsListResponse>()) ?? new MacCmsListResponse(); }
        catch (JsonException error) { throw new InvalidDataException($"接口 JSON 解析失败：{error.Message}", error); }
    }

    internal static VodResult ToResult(ApiSite site, MacCmsVod vod) => new()
    {
        Id = vod.VodId,
        Title = Whitespace().Replace(vod.VodName.Trim(), " "),
        Poster = (vod.VodPic ?? "").Trim(),
        SourceKey = site.Key,
        SourceName = site.Name,
        Lines = ParseLines(vod.VodPlayFrom, vod.VodPlayUrl),
        Class = vod.VodClass,
        Year = NormalizeYear(vod.VodYear),
        Description = CleanHtml(vod.VodContent ?? ""),
        TypeName = vod.TypeName,
        DoubanId = vod.VodDoubanId is { Length: > 0 } douban && douban != "0" ? douban : null,
        Remarks = vod.VodRemarks,
        Area = vod.VodArea,
        Director = vod.VodDirector,
        Actor = vod.VodActor,
    };

    internal static string NormalizeYear(string? year) => year is not null && FourDigits().Match(year) is { Success: true } match ? match.Value : "unknown";

    private enum LineKind { Media, Unknown, WebPage }

    /// <summary>
    /// 拆分 vod_play_from / vod_play_url（$$$ 分线路、# 分集、$ 分名与地址），只收 HTTP(S) 地址。
    /// 线路按「直链媒体 → 未知 → 网页」稳定排序；整条都是网页分享页的线路丢弃（mpv 无法播放网页）。
    /// </summary>
    internal static IReadOnlyList<PlaybackLine> ParseLines(string? playFrom, string? playUrl)
    {
        if (string.IsNullOrWhiteSpace(playUrl)) return [];
        var froms = (playFrom ?? "").Split("$$$");
        var urls = playUrl.Split("$$$");
        var lines = new List<(PlaybackLine Line, LineKind Kind, int Order)>();
        for (var i = 0; i < urls.Length; i++)
        {
            var episodes = new List<Episode>();
            var kinds = new List<LineKind>();
            foreach (var pair in urls[i].Split('#', StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = pair.IndexOf('$');
                var title = separator > 0 ? pair[..separator].Trim() : "";
                var uri = (separator >= 0 ? pair[(separator + 1)..] : pair).Trim();
                if (!Uri.TryCreate(uri, UriKind.Absolute, out var address) || address.Scheme is not ("http" or "https")) continue;
                if (title.Length == 0) title = $"第{episodes.Count + 1}集";
                episodes.Add(new Episode(uri, title, uri));
                kinds.Add(Classify(address));
            }
            if (episodes.Count == 0) continue;
            var kind = kinds.Contains(LineKind.Media) ? LineKind.Media : kinds.All(value => value == LineKind.WebPage) ? LineKind.WebPage : LineKind.Unknown;
            if (kind == LineKind.WebPage) continue;
            var name = i < froms.Length && !string.IsNullOrWhiteSpace(froms[i]) ? froms[i].Trim() : $"线路{i + 1}";
            var id = lines.Any(line => line.Line.Id == name) ? name + ":" + i.ToString(CultureInfo.InvariantCulture) : name;
            lines.Add((new PlaybackLine(id, name, episodes), kind, i));
        }
        return lines.OrderBy(line => line.Kind).ThenBy(line => line.Order).Select(line => line.Line).ToArray();
    }

    private static readonly string[] MediaExtensions = [".m3u8", ".mp4", ".flv", ".mkv", ".ts", ".m4v", ".mov", ".avi", ".webm", ".mpd", ".mp3", ".m4a"];
    private static readonly string[] PageExtensions = [".html", ".htm", ".php", ".shtml", ".asp", ".aspx", ".jsp"];

    private static LineKind Classify(Uri address)
    {
        var path = address.AbsolutePath;
        if (MediaExtensions.Any(extension => path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))) return LineKind.Media;
        if (PageExtensions.Any(extension => path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            || path.Contains("/share/", StringComparison.OrdinalIgnoreCase)) return LineKind.WebPage;
        // 「xxyun」云播线路：/play/短码 无扩展名，是网页播放器而不是媒体。
        if (path.StartsWith("/play/", StringComparison.OrdinalIgnoreCase) && !Path.HasExtension(path)) return LineKind.WebPage;
        return LineKind.Unknown;
    }

    /// <summary>LunaTV cleanHtmlTags：标签换行、合并空白、解码实体。</summary>
    public static string CleanHtml(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var cleaned = Tags().Replace(text, "\n");
        cleaned = Newlines().Replace(cleaned, "\n");
        cleaned = Spaces().Replace(cleaned, " ");
        return WebUtility.HtmlDecode(cleaned.Trim('\n').Trim());
    }

    [GeneratedRegex(@"\s+")] private static partial Regex Whitespace();
    [GeneratedRegex(@"\d{4}")] private static partial Regex FourDigits();
    [GeneratedRegex(@"<[^>]+>")] private static partial Regex Tags();
    [GeneratedRegex(@"\n+")] private static partial Regex Newlines();
    [GeneratedRegex(@"[ \t]+")] private static partial Regex Spaces();
    [GeneratedRegex(@"\$?(https?://[^""'\s]+?\.m3u8)")] private static partial Regex M3u8InText();
    [GeneratedRegex(@"\$(https?://[^""'\s]+?/\d{8}/\d+_[a-f0-9]+/index\.m3u8)")] private static partial Regex FfzyM3u8();
    [GeneratedRegex(@"\$(https?://[^""'\s]+?\.m3u8)")] private static partial Regex DollarM3u8();
    [GeneratedRegex(@"<h1[^>]*>([^<]+)</h1>")] private static partial Regex H1();
    [GeneratedRegex(@"<div[^>]*class=[""']sketch[""'][^>]*>([\s\S]*?)</div>")] private static partial Regex Sketch();
    [GeneratedRegex(@"https?://[^""'\s]+?\.jpg")] private static partial Regex Jpg();
    [GeneratedRegex(@">(\d{4})<")] private static partial Regex YearTag();

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}

public enum SearchPageStatus { Ok, Timeout, Forbidden }

/// <summary>
/// 搜索页缓存（LunaTV search-cache.ts）：键 = 源 + 关键词 + 页码；成功缓存 10 分钟，
/// 超时/403 也负缓存同样时长，避免反复打失效站点。上限 1000 条，超出按到期时间淘汰。
/// </summary>
public sealed class SearchPageCache
{
    public sealed record Entry(DateTimeOffset ExpiresAt, SearchPageStatus Status, IReadOnlyList<VodResult> Results, int PageCount);
    private readonly ConcurrentDictionary<string, Entry> _entries = new();
    private readonly Func<DateTimeOffset> _now;
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);
    public const int Capacity = 1000;

    public SearchPageCache(Func<DateTimeOffset>? now = null) => _now = now ?? (() => DateTimeOffset.UtcNow);

    private static string Key(string source, string query, int page) => $"{source}::{query.Trim()}::{page}";

    public bool TryGet(string source, string query, int page, out Entry entry)
    {
        if (_entries.TryGetValue(Key(source, query, page), out entry!) && entry.ExpiresAt > _now()) return true;
        _entries.TryRemove(Key(source, query, page), out _);
        entry = null!;
        return false;
    }

    public void Set(string source, string query, int page, SearchPageStatus status, IReadOnlyList<VodResult> results, int pageCount, int cacheSeconds = 0)
    {
        var now = _now();
        if (_entries.Count >= Capacity)
        {
            foreach (var expired in _entries.Where(pair => pair.Value.ExpiresAt <= now).Select(pair => pair.Key).ToArray()) _entries.TryRemove(expired, out _);
            foreach (var oldest in _entries.OrderBy(pair => pair.Value.ExpiresAt).Take(Math.Max(0, _entries.Count - Capacity + 1)).Select(pair => pair.Key).ToArray())
                _entries.TryRemove(oldest, out _);
        }
        _entries[Key(source, query, page)] = new Entry(now + Ttl, status, results, pageCount);
    }

    public void Clear() => _entries.Clear();
    public int Count => _entries.Count;
}
