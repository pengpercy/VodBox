using VodBox.Core;
using VodBox.Infrastructure;
using Xunit;

namespace VodBox.Tests;

/// <summary>
/// 频道 → 节目表匹配。核心约束：只按 (sourceId, channelId) 命名空间匹配，
/// 保留 4K/8K/+ 与地区差异，同名多 id 判歧义而不是任取第一项。
/// </summary>
public sealed class EpgChannelMatcherTests
{
    private static EpgProgrammeIndex Index(string sourceId, params (string Id, string[] Names)[] channels)
    {
        var names = channels.ToDictionary(c => c.Id, c => (IReadOnlyList<string>)c.Names, StringComparer.Ordinal);
        return EpgProgrammeIndex.Build(new EpgSourceData(sourceId, [], names));
    }

    [Theory]
    [InlineData("CCTV4K", "CCTV4K")]
    [InlineData("cctv-4k", "CCTV4K")]
    [InlineData("CCTV8K", "CCTV8K")]
    [InlineData("CCTV5+", "CCTV5+")]
    [InlineData("CCTV5 +", "CCTV5+")]
    [InlineData("海峽衛視", "海峡卫视")]
    public void NormalizeKeepsUhdAndPlusFoldsTraditional(string input, string expected) =>
        Assert.Equal(expected, EpgChannelMatcher.Normalize(input));

    [Fact]
    public void NormalizeDoesNotCollapseUhdIntoBaseChannel()
    {
        // 反例：全局删「4K/8K」会把 CCTV4K 折成 CCTV 造成误配，这里必须保持区分。
        Assert.NotEqual(EpgChannelMatcher.Normalize("CCTV"), EpgChannelMatcher.Normalize("CCTV4K"));
        Assert.NotEqual(EpgChannelMatcher.Normalize("CCTV5"), EpgChannelMatcher.Normalize("CCTV5+"));
    }

    [Fact]
    public void ResolvesByTvgIdWithinTheGivenSourceOnly()
    {
        var index = Index("112114", ("CCTV1", ["CCTV1综合"]), ("CCTV17", ["CCTV17"]));
        var channel = new LiveChannel { Name = "央视一套", TvgId = "CCTV17" };

        Assert.Equal(EpgMatchKind.ByTvgId, EpgChannelMatcher.Resolve(index, channel, out var key));
        Assert.Equal(new EpgChannelKey("112114", "CCTV17"), key);
    }

    [Fact]
    public void DeclaredButProgrammeLessChannelStillCountsAsMatched()
    {
        // 声明了频道、但当天没有节目记录，属于「暂无节目安排」，不能退化成「未匹配」。
        var index = Index("112114", ("HNZKTV4", ["河南公共"]));
        var channel = new LiveChannel { Name = "河南公共", TvgId = "HNZKTV4" };

        Assert.Equal(EpgMatchKind.ByTvgId, EpgChannelMatcher.Resolve(index, channel, out var key));
        Assert.True(index.HasChannel(key.ChannelId));
        Assert.False(index.TryGetSchedule(key.ChannelId, out _));
    }

    [Fact]
    public void FallsBackToUniqueNormalizedNameWhenTvgIdIsMissing()
    {
        // 51zmt 用小整数 id，直播源 tvg-id 命中不了，只能靠唯一的 display-name 候选。
        var index = Index("51zmt", ("1", ["CCTV1"]), ("2", ["CCTV2"]));
        var channel = new LiveChannel { Name = "cctv-1" };

        Assert.Equal(EpgMatchKind.ByUniqueName, EpgChannelMatcher.Resolve(index, channel, out var key));
        Assert.Equal(new EpgChannelKey("51zmt", "1"), key);
    }

    [Fact]
    public void NumericIdsAreNeverNameCandidates()
    {
        // display-name 缺失时回退成 id，纯数字 id 不能当频道名候选，否则「1」会到处乱配。
        var index = Index("51zmt", ("1", ["1"]));
        Assert.Equal(EpgMatchKind.None, EpgChannelMatcher.Resolve(index, new LiveChannel { Name = "1" }, out _));
        Assert.Equal(EpgMatchKind.None, EpgChannelMatcher.Resolve(index, new LiveChannel { Name = "  7 " }, out _));
    }

    [Fact]
    public void SameNameOnMultipleIdsIsAmbiguousNotFirstMatch()
    {
        // 台湾民視第一台在源内对应多个 id：不能任取第一项，必须报歧义等人确认。
        var index = Index("epgpw_tw", ("370137", ["民視第一台"]), ("456642", ["民視第一台"]));
        var channel = new LiveChannel { Name = "民视第一台" };

        Assert.Equal(EpgMatchKind.Ambiguous, EpgChannelMatcher.Resolve(index, channel, out var key));
        Assert.Equal(default, key);
    }

    [Fact]
    public void CrossSourceSameNumberIsNotMerged()
    {
        // id=17 与 id=CCTV17 是不同来源的数字 id，必须落在各自命名空间，不能当全局主键。
        var legacy = Index("51zmt", ("17", ["CCTV17"]));
        var modern = Index("112114", ("CCTV17", ["CCTV17"]));
        var channel = new LiveChannel { Name = "CCTV17", TvgId = "CCTV17" };

        Assert.Equal(EpgMatchKind.ByTvgId, EpgChannelMatcher.Resolve(modern, channel, out var modernKey));
        Assert.Equal(new EpgChannelKey("112114", "CCTV17"), modernKey);

        // 在 51zmt 命名空间里 tvg-id 命中不了，只能按唯一名落到 id=17；两者不同源、不同键。
        Assert.Equal(EpgMatchKind.ByUniqueName, EpgChannelMatcher.Resolve(legacy, channel, out var legacyKey));
        Assert.Equal(new EpgChannelKey("51zmt", "17"), legacyKey);
        Assert.NotEqual(modernKey, legacyKey);
    }

    [Fact]
    public void EmptyNameWithoutTvgIdMatchesNothing()
    {
        var index = Index("112114", ("CCTV1", ["CCTV1"]));
        Assert.Equal(EpgMatchKind.None, EpgChannelMatcher.Resolve(index, new LiveChannel { Name = "  " }, out _));
    }
}
