using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using VodBox.Core;
using VodBox.PluginHost;

namespace VodBox.Infrastructure;

/// <summary>drpy2 内容源；每站独立 VM，延迟装载，完整调用按站点串行。</summary>
public sealed partial class DrpySource : IResolvingContentSource, IDisposable
{
    private readonly SourceInfo _site;
    private readonly Func<CancellationToken, IDrpyRuntime> _factory;
    private readonly SemaphoreSlim _calls = new(1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _gate = new();
    private IDrpyRuntime? _runtime;
    private bool _disposed;

    public string Key => _site.Key;
    public string Name => _site.Name;

    public DrpySource(SourceInfo site, Func<IDrpyRuntime>? runtimeFactory = null)
    {
        _site = site;
        _factory = runtimeFactory is null ? token => new DrpyRuntime(token) : _ => runtimeFactory();
    }

    private async Task<T> CallAsync<T>(Func<IDrpyRuntime, CancellationToken, Task<T>> call, CancellationToken ct)
    {
        lock (_gate) ObjectDisposedException.ThrowIf(_disposed, this);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        await _calls.WaitAsync(timeout.Token).ConfigureAwait(false);
        try
        {
            timeout.Token.ThrowIfCancellationRequested();
            lock (_gate) ObjectDisposedException.ThrowIf(_disposed, this);
            if (_runtime is null)
            {
                var ext = _site.Ext;
                if (string.IsNullOrWhiteSpace(ext))
                {
                    // 独立规则脚本可直接用 api；drpy2 引擎入口必须提供 ext。
                    if (_site.Api?.Contains("drpy2", StringComparison.OrdinalIgnoreCase) != false)
                        throw new NotSupportedException("drpy2 站点缺少 ext 规则脚本。");
                    ext = _site.Api;
                }
                if (string.IsNullOrWhiteSpace(ext)) throw new NotSupportedException("缺少 drpy 规则脚本。");
                var runtime = await Task.Run(() => _factory(timeout.Token), timeout.Token).ConfigureAwait(false);
                try
                {
                    await runtime.InitAsync(ext, timeout.Token).ConfigureAwait(false);
                    timeout.Token.ThrowIfCancellationRequested();
                    _runtime = runtime;
                }
                catch { runtime.Dispose(); throw; }
            }
            try
            {
                var result = await call(_runtime, timeout.Token).ConfigureAwait(false);
                timeout.Token.ThrowIfCancellationRequested();
                return result;
            }
            catch
            {
                // 中断或执行失败后 VM 可能已释放；不能缓存到下次调用。
                var failed = _runtime;
                _runtime = null;
                failed?.Dispose();
                throw;
            }
        }
        finally { _calls.Release(); }
    }

    public Task<IReadOnlyList<Category>> GetCategoriesAsync(CancellationToken ct = default) => CallAsync<IReadOnlyList<Category>>(async (rt, token) =>
    {
        using var doc = JsonDocument.Parse(await rt.HomeAsync(token).ConfigureAwait(false));
        return List(doc.RootElement, "class").Where(c => Text(c, "type_id").Length > 0)
            .Select(c => new Category(Text(c, "type_id"), Text(c, "type_name"))).ToList();
    }, ct);

    public Task<IReadOnlyList<FilterGroup>> GetFiltersAsync(string categoryId,CancellationToken ct=default)=>CallAsync<IReadOnlyList<FilterGroup>>(async(rt,token)=>
    {
        using var document=JsonDocument.Parse(await rt.HomeAsync(token));
        return ParseFilters(document.RootElement,categoryId);
    },ct);

    internal static IReadOnlyList<FilterGroup> ParseFilters(JsonElement root,string categoryId)
    {
        var result=new List<FilterGroup>();
        if(!root.TryGetProperty("filters",out var filters)||filters.ValueKind!=JsonValueKind.Object||!filters.TryGetProperty(categoryId,out var groups)||groups.ValueKind!=JsonValueKind.Array)return result;
        foreach(var group in groups.EnumerateArray().Take(20))
        {
            if(group.ValueKind!=JsonValueKind.Object)continue;
            var key=Text(group,"key");var name=Text(group,"name");
            if(key.Length==0||!group.TryGetProperty("value",out var values)||values.ValueKind!=JsonValueKind.Array)continue;
            var options=values.EnumerateArray().Take(100).Where(value=>value.ValueKind==JsonValueKind.Object).Select(value=>new FilterValue(Text(value,"n"),Text(value,"v"))).ToList();
            result.Add(new FilterGroup(key,name,options,Text(group,"init")));
        }
        return result;
    }

    public Task<MediaPage> GetHomeAsync(CancellationToken ct = default) => CallAsync(async (rt, token) =>
        Page(await rt.HomeVodAsync(token).ConfigureAwait(false), 1), ct);

    public Task<MediaPage> GetItemsAsync(string categoryId, int page, IReadOnlyDictionary<string, string>? filters, CancellationToken ct = default) =>
        CallAsync(async (rt, token) => Page(await rt.CategoryAsync(categoryId, page, filters, token).ConfigureAwait(false), page), ct);

    public Task<MediaPage> SearchAsync(string query, int page, CancellationToken ct = default) =>
        CallAsync(async (rt, token) => Page(await rt.SearchAsync(query, page, token).ConfigureAwait(false), page), ct);

    public Task<MediaDetail> GetDetailAsync(string mediaId, CancellationToken ct = default) =>
        CallAsync(async (rt, token) => ParseDetail(await rt.DetailAsync(mediaId, token).ConfigureAwait(false), mediaId).Detail, ct);

    public Task<PlaybackRequest> ResolvePlaybackAsync(string mediaId, string episodeId, CancellationToken ct = default) => CallAsync(async (rt, token) =>
    {
        // 重新拉详情，获取可能变化的短期 token；身份不由播放 URL 决定。
        var parsed = ParseDetail(await rt.DetailAsync(mediaId, token).ConfigureAwait(false), mediaId);
        if (!parsed.Entries.TryGetValue(episodeId, out var entry)) throw new InvalidDataException("该选集已不存在，请刷新详情。");
        using var doc = JsonDocument.Parse(await rt.PlayAsync(entry.Flag, entry.PlayId, parsed.Flags, token).ConfigureAwait(false));
        var root = doc.RootElement;
        var url = Text(root, "url");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
            throw new InvalidDataException("drpy 未返回有效 HTTP 媒体地址。");
        var parse = PlaybackFlag(root, "parse");
        var jx=PlaybackFlag(root,"jx");
        if(parse is not (0 or 1)||jx is not (0 or 1))throw new NotSupportedException("drpy返回不支持的解析标志。");
        if (jx==0&&parse == 1 && !MediaExtension().IsMatch(uri.AbsolutePath))
            throw new NotSupportedException("该 drpy 选集需要网页嗅探（尚未实现）。");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("header", out var h) || root.TryGetProperty("headers", out h))
        {
            if (h.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(h.GetString()))
            {
                using var headerDoc = JsonDocument.Parse(h.GetString()!);
                ReadHeaders(headerDoc.RootElement, headers);
            }
            else if (h.ValueKind != JsonValueKind.Null) ReadHeaders(h, headers);
        }
        return new PlaybackRequest
        {
            Uri = url, Resolution = jx==1?ResolutionKind.Json:ResolutionKind.Direct, Headers = headers,
            SourceKey = Key, SourceName = Name, MediaId = mediaId, LineId = entry.LineId, EpisodeId = episodeId,
            Title = parsed.Detail.Item.Title + " · " + entry.Title,
            Poster = parsed.Detail.Item.Poster, Remarks = parsed.Detail.Item.Remarks
        };
    }, ct);

    private static void ReadHeaders(JsonElement root, Dictionary<string, string> headers)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("drpy 播放头不是 JSON 对象。");
        foreach (var h in root.EnumerateObject())
        {
            if (h.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False))
                throw new InvalidDataException("drpy 播放头值不是标量。");
            var value = h.Value.ValueKind == JsonValueKind.String ? h.Value.GetString() ?? "" : h.Value.ToString();
            if (h.Name.Length == 0 || h.Name.Any(c => c <= 32 || c >= 127 || "()<>@,;:\\\"/[]?={}".Contains(c)) || value.Any(c => c is '\r' or '\n' or '\0'))
                throw new InvalidDataException("drpy 播放头含非法字符。");
            headers[h.Name] = value;
        }
    }

    private static MediaPage Page(string json, int requestedPage)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var items = List(root, "list").Select(Item).ToList();
        var page = Math.Max(1, Number(root, "page", requestedPage));
        return new MediaPage(items, page, Math.Max(page, Number(root, "pagecount", page)));
    }

    private sealed record PlayEntry(string Flag, string PlayId, string LineId, string Title);
    private sealed record ParsedDetail(MediaDetail Detail, List<string> Flags, Dictionary<string, PlayEntry> Entries);

    private static ParsedDetail ParseDetail(string json, string mediaId)
    {
        using var doc = JsonDocument.Parse(json);
        var vod = doc.RootElement;
        if (vod.ValueKind == JsonValueKind.Object && vod.TryGetProperty("list", out _))
            vod = List(vod, "list").FirstOrDefault();
        if (vod.ValueKind != JsonValueKind.Object) throw new InvalidDataException("drpy 详情为空。");
        var flags = Text(vod, "vod_play_from").Split("$$$", StringSplitOptions.None).Select(f => f.Trim()).ToList();
        var urls = Text(vod, "vod_play_url").Split("$$$", StringSplitOptions.None);
        var entries = new Dictionary<string, PlayEntry>();
        var lines = new List<PlaybackLine>();
        var occurrences = new Dictionary<string, int>();
        for (var i = 0; i < urls.Length; i++)
        {
            var flag = i < flags.Count ? flags[i] : "";
            if (i >= flags.Count) flags.Add(flag);
            var lineName = flag.Length > 0 ? flag : "默认线路";
            occurrences.TryGetValue(flag, out var occurrence);
            occurrences[flag] = occurrence + 1;
            var lineId = Identity(flag) + ":" + occurrence.ToString(CultureInfo.InvariantCulture);
            var episodes = new List<Episode>();
            var titles = new Dictionary<string, int>();
            foreach (var pair in urls[i].Split('#', StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = pair.IndexOf('$');
                var title = separator >= 0 ? pair[..separator].Trim() : "播放";
                var playId = separator >= 0 ? pair[(separator + 1)..].Trim() : pair.Trim();
                if (playId.Length == 0) continue;
                titles.TryGetValue(title, out var count);
                titles[title] = count + 1;
                var id = lineId + ":" + Identity(title) + ":" + count.ToString(CultureInfo.InvariantCulture);
                episodes.Add(new Episode(id, title));
                entries.Add(id, new PlayEntry(flag, playId, lineId, title));
            }
            if (episodes.Count > 0) lines.Add(new PlaybackLine(lineId, lineName, episodes));
        }
        var content = Text(vod, "vod_content");
        content = BreakTags().Replace(content, "\n");
        content = WebUtility.HtmlDecode(HtmlTags().Replace(content, "")).Trim();
        return new ParsedDetail(new MediaDetail
        {
            Item = Item(vod) with { Id = Text(vod, "vod_id") is { Length: > 0 } vodId ? vodId : mediaId },
            Description = content, Director = Text(vod, "vod_director"), Actor = Text(vod, "vod_actor"), Lines = lines
        }, flags, entries);
    }

    private static IEnumerable<JsonElement> List(JsonElement root, string key)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("drpy 结果不是 JSON 对象。");
        if (!root.TryGetProperty(key, out var list) || list.ValueKind == JsonValueKind.Null) return [];
        if (list.ValueKind != JsonValueKind.Array) throw new InvalidDataException($"drpy {key} 不是数组。");
        return list.EnumerateArray();
    }

    private static int PlaybackFlag(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var value)) return 0;
        if (value.ValueKind is JsonValueKind.String or JsonValueKind.Number &&
            int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) return n;
        throw new InvalidDataException($"drpy {key} 不是整数。");
    }

    private static string Identity(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
    private static MediaItem Item(JsonElement vod) => new()
    {
        Id = Text(vod, "vod_id"), Title = Text(vod, "vod_name") is { Length: > 0 } title ? title : "(未命名)",
        Poster = Text(vod, "vod_pic"), Remarks = Text(vod, "vod_remarks"), Year = Text(vod, "vod_year"),
        Area = Text(vod, "vod_area"), TypeName = Text(vod, "type_name")
    };
    private static string Text(JsonElement root, string key) => root.TryGetProperty(key, out var value) && value.ValueKind != JsonValueKind.Null ? value.ToString().Trim() : "";
    private static int Number(JsonElement root, string key, int fallback) => int.TryParse(Text(root, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : fallback;

    [GeneratedRegex(@"\.(?:m3u8|mp4|flv|mpd|mkv|webm|m4v|mov|ts|mp3|m4a|aac)$", RegexOptions.IgnoreCase)]
    private static partial Regex MediaExtension();
    [GeneratedRegex(@"<br\s*/?>|</p>\s*(?=<p>)", RegexOptions.IgnoreCase)]
    private static partial Regex BreakTags();
    [GeneratedRegex("<[^>]*>")]
    private static partial Regex HtmlTags();

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _lifetime.Cancel();
        }
        _calls.Wait();
        try { _runtime?.Dispose(); _runtime = null; }
        finally { _calls.Release(); }
    }
}
