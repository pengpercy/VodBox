using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using VodBox.Core;

namespace VodBox.Infrastructure;

/// <summary>
/// TVBox <c>site.type == 4</c> 站点的独立 HTTP 协议适配：站点本身就是 HTTP 接口，
/// 分类/列表/详情用 <c>{ac, t, pg, wd}</c> 等参数直接取；播放时用 <c>{play, flag}</c> 回调向同一接口
/// 补取播放内容，并保留站点 <c>header</c>、<c>ext</c>（作为 <c>extend</c> 参数）、多地址、
/// <c>playUrl</c>、<c>parse</c>/<c>jx</c> 与其返回的 <c>header</c>/<c>UA</c>。
/// 不依赖 JAR/脚本宿主；无法解析的地址明确报错而不伪造可播放。
/// </summary>
public sealed partial class Type4Source : IResolvingContentSource
{
    private static readonly IReadOnlyDictionary<string, string> NoHeaders = new Dictionary<string, string>();
    private readonly SourceInfo _site;
    private readonly Func<string, IReadOnlyDictionary<string, string>, CancellationToken, Task<string>> _fetch;

    public string Key => _site.Key;
    public string Name => _site.Name;

    public Type4Source(SourceInfo site, DefaultHttp http)
        : this(site, (url, headers, ct) => FetchAsync(http, url, headers, ct)) { }

    private static async Task<string> FetchAsync(DefaultHttp http, string url, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
        => DefaultHttp.Decode(await http.GetBoundedAsync(url, 8 * 1024 * 1024, ct, headers).ConfigureAwait(false));

    /// <summary>测试用：注入捕获 URL 与请求头的抓取函数。</summary>
    internal Type4Source(SourceInfo site, Func<string, IReadOnlyDictionary<string, string>, CancellationToken, Task<string>> fetch)
    {
        _site = site;
        _fetch = fetch;
    }

    private async Task<string> FetchAsync(string url, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var scope = CancellationTokenSource.CreateLinkedTokenSource(ct);
        scope.CancelAfter(TimeSpan.FromSeconds(20));
        var text = await _fetch(url, _site.Header ?? NoHeaders, scope.Token).ConfigureAwait(false);
        scope.Token.ThrowIfCancellationRequested();
        ct.ThrowIfCancellationRequested();
        return text;
    }

    /// <summary>
    /// 构造站点接口地址：保留原查询串（新参数同名时覆盖），站点 <c>ext</c> 原值作为 <c>extend</c>，
    /// 其余参数按键排序后追加，避免不同键序产生重复缓存键。
    /// </summary>
    private string BuildRequestUrl(Dictionary<string, string> args)
    {
        if (!Uri.TryCreate(_site.Api, UriKind.Absolute, out var endpoint) || endpoint.Scheme is not ("http" or "https") || endpoint.UserInfo.Length > 0)
            throw new InvalidDataException("type4 站点接口必须是有效的 HTTP(S) 地址。");
        var values = new Dictionary<string, string>(args, StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(_site.Ext)) values["extend"] = _site.Ext!;
        var builder = new UriBuilder(endpoint) { Fragment = "" };
        var retained = endpoint.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(pair => !values.ContainsKey(Uri.UnescapeDataString(pair.Split('=', 2)[0].Replace("+", " "))));
        builder.Query = string.Join("&", retained.Concat(
            values.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value))));
        return builder.Uri.AbsoluteUri;
    }

    private static Dictionary<string, string> HomeArgs() => new(StringComparer.Ordinal) { ["filter"] = "true" };

    public async Task<IReadOnlyList<Category>> GetCategoriesAsync(CancellationToken ct = default)
    {
        using var document = JsonDocument.Parse(await FetchAsync(BuildRequestUrl(HomeArgs()), ct).ConfigureAwait(false));
        return List(document.RootElement, "class")
            .Select(entry => new Category(Text(entry, "type_id") ?? "", Text(entry, "type_name") ?? ""))
            .Where(category => category.Id.Length > 0).ToList();
    }

    public async Task<IReadOnlyList<FilterGroup>> GetFiltersAsync(string categoryId, CancellationToken ct = default)
        => ParseFilters(await FetchAsync(BuildRequestUrl(HomeArgs()), ct).ConfigureAwait(false), categoryId);

    public async Task<MediaPage> GetHomeAsync(CancellationToken ct = default)
        => ParsePage(await FetchAsync(BuildRequestUrl(HomeArgs()), ct).ConfigureAwait(false), 1);

    public async Task<MediaPage> GetItemsAsync(string categoryId, int page, IReadOnlyDictionary<string, string>? filters, CancellationToken ct = default)
    {
        if (page < 1) throw new InvalidDataException("页码无效。");
        var args = new Dictionary<string, string>(StringComparer.Ordinal) { ["ac"] = "detail", ["pg"] = page.ToString(CultureInfo.InvariantCulture) };
        if (categoryId.Length > 0 && categoryId != "0") args["t"] = categoryId;
        if (filters is not null)
            foreach (var key in new[] { "class", "area", "year", "lang" })
                if (filters.TryGetValue(key, out var value) && value.Length > 0) args[key] = value;
        return ParsePage(await FetchAsync(BuildRequestUrl(args), ct).ConfigureAwait(false), page);
    }

    public async Task<MediaPage> SearchAsync(string query, int page, CancellationToken ct = default)
    {
        if (page < 1) throw new InvalidDataException("页码无效。");
        var args = new Dictionary<string, string>(StringComparer.Ordinal)
            { ["wd"] = query, ["quick"] = "false", ["pg"] = page.ToString(CultureInfo.InvariantCulture) };
        return ParsePage(await FetchAsync(BuildRequestUrl(args), ct).ConfigureAwait(false), page);
    }

    public async Task<MediaDetail> GetDetailAsync(string mediaId, CancellationToken ct = default)
        => ParseDetail(await FetchAsync(BuildRequestUrl(new Dictionary<string, string>(StringComparer.Ordinal) { ["ac"] = "detail", ["ids"] = mediaId }), ct).ConfigureAwait(false), mediaId).Detail;

    public async Task<PlaybackRequest> ResolvePlaybackAsync(string mediaId, string episodeId, CancellationToken ct = default)
    {
        // 重新拉详情：play 的地址/flag 可能随会话变化，身份不由地址本身决定。
        var parsed = ParseDetail(await FetchAsync(BuildRequestUrl(new Dictionary<string, string>(StringComparer.Ordinal) { ["ac"] = "detail", ["ids"] = mediaId }), ct).ConfigureAwait(false), mediaId);
        if (!parsed.Entries.TryGetValue(episodeId, out var entry)) throw new InvalidDataException("该选集已不存在，请刷新详情。");
        using var document = JsonDocument.Parse(await FetchAsync(
            BuildRequestUrl(new Dictionary<string, string>(StringComparer.Ordinal) { ["play"] = entry.Address, ["flag"] = entry.Flag }), ct).ConfigureAwait(false));
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("type4 播放响应不是 JSON 对象。");

        var choices = ParseChoices(root, entry.Title);
        var playUrl = (Text(root, "playUrl") ?? "").Trim();
        var parse = IntFlag(root, "parse");
        var jx = IntFlag(root, "jx");

        var direct = choices.Count > 0 ? choices[0].Uri : "";
        // 站点没给地址时优先转述它给出的原因（如需要登录），而不是含糊的解析失败。
        if (direct.Length == 0)
            foreach (var key in new[] { "errMsg", "msg", "message" })
                if (Text(root, key) is { Length: > 0 } message) throw new NotSupportedException("type4 站点未返回播放地址：" + message);
        // 地址回退顺序：响应 url → 原始选集地址 → playUrl（与 JAR 宿主 CatVod playerContent 契约一致）。
        var uri = direct.Length > 0 ? direct : entry.Address;
        if (uri.Length == 0 && playUrl.Length > 0) uri = playUrl;
        if (uri.Length == 0) throw new InvalidDataException("type4 站点未返回播放地址。");
        // 只有响应直接给出的地址才强制 HTTP(S)；回退到原始选集地址时它是解析用的标识，交给解析器/嗅探。
        if (direct.Length > 0 && (!Uri.TryCreate(direct, UriKind.Absolute, out var address) || address.Scheme is not ("http" or "https") || address.UserInfo.Length > 0))
            throw new InvalidDataException("type4 返回的播放地址不是有效的 HTTP(S) 地址。");

        var isParse = parse != 0 || jx != 0 || (playUrl.Length > 0 && direct.Length == 0);
        var headers = PlaybackHeaders(root);
        return new PlaybackRequest
        {
            Uri = uri,
            Choices = choices,
            Resolution = isParse ? ResolutionKind.Json : ResolutionKind.Direct,
            ParseEndpoint = isParse && playUrl.Length > 0 ? playUrl : null,
            Jx = jx != 0 ? jx.ToString(CultureInfo.InvariantCulture) : null,
            Headers = headers,
            SourceKey = Key,
            SourceName = Name,
            MediaId = mediaId,
            LineId = entry.LineId,
            EpisodeId = episodeId,
            Title = parsed.Detail.Item.Title + " · " + entry.Title,
            Poster = parsed.Detail.Item.Poster,
            Remarks = parsed.Detail.Item.Remarks,
        };
    }

    /// <summary>播放返回的 url：字符串（单地址）或 [名称, 地址, ...] 成对（多地址）。</summary>
    private static List<PlaybackChoice> ParseChoices(JsonElement root, string fallbackName)
    {
        var choices = new List<PlaybackChoice>();
        if (!root.TryGetProperty("url", out var value)) return choices;
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = (value.GetString() ?? "").Trim();
            if (text.Length > 0) choices.Add(new PlaybackChoice(fallbackName, text));
            return choices;
        }
        if (value.ValueKind != JsonValueKind.Array) return choices;
        var items = value.EnumerateArray().ToArray();
        if (items.Length == 1 && items[0].ValueKind == JsonValueKind.String)
        {
            var only = (items[0].GetString() ?? "").Trim();
            if (only.Length > 0) choices.Add(new PlaybackChoice(fallbackName, only));
            return choices;
        }
        for (var index = 0; index + 1 < items.Length; index += 2)
        {
            if (items[index].ValueKind != JsonValueKind.String || items[index + 1].ValueKind != JsonValueKind.String) continue;
            var name = (items[index].GetString() ?? "").Trim();
            var uri = (items[index + 1].GetString() ?? "").Trim();
            if (uri.Length == 0 || !Uri.TryCreate(uri, UriKind.Absolute, out var address) || address.Scheme is not ("http" or "https") || address.UserInfo.Length > 0) continue;
            choices.Add(new PlaybackChoice(name.Length > 0 ? name : fallbackName, uri));
        }
        return choices;
    }

    /// <summary>站点 header 打底，叠加播放响应的 header（对象或 JSON 字符串）与 UA；不做全局会话。</summary>
    private Dictionary<string, string> PlaybackHeaders(JsonElement root)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (_site.Header is { Count: > 0 } site)
            foreach (var pair in site) headers[pair.Key] = pair.Value;
        if (root.TryGetProperty("header", out var header)) MergeHeader(headers, header);
        if (Text(root, "UA") is { Length: > 0 } upperAgent) headers["User-Agent"] = upperAgent;
        else if (Text(root, "ua") is { Length: > 0 } lowerAgent) headers["User-Agent"] = lowerAgent;
        return headers;
    }

    private static void MergeHeader(Dictionary<string, string> headers, JsonElement header)
    {
        if (header.ValueKind == JsonValueKind.String)
        {
            var text = header.GetString() ?? "";
            if (text.Trim().Length == 0) return;
            try
            {
                using var document = JsonDocument.Parse(text);
                if (document.RootElement.ValueKind == JsonValueKind.Object) MergeHeaderObject(headers, document.RootElement);
            }
            catch (JsonException) { /* 非 JSON 的 header 字符串直接忽略，不臆造。 */ }
            return;
        }
        if (header.ValueKind == JsonValueKind.Object) MergeHeaderObject(headers, header);
    }

    private static void MergeHeaderObject(Dictionary<string, string> headers, JsonElement header)
    {
        foreach (var property in header.EnumerateObject())
        {
            if (property.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)) continue;
            var value = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() ?? "" : property.Value.ToString();
            if (!IsHeaderName(property.Name) || value.IndexOfAny(['\r', '\n', '\0']) >= 0)
                throw new InvalidDataException("type4 播放头含非法字符。");
            headers[property.Name] = value;
        }
    }

    /// <summary>RFC 7230 token 字符；拒绝空格、冒号等不能原样交给 HTTP 的头名。</summary>
    private static bool IsHeaderName(string name)
    {
        if (name.Length == 0) return false;
        foreach (var c in name)
            if (!char.IsAsciiLetterOrDigit(c) && !"!#$%&'*+-.^_`|~".Contains(c)) return false;
        return true;
    }

    private sealed record PlayEntry(string Flag, string Address, string Title, string LineId);
    private sealed record ParsedDetail(MediaDetail Detail, Dictionary<string, PlayEntry> Entries);

    private static ParsedDetail ParseDetail(string json, string mediaId)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var vod = root;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("list", out var list))
        {
            if (list.ValueKind != JsonValueKind.Array) throw new InvalidDataException("type4 详情 list 不是数组。");
            vod = list.EnumerateArray().FirstOrDefault();
        }
        if (vod.ValueKind != JsonValueKind.Object) throw new InvalidDataException("type4 详情为空。");
        var flags = (Text(vod, "vod_play_from") ?? "").Split("$$$", StringSplitOptions.None).Select(flag => flag.Trim()).ToList();
        var urls = (Text(vod, "vod_play_url") ?? "").Split("$$$", StringSplitOptions.None);
        var entries = new Dictionary<string, PlayEntry>(StringComparer.Ordinal);
        var lines = new List<PlaybackLine>();
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < urls.Length; i++)
        {
            var flag = i < flags.Count ? flags[i] : "";
            if (i >= flags.Count) flags.Add(flag);
            var lineName = flag.Length > 0 ? flag : "默认线路";
            occurrences.TryGetValue(flag, out var occurrence);
            occurrences[flag] = occurrence + 1;
            var lineId = Identity(flag) + ":" + occurrence.ToString(CultureInfo.InvariantCulture);
            var episodes = new List<Episode>();
            var titles = new Dictionary<string, int>(StringComparer.Ordinal);
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
                entries[id] = new PlayEntry(flag, playId, title, lineId);
            }
            if (episodes.Count > 0) lines.Add(new PlaybackLine(lineId, lineName, episodes));
        }
        var item = Item(vod) with { Id = Text(vod, "vod_id") is { Length: > 0 } vodId ? vodId : mediaId };
        var content = Text(vod, "vod_content") ?? "";
        return new ParsedDetail(new MediaDetail
        {
            Item = item,
            Description = StripHtml(content),
            Director = Text(vod, "vod_director"),
            Actor = Text(vod, "vod_actor"),
            Lines = lines,
        }, entries);
    }

    internal static IReadOnlyList<FilterGroup> ParseFilters(string json, string categoryId)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var result = new List<FilterGroup>();
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("filters", out var filters) || filters.ValueKind != JsonValueKind.Object
            || !filters.TryGetProperty(categoryId, out var groups) || groups.ValueKind != JsonValueKind.Array) return result;
        foreach (var group in groups.EnumerateArray().Take(20))
        {
            if (group.ValueKind != JsonValueKind.Object) continue;
            var key = Text(group, "key") ?? "";
            if (key.Length == 0 || !group.TryGetProperty("value", out var values) || values.ValueKind != JsonValueKind.Array) continue;
            var options = values.EnumerateArray().Take(100).Where(value => value.ValueKind == JsonValueKind.Object)
                .Select(value => new FilterValue(Text(value, "n") ?? "", Text(value, "v") ?? "")).ToList();
            result.Add(new FilterGroup(key, Text(group, "name") ?? key, options, Text(group, "init") ?? ""));
        }
        return result;
    }

    private static MediaPage ParsePage(string json, int requestedPage)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("type4 结果不是 JSON 对象。");
        var items = List(root, "list").Select(Item).Where(item => item.Id.Length > 0).ToList();
        var page = int.TryParse(Text(root, "page"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedPage) && parsedPage > 0 ? parsedPage : requestedPage;
        var pageCount = int.TryParse(Text(root, "pagecount"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedCount) && parsedCount > 0 ? parsedCount : page;
        return new MediaPage(items, page, Math.Max(page, pageCount));
    }

    private static IEnumerable<JsonElement> List(JsonElement root, string key)
    {
        if (root.ValueKind != JsonValueKind.Object) return [];
        if (!root.TryGetProperty(key, out var list) || list.ValueKind != JsonValueKind.Array) return [];
        return list.EnumerateArray();
    }

    private static MediaItem Item(JsonElement vod) => new()
    {
        Id = Text(vod, "vod_id") ?? "",
        Title = Text(vod, "vod_name") is { Length: > 0 } title ? title : "(未命名)",
        Poster = Text(vod, "vod_pic"),
        Remarks = Text(vod, "vod_remarks"),
        Year = Text(vod, "vod_year"),
        Area = Text(vod, "vod_area"),
        TypeName = Text(vod, "type_name"),
    };

    private static string? Text(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    private static int IntFlag(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var value)) return 0;
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetInt32(out var number) ? number : 0,
            JsonValueKind.True => 1,
            JsonValueKind.String => int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0,
            _ => 0,
        };
    }

    private static string Identity(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    private static string StripHtml(string html) => WebUtility.HtmlDecode(HtmlTags().Replace(BreakTags().Replace(html, "\n"), "")).Trim();

    [GeneratedRegex(@"<\s*(?:br\s*/?|/p|/div)\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex BreakTags();
    [GeneratedRegex("<[^>]*>")]
    private static partial Regex HtmlTags();
}
