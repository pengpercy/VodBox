using VodBox.Infrastructure;
using Xunit;

namespace VodBox.Tests;

/// <summary>饭太硬真实隐写样本（in.bmp）回归测试。</summary>
public class FtyStegoFixtureTests
{
    [Fact]
    public void DecodesRealFtySample()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "fty-in.bmp");
        if (!File.Exists(path)) return; // 样本未提交时跳过
        var config = ConfigLoader.ParseBytes(File.ReadAllBytes(path));
        Assert.NotNull(config);
        Assert.True(config!.Sites.Count >= 40, $"sites={config.Sites.Count}");
        Assert.True(config.Lives.Count >= 4, $"lives={config.Lives.Count}");
        Assert.Contains(config.Lives, l => l.Url?.EndsWith(".m3u") == true);
        // csp_ 全过滤后应只剩少量 js/直播源
        var sources = ConfigLoader.ToSources(config);
        Assert.All(sources, s => Assert.NotEqual(Core.SourceRuntime.Node, s.Runtime));
    }
}
