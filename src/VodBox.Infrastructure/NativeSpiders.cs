using VodBox.Core;

namespace VodBox.Infrastructure;

/// <summary>
/// 原生 C# 爬虫注册表：把 TVBox 站点定义映射到桌面端已实现的 C# 爬虫。
///
/// 两类映射：
/// 1. 按 <c>csp_*</c> api 名——桌面端无 JVM，已重写的 csp_ 入口在此登记，未登记的保持
///    <see cref="SourceRuntime.Node"/>（桌面端不支持），UI 据此提示「该源需 Android 端」；
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
        };

    /// <summary>按站点 key 的原生实现（真实配置里以 .js 入口出现的高频站点）。</summary>
    private static readonly Dictionary<string, Func<SourceInfo, IContentSource>> KeyFactories =
        new(StringComparer.Ordinal)
        {
            ["虎牙js"] = info => new HuyaSource(info),
        };

    /// <summary>判断某 api 是否有原生 C# 实现。</summary>
    public static bool IsSupported(string api) => TryGetFactory(api, out _);

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
        return TryGetFactory(info.Api ?? "", out var factory) ? factory(info) : null;
    }

    /// <summary>已登记的 csp_ api 名（用于诊断/统计）。</summary>
    public static IReadOnlyCollection<string> Registered => Factories.Keys;

    /// <summary>已登记的站点 key（用于诊断/统计）。</summary>
    public static IReadOnlyCollection<string> RegisteredKeys => KeyFactories.Keys;
}
