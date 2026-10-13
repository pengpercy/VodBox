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
    public string? Ua { get; set; }
    public int Timeout { get; set; }
}

/// <summary>TVBox 站点定义。字段名对应原 JSON（Gson @SerializedName）。</summary>
public sealed class TvBoxSite
{
    [JsonConverter(typeof(LenientStringConverter))] public string Key { get; set; } = "";
    [JsonConverter(typeof(LenientStringConverter))] public string Name { get; set; } = "";
    public string Api { get; set; } = "";
    [JsonPropertyName("ext")] public JsonElement? Ext { get; set; }
    /// <summary>站点自带 JAR 地址（相对订阅 origin 解析）；空则回退到配置级 spider。</summary>
    public string? Jar { get; set; }
    public string? PlayUrl { get; set; }
    public int Type { get; set; }
    public int Searchable { get; set; } = 1;
    public int QuickSearch { get; set; } = 1;
    public int Changeable { get; set; } = 1;
    public List<string> Categories { get; set; } = [];
    public int Timeout { get; set; }
    public JsonElement? Style { get; set; }
    /// <summary>
    /// 站点在配置里的原始字段（未建模的键，如 <c>jar</c>/<c>logo</c>/<c>playerType</c> 等）。
    /// JAR 插件宿主需要「raw 上下文」时从这里取，避免为了兼容个别站点反复扩模型。
    /// </summary>
    [JsonExtensionData] public Dictionary<string, JsonElement>? Raw { get; set; }

    public SourceRuntime Runtime => Api.Contains(".js", StringComparison.OrdinalIgnoreCase) ? SourceRuntime.QuickJs
        : Api.Contains(".py", StringComparison.OrdinalIgnoreCase) ? SourceRuntime.Python
        : Api.StartsWith("csp_", StringComparison.Ordinal) ? SourceRuntime.Node /* 桌面端 csp_ 暂不支持，标记为 Node 待适配层提示 */
        : Type == 4 ? SourceRuntime.Type4 /* TVBox type4：原生 HTTP + play/flag 回调 */
        : SourceRuntime.MacCms;
}

/// <summary>TVBox 解析器定义（type: 0=web嗅探 1=json 2=jsonExt 3=jsonMix 4=超级解析）。</summary>
public sealed class TvBoxParse
{
    [JsonConverter(typeof(LenientStringConverter))] public string Name { get; set; } = "";
    [JsonConverter(typeof(LenientStringConverter))] public string Url { get; set; } = "";
    public int Type { get; set; }
    /// <summary>
    /// 解析器扩展：TVBox 里是多态 Object（可能是字符串，也可能是 {"header":...,"flag":[...]} 对象），
    /// 与 Gson 宽容行为对齐用 JsonElement 承载。宝盒真实配置 17 个 parses 中 11 个 ext 是对象——
    /// 若声明为 string 会导致整份配置反序列化失败（严格 System.Text.Json 与 Gson 的关键差异）。
    /// </summary>
    [JsonPropertyName("ext")] public JsonElement? Ext { get; set; }
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
    public string? PlayUrl {get;init;}
    public int Type { get; init; }
    public bool Searchable { get; init; } = true;
    public bool Changeable { get; init; } = true;
    public List<string> Categories { get; init; } = [];
    /// <summary>配置级全局 spider JAR（<c>config.spider</c> 原值，含 <c>;md5;</c> 段）。JAR 宿主用。</summary>
    public string? Spider { get; init; }
    /// <summary>站点自带 JAR（<c>site.jar</c> 原值，含 <c>;md5;</c> 段）。JAR 宿主用。</summary>
    public string? Jar { get; init; }
    /// <summary>订阅的最终 origin（跟随重定向后的地址），相对 jar/ext 以此为基准解析。</summary>
    public string? Origin { get; init; }
    /// <summary>
    /// 站点级请求头（<c>site.header</c> 原值原样保留）。type4 等原生 HTTP 站点靠它带 Cookie/UA/Referer
    /// 访问接口与上游媒体；不臆造缺失字段，也不把它改成全局会话。
    /// </summary>
    public IReadOnlyDictionary<string, string>? Header { get; init; }
    /// <summary>站点在配置里的原始 JSON（raw 上下文），JAR 宿主按需透传，不臆造缺失字段。</summary>
    public string? SiteJson { get; init; }
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
    /// <summary>
    /// 搜索结果来源站点的显示名（聚合搜索时按站点打标；浏览/详情条目为 null）。
    /// 卡片据此显示「来源：××」——跨站点结果必须能一眼看出每条来自哪个站点。
    /// </summary>
    public string? SourceName { get; init; }
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
    public ResolutionKind Resolution { get; init; }
    public string? ParseEndpoint {get;init;}
    /// <summary>
    /// 解析器标识（CatVod playerContent 的 <c>jx</c> / parse flag）。保留宿主返回的原始值，
    /// 让上层能区分「用哪个解析器」而不是丢成一个布尔。
    /// </summary>
    public string? Jx { get; init; }
    public Dictionary<string, string> Headers { get; init; } = [];
    /// <summary>
    /// 源站已知媒体必须经本机代理改写才能播放（如 TS 片段伪装成图片）。与请求头无关，
    /// 置位后即使只有 UA/Referer 也走代理。
    /// </summary>
    public bool ProxyMedia { get; init; }
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
