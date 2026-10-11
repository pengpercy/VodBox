using VodBox.Core;

namespace VodBox.Infrastructure;

/// <summary>
/// 节目表频道稳定键。跨源数字 id 不能当全局主键（CCTV1 在 112114 是 "CCTV1"，在 51zmt 是 1），
/// 因此内部键必须是 (sourceId, channelId)。
/// </summary>
public readonly record struct EpgChannelKey(string SourceId, string ChannelId);

/// <summary>一个来源内解析出的原始数据：节目 + 频道声明名（含别名），尚未建索引。</summary>
/// <param name="SourceId">来源命名空间。</param>
/// <param name="Programmes">已去重、按开始时间升序的节目。</param>
/// <param name="ChannelNames">channelId → 全部 display-name（无 display-name 时回退为 id）。</param>
public sealed record EpgSourceData(
    string SourceId,
    IReadOnlyList<Programme> Programmes,
    IReadOnlyDictionary<string, IReadOnlyList<string>> ChannelNames)
{
    public static EpgSourceData Empty(string sourceId) =>
        new(sourceId, [], new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal));
}

/// <summary>
/// 单频道有序节目表。节目按开始时间升序，当前 / 下一节目用二分查找，
/// 避免「每 30 秒为每个频道扫描全表」。
/// </summary>
public sealed class EpgChannelSchedule
{
    internal EpgChannelSchedule(EpgChannelKey key, string displayName, IReadOnlyList<Programme> programmes)
    {
        Key = key;
        DisplayName = displayName;
        Programmes = programmes;
        LastEnd = programmes.Count == 0 ? null : programmes[^1].End;
    }

    public EpgChannelKey Key { get; }
    public string DisplayName { get; }
    public IReadOnlyList<Programme> Programmes { get; }

    /// <summary>本频道已知最后一条节目的结束时间（判过期用，不受其他频道的包络影响）。</summary>
    public DateTimeOffset? LastEnd { get; }

    /// <summary>含 now 的节目；空档返回 null（不臆造）。</summary>
    public Programme? Current(DateTimeOffset now)
    {
        var index = LastStartAtOrBefore(now);
        if (index < 0) return null;
        var candidate = Programmes[index];
        return now < candidate.End ? candidate : null;
    }

    /// <summary>Start &gt; now 的第一条节目；无则 null（不设未来距离上限）。</summary>
    public Programme? Next(DateTimeOffset now)
    {
        var low = 0;
        var high = Programmes.Count;
        while (low < high)
        {
            var mid = (low + high) >> 1;
            if (Programmes[mid].Start <= now) low = mid + 1;
            else high = mid;
        }
        return low < Programmes.Count ? Programmes[low] : null;
    }

    /// <summary>已匹配、存在历史记录，且最后有效结束时间不晚于 now。</summary>
    public bool IsExpired(DateTimeOffset now) => LastEnd is { } end && end <= now;

    private int LastStartAtOrBefore(DateTimeOffset now)
    {
        var low = 0;
        var high = Programmes.Count;
        while (low < high)
        {
            var mid = (low + high) >> 1;
            if (Programmes[mid].Start <= now) low = mid + 1;
            else high = mid;
        }
        return low - 1;
    }
}

/// <summary>
/// 单来源节目索引：channelId → 有序节目表，规范化名称 → 候选 channelId。
/// 名称候选用于「tvg-id 失败后的唯一名匹配」；同名多 id（如台湾民視第一台）会产生多于一个候选，
/// 由调用方判为歧义而非任取第一项。
/// </summary>
public sealed class EpgProgrammeIndex
{
    private readonly Dictionary<string, EpgChannelSchedule> _schedules;
    private readonly Dictionary<string, IReadOnlyList<string>> _nameIndex;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _channelNames;

    private EpgProgrammeIndex(string sourceId, Dictionary<string, EpgChannelSchedule> schedules,
        Dictionary<string, IReadOnlyList<string>> nameIndex,
        IReadOnlyDictionary<string, IReadOnlyList<string>> channelNames)
    {
        SourceId = sourceId;
        _schedules = schedules;
        _nameIndex = nameIndex;
        _channelNames = channelNames;
    }

    public string SourceId { get; }
    public int ChannelCount => _schedules.Count;

    private static readonly EpgProgrammeIndex Empty = new("", [], [], new Dictionary<string, IReadOnlyList<string>>());

    /// <summary>空索引（未加载 / 无该来源数据）。</summary>
    public static EpgProgrammeIndex CreateEmpty(string sourceId) =>
        sourceId.Length == 0 ? Empty : new EpgProgrammeIndex(sourceId, [], [], new Dictionary<string, IReadOnlyList<string>>());

    public static EpgProgrammeIndex Build(EpgSourceData data)
    {
        var byChannel = new Dictionary<string, List<Programme>>(StringComparer.Ordinal);
        foreach (var programme in data.Programmes)
        {
            if (!byChannel.TryGetValue(programme.ChannelKey, out var list))
                byChannel[programme.ChannelKey] = list = [];
            list.Add(programme);
        }

        var schedules = new Dictionary<string, EpgChannelSchedule>(StringComparer.Ordinal);
        foreach (var (channelId, list) in byChannel)
        {
            var displayName = data.ChannelNames.TryGetValue(channelId, out var channelNames) && channelNames.Count > 0
                ? channelNames[0] : list[0].ChannelName;
            schedules[channelId] = new EpgChannelSchedule(new EpgChannelKey(data.SourceId, channelId), displayName, list);
        }

        var names = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (channelId, displayNames) in data.ChannelNames)
        {
            AddName(names, channelId, channelId);
            foreach (var name in displayNames) AddName(names, channelId, name);
        }
        var nameIndex = names.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value.ToArray(), StringComparer.Ordinal);
        return new EpgProgrammeIndex(data.SourceId, schedules, nameIndex, data.ChannelNames);
    }

    private static void AddName(Dictionary<string, List<string>> names, string channelId, string rawName)
    {
        // 纯数字 id 不作为名称候选：51zmt/erw 用小整数 id，把 "1" 当成频道名会制造误配。
        if (EpgChannelMatcher.IsNumericId(rawName)) return;
        var normalized = EpgChannelMatcher.Normalize(rawName);
        if (normalized.Length == 0) return;
        if (!names.TryGetValue(normalized, out var list)) names[normalized] = list = [];
        if (!list.Contains(channelId, StringComparer.Ordinal)) list.Add(channelId);
    }

    public bool TryGetSchedule(string channelId, out EpgChannelSchedule schedule) =>
        _schedules.TryGetValue(channelId, out schedule!);

    /// <summary>
    /// 源内是否声明过该频道（即使没有节目记录）。用于区分「已匹配但无节目」和「未匹配」：
    /// 没有节目记录的频道不在 <c>_schedules</c> 里，只在 <c>_channelNames</c> 里。
    /// </summary>
    public bool HasChannel(string channelId) =>
        _channelNames.ContainsKey(channelId) || _schedules.ContainsKey(channelId);

    /// <summary>规范化名称对应的候选 channelId（源内）。</summary>
    public IReadOnlyList<string> ChannelIdsByName(string normalizedName) =>
        _nameIndex.TryGetValue(normalizedName, out var ids) ? ids : [];

    /// <summary>频道声明名（用于歧义提示）。</summary>
    public IReadOnlyList<string> NamesFor(string channelId) =>
        _channelNames.TryGetValue(channelId, out var names) ? names : [];

    public Programme? Current(EpgChannelKey key, DateTimeOffset now) =>
        _schedules.TryGetValue(key.ChannelId, out var schedule) ? schedule.Current(now) : null;

    public Programme? Next(EpgChannelKey key, DateTimeOffset now) =>
        _schedules.TryGetValue(key.ChannelId, out var schedule) ? schedule.Next(now) : null;

    /// <summary>本来源所有频道最后结束时间的最大值，作为数据覆盖包络（仅展示用，不用于单频道时效）。</summary>
    public DateTimeOffset? CoverageEnd
    {
        get
        {
            DateTimeOffset? end = null;
            foreach (var schedule in _schedules.Values)
                if (schedule.LastEnd is { } value && (end is null || value > end)) end = value;
            return end;
        }
    }
}
