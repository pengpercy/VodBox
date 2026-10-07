using VodBox.Core;
using Xunit;

namespace VodBox.Tests;

/// <summary>历史续播的线路/选集恢复逻辑（DetailViewModel.Resume 使用的纯查找）。</summary>
public class MediaDetailResumeTests
{
    private static MediaDetail Detail() => new()
    {
        Item = new MediaItem { Id = "m1", Title = "片名" },
        Lines =
        [
            new PlaybackLine("line1", "线路一", [new Episode("e1", "第1集"), new Episode("e2", "第2集")]),
            new PlaybackLine("line2", "线路二", [new Episode("e7", "第7集"), new Episode("e8", "第8集")]),
        ],
    };

    [Fact]
    public void RestoresStoredLineAndEpisode()
    {
        var detail = Detail();
        var line = detail.FindLine("line2");
        Assert.Equal("line2", line!.Id);
        Assert.Equal("e8", detail.FindEpisode(line, "e8")!.Id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("missing")]
    public void FallsBackToFirstLineAndFirstEpisode(string? lineId)
    {
        var detail = Detail();
        var line = detail.FindLine(lineId);
        Assert.Equal("line1", line!.Id);
        Assert.Equal("e1", detail.FindEpisode(line, "missing")!.Id);
    }

    [Theory]
    [InlineData("line2", "e8", "line2", "e8", true)]   // 同一集：复用位置
    [InlineData("line2", "e8", "line1", "e1", false)]  // 换集：不能把上集进度套过来
    [InlineData("line2", "e8", "line2", "e7", false)]  // 同线路换集：同样不能
    [InlineData("line2", "e8", "line1", "e8", false)]  // 换线路：不能
    [InlineData("", "", "line1", "e1", true)]          // 旧记录没存集号：按「未知」处理，保持兼容
    public void ResumePositionOnlyAppliesToMatchingEpisode(string storedLine, string storedEpisode, string line, string episode, bool expected)
    {
        Assert.Equal(expected, MediaDetail.ResumePositionApplies(storedLine, storedEpisode, line, episode));
    }

    [Fact]
    public void MissingLineYieldsNoEpisode()
    {
        var empty = new MediaDetail { Item = new MediaItem { Id = "x", Title = "x" } };
        Assert.Null(empty.FindLine("any"));
        Assert.Null(empty.FindEpisode(null, "any"));
    }
}
