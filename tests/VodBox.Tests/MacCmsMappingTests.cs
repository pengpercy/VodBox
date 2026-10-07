using VodBox.Core;
using VodBox.Infrastructure;
using Xunit;

namespace VodBox.Tests;

public class MacCmsMappingTests
{
    [Fact]
    public void MapsVodToItem()
    {
        var vod = new MacCmsVod
        {
            VodId = "42",
            VodName = "庆余年",
            VodPic = "https://pic/1.jpg",
            VodRemarks = "更新至36集",
            VodYear = "2024",
            VodArea = "大陆",
            TypeName = "古装",
        };
        var item = MacCmsSource.ToItem(vod);
        Assert.Equal("42", item.Id);
        Assert.Equal("庆余年", item.Title);
        Assert.Equal("更新至36集", item.Remarks);
        Assert.Equal("2024", item.Year);
    }

    [Fact]
    public void SplitsPlayFromAndPlayUrlIntoLines()
    {
        var vod = new MacCmsVod
        {
            VodId = "42",
            VodName = "庆余年",
            VodPlayFrom = "泥巴$$$备选",
            VodPlayUrl = "第01集$https://v/1.mp4#第02集$https://v/2.mp4$$$第01集$https://b/1.mp4",
        };
        var detail = MacCmsSource.ToDetail(vod);
        Assert.Equal(2, detail.Lines.Count);
        Assert.Equal("泥巴", detail.Lines[0].Name);
        Assert.Equal(2, detail.Lines[0].Episodes.Count);
        Assert.Equal("第01集", detail.Lines[0].Episodes[0].Title);
        Assert.Equal("https://v/1.mp4", detail.Lines[0].Episodes[0].Uri);
        Assert.Single(detail.Lines[1].Episodes);
    }

    [Fact]
    public void StripsHtmlInDescription()
    {
        var vod = new MacCmsVod
        {
            VodId = "1",
            VodName = "X",
            VodContent = "<p>剧情简介</p><br>第二段",
        };
        var detail = MacCmsSource.ToDetail(vod);
        Assert.DoesNotContain("<", detail.Description);
        Assert.Contains("剧情简介", detail.Description);
    }
}
