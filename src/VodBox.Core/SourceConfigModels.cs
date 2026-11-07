using System.Text.Json.Serialization;

namespace VodBox.Core;

// ---------- LunaTV / MoonTV 配置文件格式（config.json） ----------

/// <summary>
/// LunaTV 订阅配置文件根：<c>{ cache_time, api_site: {key: {name, api, detail}}, custom_category: [...], lives: {...} }</c>。
/// 订阅地址返回的可能是原始 JSON，也可能是 base58 编码后的 JSON（LunaTV 管理后台的订阅格式）。
/// </summary>
public sealed class LunaConfigFile
{
    [JsonPropertyName("cache_time")] public int? CacheTime { get; set; }
    [JsonPropertyName("api_site")] public Dictionary<string, LunaApiSite>? ApiSite { get; set; }
    [JsonPropertyName("custom_category")] public List<LunaCustomCategory>? CustomCategory { get; set; }
    [JsonPropertyName("lives")] public Dictionary<string, LunaLive>? Lives { get; set; }
}

public sealed class LunaApiSite
{
    [JsonPropertyName("name")] [JsonConverter(typeof(LenientStringConverter))] public string Name { get; set; } = "";
    [JsonPropertyName("api")] [JsonConverter(typeof(LenientStringConverter))] public string Api { get; set; } = "";
    [JsonPropertyName("detail")] [JsonConverter(typeof(LenientStringConverter))] public string? Detail { get; set; }
}

public sealed class LunaCustomCategory
{
    [JsonPropertyName("name")] [JsonConverter(typeof(LenientStringConverter))] public string? Name { get; set; }
    /// <summary>movie / tv。</summary>
    [JsonPropertyName("type")] [JsonConverter(typeof(LenientStringConverter))] public string Type { get; set; } = "movie";
    /// <summary>豆瓣标签（search_subjects 的 tag）。</summary>
    [JsonPropertyName("query")] [JsonConverter(typeof(LenientStringConverter))] public string Query { get; set; } = "";
}

public sealed class LunaLive
{
    [JsonPropertyName("name")] [JsonConverter(typeof(LenientStringConverter))] public string Name { get; set; } = "";
    [JsonPropertyName("url")] [JsonConverter(typeof(LenientStringConverter))] public string Url { get; set; } = "";
    [JsonPropertyName("ua")] [JsonConverter(typeof(LenientStringConverter))] public string? Ua { get; set; }
    [JsonPropertyName("epg")] [JsonConverter(typeof(LenientStringConverter))] public string? Epg { get; set; }
}

// ---------- 本机持久化的源配置（对应 LunaTV AdminConfig 的桌面单用户子集） ----------

/// <summary>条目来源：订阅配置文件（config）或用户手动添加（custom）。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SourceOrigin>))]
public enum SourceOrigin { Config, Custom }

/// <summary>采集源（苹果 CMS V10 JSON 接口）。</summary>
public sealed record ApiSite
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    /// <summary>采集接口地址，如 <c>https://xx/api.php/provide/vod</c>。</summary>
    public required string Api { get; init; }
    /// <summary>详情页站点根（非空时详情改从 HTML 页面抓取 m3u8，对应 LunaTV 的特殊源）。</summary>
    public string? Detail { get; init; }
    public SourceOrigin From { get; init; } = SourceOrigin.Config;
    public bool Disabled { get; init; }
}

/// <summary>自定义豆瓣分类（点播页额外 Tab）。</summary>
public sealed record CustomCategory
{
    public string? Name { get; init; }
    public string Type { get; init; } = "movie";
    public required string Query { get; init; }
    public SourceOrigin From { get; init; } = SourceOrigin.Config;
    public bool Disabled { get; init; }
    [JsonIgnore] public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Query : Name!;
    [JsonIgnore] public string Identity => Query + Type;
}

/// <summary>直播源（m3u/txt 地址 + 可选 UA 与节目单）。</summary>
public sealed record LiveSite
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public required string Url { get; init; }
    public string? Ua { get; init; }
    public string? Epg { get; init; }
    public SourceOrigin From { get; init; } = SourceOrigin.Config;
    public bool Disabled { get; init; }
}

/// <summary>配置订阅状态（LunaTV ConfigSubscribtion）。</summary>
public sealed record SubscriptionState
{
    public string Url { get; init; } = "";
    public bool AutoUpdate { get; init; } = true;
    public DateTimeOffset? LastCheck { get; init; }
}

/// <summary>豆瓣数据代理方式（与 LunaTV DoubanProxyType 取值一致）。</summary>
public static class DoubanProxyTypes
{
    public const string Direct = "direct";
    public const string CorsProxyZwei = "cors-proxy-zwei";
    public const string TencentCdn = "cmliussss-cdn-tencent";
    public const string AliCdn = "cmliussss-cdn-ali";
    public const string CorsAnywhere = "cors-anywhere";
    public const string Custom = "custom";
    public static readonly string[] All = [Direct, CorsProxyZwei, TencentCdn, AliCdn, CorsAnywhere, Custom];
}

/// <summary>豆瓣图片代理方式（LunaTV DoubanImageProxyType；桌面端「server」= 本机直连并带豆瓣 Referer）。</summary>
public static class DoubanImageProxyTypes
{
    public const string Direct = "direct";
    public const string TencentCdn = "cmliussss-cdn-tencent";
    public const string AliCdn = "cmliussss-cdn-ali";
    public const string Custom = "custom";
    public static readonly string[] All = [Direct, TencentCdn, AliCdn, Custom];
}

/// <summary>站点级设置（LunaTV SiteConfig 中桌面端有意义的部分）。</summary>
public sealed record SiteSettings
{
    /// <summary>每个源搜索最多拉取的页数（SearchDownstreamMaxPage）。</summary>
    public int SearchMaxPage { get; init; } = 5;
    /// <summary>接口缓存时间（秒），来自配置文件 cache_time。</summary>
    public int CacheTimeSeconds { get; init; } = 7200;
    public string DoubanProxyType { get; init; } = DoubanProxyTypes.TencentCdn;
    public string DoubanProxy { get; init; } = "";
    public string DoubanImageProxyType { get; init; } = DoubanImageProxyTypes.TencentCdn;
    public string DoubanImageProxy { get; init; } = "";
    public bool DisableYellowFilter { get; init; }
    /// <summary>流式搜索：每个源完成即展示（LunaTV FluidSearch）。</summary>
    public bool FluidSearch { get; init; } = true;
    /// <summary>未指定源时测速优选最佳播放源（LunaTV 播放页 optimizationEnabled）。</summary>
    public bool PreferBestSource { get; init; } = true;
    /// <summary>HLS 去广告：过滤插播的广告分段（LunaTV blockAdEnabled）。</summary>
    public bool BlockAds { get; init; } = true;
}

/// <summary>本机持久化的完整源配置。</summary>
public sealed record SourceConfigData
{
    /// <summary>最近一次应用的订阅配置原文（已解码的 JSON）。</summary>
    public string ConfigFile { get; init; } = "";
    public SubscriptionState Subscription { get; init; } = new();
    public SiteSettings Settings { get; init; } = new();
    public List<ApiSite> Sources { get; init; } = [];
    public List<CustomCategory> CustomCategories { get; init; } = [];
    public List<LiveSite> Lives { get; init; } = [];
    /// <summary>用户删除过的订阅源键：刷新订阅时不再加回（可在设置里恢复）。</summary>
    public List<string> DeletedSourceKeys { get; init; } = [];
}

// ---------- 采集结果（LunaTV SearchResult） ----------

/// <summary>某个采集源上的一部影片（搜索/详情统一形态）。</summary>
public sealed record VodResult
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public string Poster { get; init; } = "";
    public required string SourceKey { get; init; }
    public required string SourceName { get; init; }
    /// <summary>该源上的全部可播线路（已过滤无地址线路；直链媒体线路在前）。</summary>
    public IReadOnlyList<PlaybackLine> Lines { get; init; } = [];
    public string? Class { get; init; }
    /// <summary>四位年份；未知为 "unknown"（与 LunaTV 一致，参与聚合键）。</summary>
    public string Year { get; init; } = "unknown";
    public string Description { get; init; } = "";
    public string? TypeName { get; init; }
    public string? DoubanId { get; init; }
    public string? Remarks { get; init; }
    public string? Area { get; init; }
    public string? Director { get; init; }
    public string? Actor { get; init; }

    /// <summary>主线路集数（聚合键里区分电影/剧集用）。</summary>
    [JsonIgnore] public int EpisodeCount => Lines.Count == 0 ? 0 : Lines.Max(line => line.Episodes.Count);
    [JsonIgnore] public bool IsMovie => EpisodeCount == 1;
}

/// <summary>豆瓣条目（DoubanItem）。</summary>
public sealed record DoubanItem(string Id, string Title, string Poster, string Rate, string Year);
