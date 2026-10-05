using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using VodBox.Core;

namespace VodBox.Infrastructure;

/// <summary>Independent adapter for the public MacCMS collection API; not a TV config adapter.</summary>
public sealed class MacCmsProvider(SourceDefinition source, HttpClient http, bool xml) : IContentProvider
{
    private readonly ConcurrentDictionary<string, Entry> _details = new(StringComparer.Ordinal);
    private bool _disposed;
    public string SourceId => source.Id;
    private sealed record Entry(MediaDetail Detail, Dictionary<string, string> Urls);
    private sealed record Response(IReadOnlyList<Category> Categories, IReadOnlyList<Entry> Entries, int Page, int PageCount);

    public async Task<IReadOnlyList<Category>> GetCategoriesAsync(CancellationToken token) => (await FetchAsync("list", null, null, null, null, token)).Categories;
    public Task<MediaPage> GetItemsAsync(string? categoryId, string? cursor, CancellationToken token) => GetItemsFilteredAsync(categoryId, cursor, new Dictionary<string, string>(), token);
    public async Task<MediaPage> GetItemsFilteredAsync(string? categoryId, string? cursor, IReadOnlyDictionary<string, string> filters, CancellationToken token)
    {
        int page = cursor is null ? 1 : int.TryParse(cursor, out int value) && value > 0 ? value : throw new InvalidDataException("无效分页游标。");
        return Page(await FetchAsync("detail", categoryId, page, null, null, token, filters));
    }
    public Task<MediaPage> SearchAsync(string query, CancellationToken token) => SearchPageAsync(query, null, token);
    public async Task<MediaPage> SearchPageAsync(string query, string? cursor, CancellationToken token)
    {
        int page = cursor is null ? 1 : int.TryParse(cursor, out int value) && value > 0 ? value : throw new InvalidDataException("无效搜索分页游标。");
        return Page(await FetchAsync("detail", null, page, query, null, token));
    }
    private static MediaPage Page(Response response) => new(response.Entries.Select(x => x.Detail.Item).ToList(),
        response.Page < response.PageCount ? (response.Page + 1).ToString(CultureInfo.InvariantCulture) : null);
    public async Task<MediaDetail> GetDetailAsync(string mediaId, CancellationToken token) => (await DetailAsync(mediaId, token)).Detail;
    private async Task<Entry> DetailAsync(string mediaId, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_details.TryGetValue(mediaId, out var cached)) return cached;
        var result = (await FetchAsync("detail", null, null, null, mediaId, token)).Entries.FirstOrDefault(x => x.Detail.Item.Id == mediaId)
            ?? throw new InvalidDataException($"内容源没有返回媒体 {mediaId}。");
        if (_details.Count >= 128) _details.Clear();
        _details[mediaId] = result;
        return result;
    }
    public async Task<PlaybackRequest> ResolvePlaybackAsync(string mediaId, string episodeId, CancellationToken token)
    {
        var entry = await DetailAsync(mediaId, token);
        if (!entry.Urls.TryGetValue(episodeId, out string? url)) throw new InvalidDataException("该集或线路不存在。");
        var episode = entry.Detail.PlaybackLines.SelectMany(x => x.Episodes).First(x => x.Id == episodeId);
        return new() { Uri = new Uri(new Uri(source.Entry!), url).AbsoluteUri, Title = $"{entry.Detail.Item.Title} · {episode.Title}",
            SourceId = SourceId, MediaId = mediaId, EpisodeId = episodeId, Headers = Headers() };
    }
    private Dictionary<string, string> Headers()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (source.Options.TryGetValue("headers", out var headers) && headers.ValueKind == JsonValueKind.Object)
            foreach (var header in headers.EnumerateObject()) result[header.Name] = header.Value.GetString() ?? "";
        return result;
    }
    private async Task<Response> FetchAsync(string action, string? category, int? page, string? query, string? ids, CancellationToken token, IReadOnlyDictionary<string, string>? filters = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Uri.TryCreate(source.Entry, UriKind.Absolute, out var endpoint) || endpoint.Scheme is not ("http" or "https"))
            throw new InvalidDataException("采集 API entry 必须是 HTTP/HTTPS 地址。");
        var parameters = new List<KeyValuePair<string, string>> { new("ac", action), new("at", xml ? "xml" : "json") };
        if (category is not null) parameters.Add(new("t", category));
        if (page is not null) parameters.Add(new("pg", page.Value.ToString(CultureInfo.InvariantCulture)));
        if (query is not null) parameters.Add(new("wd", query));
        if (ids is not null) parameters.Add(new("ids", ids));
        if (filters is not null)
            foreach (var filter in filters)
            {
                if (filter.Key is not ("year" or "isend" or "h" or "from")) throw new NotSupportedException("采集 API 不支持筛选字段：" + filter.Key);
                if (!string.IsNullOrEmpty(filter.Value)) parameters.Add(new(filter.Key, filter.Value));
            }
        // Preserve API-specific query parameters without retaining previous paging/action values.
        var replaced = parameters.Select(x => x.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var existing = endpoint.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(x => !replaced.Contains(Uri.UnescapeDataString(x.Split('=')[0])));
        var builder = new UriBuilder(endpoint) { Query = string.Join('&', existing.Concat(parameters.Select(x => Uri.EscapeDataString(x.Key) + "=" + Uri.EscapeDataString(x.Value)))) };
        using var request = new HttpRequestMessage(HttpMethod.Get, builder.Uri);
        foreach (var header in Headers()) request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        var bytes = await BoundedContent.ReadAsync(stream, 8 * 1024 * 1024, token);
        return await Task.Run(() => xml ? ReadXml(bytes) : ReadJson(bytes), token);
    }
    private Response ReadJson(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes); var root = document.RootElement;
        if (root.TryGetProperty("code", out var code) && Text(code) is not ("1" or "200"))
            throw new InvalidDataException(root.TryGetProperty("msg", out var message) ? Text(message) : "采集接口返回错误。");
        var categories = root.TryGetProperty("class", out var classes) && classes.ValueKind == JsonValueKind.Array
            ? classes.EnumerateArray().Select(x => new Category(Field(x, "type_id"), Field(x, "type_name"))).ToList() : [];
        var entries = new List<Entry>();
        if (root.TryGetProperty("list", out var items) && items.ValueKind == JsonValueKind.Array)
            foreach (var item in items.EnumerateArray())
                entries.Add(Build(Field(item, "vod_id"), Field(item, "vod_name"), Field(item, "vod_pic"), Field(item, "vod_remarks"), Field(item, "vod_content"),
                    Field(item, "vod_play_from").Split(Field(item, "vod_play_from").Contains("$$$", StringComparison.Ordinal) ? "$$$" : ",", StringSplitOptions.None), Field(item, "vod_play_url").Split("$$$", StringSplitOptions.None)));
        return new(categories, entries, Number(root, "page", 1), Number(root, "pagecount", 1));
    }
    private Response ReadXml(byte[] bytes)
    {
        TextEncoding.EnsureRegistered();
        using var stream = new MemoryStream(bytes);
        using var reader = XmlReader.Create(stream, new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 8 * 1024 * 1024 });
        var root = XDocument.Load(reader).Root ?? throw new InvalidDataException("XML 响应为空。");
        var categories = root.Element("class")?.Elements("ty").Select(x => new Category((string?)x.Attribute("id") ?? "", x.Value)).ToList() ?? [];
        var list = root.Element("list");
        var entries = list?.Elements("video").Select(x => Build(Value(x, "id"), Value(x, "name"), Value(x, "pic"), Value(x, "note"), Value(x, "des"),
            x.Element("dl")?.Elements("dd").Select(d => (string?)d.Attribute("flag") ?? "主线路").ToArray() ?? [],
            x.Element("dl")?.Elements("dd").Select(d => d.Value).ToArray() ?? [])).ToList() ?? [];
        return new(categories, entries, ParseNumber((string?)list?.Attribute("page"), 1), ParseNumber((string?)list?.Attribute("pagecount"), 1));
    }
    private Entry Build(string id, string title, string poster, string remarks, string description, string[] names, string[] playlists)
    {
        var urls = new Dictionary<string, string>(StringComparer.Ordinal); var lines = new List<PlaybackLine>();
        for (int line = 0; line < playlists.Length; line++)
        {
            var episodes = new List<Episode>();
            var segments = playlists[line].Split('#', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            for (int index = 0; index < segments.Length; index++)
            {
                int separator = segments[index].IndexOf('$');
                string url = separator < 0 ? segments[index] : segments[index][(separator + 1)..];
                if (url.Length == 0) continue;
                string episodeId = $"{line}:{index}";
                episodes.Add(new(episodeId, separator < 0 ? $"第 {index + 1} 集" : segments[index][..separator])); urls[episodeId] = url;
            }
            if (episodes.Count > 0) lines.Add(new(line.ToString(CultureInfo.InvariantCulture), line < names.Length && names[line].Length > 0 ? names[line] : $"线路 {line + 1}", episodes));
        }
        string? image = Uri.TryCreate(new Uri(source.Entry!), poster, out var uri) && poster.Length > 0 ? uri.AbsoluteUri : null;
        return new(new(new(id, title, image, remarks), WebUtility.HtmlDecode(description), lines), urls);
    }
    private static string Text(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ToString();
    private static string Field(JsonElement element, string name) => element.TryGetProperty(name, out var value) ? Text(value) : "";
    private static int Number(JsonElement element, string name, int fallback) => ParseNumber(Field(element, name), fallback);
    private static int ParseNumber(string? value, int fallback) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number) ? number : fallback;
    private static string Value(XElement element, string name) => element.Element(name)?.Value ?? "";
    public ValueTask DisposeAsync() { _disposed = true; _details.Clear(); return ValueTask.CompletedTask; }
}

internal static class BoundedContent
{
    public static async Task<byte[]> ReadAsync(Stream stream, int maximum, CancellationToken token)
    {
        using var result = new MemoryStream(); var buffer = new byte[16384]; int read;
        while ((read = await stream.ReadAsync(buffer, token)) > 0)
        {
            if (result.Length + read > maximum) throw new InvalidDataException($"响应超过 {maximum / 1024 / 1024} MiB 限制。");
            result.Write(buffer, 0, read);
        }
        return result.ToArray();
    }
}
