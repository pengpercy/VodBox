using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using VodBox.Core;

namespace VodBox.PluginHost.JarPlugin;

/// <summary>
/// CatVod spider 方法的 JSON 载荷解析与到 VodBox 模型的映射。
/// 解析用 <see cref="JsonDocument"/>（宽松、可容忍字符串/数字混用），不臆造缺失字段。
/// </summary>
public static partial class CatVodCatalog
{
    /// <summary>线路 flag 与剧集地址的编码分隔符：控制字符，正常 URL/线路名不会出现。</summary>
    public const char EpisodeSeparator = '\u0001';

    /// <summary>把线路标识与剧集地址编码成详情里的剧集 id（播放时再解出 flag）。</summary>
    public static string EncodeEpisodeId(string? flag, string url) => string.IsNullOrEmpty(flag) ? url : flag + EpisodeSeparator + url;

    /// <summary>从剧集 id 解出（线路标识, 剧集地址）。</summary>
    public static (string Flag, string Url) DecodeEpisodeId(string episodeId)
    {
        var index = episodeId.IndexOf(EpisodeSeparator);
        return index < 0 ? ("", episodeId) : (episodeId[..index], episodeId[(index + 1)..]);
    }

    private static string? Text(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var element)) return null;
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    /// <summary>宽松读取整数标志（parse/jx）：字符串、数字、布尔混用都接受，缺省或非法回退 0，不臆造。</summary>
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

    private static JsonElement Root(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            // Clone 让元素脱离文档，可安全在 dispose 之后使用。
            return document.RootElement.Clone();
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("插件返回的 JSON 无法解析：" + error.Message, error);
        }
    }

    /// <summary>homeContent 的 class[] → 分类列表。</summary>
    public static IReadOnlyList<Category> ParseCategories(string json)
    {
        var root = Root(json);
        if (!root.TryGetProperty("class", out var classes) || classes.ValueKind != JsonValueKind.Array) return [];
        var result = new List<Category>();
        foreach (var entry in classes.EnumerateArray())
        {
            var id = Text(entry, "type_id");
            if (string.IsNullOrWhiteSpace(id)) continue;
            result.Add(new Category(id!, Text(entry, "type_name") ?? id!));
        }
        return result;
    }

    /// <summary>homeContent 的 filters{} → 指定分类的筛选组。</summary>
    public static IReadOnlyList<FilterGroup> ParseFilters(string json, string categoryId)
    {
        var root = Root(json);
        if (!root.TryGetProperty("filters", out var filters) || filters.ValueKind != JsonValueKind.Object) return [];
        if (!filters.TryGetProperty(categoryId, out var groups) || groups.ValueKind != JsonValueKind.Array) return [];
        var result = new List<FilterGroup>();
        foreach (var group in groups.EnumerateArray())
        {
            var key = Text(group, "key");
            if (string.IsNullOrWhiteSpace(key)) continue;
            var values = new List<FilterValue>();
            if (group.TryGetProperty("value", out var items) && items.ValueKind == JsonValueKind.Array)
                foreach (var item in items.EnumerateArray())
                    values.Add(new FilterValue(Text(item, "n") ?? "", Text(item, "v") ?? ""));
            if (values.Count == 0) continue;
            result.Add(new FilterGroup(key!, Text(group, "name") ?? key!, values, values[0].Value));
        }
        return result;
    }

    /// <summary>homeContent / categoryContent / searchContent 的 list[] → 分页结果。</summary>
    public static MediaPage ParsePage(string json, int requestedPage)
    {
        var root = Root(json);
        var page = int.TryParse(Text(root, "page"), out var parsedPage) && parsedPage > 0 ? parsedPage : requestedPage;
        var pageCount = int.TryParse(Text(root, "pagecount"), out var parsedCount) && parsedCount > 0 ? parsedCount : page;
        var items = new List<MediaItem>();
        if (root.TryGetProperty("list", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var entry in list.EnumerateArray())
            {
                var item = ToItem(entry);
                if (item is not null) items.Add(item);
            }
        return new MediaPage(items, page, Math.Max(page, pageCount));
    }

    private static MediaItem? ToItem(JsonElement entry)
    {
        var id = Text(entry, "vod_id");
        if (string.IsNullOrWhiteSpace(id)) return null;
        return new MediaItem
        {
            Id = id!,
            Title = Text(entry, "vod_name") is { Length: > 0 } title ? title : "(未命名)",
            Poster = Text(entry, "vod_pic"),
            Remarks = Text(entry, "vod_remarks"),
            Year = Text(entry, "vod_year"),
            Area = Text(entry, "vod_area"),
            TypeName = Text(entry, "type_name"),
        };
    }

    /// <summary>detailContent 的 list[0] → 媒体详情（线路/剧集）。</summary>
    public static MediaDetail ParseDetail(string json, string mediaId)
    {
        var root = Root(json);
        if (!root.TryGetProperty("list", out var list) || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() == 0)
            throw new InvalidDataException($"插件未返回条目 {mediaId} 的详情。");
        var entry = list[0];
        var content = Text(entry, "vod_content") ?? "";
        if (content.Contains('<')) content = StripTags(content);
        var lines = ParseLines(Text(entry, "vod_play_from") ?? "", Text(entry, "vod_play_url") ?? "");
        return new MediaDetail
        {
            Item = ToItem(entry) ?? new MediaItem { Id = mediaId, Title = Text(entry, "vod_name") ?? mediaId },
            Description = content.Trim(),
            Director = Text(entry, "vod_director"),
            Actor = Text(entry, "vod_actor"),
            Lines = lines,
        };
    }

    private static IReadOnlyList<PlaybackLine> ParseLines(string froms, string urls)
    {
        var flags = froms.Split("$$$", StringSplitOptions.None);
        var groups = urls.Split("$$$", StringSplitOptions.None);
        var lines = new List<PlaybackLine>();
        for (var i = 0; i < groups.Length; i++)
        {
            var flag = i < flags.Length && !string.IsNullOrWhiteSpace(flags[i]) ? flags[i].Trim() : $"线路{i + 1}";
            var episodes = new List<Episode>();
            foreach (var pair in groups[i].Split('#', StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = pair.IndexOf('$');
                var title = separator > 0 ? pair[..separator].Trim() : $"第{episodes.Count + 1}集";
                var url = (separator >= 0 ? pair[(separator + 1)..] : pair).Trim();
                if (url.Length == 0) continue;
                // Uri 留空：JAR 源一律走 ResolvePlaybackAsync（地址可能带签名，不能预置）。
                episodes.Add(new Episode(EncodeEpisodeId(flag, url), title));
            }
            if (episodes.Count == 0) continue;
            var id = lines.Any(line => line.Id == flag) ? flag + ":" + i : flag;
            lines.Add(new PlaybackLine(id, flag, episodes));
        }
        return lines;
    }

    /// <summary>
    /// playerContent 结果 → 播放请求。<paramref name="episodeUrl"/> 为剧集原始地址。
    /// 完整保留：合法播放头（UA/Cookie/Authorization/Referer…）、parse/jx/playUrl、以及 url 多地址；
    /// 引擎直传不了的 Cookie/Authorization 等头由调用方经现有受控媒体代理转发，不在这里丢弃。
    /// </summary>
    public static PlaybackRequest ParsePlayback(string json, string episodeUrl, string title, string sourceKey, string sourceName, string mediaId, string lineId, string episodeId, string? poster, string? remarks)
    {
        var root = Root(json);
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("插件播放响应不是 JSON 对象。");
        var headers = ParseHeaders(root);
        var choices = ParseChoices(root, lineId);
        var direct = choices.Count > 0 ? choices[0].Uri : "";
        var playUrl = (Text(root, "playUrl") ?? "").Trim();
        var parse = IntFlag(root, "parse");
        var jx = IntFlag(root, "jx");
        // 地址回退顺序：响应 url → 原始选集地址 → playUrl（与 TVBox 契约一致）。
        var uri = direct.Length > 0 ? direct : episodeUrl;
        if (string.IsNullOrWhiteSpace(uri) && playUrl.Length > 0) uri = playUrl;
        if (string.IsNullOrWhiteSpace(uri))
        {
            // 完全没有可播地址时，优先转述站点给出的原因（如需要登录），而不是含糊的「无地址」。
            foreach (var key in new[] { "errMsg", "msg", "message" })
                if (Text(root, key) is { Length: > 0 } message) throw new NotSupportedException("插件未返回播放地址：" + message);
            throw new InvalidDataException("插件未返回播放地址。");
        }
        // 只有响应直接给出的地址才强制 HTTP(S)；回退到原始选集地址时它是解析用的标识，交给解析器。
        if (direct.Length > 0 && (!Uri.TryCreate(direct, UriKind.Absolute, out var address) || address.Scheme is not ("http" or "https") || address.UserInfo.Length > 0))
            throw new InvalidDataException("插件返回的播放地址不是有效的 HTTP(S) 地址。");
        // parse/jx 任一非零，或只有 playUrl 没有直连地址，都视为需要 JSON 解析；保留原始 jx 供上层区分解析器。
        var isParse = parse != 0 || jx != 0 || (playUrl.Length > 0 && direct.Length == 0);
        return new PlaybackRequest
        {
            Uri = uri,
            Choices = choices,
            Title = title,
            Resolution = isParse ? ResolutionKind.Json : ResolutionKind.Direct,
            ParseEndpoint = isParse && playUrl.Length > 0 ? playUrl : null,
            Jx = jx != 0 ? jx.ToString(CultureInfo.InvariantCulture) : null,
            Headers = headers,
            SourceKey = sourceKey,
            SourceName = sourceName,
            MediaId = mediaId,
            LineId = lineId,
            EpisodeId = episodeId,
            Poster = poster,
            Remarks = remarks,
        };
    }

    /// <summary>
    /// 播放返回的 url：字符串（单地址）或 [名称, 地址, 名称, 地址…] 成对（多地址）。
    /// 多地址完整保留到 <see cref="PlaybackRequest.Choices"/>，不静默只留首个。
    /// </summary>
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

    /// <summary>
    /// 播放响应 header：对象或 JSON 字符串，叠加响应级 UA/ua 字段。完整保留 Cookie/Authorization/Referer 等
    /// 合法头（不因为引擎只认 UA/Referer 就在这里丢弃），非法字符（控制字符/非 token 头名）明确拒绝而非悄悄吞掉。
    /// </summary>
    private static Dictionary<string, string> ParseHeaders(JsonElement root)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("header", out var header)) MergeHeader(result, header);
        if (Text(root, "UA") is { Length: > 0 } upperAgent) result["User-Agent"] = upperAgent;
        else if (Text(root, "ua") is { Length: > 0 } lowerAgent) result["User-Agent"] = lowerAgent;
        return result;
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
                throw new InvalidDataException("插件播放头含非法字符。");
            headers[property.Name] = value;
        }
    }

    /// <summary>RFC 7230 token 字符：非 token 的头名（含空格、冒号等）拒绝，不原样透传。</summary>
    private static bool IsHeaderName(string name)
    {
        if (name.Length == 0) return false;
        foreach (var c in name)
            if (!char.IsAsciiLetterOrDigit(c) && !"!#$%&'*+-.^_`|~".Contains(c)) return false;
        return true;
    }

    private static string StripTags(string html) => WebUtility.HtmlDecode(Tag().Replace(html, "")).Trim();

    [GeneratedRegex(@"<[^>]*>")]
    private static partial Regex Tag();
}
