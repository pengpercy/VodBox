using VodBox.Core;

namespace VodBox.Infrastructure;

/// <summary>
/// 原生 C# 爬虫注册表：把 TVBox 站点定义映射到桌面端已实现的 C# 爬虫。
///
/// 两类映射：
/// 1. 按 <c>csp_*</c> api 名登记已重写入口；部分协议还要核对 ext 形态。
///    未适配定义由 ConfigLoader 过滤，并由 SourceRegistry 逐项报告；
/// 2. 按站点 key——部分高频站点（虎牙/斗鱼/兔小贝）在真实配置里以 .js 入口出现，
///    本应有 QuickJS 宿主（S3a），但旧代码已有经真机 AOT 验证的原生 C# 实现，
///    按「先易后难」直接复用，比先搭 JS 宿主收益大。
/// 映射按两源真实配置的使用频次推进（见 docs/SALVAGE.md、docs/ROADMAP.md S3b）。
/// </summary>
public static class NativeSpiders
{
    /// <summary>
    /// 一个已重写的 csp_ 入口：工厂 + 可选 ext 形态闸门。闸门非空时，只有 ext 被接受才提升为原生，
    /// 避免只凭 api 名把未验证的配置变体当成可运行。
    /// </summary>
    private sealed record Entry(Func<SourceInfo, IContentSource> Create, Func<string?, bool>? AcceptsExt = null);

    /// <summary>已实现的 csp_ 入口。键为 api 名精确匹配（Guard 变体需单独登记）。</summary>
    private static readonly Dictionary<string, Entry> Entries =
        new(StringComparer.Ordinal)
        {
            ["csp_Bili"] = new(info => new BilibiliSource(info)),
            ["csp_BiliGuard"] = new(info => new BilibiliSource(info)),
            ["csp_FirstAid"] = new(info => new FirstAidSource(info)),
            ["csp_FirstAidGuard"] = new(info => new FirstAidSource(info)),
            ["csp_YGP"] = new(info => new TrailerSource(info)),
            ["csp_YGPGuard"] = new(info => new TrailerSource(info)),
            ["csp_Huya"] = new(info => new HuyaSource(info)),
            ["csp_AppGet"] = new(info => new AppGetSource(info)),
            ["csp_App99"] = new(info => new App99Source(info)),
            ["csp_App99Guard"] = new(info => new App99Source(info), App99Source.SupportsGuard),
            ["csp_T4Guard"] = new(info => new FtyT4Source(info), FtyT4Source.Supports),
            ["csp_AppTTGuard"] = new(info => new AppTtSource(info), AppTtSource.Supports),
            ["csp_AppSxGuard"] = new(info => new AppSxSource(info), AppSxSource.Supports),
            ["csp_LiveGzGuard"] = new(info => new LiveGzSource(info), LiveGzSource.Supports),
            ["csp_Tingshu275Guard"] = new(info => new Tingshu275Source(info), Tingshu275Source.Supports),
            ["csp_AueteGuard"] = new(info => new AueteSource(info), AueteSource.Supports),
            ["csp_LibvioGuard"] = new(info => new LibvioSource(info), LibvioSource.Supports),
            ["csp_NewCzGuard"] = new(info => new NewCzSource(info), NewCzSource.Supports),
            ["csp_YCyzGuard"] = new(info => new YCyzSource(info), YCyzSource.Supports),
            ["csp_JPJGuard"] = new(info => new JPJSource(info), JPJSource.Supports),
            ["csp_JpysGuard"] = new(info => new JpysSource(info), JpysSource.Supports),
            ["csp_Jpys"] = new(info => new JpysSource(info), JpysSource.Supports),
            ["csp_AllliveGuard"] = new(info => new AllliveSource(info), AllliveSource.Supports),
            ["csp_MusicGuard"] = new(info => new MusicSource(info), MusicSource.Supports),
            ["csp_NmyswvGuard"] = new(info => new NmyswvSource(info), NmyswvSource.Supports),
            ["csp_DoubaoGuard"] = new(info => new DoubaoSource(info), DoubaoSource.Supports),
            ["csp_AppgzGuard"] = new(info => new AppGzSource(info), AppGzSource.Supports),
            ["csp_AiDjGuard"] = new(info => new AiDjSource(info), AiDjSource.Supports),
            ["csp_XPathGuard"] = new(info => new XPathGuardNoticeSource(info), XPathGuardNoticeSource.Supports),
            ["csp_DouDouGuard"] = new(info => new DouDouNavigationSource(info), DouDouNavigationSource.Supports),
            ["csp_SixVGuard"] = new(info => new SixVSource(info), SixVSource.Supports),
            ["csp_Push"] = new(info => new PushSource(info)),
            ["csp_AppRJ"] = new(info => new AppRjSource(info)),
            ["csp_QiutongTY"] = new(info => new QiutongTySource(info)),
            ["csp_QingtingFM"] = new(info => new QingtingFmSource(info)),
            ["csp_KafeiTY"] = new(info => new KafeiTySource(info)),
            ["csp_GuaziTY"] = new(info => new GuaziTySource(info)),
            ["csp_919TY"] = new(info => new Sports919Source(info)),
            ["csp_Bttwoo"] = new(info => new BttwooSource(info)),
            ["csp_BttwooGuard"] = new(info => new BttwooSource(info)),
            ["csp_Dm84"] = new(info => new Dm84Source(info)),
            ["csp_Dm84Guard"] = new(info => new Dm84Source(info)),
            ["csp_Kanqiu"] = new(info => new KanqiuSource(info)),
            ["csp_KanqiuGuard"] = new(info => new KanqiuSource(info)),
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
    public static bool IsSupported(TvBoxSite site)
    {
        var api = site.Api.Trim();
        if (Entries.TryGetValue(api, out var entry)) return entry.AcceptsExt is null || entry.AcceptsExt(Ext(site));
        return (api == "csp_XPath" && (site.Ext is null || site.Ext.Value.ValueKind == System.Text.Json.JsonValueKind.Null)) ||
            (api == "csp_AppYs" && IsAppYsRule(site.Ext is { ValueKind: System.Text.Json.JsonValueKind.String } ext ? ext.GetString() : null)) ||
            (api == "csp_XBPQ" && IsAudioRule(Ext(site)));
    }

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
        if (!string.IsNullOrWhiteSpace(api) && Entries.TryGetValue(api.Trim(), out var found))
        {
            factory = found.Create;
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
    public static IReadOnlyCollection<string> Registered => Entries.Keys;

    /// <summary>已登记的站点 key（用于诊断/统计）。</summary>
    public static IReadOnlyCollection<string> RegisteredKeys => KeyFactories.Keys;
}
