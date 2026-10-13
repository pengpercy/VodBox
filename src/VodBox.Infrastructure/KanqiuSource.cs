using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using VodBox.Core;

namespace VodBox.Infrastructure;

/// <summary>
/// 88看球原生爬虫：赛程来自首页/联赛页 HTML（按日期分组）；线路来自 <c>/live/{id}/source</c>，
/// 其 <c>data</c> 为去掉前 6 位与末 2 位干扰字符后的 Base64(UTF-8 JSON)。线路地址是 88player 的
/// 包装页，真实流在其 <c>url</c> 查询参数里。
/// </summary>
public sealed partial class KanqiuSource : IResolvingContentSource
{
    private const string DefaultHost = "https://www.88kanqiu.one";
    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36";
    private readonly SourceInfo _site;
    private readonly string _host;
    private readonly Func<string, string?, CancellationToken, Task<string>> _get;
    private IReadOnlyList<Category>? _categories;
    public string Key => _site.Key;
    public string Name => _site.Name;

    public KanqiuSource(SourceInfo site) : this(site, null) { }
    internal KanqiuSource(SourceInfo site, Func<string, string?, CancellationToken, Task<string>>? get)
    {
        _site = site;
        _host = Uri.TryCreate(site.Ext, UriKind.Absolute, out var ext) && ext.Scheme is "http" or "https" ? ext.GetLeftPart(UriPartial.Authority) : DefaultHost;
        _get = get ?? Get;
    }

    private static async Task<string> Get(string url, string? referer, CancellationToken ct)
    {
        using var http = new DefaultHttp();
        var headers = new Dictionary<string, string> { ["User-Agent"] = UserAgent };
        if (referer is not null) headers["Referer"] = referer;
        return DefaultHttp.Decode(await http.GetBoundedAsync(url, 4 * 1024 * 1024, ct, headers));
    }

    private async Task<string> Request(string path, CancellationToken ct, string? referer = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        return await _get(_host + path, referer, timeout.Token);
    }

    public async Task<IReadOnlyList<Category>> GetCategoriesAsync(CancellationToken ct = default)
    {
        if (_categories is { } cached) return cached;
        var leagues = League().Matches(await Request("/", ct))
            .Select(match => new Category(match.Groups[1].Value, Text(match.Groups[2].Value)))
            .Where(category => category.Name.Length > 0).DistinctBy(category => category.Id);
        return _categories = [new Category("0", "全部直播"), .. leagues];
    }

    public Task<MediaPage> GetHomeAsync(CancellationToken ct = default) => GetItemsAsync("0", 1, null, ct);

    public async Task<MediaPage> GetItemsAsync(string categoryId, int page, IReadOnlyDictionary<string, string>? filters, CancellationToken ct = default)
    {
        if (page < 1 || categoryId.Length is 0 or > 6 || !categoryId.All(char.IsAsciiDigit)) throw new InvalidDataException("88看球分类或页码无效。");
        if (page > 1) return new MediaPage([], page, page);
        var items = ParseSchedule(await Request(categoryId == "0" ? "/" : $"/match/{categoryId}/live", ct));
        return new MediaPage(items, 1, 1);
    }

    /// <summary>按出现顺序读取日期分组与比赛条目；日期写进备注，便于区分跨天赛程。</summary>
    internal static IReadOnlyList<MediaItem> ParseSchedule(string html)
    {
        var items = new List<MediaItem>();
        var date = "";
        foreach (Match match in ScheduleEntry().Matches(html))
        {
            if (match.Groups["date"].Success) { date = Text(match.Groups["date"].Value); continue; }
            var game = match.Groups["game"].Value;
            var id = GameId().Match(game).Groups[1].Value;
            var teams = TeamName().Matches(game).Select(team => WebUtility.HtmlDecode(team.Groups[1].Value).Trim()).ToArray();
            if (id.Length == 0 || teams.Length == 0) continue;
            var status = Text(StatusButton().Match(game).Groups[1].Value);
            var remarks = string.Join(" ", new[] { date, Text(GameTime().Match(game).Groups[1].Value), Text(GameType().Match(game).Groups[1].Value), status == "暂无" ? "" : status }.Where(part => part.Length > 0));
            items.Add(new MediaItem { Id = id, Title = string.Join(" VS ", teams), Remarks = remarks });
        }
        return items.DistinctBy(item => item.Id).ToArray();
    }

    public Task<MediaPage> SearchAsync(string query, int page, CancellationToken ct = default) => throw new NotSupportedException("88看球没有搜索入口。");

    public async Task<MediaDetail> GetDetailAsync(string mediaId, CancellationToken ct = default)
    {
        if (mediaId.Length is 0 or > 12 || !mediaId.All(char.IsAsciiDigit)) throw new InvalidDataException("88看球比赛编号无效。");
        var play = $"/live/{mediaId}/play";
        var page = await Request(play, ct);
        var title = Text(Title().Match(page).Groups[1].Value).Split('_')[0].Trim();
        var source = await Request($"/live/{mediaId}/source", ct, _host + play);
        var episodes = ParseLines(source);
        if (episodes.Count == 0) throw new InvalidDataException("比赛尚未开始或暂无直播线路。");
        return new MediaDetail
        {
            Item = new MediaItem { Id = mediaId, Title = title.Length > 0 ? title : Name },
            Lines = [new PlaybackLine("88看球", "直播线路", episodes)],
        };
    }

    /// <summary>解开 <c>/source</c> 的干扰 Base64，取出各线路的真实流地址（包装页取其 url 参数）。</summary>
    internal static IReadOnlyList<Episode> ParseLines(string json)
    {
        using var envelope = JsonDocument.Parse(json);
        if (!envelope.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.String) throw new InvalidDataException("88看球线路接口格式无效。");
        var wrapped = data.GetString() ?? "";
        if (wrapped.Length < 9) throw new InvalidDataException("88看球线路数据过短。");
        using var payload = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(wrapped[6..^2])));
        if (!payload.RootElement.TryGetProperty("links", out var links) || links.ValueKind != JsonValueKind.Array) return [];
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        return links.EnumerateArray().Select((link, index) =>
        {
            var url = AppRjSource.Text(link, "url");
            if (Uri.TryCreate(url, UriKind.Absolute, out var wrapper) && System.Web.HttpUtility.ParseQueryString(wrapper.Query)["url"] is { Length: > 0 } inner) url = inner;
            var name = AppRjSource.Text(link, "name");
            if (name.Length == 0) name = "线路" + (index + 1);
            // 同名线路是不同的流，追加序号以便区分。
            seen[name] = seen.GetValueOrDefault(name) + 1;
            if (seen[name] > 1) name += " (" + seen[name] + ")";
            return new Episode(index.ToString(System.Globalization.CultureInfo.InvariantCulture), name, url);
        }).Where(episode => Uri.TryCreate(episode.Uri, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https").ToArray();
    }

    public async Task<PlaybackRequest> ResolvePlaybackAsync(string mediaId, string episodeId, CancellationToken ct = default)
    {
        var detail = await GetDetailAsync(mediaId, ct);
        var episodes = detail.Lines[0].Episodes;
        var chosen = episodes.SingleOrDefault(item => item.Id == episodeId) ?? throw new InvalidDataException("88看球线路已失效，请刷新。");
        var choices = episodes.OrderBy(item => item.Id == chosen.Id ? 0 : 1).Select(item => new PlaybackChoice(item.Title, item.Uri!)).ToArray();
        return new PlaybackRequest
        {
            Uri = chosen.Uri!, Choices = choices.Length > 1 ? choices : [], Title = detail.Item.Title,
            SourceKey = Key, SourceName = Name, MediaId = mediaId, LineId = detail.Lines[0].Id, EpisodeId = episodeId, IsLive = true,
        };
    }

    private static string Text(string html) => WebUtility.HtmlDecode(Tag().Replace(html, "")).Trim();

    [GeneratedRegex("href='/match/(\\d+)/live'>\\s*<img[^>]*>([^<]+)</a>")] private static partial Regex League();
    [GeneratedRegex("<li class=\"list-group-item live-list-group-time\">(?<date>.*?)</li>|<li class=\"list-group-item group-game-item\">(?<game>.*?)</li>", RegexOptions.Singleline)] private static partial Regex ScheduleEntry();
    [GeneratedRegex("data-id=\"(\\d+)\"")] private static partial Regex GameId();
    [GeneratedRegex("<span class=\"team-name\"\\s+title=\"([^\"]*)\"")] private static partial Regex TeamName();
    [GeneratedRegex("category-game-time\">([^<]*)<")] private static partial Regex GameTime();
    [GeneratedRegex("<span class=\"game-type\">([^<]*)<")] private static partial Regex GameType();
    [GeneratedRegex("role=\"button\">(.*?)</a>", RegexOptions.Singleline)] private static partial Regex StatusButton();
    [GeneratedRegex("<title>([^<]*)</title>")] private static partial Regex Title();
    [GeneratedRegex("<[^>]+>")] private static partial Regex Tag();
}
