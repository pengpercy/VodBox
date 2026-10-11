using VodBox.Core;
using VodBox.Infrastructure;
using Xunit;

namespace VodBox.Tests;

/// <summary>
/// 单来源节目索引：按频道建有序节目表，当前 / 下一节目用二分查找，
/// 区分「已匹配但无节目」与「未匹配」，时效只看本频道自己的节目区间。
/// </summary>
public sealed class EpgProgrammeIndexTests
{
    private static readonly TimeSpan Zone = TimeSpan.FromHours(8);
    private static DateTimeOffset At(int hour, int minute = 0) =>
        new(2026, 10, 11, hour, minute, 0, Zone);

    private static Programme P(string channel, string title, int startHour, int endHour) =>
        new(channel, title, At(startHour), At(endHour));

    private static EpgProgrammeIndex Build(params Programme[] programmes)
    {
        var names = programmes
            .Select(p => p.ChannelKey)
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(id => id, id => (IReadOnlyList<string>)new[] { id }, StringComparer.Ordinal);
        return EpgProgrammeIndex.Build(new EpgSourceData("112114", programmes, names));
    }

    [Fact]
    public void CurrentUsesBinarySearchAndTreatsGapsAndBoundariesAsNoProgramme()
    {
        var index = Build(P("c1", "早间", 8, 9), P("c1", "午间", 10, 11), P("c1", "晚间", 12, 13));
        Assert.True(index.TryGetSchedule("c1", out var schedule));

        Assert.Equal("早间", schedule.Current(At(8, 30))?.Title);
        Assert.Equal("午间", schedule.Current(At(10, 0))?.Title);
        Assert.Equal("晚间", schedule.Current(At(12, 59))?.Title);

        // 边界：now == End 已不算当前；空档返回 null，绝不臆造节目。
        Assert.Null(schedule.Current(At(9)));
        Assert.Null(schedule.Current(At(9, 30)));
        Assert.Null(schedule.Current(At(13)));
        Assert.Null(schedule.Current(At(7)));
    }

    [Fact]
    public void NextIsFirstProgrammeStartingAfterNowWithoutFutureHorizon()
    {
        var index = Build(P("c1", "早间", 8, 9), P("c1", "午间", 10, 11), P("c1", "晚间", 12, 13));
        index.TryGetSchedule("c1", out var schedule);

        Assert.Equal("午间", schedule.Next(At(8, 30))?.Title);
        Assert.Equal("午间", schedule.Next(At(9, 30))?.Title);
        Assert.Equal("晚间", schedule.Next(At(11, 30))?.Title);
        Assert.Null(schedule.Next(At(12, 30)));
        Assert.Equal("早间", schedule.Next(At(7))?.Title);
    }

    [Fact]
    public void ExpiryIsPerChannelLastProgrammeEndNotSourceEnvelope()
    {
        var index = Build(P("old", "昨日", 8, 9), P("fresh", "今日", 8, 9));
        index.TryGetSchedule("old", out var oldSchedule);
        index.TryGetSchedule("fresh", out var freshSchedule);

        Assert.Equal(At(9), oldSchedule.LastEnd);
        Assert.True(oldSchedule.IsExpired(At(10)));
        Assert.False(oldSchedule.IsExpired(At(8, 30)));
        Assert.False(freshSchedule.IsExpired(At(8, 30)));
    }

    [Fact]
    public void DeclaredChannelWithoutProgrammesIsMatchedButHasNoSchedule()
    {
        var names = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["HNZKTV4"] = ["河南公共"],
        };
        var index = EpgProgrammeIndex.Build(new EpgSourceData("112114", [], names));

        Assert.True(index.HasChannel("HNZKTV4"));
        Assert.False(index.TryGetSchedule("HNZKTV4", out _));
        Assert.Equal(0, index.ChannelCount);
        Assert.Null(index.CoverageEnd);
        Assert.Null(index.Current(new EpgChannelKey("112114", "HNZKTV4"), At(12)));
    }

    [Fact]
    public void CoverageEndIsMaxLastEndAcrossChannels()
    {
        var index = Build(P("c1", "短", 8, 9), P("c2", "长", 8, 20));
        Assert.Equal(At(20), index.CoverageEnd);
    }

    [Fact]
    public void SameStartUsesLatestStartAtOrBeforeNow()
    {
        // 相邻/重叠档：取「最后一个开始时间不晚于 now」的节目，保证二分会话稳定。
        var index = Build(P("c1", "整点", 8, 9), P("c1", "半点", 8, 9));
        index.TryGetSchedule("c1", out var schedule);
        Assert.Equal("半点", schedule.Current(At(8, 45))?.Title);
    }

    [Fact]
    public void EmptyIndexHasNoChannels()
    {
        var index = EpgProgrammeIndex.CreateEmpty("112114");
        Assert.Equal(0, index.ChannelCount);
        Assert.False(index.HasChannel("CCTV1"));
        Assert.Empty(index.ChannelIdsByName("CCTV1"));
        Assert.Null(index.CoverageEnd);
    }
}
