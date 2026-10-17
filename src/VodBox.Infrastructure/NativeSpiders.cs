using VodBox.Core;

namespace VodBox.Infrastructure;

/// <summary>
/// 原生 C# 爬虫注册表：把 TVBox 站点定义映射到桌面端已实现的 C# 爬虫。
///
/// 两类映射：
/// 1. 按 <c>csp_*</c> api 名登记已重写入口；部分协议还要核对 ext 形态。
///    未适配定义由 ConfigLoader 根据策略过滤或交给现有 JAR 宿主；
/// 2. 按站点 key——部分高频站点（虎牙/斗鱼/兔小贝）在真实配置里以 .js 入口出现，
///    本应有 QuickJS 宿主（S3a），但旧代码已有经真机 AOT 验证的原生 C# 实现，
///    按「先易后难」直接复用，比先搭 JS 宿主收益大。
/// 映射按两源真实配置的使用频次推进（见 docs/SALVAGE.md、docs/ROADMAP.md S3b）。
/// </summary>
public static class NativeSpiders
{
    /// <summary>已实现的 csp_ 入口 → 工厂。键为 api 名精确匹配（Guard 变体需单独登记）。</summary>
    private static readonly Dictionary<string, Func<SourceInfo, IContentSource>> Factories =
        new(StringComparer.Ordinal)
        {
            ["csp_Bili"] = info => new BilibiliSource(info),
            ["csp_BiliGuard"] = info => new BilibiliSource(info),
            ["csp_FirstAid"] = info => new FirstAidSource(info),
            ["csp_FirstAidGuard"] = info => new FirstAidSource(info),
            ["csp_YGP"] = info => new TrailerSource(info),
            ["csp_YGPGuard"] = info => new TrailerSource(info),
            ["csp_Huya"] = info => new HuyaSource(info),
            ["csp_AppGet"] = info => new AppGetSource(info),
            ["csp_App99"] = info => new App99Source(info),
            ["csp_App99Guard"] = info => new App99Source(info),
            ["csp_T4Guard"] = info => new FtyT4Source(info),
            ["csp_AppTTGuard"] = info => new AppTtSource(info),
            ["csp_AppSxGuard"] = info => new AppSxSource(info),
            ["csp_LiveGzGuard"] = info => new LiveGzSource(info),
            ["csp_Tingshu275Guard"] = info => new Tingshu275Source(info),
            ["csp_AueteGuard"] = info => new AueteSource(info),
            ["csp_LibvioGuard"] = info => new LibvioSource(info),
            ["csp_NewCzGuard"] = info => new NewCzSource(info),
            ["csp_YCyzGuard"] = info => new YCyzSource(info),
            ["csp_JPJGuard"] = info => new JPJSource(info),
            ["csp_JpysGuard"] = info => new JpysSource(info),
            ["csp_Jpys"] = info => new JpysSource(info),
            ["csp_AllliveGuard"] = info => new AllliveSource(info),
            ["csp_MusicGuard"] = info => new MusicSource(info),
            ["csp_NmyswvGuard"] = info => new NmyswvSource(info),
            ["csp_DoubaoGuard"] = info => new DoubaoSource(info),
            ["csp_AppgzGuard"] = info => new AppGzSource(info),
            ["csp_AiDjGuard"] = info => new AiDjSource(info),
            ["csp_XPathGuard"] = info => new XPathGuardNoticeSource(info),
            ["csp_DouDouGuard"] = info => new DouDouNavigationSource(info),
            ["csp_SixVGuard"] = info => new SixVSource(info),
            ["csp_Push"] = info => new PushSource(info),
            ["csp_AppRJ"] = info => new AppRjSource(info),
            ["csp_QiutongTY"] = info => new QiutongTySource(info),
            ["csp_QingtingFM"] = info => new QingtingFmSource(info),
            ["csp_KafeiTY"] = info => new KafeiTySource(info),
            ["csp_GuaziTY"] = info => new GuaziTySource(info),
            ["csp_919TY"] = info => new Sports919Source(info),
            ["csp_Bttwoo"] = info => new BttwooSource(info),
            ["csp_BttwooGuard"] = info => new BttwooSource(info),
            ["csp_Dm84"] = info => new Dm84Source(info),
            ["csp_Dm84Guard"] = info => new Dm84Source(info),
            ["csp_Kanqiu"] = info => new KanqiuSource(info),
            ["csp_KanqiuGuard"] = info => new KanqiuSource(info),
        };

    /// <summary>按站点 key 的原生实现（真实配置里以 .js 入口出现的高频站点）。</summary>
    private static readonly Dictionary<string, Func<SourceInfo, IContentSource>> KeyFactories =
        new(StringComparer.Ordinal)
        {
            ["虎牙js"] = info => new HuyaSource(info),
            ["dr_兔小贝"] = info => new TuxiaobeiSource(info),
            ["斗鱼js"] = info => new DouyuSource(info),
        };

    /// <summary>判断某 api 是否有原生 C# 实现。</summary>
    public static bool IsSupported(string api) => TryGetFactory(api, out _);

    /// <summary>有限规则适配按定义检查，不能把特定音频站支持扩大成通用 XBPQ。</summary>
    public static bool IsSupported(TvBoxSite site) => site.Api.Trim() switch
    {
        "csp_App99Guard" => App99Source.SupportsGuard(Ext(site)),
        "csp_T4Guard" => FtyT4Source.Supports(Ext(site)),
        "csp_AppTTGuard" => AppTtSource.Supports(Ext(site)),
        "csp_AppSxGuard" => AppSxSource.Supports(Ext(site)),
        "csp_LiveGzGuard" => LiveGzSource.Supports(Ext(site)),
        "csp_Tingshu275Guard" => Tingshu275Source.Supports(Ext(site)),
        "csp_AueteGuard" => AueteSource.Supports(Ext(site)),
        "csp_LibvioGuard" => LibvioSource.Supports(Ext(site)),
        "csp_NewCzGuard" => NewCzSource.Supports(Ext(site)),
        "csp_YCyzGuard" => YCyzSource.Supports(Ext(site)),
        "csp_JPJGuard" => JPJSource.Supports(Ext(site)),
        "csp_JpysGuard" or "csp_Jpys" => JpysSource.Supports(Ext(site)),
        "csp_AllliveGuard" => AllliveSource.Supports(Ext(site)),
        "csp_MusicGuard" => MusicSource.Supports(Ext(site)),
        "csp_NmyswvGuard" => NmyswvSource.Supports(Ext(site)),
        "csp_DoubaoGuard" => DoubaoSource.Supports(Ext(site)),
        "csp_AppgzGuard" => AppGzSource.Supports(Ext(site)),
        "csp_AiDjGuard" => AiDjSource.Supports(Ext(site)),
        "csp_XPathGuard" => XPathGuardNoticeSource.Supports(Ext(site)),
        "csp_DouDouGuard" => DouDouNavigationSource.Supports(Ext(site)),
        "csp_SixVGuard" => SixVSource.Supports(Ext(site)),
        _ => IsSupported(site.Api) ||
        (site.Api == "csp_XPath" && (site.Ext is null || site.Ext.Value.ValueKind == System.Text.Json.JsonValueKind.Null)) ||
        (site.Api == "csp_AppYs" && IsAppYsRule(site.Ext is { ValueKind: System.Text.Json.JsonValueKind.String } ext ? ext.GetString() : null)) ||
        site.Api == "csp_XBPQ" && IsAudioRule(Ext(site))
    };

    private static string? Ext(TvBoxSite site) => site.Ext is { ValueKind: System.Text.Json.JsonValueKind.String } value ? value.GetString() : site.Ext?.GetRawText();

    internal static bool IsAppYsRule(string? ext) => Uri.TryCreate(ext, UriKind.Absolute, out var address) &&
        address.Scheme is "http" or "https" && address.UserInfo.Length == 0 && address.Query.Length == 0 &&
        (address.AbsolutePath.Contains("api.php/app", StringComparison.Ordinal) || address.AbsolutePath.Contains("xgapp", StringComparison.Ordinal));

    internal static bool IsAudioRule(string? ext)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(ext ?? "{}");
            if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("主页url", out var home) || home.ValueKind != System.Text.Json.JsonValueKind.String) return false;
            return Uri.TryCreate(home.GetString(), UriKind.Absolute, out var address) && address.Scheme == "https" && address.Port == 443 &&
                address.Host is "www.xsmp3.com" or "www.psmp3.com" && address.AbsolutePath == "/" &&
                address.Query.Length == 0 && address.Fragment.Length == 0 && address.UserInfo.Length == 0;
        }
        catch (System.Text.Json.JsonException) { return false; }
    }

    /// <summary>判断某站点 key 是否有原生 C# 实现（js 入口的原生适配）。</summary>
    public static bool IsSupportedKey(string key) =>
        !string.IsNullOrWhiteSpace(key) && KeyFactories.ContainsKey(key.Trim());

    /// <summary>尝试按 api 名取得工厂（区分 Guard 变体，不做家族合并）。</summary>
    public static bool TryGetFactory(string api, out Func<SourceInfo, IContentSource> factory)
    {
        if (!string.IsNullOrWhiteSpace(api) && Factories.TryGetValue(api.Trim(), out var found))
        {
            factory = found;
            return true;
        }
        factory = null!;
        return false;
    }

    /// <summary>按站点定义创建原生爬虫实例（先按 key，再按 api）；未实现返回 null。</summary>
    public static IContentSource? Create(SourceInfo info)
    {
        if (!string.IsNullOrWhiteSpace(info.Key) && KeyFactories.TryGetValue(info.Key.Trim(), out var byKey))
            return byKey(info);
        if (info.Api == "csp_AppYs" && IsAppYsRule(info.Ext)) return new AppYsSource(info);
        if (info.Api == "csp_XPath" && (string.IsNullOrWhiteSpace(info.Ext) || info.Ext == "null")) return new XPathNoticeSource(info);
        if (info.Api == "csp_XBPQ" && IsAudioRule(info.Ext)) return new AudioSiteSource(info);
        return TryGetFactory(info.Api ?? "", out var factory) ? factory(info) : null;
    }

    /// <summary>已登记的 csp_ api 名（用于诊断/统计）。</summary>
    public static IReadOnlyCollection<string> Registered => Factories.Keys;

    /// <summary>已登记的站点 key（用于诊断/统计）。</summary>
    public static IReadOnlyCollection<string> RegisteredKeys => KeyFactories.Keys;

    /// <summary>已登记且行为依赖精确 ext 形态的 csp_ 入口（ext 不匹配时不得提升为原生）。</summary>
    private static readonly HashSet<string> ExtGated = new(StringComparer.Ordinal)
    {
        "csp_App99Guard", "csp_T4Guard", "csp_AppTTGuard", "csp_AppSxGuard",
        "csp_LiveGzGuard", "csp_Tingshu275Guard",
        "csp_AueteGuard", "csp_LibvioGuard", "csp_NewCzGuard", "csp_YCyzGuard",
        "csp_JPJGuard", "csp_JpysGuard", "csp_Jpys", "csp_AllliveGuard", "csp_MusicGuard",
        "csp_NmyswvGuard", "csp_DoubaoGuard", "csp_AppgzGuard", "csp_AiDjGuard",
        "csp_XPathGuard", "csp_DouDouGuard",
        "csp_SixVGuard",
    };

    /// <summary>仅当该 api 已登记且 ext 形态被接受时才返回 true，避免只凭 api 名提升。</summary>
    public static bool Supports(TvBoxSite site) =>
        TryGetFactory(site.Api ?? "", out _) &&
        (!ExtGated.Contains(site.Api ?? "") || IsSupported(site));
}
