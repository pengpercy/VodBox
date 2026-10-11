using VodBox.Infrastructure;
using Xunit;

namespace VodBox.Tests;

/// <summary>
/// 按来源解析 XMLTV：强制来源命名空间、保留每个频道的全部 display-name（别名候选），
/// 同时不改变既有 <see cref="XmlTvParser.Parse"/> 的扁平语义。
/// </summary>
public sealed class EpgSourceParsingTests
{
    private const string Xml = """
    <tv>
      <channel id="CCTV1">
        <display-name>央视一套</display-name>
        <display-name>CCTV1综合</display-name>
      </channel>
      <channel id="CCTV4K"><display-name>CCTV4K</display-name></channel>
      <programme channel="CCTV1" source="ignored" start="20261011080000 +0800" stop="20261011090000 +0800"><title>朝闻天下</title></programme>
      <programme channel="GHOST" start="20261011080000 +0800" stop="20261011090000 +0800"><title>未声明频道</title></programme>
    </tv>
    """;

    [Fact]
    public void ParseSourceForcesSourceIdAndKeepsEveryDisplayName()
    {
        var data = XmlTvParser.ParseSource(Xml, "112114");

        Assert.Equal("112114", data.SourceId);
        Assert.All(data.Programmes, programme => Assert.Equal("112114", programme.SourceId));
        Assert.Equal(2, data.ChannelNames["CCTV1"].Count);
        Assert.Contains("央视一套", data.ChannelNames["CCTV1"]);
        Assert.Contains("CCTV1综合", data.ChannelNames["CCTV1"]);
    }

    [Fact]
    public void ParseSourceKeepsChannelNameFirstDisplayNameAndSortsProgrammes()
    {
        var data = XmlTvParser.ParseSource(Xml, "112114");

        Assert.Equal(2, data.Programmes.Count);
        Assert.Equal("央视一套", data.Programmes[0].ChannelName);
        // 未声明频道仍保留，ChannelName 回退成 channel id（不会伪造匹配）。
        Assert.Equal("GHOST", data.Programmes[1].ChannelName);
    }

    [Fact]
    public void BuildsSearchableIndexFromMultiNameChannels()
    {
        var index = EpgProgrammeIndex.Build(XmlTvParser.ParseSource(Xml, "51zmt"));

        // 别名与主名都能作为唯一名候选命中同一个 channelId。
        Assert.Equal(new[] { "CCTV1" }, index.ChannelIdsByName(EpgChannelMatcher.Normalize("央视一套")));
        Assert.Equal(new[] { "CCTV1" }, index.ChannelIdsByName(EpgChannelMatcher.Normalize("CCTV1综合")));
        Assert.True(index.HasChannel("CCTV4K"));
    }

    [Fact]
    public void EmptySourceIdIsRejected()
    {
        Assert.Throws<ArgumentException>(() => XmlTvParser.ParseSource(Xml, "  "));
    }
}
