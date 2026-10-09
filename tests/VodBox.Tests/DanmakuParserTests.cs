using System.Xml;
using VodBox.Core;
using VodBox.Infrastructure;
using Xunit;

namespace VodBox.Tests;

public sealed class DanmakuParserTests
{
    [Fact]
    public void ParsesTimelineColorsAndModesWithoutExecutingAdvancedComments()
    {
        const string xml = "<i><d p='2,5,25,16777215'>顶部 &amp; 文本</d><d p='1,1,25,255'>滚动</d><d p='3,4,25,123'>底部</d><d p='0,7,25,0'>高级脚本</d></i>";
        var result = DanmakuParser.ParseXml(xml);
        Assert.Equal(3, result.Count);
        Assert.Equal(DanmakuMode.Scroll, result[0].Mode);
        Assert.Equal("顶部 & 文本", result[1].Text);
        Assert.Equal(0xFFFFFFu, result[1].Color);
        Assert.Equal(DanmakuMode.Bottom, result[2].Mode);
    }

    [Fact]
    public void JsonAndGzipFormatsUseSameSortedTimeline()
    {
        const string json = "{\"comments\":[{\"time\":2,\"text\":\"顶部\",\"mode\":\"top\",\"color\":255},{\"time\":1,\"text\":\"滚动\"}]}";
        var result = DanmakuParser.Parse(System.Text.Encoding.UTF8.GetBytes(json));
        Assert.Equal(2, result.Count);
        Assert.Equal("滚动", result[0].Text);
        Assert.Equal(DanmakuMode.Top, result[1].Mode);
        using var compressed = new MemoryStream();
        using (var gzip = new System.IO.Compression.GZipStream(compressed, System.IO.Compression.CompressionLevel.Fastest, true))
            gzip.Write(System.Text.Encoding.UTF8.GetBytes(json));
        Assert.Equal(result, DanmakuParser.Parse(compressed.ToArray()));
    }

    [Fact]
    public void JsonSkipsMalformedEntriesAndRejectsWrongRoot()
    {
        Assert.Empty(DanmakuParser.ParseJson("[{\"time\":\"bad\",\"text\":\"字符串时间\"},{\"time\":1,\"text\":\"字符串颜色\",\"color\":\"red\"},{\"time\":-1,\"text\":\"坏时间\"},{\"time\":1,\"text\":\"未知模式\",\"mode\":\"script\"}]"));
        Assert.Throws<InvalidDataException>(() => DanmakuParser.ParseJson("{}"));
    }

    [Fact]
    public void InvalidTimesColorsAndExternalEntitiesAreRejected()
    {
        Assert.Empty(DanmakuParser.ParseXml("<i><d p='NaN,1,25,0'>坏时间</d><d p='-1,1,25,0'>负时间</d><d p='1,1,25,999999999'>坏颜色</d></i>"));
        Assert.Throws<XmlException>(() => DanmakuParser.ParseXml("<!DOCTYPE i [<!ENTITY x SYSTEM 'file:///etc/passwd'>]><i>&x;</i>"));
    }
}
