using VodBox.Core;

namespace VodBox.Infrastructure;

/// <summary>
/// 爬虫实例内的短期详情缓存。原 Java/Python 插件在 detailContent 里保存线路/解析器等状态，
/// playerContent 直接复用；无状态的 C# 实现若在 ResolvePlaybackAsync 里重新抓详情，每次播放都多一轮（或多轮）请求。
/// 「打开详情 → 点播」在有效期内命中缓存；过期或从历史直接续播时仍会重新抓取并校验选集归属。
/// </summary>
internal sealed class DetailCache(TimeSpan lifetime, int capacity = 16)
{
    private readonly Dictionary<string, (DateTimeOffset Stored, MediaDetail Detail)> _entries = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <summary>只读命中（不触发加载）：给按原插件「不回抓详情」直接播放、只想补标题的调用方。</summary>
    public MediaDetail? TryGet(string mediaId)
    {
        lock (_gate)
            return _entries.TryGetValue(mediaId, out var entry) && DateTimeOffset.UtcNow - entry.Stored < lifetime ? entry.Detail : null;
    }

    public async Task<MediaDetail> GetAsync(string mediaId, Func<string, CancellationToken, Task<MediaDetail>> load, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
            if (_entries.TryGetValue(mediaId, out var entry) && now - entry.Stored < lifetime) return entry.Detail;
        var detail = await load(mediaId, ct).ConfigureAwait(false);
        lock (_gate)
        {
            if (_entries.Count >= capacity && !_entries.ContainsKey(mediaId))
                _entries.Remove(_entries.MinBy(pair => pair.Value.Stored).Key);
            _entries[mediaId] = (DateTimeOffset.UtcNow, detail);
        }
        return detail;
    }
}
