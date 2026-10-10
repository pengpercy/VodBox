using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using VodBox.Core;

namespace VodBox.Infrastructure;

/// <summary>Explicit xsmp3/psmp3 Z-Blog/APlayer profile, not a general XBPQ rule engine.</summary>
public sealed partial class AudioSiteSource : IResolvingContentSource, IDisposable
{
    private static readonly HttpClient DefaultHttp = new(new SocketsHttpHandler
    { AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.All });
    private readonly HttpClient _http;
    private readonly Uri _base;
    private readonly IReadOnlyList<Category> _categories;
    private readonly SemaphoreSlim _requests = new(4, 4);
    private readonly ConcurrentDictionary<string, Cached> _details = new(StringComparer.Ordinal);
    private bool _disposed;
    private const string UserAgent = "Mozilla/5.0";
    public string Key { get; }
    public string Name { get; }
    private sealed record Track(string Name, string Url);
    private sealed record Cached(MediaDetail Detail, IReadOnlyList<Track> Tracks, DateTimeOffset Expires);

    public AudioSiteSource(SourceInfo source) : this(source, DefaultHttp) { }
    public AudioSiteSource(SourceInfo source, HttpClient http)
    {
        using var rule = JsonDocument.Parse(source.Ext ?? "{}");
        if (rule.RootElement.ValueKind != JsonValueKind.Object || !rule.RootElement.TryGetProperty("主页url", out var home))
            throw new NotSupportedException("XBPQ 当前仅支持已核对的 xsmp3/psmp3 音频规则，需要主页url。");
        var address = home.GetString();
        if (!Uri.TryCreate(address, UriKind.Absolute, out var entry) || entry.Scheme != "https" || entry.Port != 443 ||
            entry.Host is not ("www.xsmp3.com" or "www.psmp3.com") || entry.AbsolutePath != "/" || entry.Query.Length != 0 || entry.Fragment.Length != 0 || entry.UserInfo.Length != 0)
            throw new InvalidDataException("音频站点 entry 只能是已核对的 xsmp3/psmp3 HTTPS 首页。");
        _base = entry; _http = http; Key = source.Key; Name = source.Name;
        _categories = entry.Host == "www.xsmp3.com"
            ? [new("gdg", "郭德纲"), new("dys", "德云社"), new("xsxsl", "新势力"), new("qqs", "青区社"), new("msl", "马三立"), new("xsmj", "更多")]
            : [new("ykc", "袁阔成"), new("stf", "单田芳"), new("tly", "田连元"), new("llf", "刘兰芳"), new("llr", "连丽如"), new("zsz", "张少佐"), new("tzy", "田战义")];
    }
    public Task<IReadOnlyList<Category>> GetCategoriesAsync(CancellationToken token = default)
    { Check(token); return Task.FromResult(_categories); }

    public Task<MediaPage> GetHomeAsync(CancellationToken ct = default) => GetItemsAsync(_categories[0].Id, 1, null, ct);
    public async Task<MediaPage> GetItemsAsync(string categoryId, int page, IReadOnlyDictionary<string, string>? filters, CancellationToken token = default)
    {
        Check(token); string category = categoryId ?? _categories[0].Id;
        if (_categories.All(x => x.Id != category)) throw new InvalidDataException("音频分类不存在。");
        if (page is < 1 or > 1000) throw new InvalidDataException("音频分页编号无效。");
        string html = await ReadAsync(new(_base, $"{category}/{page}.html"), token).ConfigureAwait(false);
        var items = Cards(html);
        string expected = new Uri(_base, $"{category}/{page + 1}.html").AbsoluteUri;
        bool more = items.Count > 0 && page < 1000 && Anchors().Matches(html).Cast<Match>()
            .Any(x => Attribute(x.Groups["attrs"].Value, "class").Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("next") &&
                Uri.TryCreate(_base, Attribute(x.Groups["attrs"].Value, "href"), out var next) && next.AbsoluteUri == expected);
        return new(items, page, more ? page + 1 : page);
    }
    public Task<MediaPage> SearchAsync(string query, int page, CancellationToken token = default)
    { Check(token); throw new NotSupportedException("音频站点公开搜索当前跳回首页，暂不支持搜索。"); }
    public async Task<MediaDetail> GetDetailAsync(string mediaId, CancellationToken token = default) => (await DetailAsync(mediaId, false, token).ConfigureAwait(false)).Detail;
    private async Task<Cached> DetailAsync(string mediaId, bool refresh, CancellationToken token)
    {
        Check(token); Uri uri = Album(mediaId);
        if (!refresh && _details.TryGetValue(mediaId, out var cached) && cached.Expires > DateTimeOffset.UtcNow) return cached;
        string html = await ReadAsync(uri, token).ConfigureAwait(false);
        string title = Meta(html, "og:title"), poster = Meta(html, "og:image"), description = Meta(html, "description");
        if (title.Length == 0 && Heading().Match(html) is { Success: true } heading)
            title = WebUtility.HtmlDecode(Tags().Replace(heading.Groups["body"].Value, "")).Trim();
        if (title.Length == 0) throw new InvalidDataException("音频专辑缺少标题。");
        var tracks = ParseTracks(html, uri);
        var detail = new MediaDetail
        {
            Item = new MediaItem { Id = mediaId, Title = title, Poster = MediaUri(poster, uri)?.AbsoluteUri },
            Description = description,
            Lines = [new("audio", _base.Host == "www.xsmp3.com" ? "相声随身听" : "评书随身听", tracks.Select((x, i) => new Episode(i.ToString(CultureInfo.InvariantCulture), x.Name)).ToArray())]
        };
        var result = new Cached(detail, tracks, DateTimeOffset.UtcNow.AddMinutes(10));
        if (_details.Count >= 128) _details.Clear(); _details[mediaId] = result; return result;
    }
    public async Task<PlaybackRequest> ResolvePlaybackAsync(string mediaId, string episodeId, CancellationToken token = default)
    {
        Check(token);
        if (!int.TryParse(episodeId, NumberStyles.None, CultureInfo.InvariantCulture, out int index) || index is < 0 or >= 10000)
            throw new InvalidDataException("音频分集编号无效。");
        var detail = await DetailAsync(mediaId, true, token).ConfigureAwait(false);
        if (index >= detail.Tracks.Count) throw new InvalidDataException("音频分集不存在。");
        var track = detail.Tracks[index];
        return new() { Uri = track.Url, Title = $"{detail.Detail.Item.Title} · {track.Name}", SourceKey = Key, SourceName = Name, LineId = "audio", MediaId = mediaId, EpisodeId = episodeId,
            Headers = new() { ["User-Agent"] = UserAgent, ["Referer"] = Album(mediaId).AbsoluteUri } };
    }
    private IReadOnlyList<MediaItem> Cards(string html)
    {
        var list = ListBox().Match(html); if (!list.Success) throw new InvalidDataException("音频页面缺少专辑列表，不能当作空列表成功。");
        var items = new List<MediaItem>(); var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match post in Posts().Matches(list.Groups["body"].Value))
        {
            if (items.Count >= 1000) throw new InvalidDataException("音频列表过长。");
            var anchor = Anchors().Match(post.Groups["body"].Value); if (!anchor.Success) throw new InvalidDataException("音频专辑缺少链接。");
            var uri = SameOrigin(Attribute(anchor.Groups["attrs"].Value, "href")); string id = uri.AbsolutePath.TrimStart('/'); Album(id);
            string title = Attribute(anchor.Groups["attrs"].Value, "title"); if (title.Length == 0) throw new InvalidDataException("音频专辑缺少标题。");
            var image = Images().Match(post.Groups["body"].Value); string picture = image.Success ? Attribute(image.Groups["attrs"].Value, "src") : "";
            if (ids.Add(id)) items.Add(new MediaItem { Id = id, Title = title, Poster = MediaUri(picture, _base)?.AbsoluteUri });
        }
        return items;
    }
    private async Task<string> ReadAsync(Uri uri, CancellationToken token)
    {
        Check(token); using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(20));
        await _requests.WaitAsync(deadline.Token).ConfigureAwait(false);
        try
        {
            Check(deadline.Token); using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (response.RequestMessage?.RequestUri is { } final && final != uri) throw new InvalidDataException("音频页面不允许隐式重定向。");
            return await HtmlAsync(response, uri, deadline.Token).ConfigureAwait(false);
        }
        finally { _requests.Release(); }
    }
    private static async Task<string> HtmlAsync(HttpResponseMessage response, Uri expected, CancellationToken token)
    {
        if ((int)response.StatusCode is >= 300 and < 400 || response.RequestMessage?.RequestUri is { } final && final != expected)
            throw new InvalidDataException("音频页面不允许重定向。");
        response.EnsureSuccessStatusCode(); await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        return new UTF8Encoding(false, true).GetString(await BoundedContent.ReadAsync(stream, 2 * 1024 * 1024, token).ConfigureAwait(false));
    }
    private Uri Album(string id)
    { if (!AlbumPath().IsMatch(id)) throw new InvalidDataException("音频专辑编号必须是站内专辑路径。"); return new(_base, id); }
    private Uri SameOrigin(string url)
    {
        if (!Uri.TryCreate(_base, url, out var uri) || uri.Scheme != _base.Scheme || uri.Host != _base.Host || uri.Port != _base.Port || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidDataException("音频页面只能从原站读取。");
        return uri;
    }
    private static Uri? MediaUri(string url, Uri page) => url.Length is > 0 and <= 16384 && Uri.TryCreate(page, url, out var uri) && uri.Scheme is "https" or "http" && uri.UserInfo.Length == 0 ? uri : null;
    private static string Attribute(string text, string key) => Attributes().Matches(text).Cast<Match>().FirstOrDefault(x => x.Groups["key"].Value.Equals(key, StringComparison.OrdinalIgnoreCase)) is { } match
        ? WebUtility.HtmlDecode(match.Groups["value"].Value) : "";
    private static string Meta(string html, string key) => Metas().Matches(html).Cast<Match>().FirstOrDefault(x => Attribute(x.Groups["attrs"].Value, "property") == key || Attribute(x.Groups["attrs"].Value, "name") == key) is { } meta
        ? Attribute(meta.Groups["attrs"].Value, "content") : "";
    private void Check(CancellationToken token) { ObjectDisposedException.ThrowIf(_disposed, this); token.ThrowIfCancellationRequested(); }
    public void Dispose() { _disposed = true; _details.Clear(); }

    // Restricted data grammar: arrays of objects with double-quoted string values. No JS evaluation.
    private static IReadOnlyList<Track> ParseTracks(string html, Uri page)
    {
        var script = PlayerScript().Match(html); if (!script.Success) throw new InvalidDataException("音频专辑缺少 APlayer 数据。");
        var start = AudioStart().Match(script.Groups["body"].Value); if (!start.Success) throw new InvalidDataException("音频专辑缺少 audio 数组。");
        var reader = new TrackReader(script.Groups["body"].Value.AsSpan(start.Index + start.Length - 1));
        var tracks = new List<Track>(); reader.Expect('[');
        while (!reader.Take(']'))
        {
            if (tracks.Count >= 10000) throw new InvalidDataException("音频分集过多。");
            reader.Expect('{'); string name = "", url = ""; var keys = new HashSet<string>(StringComparer.Ordinal);
            while (!reader.Take('}'))
            {
                string key = reader.Key(); if (!keys.Add(key)) throw new InvalidDataException("音频数据字段重复。");
                reader.Expect(':'); string value = reader.String();
                if (key == "name") name = value; else if (key == "url") url = value;
                if (!reader.Take(',')) { reader.Expect('}'); break; }
            }
            var uri = MediaUri(url, page); if (string.IsNullOrWhiteSpace(name) || uri is null) throw new InvalidDataException("音频分集缺少标题或有效媒体地址。");
            tracks.Add(new(name, uri.AbsoluteUri));
            if (!reader.Take(',')) { reader.Expect(']'); break; }
        }
        if (tracks.Count == 0) throw new InvalidDataException("音频专辑没有分集。");
        reader.Expect('}'); reader.Expect(')'); return tracks;
    }
    private ref struct TrackReader(ReadOnlySpan<char> text)
    {
        private ReadOnlySpan<char> _text = text;
        private int _position;
        private void White() { while (_position < _text.Length && char.IsWhiteSpace(_text[_position])) _position++; }
        public bool Take(char value) { White(); if (_position < _text.Length && _text[_position] == value) { _position++; return true; } return false; }
        public void Expect(char value) { if (!Take(value)) throw new InvalidDataException("音频数据不是受支持的静态数组。"); }
        public string Key()
        {
            White(); if (_position < _text.Length && _text[_position] == '"') return String();
            int start = _position; while (_position < _text.Length && char.IsAsciiLetter(_text[_position])) _position++;
            if (_position == start || _position - start > 64) throw new InvalidDataException("音频数据字段无效。");
            return _text[start.._position].ToString();
        }
        public string String()
        {
            White(); int start = _position; Expect('"');
            while (_position < _text.Length)
            {
                char value = _text[_position++];
                if (value == '\\') { if (_position >= _text.Length) break; _position++; }
                else if (value == '"')
                {
                    if (_position - start > 16384) throw new InvalidDataException("音频数据字符串过长。");
                    using var parsed = JsonDocument.Parse(_text[start.._position].ToString());
                    return parsed.RootElement.GetString() ?? "";
                }
            }
            throw new InvalidDataException("音频数据字符串未结束。");
        }
    }
    [GeneratedRegex("\\A[a-z0-9-]+/[a-z0-9-]+\\.html\\z", RegexOptions.CultureInvariant, 1000)] private static partial Regex AlbumPath();
    [GeneratedRegex("<ul\\b[^>]*id\\s*=\\s*[\"']post_list_box[\"'][^>]*>(?<body>.*?)</ul\\s*>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)] private static partial Regex ListBox();
    [GeneratedRegex("<li\\b[^>]*class\\s*=\\s*[\"'][^\"']*\\bpost_list_li\\b[^\"']*[\"'][^>]*>(?<body>.*?)</li\\s*>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)] private static partial Regex Posts();
    [GeneratedRegex("<a\\b(?<attrs>[^>]*)>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)] private static partial Regex Anchors();
    [GeneratedRegex("<img\\b(?<attrs>[^>]*)>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)] private static partial Regex Images();
    [GeneratedRegex("<meta\\b(?<attrs>[^>]*)>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)] private static partial Regex Metas();
    [GeneratedRegex("(?<key>[a-zA-Z-]+)\\s*=\\s*(?:\"(?<value>[^\"]*)\"|'(?<value>[^']*)')", RegexOptions.CultureInvariant, 1000)] private static partial Regex Attributes();
    [GeneratedRegex("<script\\b[^>]*>\\s*(?:const|let|var)\\s+\\w+\\s*=\\s*new\\s+APlayer\\s*\\(\\s*\\{(?<body>.*?)</script\\s*>", RegexOptions.Singleline | RegexOptions.CultureInvariant, 1000)] private static partial Regex PlayerScript();
    [GeneratedRegex("\\baudio\\s*:\\s*\\[", RegexOptions.CultureInvariant, 1000)] private static partial Regex AudioStart();
    [GeneratedRegex("<h1\\b[^>]*>(?<body>.*?)</h1\\s*>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)] private static partial Regex Heading();
    [GeneratedRegex("<[^>]*>", RegexOptions.CultureInvariant, 1000)] private static partial Regex Tags();
}
