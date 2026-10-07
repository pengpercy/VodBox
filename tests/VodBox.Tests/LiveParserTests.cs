using VodBox.Infrastructure;
using Xunit;

namespace VodBox.Tests;

public class LiveParserTests
{
    [Fact]
    public void ParsesM3uWithGroupsAndLogos()
    {
        const string m3u = """
        #EXTM3U
        #EXTINF:-1 tvg-id="cctv1" tvg-logo="https://logo/cctv1.png" group-title="央视",CCTV-1 综合
        http://stream.example.com/cctv1
        #EXTINF:-1 group-title="央视",CCTV-2 财经
        http://stream.example.com/cctv2
        #EXTINF:-1 group-title="卫视",湖南卫视
        http://stream.example.com/hntv
        """;
        var groups = M3uParser.ParseGroups(m3u);
        Assert.Equal(2, groups.Count);
        Assert.Equal("央视", groups[0].Name);
        Assert.Equal(2, groups[0].Channels.Count);
        var first = groups[0].Channels[0];
        Assert.Equal("CCTV-1 综合", first.Name);
        Assert.Equal("https://logo/cctv1.png", first.Logo);
        Assert.Equal("cctv1", first.TvgId);
        Assert.Equal(1, first.Number);
    }

    [Fact]
    public void ParsesTxtFormatWithGenres()
    {
        const string txt = """
        央视频道,#genre#
        CCTV-1,http://stream.example.com/cctv1
        CCTV-2,http://stream.example.com/cctv2
        卫视频道,#genre#
        湖南卫视,http://stream.example.com/hntv$http://backup.example.com/hntv
        """;
        var groups = TxtLiveParser.Parse(txt);
        Assert.Equal(2, groups.Count);
        Assert.Equal(2, groups[0].Channels.Count);
        // 多线路按 $ 分隔
        Assert.Equal(2, groups[1].Channels[0].Uris.Count);
        Assert.Equal("http://backup.example.com/hntv", groups[1].Channels[0].Uris[1]);
    }

    [Fact]
    public void SkipsCommentsAndEmptyLines()
    {
        const string txt = """
        // 注释行
        分组,#genre#

        频道,
        """;
        var groups = TxtLiveParser.Parse(txt);
        Assert.Empty(groups);
    }

    [Fact]
    public void DetectsM3uFormat()
    {
        Assert.True(M3uParser.IsM3u("#EXTM3U\n#EXTINF:-1,x\nhttp://a"));
        Assert.False(M3uParser.IsM3u("分组,#genre#\n频道,http://a"));
    }
}
