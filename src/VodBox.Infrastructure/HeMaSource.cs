using System.Text;
using System.Text.Json;
using VodBox.Core;

namespace VodBox.Infrastructure;

/// <summary>
/// 盒马短剧（宝盒 <c>csp_HeMa</c>）原生实现，按随宝盒下发的 <c>bh0822.jar</c> 反编译源码
/// （<c>com.github.catvod.spider.HeMa</c>）重写。原插件把业务请求包在 <c>hema.php</c> 转发层后面，
/// 这里保留同一转发协议与同样的四个 apiId（1125/1131/1139/1802）。
/// </summary>
public sealed class HeMaSource : IResolvingContentSource
{
    private const string Relay = "https://www.tangsan.fun/hema.php";
    private const string Portal = "https://freevideo.zqqds.cn/free-video-portal/portal/";
    private const string PlayAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/117.0.0.0 Safari/537.36";
    private const string PlayReferer = "https://freevideo.zqqds.cn/";

    private static readonly string[] ChapterKeys = ["chapterId", "chapter_id", "id"];
    private static readonly string[] BookKeys = ["bookId", "id", "book_id", "novelId"];
    private static readonly string[] BookNameKeys = ["bookName", "name", "title", "bookAlias"];
    private static readonly string[] CoverKeys = ["coverWap", "cover", "coverUrl", "image", "pic"];
    private static readonly string[] RemarkKeys = ["chapterInfo", "serialCount", "latestChapterName", "categoryName", "desc"];

    private static readonly (string Value, string Name)[] Genres =
        [("", "全部"), ("959", "精选"), ("964", "穿越"), ("961", "重生"), ("960", "古装"), ("963", "言情"), ("965", "强者回归"),
         ("967", "萌宝"), ("962", "伦理"), ("969", "超能"), ("966", "后悔流"), ("968", "修仙"), ("971", "年代"), ("972", "悬疑"), ("970", "奇幻")];

    private readonly SourceInfo _site;
    private readonly Func<string, string, CancellationToken, Task<string>> _post;
    private readonly string _version;
    private readonly string _pname;
    private readonly string _channelCode;
    private readonly string _utdidTmp;
    private readonly string _utdid;
    private readonly string _userId;
    private readonly long _installTime;
    private readonly string _boxId;

    public string Key => _site.Key;
    public string Name => _site.Name;

    public HeMaSource(SourceInfo site) : this(site, null) { }

    internal HeMaSource(SourceInfo site, Func<string, string, CancellationToken, Task<string>>? post)
    {
        _site = site;
        if (!Supports(site.Ext)) throw new NotSupportedException("HeMa ext 配置不受支持。");
        _version = "3.7.0";
        _pname = "com.dz.hmjc";
        _channelCode = "HMJC1000000";
        _utdidTmp = "A20260716161211528mStog5";
        _utdid = "45a078a3e0a0c07e9da15cc1bd98041a";
        _userId = "2854498494";
        _installTime = 1784189517540L;
        _boxId = "BnCMa2+Kbcl9D1ctCsHNdkR0b6tNUb4MWkCbJX2wXsVtqTKVONaSzTI3b+ulP+SEOFULO5vnXXaMNkE6Gcy7K0g==";
        if (!string.IsNullOrWhiteSpace(site.Ext))
        {
            using var document = JsonDocument.Parse(site.Ext);
            var root = document.RootElement;
            _version = String(root, "version") is { Length: > 0 } version ? version : _version;
            _pname = String(root, "pname") is { Length: > 0 } pname ? pname : _pname;
            _channelCode = String(root, "channelCode") is { Length: > 0 } channel ? channel : _channelCode;
            _utdidTmp = String(root, "utdidTmp") is { Length: > 0 } temp ? temp : _utdidTmp;
            _utdid = String(root, "utdid") is { Length: > 0 } utdid ? utdid : _utdid;
            _userId = String(root, "userId") is { Length: > 0 } user ? user : _userId;
            _boxId = String(root, "boxId") is { Length: > 0 } box ? box : _boxId;
            if (root.TryGetProperty("installTime", out var install) && install.ValueKind == JsonValueKind.Number) _installTime = install.GetInt64();
        }
        _post = post ?? Post;
    }

    internal static bool Supports(string? ext)
    {
        if (string.IsNullOrWhiteSpace(ext)) return true;
        var value = ext.Trim();
        if (!value.StartsWith('{')) return false;
        try
        {
            using var document = JsonDocument.Parse(value);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                switch (property.Name)
                {
                    case "version": case "pname": case "channelCode": case "utdidTmp": case "utdid": case "userId": case "boxId":
                        if (property.Value.ValueKind != JsonValueKind.String) return false;
                        break;
                    case "installTime":
                        if (property.Value.ValueKind != JsonValueKind.Number) return false;
                        break;
                    default: return false;
                }
            }
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static async Task<string> Post(string url, string body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body, Encoding.UTF8) };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        using var response = await DefaultHttp.PlainClient.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    /// <summary>转发层调用：返回 <c>data</c> 字段（对象/数组直接序列化，标量取原文），与插件 a() 等价。</summary>
    private async Task<string> CallAsync(string apiId, string body, CancellationToken ct)
    {
        var payload = new StringBuilder("{\"apiId\":").Append(Quote(apiId))
            .Append(",\"url\":").Append(Quote(Portal + apiId))
            .Append(",\"body\":").Append(Quote(body))
            .Append(",\"config\":{\"version\":").Append(Quote(_version))
            .Append(",\"pname\":").Append(Quote(_pname))
            .Append(",\"channelCode\":").Append(Quote(_channelCode))
            .Append(",\"utdidTmp\":").Append(Quote(_utdidTmp))
            .Append(",\"utdid\":").Append(Quote(_utdid))
            .Append(",\"userId\":").Append(Quote(_userId))
            .Append(",\"installTime\":").Append(_installTime)
            .Append(",\"boxId\":").Append(Quote(_boxId))
            .Append("}}").ToString();
        var raw = await _post(Relay, payload, ct).ConfigureAwait(false);
        if (raw.Length == 0) return "";
        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data))
        {
            if (data.ValueKind is JsonValueKind.Object or JsonValueKind.Array) return data.GetRawText();
            if (data.ValueKind == JsonValueKind.String && data.GetString() is { Length: > 0 } text) return text;
        }
        return raw;
    }

    private async Task<JsonElement?> DataAsync(string apiId, string body, CancellationToken ct)
    {
        var raw = await CallAsync(apiId, body, ct).ConfigureAwait(false);
        if (raw.Trim().Length == 0) return null;
        try
        {
            using var document = JsonDocument.Parse(raw);
            if (document.RootElement.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array)) return null;
            return document.RootElement.Clone();
        }
        catch (JsonException) { return null; }
    }

    public Task<IReadOnlyList<Category>> GetCategoriesAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<Category>>(
            [new("drama_all", "短剧"), new("manju_all", "漫剧"), new("rank_hot", "热播榜"), new("rank_rise", "飙升榜"), new("rank_new", "新剧榜"), new("rank_search", "热搜榜")]);
    }

    public Task<IReadOnlyList<FilterGroup>> GetFiltersAsync(string categoryId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        IReadOnlyList<FilterGroup> groups = categoryId == "drama_all"
            ? [new FilterGroup("channelId", "分类", Genres.Select(g => new FilterValue(g.Name, g.Value)).ToList(), "")]
            : [];
        return Task.FromResult(groups);
    }

    public async Task<MediaPage> GetHomeAsync(CancellationToken ct = default) => await GetItemsAsync("drama_all", 1, null, ct).ConfigureAwait(false);

    public async Task<MediaPage> GetItemsAsync(string categoryId, int page, IReadOnlyDictionary<string, string>? filters, CancellationToken ct = default)
    {
        if (page < 1) throw new InvalidDataException("页码无效。");
        var category = string.IsNullOrEmpty(categoryId) ? "drama_all" : categoryId;
        var body = new StringBuilder("{\"recSwitch\":true,\"kingKongSwitch\":false,\"storePageId\":10002,\"audioBook\":1,")
            .Append("\"theaterSubscriptSwitch\":true,\"needPlayLink\":1,\"isOldAgeMode\":false,\"resolutionRate\":\"720P\",\"preview\":1");
        if (category.StartsWith("rank_", StringComparison.Ordinal))
        {
            var channel = category switch { "rank_rise" => 69, "rank_new" => 70, "rank_search" => 71, _ => 68 };
            var name = category switch { "rank_rise" => "飙升榜", "rank_new" => "新剧榜", "rank_search" => "热搜榜", _ => "热播榜" };
            body.Append(",\"channelGroupId\":\"14\",\"channelId\":").Append(channel).Append(",\"channelName\":").Append(Quote(name));
        }
        else
        {
            body.Append(",\"channelGroupId\":").Append(Quote(category == "manju_all" ? "78" : "157"));
            var channel = filters is not null && filters.TryGetValue("channelId", out var value) ? value : "";
            if (channel.Length == 0) channel = category.StartsWith("drama_", StringComparison.Ordinal) ? category[6..] : "";
            if (channel.Length > 0 && channel != "all")
            {
                body.Append(",\"channelId\":").Append(int.TryParse(channel, out var id) ? id : 0)
                    .Append(",\"channelName\":").Append(Quote(GenreName(channel)));
            }
        }
        body.Append('}');
        var data = await DataAsync("1125", body.ToString(), ct).ConfigureAwait(false);
        var items = new List<MediaItem>();
        if (data is { } node) CollectBooks(node, items);
        return new MediaPage(items, page, items.Count > 0 ? page + 1 : page);
    }

    private static string GenreName(string value)
    {
        foreach (var genre in Genres)
            if (genre.Value == value) return genre.Name;
        return "";
    }

    public async Task<MediaPage> SearchAsync(string query, int page, CancellationToken ct = default)
    {
        if (page < 1) throw new InvalidDataException("页码无效。");
        if (page > 1) return new MediaPage([], page, page);
        var body = "{\"keyword\":" + Quote(query) + ",\"guessReserveSwitch\":0,\"reservationSwitch\":true,\"guessNewStyleSwitch\":1,\"guessStyle\":0}";
        var data = await DataAsync("1802", body, ct).ConfigureAwait(false);
        var items = new List<MediaItem>();
        if (data is { } node) CollectBooks(node, items);
        return new MediaPage(items, 1, 1);
    }

    public async Task<MediaDetail> GetDetailAsync(string mediaId, CancellationToken ct = default)
    {
        if (mediaId.Length == 0) throw new InvalidDataException("HeMa 剧集编号无效。");
        var body = "{\"bookId\":" + Quote(mediaId) + ",\"needNextChapter\":0,\"isNeedAlias\":\"\",\"bookAlias\":\"\","
            + "\"playSourceL1\":\"剧场\",\"playSourceL2\":\"剧场-热播\",\"playSourceL3\":\"热播-精选\",\"resolutionRate\":\"720P\","
            + "\"preview\":1,\"commentType\":2,\"excludeInfo\":0,\"screenClearType\":3,\"bottomStyle\":2,\"showOriginNovel\":0,\"interacPop\":0}";
        var data = await DataAsync("1131", body, ct).ConfigureAwait(false);
        var book = data is { } payload
            ? FindByField(payload, "bookId", mediaId) ?? FindFirstObject(payload, ["bookName", "bookId", "chapterList", "chapters"])
            : null;
        if (book is null && data is { ValueKind: JsonValueKind.Object } objectNode) book = objectNode;

        var episodes = new List<Episode>();
        if (data is { } source)
        {
            CollectChapters(source, mediaId, episodes, new HashSet<string>(StringComparer.Ordinal));
            if (episodes.Count <= 1 && book is { } chapterMeta)
            {
                var total = Int(chapterMeta, "updateNum");
                var rawIndex = Int(chapterMeta, "chapterIndex");
                var index = rawIndex == 0 ? 1 : rawIndex;
                var chapterId = String(chapterMeta, "chapterId");
                if (total > 1 && chapterId.Length > 0 && long.TryParse(chapterId, out var baseId))
                {
                    episodes.Clear();
                    var start = baseId - Math.Max(index - 1, 0);
                    for (var i = 1; i <= total; i++)
                    {
                        var id = (start + i - 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
                        episodes.Add(new Episode(mediaId + "|||" + id + "|||" + id, "第" + i + "集"));
                    }
                }
            }
        }
        var item = new MediaItem
        {
            Id = mediaId,
            Title = book is { } meta ? String(meta, BookNameKeys) : "",
            Poster = book is { } meta2 ? HttpString(meta2, CoverKeys) : "",
            Remarks = book is { } meta3 ? String(meta3, RemarkKeys) : "",
        };
        var detail = new MediaDetail
        {
            Item = item,
            Description = book is { } meta4 ? String(meta4, ["intro", "introduction", "summary", "desc", "description"]) : "",
            Actor = "",
            Director = "",
            Lines = episodes.Count > 0 ? [new PlaybackLine("盒马短剧", "盒马短剧", episodes)] : [],
        };
        return detail;
    }

    public async Task<PlaybackRequest> ResolvePlaybackAsync(string mediaId, string episodeId, CancellationToken ct = default)
    {
        var parts = episodeId.Split("|||");
        if (parts.Length < 2 || parts[0].Length == 0 || parts[1].Length == 0) throw new InvalidDataException("HeMa 播放标识无效。");
        var bookId = parts[0];
        var chapterId = parts[1];
        var previous = parts.Length > 2 && parts[2].Length > 0 ? parts[2] : chapterId;
        var body = "{\"bookId\":" + Quote(bookId) + ",\"chapterIds\":[" + Quote(chapterId) + "],\"unClockType\":\"load\","
            + "\"chapterId\":" + Quote(previous) + ",\"omap\":{},"
            + "\"tierPlaySource\":{\"firstTierPlaySource\":\"剧场\",\"secondTierPlaySource\":\"剧场-热播\",\"thirdTierPlaySource\":\"热播-精选\"},"
            + "\"resolutionRate\":\"720P\",\"preview\":1,\"playPercentage\":0,\"continuousAd\":0,\"commentType\":2}";
        var data = await DataAsync("1139", body, ct).ConfigureAwait(false);
        var uri = "";
        if (data is { } node)
        {
            uri = Media(FindByField(node, "chapterId", chapterId));
            if (uri.Length == 0) uri = Media(FindFirstObject(node, ["mp4Url", "mp4SwitchUrl"]));
            if (uri.Length == 0) uri = FirstMediaUrl(node);
            if (uri.Length == 0) uri = String(node, ["mp4Url", "playUrl", "videoUrl", "url", "chapterUrl"]);
        }
        if (uri.Length == 0 || !Uri.TryCreate(uri, UriKind.Absolute, out var address) || address.Scheme is not ("http" or "https"))
            throw new InvalidDataException("play url not found");
        return new PlaybackRequest
        {
            Uri = uri,
            SourceKey = Key,
            SourceName = Name,
            MediaId = mediaId,
            EpisodeId = episodeId,
            LineId = "盒马短剧",
            Title = Name,
            Headers = new() { ["User-Agent"] = PlayAgent, ["Referer"] = PlayReferer },
        };
    }

    // ---------- 递归取值（与插件同名的工具方法） ----------

    private static void CollectBooks(JsonElement node, List<MediaItem> items)
    {
        if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in node.EnumerateArray()) CollectBooks(entry, items);
            return;
        }
        if (node.ValueKind != JsonValueKind.Object) return;
        var id = String(node, BookKeys);
        var name = String(node, BookNameKeys);
        if (id.Length > 0 && name.Length > 0)
        {
            items.Add(new MediaItem { Id = id, Title = name, Poster = HttpString(node, CoverKeys), Remarks = String(node, RemarkKeys) });
            return;
        }
        foreach (var property in node.EnumerateObject()) CollectBooks(property.Value, items);
    }

    private static void CollectChapters(JsonElement node, string bookId, List<Episode> episodes, HashSet<string> seen)
    {
        if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in node.EnumerateArray()) CollectChapters(entry, bookId, episodes, seen);
            return;
        }
        if (node.ValueKind != JsonValueKind.Object) return;
        var chapterId = String(node, ChapterKeys);
        if (chapterId.Length > 0 && !seen.Contains(chapterId) &&
            (node.TryGetProperty("chapterId", out _) || node.TryGetProperty("chapterName", out _) || node.TryGetProperty("duration", out _) ||
             node.TryGetProperty("videoId", out _) || node.TryGetProperty("playUrl", out _)))
        {
            seen.Add(chapterId);
            var name = String(node, ["chapterName", "name", "title"]);
            if (name.Length == 0) name = (episodes.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var previous = String(node, ["preChapterId", "prevChapterId"]);
            if (previous.Length == 0) previous = chapterId;
            episodes.Add(new Episode(bookId + "|||" + chapterId + "|||" + previous, name));
        }
        foreach (var property in node.EnumerateObject()) CollectChapters(property.Value, bookId, episodes, seen);
    }

    private static JsonElement? FindByField(JsonElement node, string name, string value)
    {
        if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in node.EnumerateArray())
            {
                if (FindByField(entry, name, value) is { } found) return found;
            }
            return null;
        }
        if (node.ValueKind != JsonValueKind.Object) return null;
        if (node.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String && property.GetString() == value) return node;
        foreach (var entry in node.EnumerateObject())
        {
            if (FindByField(entry.Value, name, value) is { } found) return found;
        }
        return null;
    }

    private static JsonElement? FindFirstObject(JsonElement node, string[] names)
    {
        if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in node.EnumerateArray())
            {
                if (FindFirstObject(entry, names) is { } found) return found;
            }
            return null;
        }
        if (node.ValueKind != JsonValueKind.Object) return null;
        if (names.Any(name => node.TryGetProperty(name, out _))) return node;
        foreach (var entry in node.EnumerateObject())
        {
            if (FindFirstObject(entry.Value, names) is { } found) return found;
        }
        return null;
    }

    /// <summary>对应插件 f()：先 content 内的直链字段，再 mp4SwitchUrl 数组，再对象自身同名字段。</summary>
    private static string Media(JsonElement? node)
    {
        if (node is not { ValueKind: JsonValueKind.Object } value) return "";
        JsonElement? content = value.TryGetProperty("content", out var inner) && inner.ValueKind == JsonValueKind.Object ? inner : null;
        foreach (var key in new[] { "mp4Url", "playUrl", "videoUrl", "url" })
        {
            if (content is { } contentNode && contentNode.TryGetProperty(key, out var fromContent) && fromContent.ValueKind == JsonValueKind.String && IsMedia(fromContent.GetString())) return fromContent.GetString()!;
        }
        if (content is { } node2 && node2.TryGetProperty("mp4SwitchUrl", out var switches) && FirstArrayString(switches) is { Length: > 0 } first && IsMedia(first)) return first;
        foreach (var key in new[] { "mp4Url", "playUrl", "videoUrl", "url" })
        {
            if (value.TryGetProperty(key, out var fromNode) && fromNode.ValueKind == JsonValueKind.String && IsMedia(fromNode.GetString())) return fromNode.GetString()!;
        }
        if (value.TryGetProperty("mp4SwitchUrl", out var array) && FirstArrayString(array) is { Length: > 0 } second && IsMedia(second)) return second;
        return "";
    }

    private static string FirstMediaUrl(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in node.EnumerateArray())
            {
                var found = FirstMediaUrl(entry);
                if (found.Length > 0) return found;
            }
            return "";
        }
        if (node.ValueKind == JsonValueKind.String)
        {
            var text = node.GetString() ?? "";
            if (IsMedia(text)) return text;
            return text.StartsWith('{') || text.StartsWith('[') ? FirstMediaUrl(Parse(text)) : "";
        }
        if (node.ValueKind != JsonValueKind.Object) return "";
        foreach (var property in node.EnumerateObject())
        {
            var found = FirstMediaUrl(property.Value);
            if (found.Length > 0) return found;
        }
        return "";
    }

    private static JsonElement Parse(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private static string? FirstArrayString(JsonElement array)
    {
        if (array.ValueKind != JsonValueKind.Array) return null;
        foreach (var entry in array.EnumerateArray())
            if (entry.ValueKind == JsonValueKind.String && entry.GetString() is { Length: > 0 } text) return text;
        return null;
    }

    private static bool IsMedia(string? value) =>
        value is { Length: > 0 } && value.StartsWith("http", StringComparison.OrdinalIgnoreCase) &&
        (value.Contains(".m3u8", StringComparison.OrdinalIgnoreCase) || value.Contains(".mp4", StringComparison.OrdinalIgnoreCase)) &&
        !value.Contains(".jpg", StringComparison.OrdinalIgnoreCase) && !value.Contains(".jpeg", StringComparison.OrdinalIgnoreCase) &&
        !value.Contains(".png", StringComparison.OrdinalIgnoreCase) && !value.Contains(".webp", StringComparison.OrdinalIgnoreCase);

    private static string String(JsonElement node, string[] names)
    {
        foreach (var name in names)
        {
            if (node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text && text != "null") return text;
        }
        return "";
    }

    private static string String(JsonElement node, string name) => String(node, [name]);

    private static string HttpString(JsonElement node, string[] names)
    {
        foreach (var name in names)
        {
            if (node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text && text.StartsWith("http", StringComparison.Ordinal)) return text;
        }
        return "";
    }

    private static int Int(JsonElement node, string name) =>
        node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : 0;

    private static string Quote(string value)
    {
        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (c < 0x20) builder.Append("\\u").Append(((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
                    else builder.Append(c);
                    break;
            }
        }
        builder.Append('"');
        return builder.ToString();
    }
}
