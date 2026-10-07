using System.Text.Json;
using System.Text.Json.Serialization;

namespace VodBox.Core;

// ---------- TVBox 兼容配置模型（字段与 FongMi/TV Gson 模型对齐） ----------

/// <summary>TVBox 订阅配置根（site.json）。</summary>
public sealed class TvBoxConfig
{
    [JsonPropertyName("spider")] public string? Spider { get; set; }
    [JsonPropertyName("sites")] public List<TvBoxSite> Sites { get; set; } = [];
    [JsonPropertyName("lives")] public List<TvBoxLive> Lives { get; set; } = [];
    [JsonPropertyName("parses")] public List<TvBoxParse> Parses { get; set; } = [];
    [JsonPropertyName("flags")] public List<string> Flags { get; set; } = [];
    [JsonPropertyName("wallpaper")] public string? Wallpaper { get; set; }
    [JsonPropertyName("rules")] public JsonElement? Rules { get; set; }
    [JsonPropertyName("ads")] public List<string> Ads { get; set; } = [];
    [JsonPropertyName("warningText")] public string? WarningText { get; set; }
}

/// <summary>TVBox lives 条目：{name, type, url}（type 0=文本 1=接口；桌面版统一按 url 拉文本解析）。</summary>
public sealed class TvBoxLive
{
    [JsonConverter(typeof(LenientStringConverter))] public string Name { get; set; } = "";
    public int Type { get; set; }
    public string? Url { get; set; }
    [JsonPropertyName("ext")] public JsonElement? Ext { get; set; }
    public string? Epg { get; set; }
}

/// <summary>TVBox 站点定义。字段名对应原 JSON（Gson @SerializedName）。</summary>
public sealed class TvBoxSite
{
    [JsonConverter(typeof(LenientStringConverter))] public string Key { get; set; } = "";
    [JsonConverter(typeof(LenientStringConverter))] public string Name { get; set; } = "";
    public string Api { get; set; } = "";
    [JsonPropertyName("ext")] public JsonElement? Ext { get; set; }
    public string? PlayUrl { get; set; }
    public int Type { get; set; }
    public int Searchable { get; set; } = 1;
    public int QuickSearch { get; set; } = 1;
    public int Changeable { get; set; } = 1;
    public List<string> Categories { get; set; } = [];
    public int Timeout { get; set; }
    public JsonElement? Style { get; set; }

    public SourceRuntime Runtime => Api.Contains(".js", StringComparison.OrdinalIgnoreCase) ? SourceRuntime.QuickJs
        : Api.Contains(".py", StringComparison.OrdinalIgnoreCase) ? SourceRuntime.Python
        : Api.StartsWith("csp_", StringComparison.Ordinal) ? SourceRuntime.Node /* 桌面端 csp_ 暂不支持，标记为 Node 待适配层提示 */
        : SourceRuntime.MacCms;
}

/// <summary>TVBox 解析器定义（type: 0=web嗅探 1=json 2=jsonExt 3=jsonMix 4=超级解析）。</summary>
public sealed class TvBoxParse
{
    [JsonConverter(typeof(LenientStringConverter))] public string Name { get; set; } = "";
    public int Type { get; set; }
    public string Url { get; set; } = "";
    public string? Ext { get; set; }
    public Dictionary<string, string>? Header { get; set; }
    public List<string> Flag { get; set; } = [];
}

// ---------- VodBox 桌面版运行时模型 ----------

/// <summary>内容源（运行时视图，来自 TVBox site 或本地目录源）。</summary>
public sealed record SourceInfo
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public required SourceRuntime Runtime { get; init; }
    public string? Api { get; init; }
    public string? Ext { get; init; }
    public int Type { get; init; }
    public bool Searchable { get; init; } = true;
    public bool Changeable { get; init; } = true;
    public List<string> Categories { get; init; } = [];
}

/// <summary>分类（Class）。</summary>
public sealed record Category(string Id, string Name);

/// <summary>筛选器（Filter/Value）。</summary>
public sealed record FilterValue(string Name, string Value);
public sealed record FilterGroup(string Key, string Name, List<FilterValue> Values, string Init);

/// <summary>媒体卡片（Vod 列表项）。</summary>
public sealed record MediaItem
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public string? Poster { get; init; }
    public string? Remarks { get; init; }
    public string? Year { get; init; }
    public string? Area { get; init; }
    public string? TypeName { get; init; }
}

/// <summary>剧集。</summary>
public sealed record Episode(string Id, string Title, string? Uri = null);

/// <summary>播放线路（Flag 拆分后的分组）。</summary>
public sealed record PlaybackLine(string Id, string Name, IReadOnlyList<Episode> Episodes);

/// <summary>媒体详情（Vod 详情）。</summary>
public sealed record MediaDetail
{
    public required MediaItem Item { get; init; }
    public string Description { get; init; } = "";
    public string? Director { get; init; }
    public string? Actor { get; init; }
    public IReadOnlyList<PlaybackLine> Lines { get; init; } = [];

    /// <summary>按线路 id 查找（历史续播：找不到回退首条线路）。</summary>
    public PlaybackLine? FindLine(string? lineId) =>
        Lines.FirstOrDefault(l => l.Id == lineId) ?? Lines.FirstOrDefault();

    /// <summary>按选集 id 在指定线路内查找（找不到回退该线路首集）。</summary>
    public Episode? FindEpisode(PlaybackLine? line, string? episodeId) =>
        line?.Episodes.FirstOrDefault(e => e.Id == episodeId) ?? line?.Episodes.FirstOrDefault();

    /// <summary>
    /// 历史里的续播位置是否适用于当前选集：集号一致才复用，避免把上一集的进度套到别的集上
    /// （例如从第 8 集切到第 1 集时不能沿用第 8 集的时间戳）。
    /// 旧记录没存集号（空）时按「未知 = 允许」处理，保持向后兼容。
    /// </summary>
    public static bool ResumePositionApplies(string? storedLineId, string? storedEpisodeId, string? lineId, string? episodeId) =>
        (string.IsNullOrEmpty(storedLineId) || storedLineId == lineId) &&
        (string.IsNullOrEmpty(storedEpisodeId) || storedEpisodeId == episodeId);
}

/// <summary>分页结果。</summary>
public sealed record MediaPage(IReadOnlyList<MediaItem> Items, int Page, int PageCount);

/// <summary>播放请求（解析完成或直连）。</summary>
public sealed record PlaybackRequest
{
    public required string Uri { get; init; }
    public string Title { get; init; } = "媒体";
    public ResolutionKind Resolution { get; init; }
    public Dictionary<string, string> Headers { get; init; } = [];
    public long StartPositionMs { get; init; }
    public string SourceKey { get; init; } = "local";
    /// <summary>来源显示名（写历史用）。本地文件为「本地」，点播为站点名。</summary>
    public string SourceName { get; init; } = "本地";
    public string MediaId { get; init; } = "";
    /// <summary>线路 id（历史续播时恢复选集上下文）。</summary>
    public string LineId { get; init; } = "";
    public string EpisodeId { get; init; } = "";
    public string? Poster { get; init; }
    public string? Remarks { get; init; }
    public bool IsLive { get; init; }
    /// <summary>弹幕文件地址（XML/JSON，如哔哩哔哩 comment.bilibili.com/{cid}.xml）。无则 null。</summary>
    public string? DanmakuUri { get; init; }
}

// ---------- 直播模型 ----------

public sealed record LiveSource(string Id, string Name, string Uri);
public sealed record LiveChannel
{
    public required string Name { get; init; }
    public IReadOnlyList<string> Uris { get; init; } = [];
    public string? Logo { get; init; }
    public string Group { get; init; } = "未分组";
    public string? TvgId { get; init; }
    public int Number { get; init; }
}
public sealed record LiveGroup(string Name, IReadOnlyList<LiveChannel> Channels, bool Locked);
public sealed record Programme(string ChannelKey, string Title, DateTimeOffset Start, DateTimeOffset End, string ChannelName = "");

// ---------- 历史与收藏 ----------

public sealed record HistoryEntry
{
    public required string SourceKey { get; init; }
    public required string SourceName { get; init; }
    public required string MediaId { get; init; }
    public required string Title { get; init; }
    public string? Poster { get; init; }
    public string? Remarks { get; init; }
    public string LineId { get; init; } = "";
    public string EpisodeId { get; init; } = "";
    public long PositionMs { get; init; }
    public long DurationMs { get; init; }
    public double Rate { get; init; } = 1.0;
    public int OpeningSkipSec { get; init; }
    public int EndingSkipSec { get; init; }
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.Now;
}

public sealed record FavoriteEntry
{
    public FavoriteKind Kind { get; init; }
    public required string SourceKey { get; init; }
    public required string SourceName { get; init; }
    public required string MediaId { get; init; }
    public required string Title { get; init; }
    public string? Poster { get; init; }
    public string? Remarks { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
}

// ---------- 播放器 ----------

public sealed record MediaTrack(string Id, string Name, TrackKind Kind, bool IsSelected);
public sealed record PlaybackSnapshot(PlaybackState State, TimeSpan Position, TimeSpan Duration, bool CanSeek, string? Error = null);
public sealed record PlaybackEvent(long SessionId, PlaybackSnapshot Snapshot);
