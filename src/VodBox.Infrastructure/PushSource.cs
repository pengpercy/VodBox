using System.Globalization;
using VodBox.Core;

namespace VodBox.Infrastructure;

/// <summary>csp_Push 的有限桌面适配：HTTP(S) 单地址或标题$地址列表；无网页嗅探、磁力或文件访问。</summary>
public sealed class PushSource(SourceInfo site) : IResolvingContentSource
{
    public string Key => site.Key;
    public string Name => site.Name;
    public Task<IReadOnlyList<Category>> GetCategoriesAsync(CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); return Task.FromResult<IReadOnlyList<Category>>([]); }
    public Task<MediaPage> GetHomeAsync(CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); return Task.FromResult(new MediaPage([], 1, 1)); }
    public Task<MediaPage> GetItemsAsync(string categoryId, int page, IReadOnlyDictionary<string, string>? filters, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); throw new NotSupportedException("推送入口没有分类列表，请使用搜索输入 HTTP(S) 地址。"); }
    public Task<MediaPage> SearchAsync(string query, int page, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (page != 1) return Task.FromResult(new MediaPage([], page, page));
        var detail = Parse(query);
        return Task.FromResult(new MediaPage([detail.Item], 1, 1));
    }
    public Task<MediaDetail> GetDetailAsync(string mediaId, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); return Task.FromResult(Parse(mediaId)); }
    public Task<PlaybackRequest> ResolvePlaybackAsync(string mediaId, string episodeId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var detail = Parse(mediaId);
        foreach (var line in detail.Lines)
        foreach (var episode in line.Episodes)
            if (episode.Id == episodeId)
                return Task.FromResult(new PlaybackRequest
                {
                    Uri = episode.Uri!, SourceKey = Key, SourceName = Name, MediaId = mediaId,
                    LineId = line.Id, EpisodeId = episode.Id, Title = episode.Title,
                    Resolution = line.Id == "parse" ? ResolutionKind.Json : ResolutionKind.Direct,
                    ParseEndpoint = site.PlayUrl
                });
        throw new InvalidDataException("推送选集不存在。");
    }
    private static MediaDetail Parse(string input)
    {
        if (string.IsNullOrWhiteSpace(input) || input.Length > 65536)
            throw new InvalidDataException("推送地址为空或过长。");
        var records = input.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (records.Length > 100) throw new InvalidDataException("推送列表最多100项。");
        var entries = new List<(string Title, string Url)>();
        foreach (var record in records)
        {
            var separator = record.IndexOf('$');
            // 单个 URL 的查询参数也可能含 $，不能误当标题分隔。
            var titled = separator > 0 && !record[..separator].Contains("://", StringComparison.Ordinal);
            var url = (titled ? record[(separator + 1)..] : record).Trim().Replace("***", "#", StringComparison.Ordinal);
            if (!Uri.TryCreate(url, UriKind.Absolute, out var address) || address.Scheme is not ("http" or "https") || address.UserInfo.Length > 0)
                throw new NotSupportedException("推送入口仅支持不含用户凭据的 HTTP(S) 地址，不支持文件、迅雷或磁力链接。");
            if (address.Host == "youtube.com" || address.Host.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase) || address.Host == "youtu.be")
                throw new NotSupportedException("YouTube 专用提取尚未实现。");
            entries.Add((titled ? record[..separator].Trim() : "推送播放", address.AbsoluteUri));
        }
        var lines = new List<PlaybackLine>();
        foreach (var (id, name) in new[] { ("direct", "直連"), ("parse", "解析") })
        {
            var episodes = entries.Select((entry, index) => new Episode(id + ":" + index.ToString(CultureInfo.InvariantCulture), entry.Title, entry.Url)).ToArray();
            lines.Add(new PlaybackLine(id, name, episodes));
        }
        return new MediaDetail
        {
            Item = new MediaItem { Id = input, Title = entries.Count == 1 ? entries[0].Title : $"推送列表 · {entries.Count}项" },
            Description = "HTTP(S) 推送；支持直连与 JSON 解析，不提供网页嗅探。", Lines = lines
        };
    }
}
