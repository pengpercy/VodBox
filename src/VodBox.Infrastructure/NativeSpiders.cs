using VodBox.Core;

namespace VodBox.Infrastructure;

/// <summary>
/// 原生 C# 爬虫注册表：把 TVBox 站点定义里的 <c>csp_*</c> 入口映射到桌面端已实现的 C# 爬虫。
///
/// 桌面端无 JVM，无法加载原 JAR；已重写的入口在此登记，未登记的 csp_ 站点保持
/// <see cref="SourceRuntime.Node"/>（桌面端不支持），UI 据此提示「该源需 Android 端」。
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
        };

    /// <summary>判断某 api 是否有原生 C# 实现。</summary>
    public static bool IsSupported(string api) => TryGetFactory(api, out _);

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

    /// <summary>按站点定义创建原生爬虫实例；未实现返回 null。</summary>
    public static IContentSource? Create(SourceInfo info) =>
        TryGetFactory(info.Api ?? "", out var factory) ? factory(info) : null;

    /// <summary>已登记的 csp_ 入口名（用于诊断/统计）。</summary>
    public static IReadOnlyCollection<string> Registered => Factories.Keys;
}
