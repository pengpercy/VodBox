using VodBox.Infrastructure;
using Xunit;

namespace VodBox.Tests;

public class DroppedFilesTests
{
    private static string TempFile(string extension)
    {
        var path = Path.Combine(Path.GetTempPath(), $"vodbox-drop-{Guid.NewGuid():N}{extension}");
        File.WriteAllText(path, "#EXTM3U");
        return path;
    }

    [Fact]
    public void PlaylistWinsOverMediaInSameDrop()
    {
        var playlist = TempFile(".m3u");
        var media = TempFile(".mp4");
        try
        {
            // 同时拖入播放列表和视频时，应当按直播列表打开（而不是把 m3u 当单条流播）。
            var (intent, path) = DroppedFiles.Classify([media, playlist]);
            Assert.Equal(DroppedIntent.PlayLive, intent);
            Assert.Equal(playlist, path);
        }
        finally { File.Delete(playlist); File.Delete(media); }
    }

    [Fact]
    public void MediaFilePlaysDirectly()
    {
        var media = TempFile(".mkv");
        try
        {
            var (intent, path) = DroppedFiles.Classify([media]);
            Assert.Equal(DroppedIntent.PlayMedia, intent);
            Assert.Equal(media, path);
        }
        finally { File.Delete(media); }
    }

    [Fact]
    public void FileUriIsAccepted()
    {
        var media = TempFile(".mp4");
        try
        {
            var (intent, _) = DroppedFiles.Classify([new Uri(media).AbsoluteUri]);
            Assert.Equal(DroppedIntent.PlayMedia, intent);
        }
        finally { File.Delete(media); }
    }

    [Fact]
    public void UnusableDropsReportNone()
    {
        Assert.Equal(DroppedIntent.None, DroppedFiles.Classify([]).Intent);
        Assert.Equal(DroppedIntent.None, DroppedFiles.Classify([null, ""]).Intent);
        // 不存在的路径、远程地址、目录、未知扩展名都不接受。
        Assert.Equal(DroppedIntent.None, DroppedFiles.Classify(["/tmp/does-not-exist-9f3.m3u"]).Intent);
        Assert.Equal(DroppedIntent.None, DroppedFiles.Classify(["https://host/list.m3u"]).Intent);
        Assert.Equal(DroppedIntent.None, DroppedFiles.Classify([Path.GetTempPath()]).Intent);
        Assert.Equal(DroppedIntent.None, DroppedFiles.Classify([TempFile(".txt")]).Intent);
    }
}
