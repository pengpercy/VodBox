using VodBox.Core;

namespace VodBox.Infrastructure;

/// <summary>
/// 为一个直播频道选择可信节目表，按来源命名空间匹配，绝不跨源拼接时间表。
/// <para>
/// 规则：主源可信匹配且当前有节目 → 直接用主源；
/// 主源没有可信匹配、或主源可信匹配但「当前缺节目」→ 按来源级改用补充源；
/// 补充源也没有当前节目时保留主源已匹配的结果；主源歧义时不任取第一项、也不用补充源掩盖。
/// </para>
/// </summary>
public static class EpgScheduleResolver
{
    public static EpgChannelSchedule? Resolve(EpgProgrammeIndex? primary, EpgProgrammeIndex? supplement,
        LiveChannel channel, DateTimeOffset now, out EpgMatchKind kind)
    {
        kind = EpgMatchKind.None;

        EpgChannelSchedule? primarySchedule = null;
        if (primary is not null)
        {
            var primaryKind = EpgChannelMatcher.Resolve(primary, channel, out var primaryKey);
            if (primaryKind is EpgMatchKind.ByTvgId or EpgMatchKind.ByUniqueName)
            {
                kind = primaryKind;
                primarySchedule = primary.TryGetSchedule(primaryKey.ChannelId, out var schedule) ? schedule : null;
                // 主源可信匹配且当前有节目：直接采用，不查补充源。
                if (primarySchedule?.Current(now) is not null) return primarySchedule;
            }
            else if (primaryKind == EpgMatchKind.Ambiguous)
            {
                // 主源歧义：既不任取第一项，也不用补充源掩盖歧义。
                kind = primaryKind;
                return null;
            }
        }

        if (supplement is not null)
        {
            var supplementKind = EpgChannelMatcher.Resolve(supplement, channel, out var supplementKey);
            if (supplementKind is EpgMatchKind.ByTvgId or EpgMatchKind.ByUniqueName)
            {
                var candidate = supplement.TryGetSchedule(supplementKey.ChannelId, out var schedule) ? schedule : null;
                // 只有补充源真的补上了「当前节目」才改用它；
                // 否则保留主源已匹配的结果（哪怕当前空档），避免用更差的来源顶替。
                if (candidate?.Current(now) is not null || primarySchedule is null)
                {
                    kind = supplementKind;
                    return candidate;
                }
            }
            else if (supplementKind == EpgMatchKind.Ambiguous && primarySchedule is null)
            {
                kind = supplementKind;
            }
        }
        return primarySchedule;
    }
}
