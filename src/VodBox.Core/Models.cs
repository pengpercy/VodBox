using System.Text.Json;
using System.Text.Json.Serialization;

namespace VodBox.Core;

// ---------- VodBox 桌面版运行时模型 ----------

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
    /// <summary>
    /// 搜索结果来源站点的显示名（聚合搜索时按站点打标；浏览/详情条目为 null）。
    /// 卡片据此显示「来源：××」——跨站点结果必须能一眼看出每条来自哪个站点。
    /// </summary>
    public string? SourceName { get; init; }
    /// <summary>来源键：采集源键，豆瓣条目为 <see cref="MediaItem.DoubanSourceKey"/>。</summary>
    public string? SourceKey { get; init; }
    /// <summary>豆瓣条目 id（豆瓣卡片或采集源返回的 vod_douban_id）。</summary>
    public string? DoubanId { get; init; }
    /// <summary>豆瓣评分。</summary>
    public string? Rating { get; init; }
    /// <summary>movie / tv（豆瓣分类卡片已知，聚合换源时据此区分电影与剧集）。</summary>
    public string? Kind { get; init; }
    /// <summary>聚合卡片：提供该影片的源数量（0 = 非聚合卡片）。</summary>
    public int SourceCount { get; init; }

    /// <summary>豆瓣浏览卡片的伪来源键：打开时按片名聚合搜索全部采集源。</summary>
    public const string DoubanSourceKey = "douban";
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

/// <summary>
/// 一次播放返回里的一个可选地址（type4 的 <c>url</c> 数组 / 多线路）。<see cref="Name"/> 为展示名，
/// <see cref="Uri"/> 为完整地址。多地址时由界面提供「线路地址」切换，不静默丢弃其余地址。
/// </summary>
public sealed record PlaybackChoice(string Name, string Uri);

/// <summary>播放请求（解析完成或直连）。</summary>
public sealed record PlaybackRequest
{
    public required string Uri { get; init; }
    /// <summary>
    /// 同一选集返回的多个地址（如 type4 的 <c>url</c> 数组）。空表示只有 <see cref="Uri"/> 一个地址；
    /// 多于一个时界面据此提供切换，<see cref="Uri"/> 始终是其中之一（首个）。
    /// </summary>
    public IReadOnlyList<PlaybackChoice> Choices { get; init; } = [];
    public string Title { get; init; } = "媒体";
    public Dictionary<string, string> Headers { get; init; } = [];
    /// <summary>
    /// 源站已知媒体必须经本机代理改写才能播放（如 TS 片段伪装成图片）。与请求头无关，
    /// 置位后即使只有 UA/Referer 也走代理。
    /// </summary>
    public bool ProxyMedia { get; init; }
    /// <summary>经本机代理过滤 HLS 插播广告分段（隐含走代理）。</summary>
    public bool FilterAds { get; init; }
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
public sealed record LiveChannel : System.ComponentModel.INotifyPropertyChanged
{
    public required string Name { get; init; }
    public IReadOnlyList<string> Uris { get; init; } = [];
    public Dictionary<string, string> Headers { get; init; } = [];
    public string? Logo { get; init; }
    public string Group { get; init; } = "未分组";
    public string? TvgId { get; init; }
    public string? CatchupSource { get; init; }
    public int CatchupDays { get; init; }
    public int Number { get; init; }

    // ---- 展示态（运行时可变；EPG/台标由 S4 直播中心刷新） ----
    /// <summary>台标短字（如 央1 / 芒果），无 Logo 时的占位。</summary>
    public string Badge { get; set; } = "TV";
    /// <summary>台标底色（hex），演示期占位。</summary>
    public string BadgeColor { get; set; } = "#C0392B";
    /// <summary>当前节目（EPG now）。</summary>
    public string EpgNow
    {
        get => _epgNow;
        set { if (_epgNow == value) return; _epgNow = value; PropertyChanged?.Invoke(this, new(nameof(EpgNow))); }
    }
    private string _epgNow = "";
    /// <summary>下一节目（EPG next）。</summary>
    public string EpgNext
    {
        get => _epgNext;
        set { if (_epgNext == value) return; _epgNext = value; PropertyChanged?.Invoke(this, new(nameof(EpgNext))); }
    }
    private string _epgNext = "";

    /// <summary>是否正在播放（列表行 LIVE 角标 + 选中底色）。列表渲染后再赋值，需通知 UI。</summary>
    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            if (_isCurrent == value) return;
            _isCurrent = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsCurrent)));
        }
    }
    private bool _isCurrent;

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}
public sealed record LiveGroup(string Name, IReadOnlyList<LiveChannel> Channels, bool Locked, int DisplayCount = -1)
{
    [JsonIgnore] public string? PasswordHash {get;init;}
    /// <summary>面板显示的频道数（密码分组未解锁时也可显示总量的假计数）。</summary>
    public int Count => DisplayCount >= 0 ? DisplayCount : Channels.Count;
}
/// <summary>
/// 一条节目。ChannelKey 是来源内的原始频道 id；SourceId 标识来源命名空间，
/// 跨源数字 id 不能当全局主键（CCTV1 在 112114 是 "CCTV1"，在 51zmt 是 1）。
/// 手动导入 / 旧缓存的 SourceId 为空字符串。
/// </summary>
public sealed record Programme(string ChannelKey, string Title, DateTimeOffset Start, DateTimeOffset End, string ChannelName = "", string SourceId = "");

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
public sealed record PlaybackSnapshot(PlaybackState State, TimeSpan Position, TimeSpan Duration, bool CanSeek, string? Error = null,double? VideoAspectRatio=null);
public sealed record PlaybackEvent(long SessionId, PlaybackSnapshot Snapshot);

/// <summary>
/// 播放器「统计信息」面板的一次采样。每个字段都可能缺失（引擎不支持、媒体还没解析出来、纯音频等），
/// 速率单位：码率与网速为 bit/s，缓冲为秒。
/// </summary>
public sealed record PlaybackStats
{
    public int? VideoWidth { get; init; }
    public int? VideoHeight { get; init; }
    public double? FramesPerSecond { get; init; }
    public string? VideoCodec { get; init; }
    public string? AudioCodec { get; init; }
    public string? PixelFormat { get; init; }
    public string? ColorMatrix { get; init; }
    public string? ColorPrimaries { get; init; }
    public string? HardwareDecoder { get; init; }
    public int? AudioSampleRate { get; init; }
    public int? AudioChannels { get; init; }
    public double? VideoBitrate { get; init; }
    public double? AudioBitrate { get; init; }
    public long? DroppedFrames { get; init; }
    public long? DecoderDroppedFrames { get; init; }
    public long? FrameNumber { get; init; }
    public double? NetworkSpeed { get; init; }
    public double? BufferSeconds { get; init; }
    public string? Container { get; init; }
}
