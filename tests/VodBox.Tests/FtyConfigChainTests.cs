using System.Text;
using VodBox.Core;
using VodBox.Infrastructure;
using Xunit;

namespace VodBox.Tests;

/// <summary>
/// 饭太硬真实样本的全链路锚点测试：raw JPEG 字节 →（尾部 base64 提取 + 注释剥离）→ TvBoxConfig → SourceInfo。
/// 后续阶段（S3 QuickJS 宿主）以此为基准，数字必须精确。
/// 样本：<c>fixtures/fty-in.bmp</c>（http://www.饭太硬.net/tv 的原始响应，19915 字节，magic FFD8FFE0）。
/// </summary>
public class FtyConfigChainTests
{
    private static readonly string FixturePath =
        Path.Combine(AppContext.BaseDirectory, "fixtures", "fty-in.bmp");

    [Fact]
    public void RawBytesDecodeToExpectedSiteAndLiveCounts()
    {
        Assert.True(File.Exists(FixturePath), $"缺少测试样本：{FixturePath}");
        var bytes = File.ReadAllBytes(FixturePath);
        Assert.Equal(19915, bytes.Length);
        Assert.Equal(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, bytes[..4]); // JPEG/JFIF magic

        var config = ConfigLoader.ParseBytes(bytes);

        Assert.NotNull(config);
        Assert.Equal(48, config!.Sites.Count);
        Assert.Equal(6, config.Lives.Count);
        Assert.Empty(config.Parses);
        Assert.Equal("https://动态壁纸.饭.eu.org/", config.Wallpaper);
    }

    [Fact]
    public void ExplicitStripCommentsStepAlsoYields48Sites()
    {
        // 独立复现「**」分隔的 base64 尾载荷，显式跑一遍 StripComments（Parse 内部的注释剥离步骤）
        var payload = ExtractPayload(File.ReadAllBytes(FixturePath));
        var config = ConfigLoader.Parse(ConfigLoader.StripComments(payload));

        Assert.NotNull(config);
        Assert.Equal(48, config!.Sites.Count);
    }

    [Fact]
    public void CspSitesAreNodeRuntimeAndFilteredOutWhileJsSitesSurvive()
    {
        var config = ConfigLoader.ParseBytes(File.ReadAllBytes(FixturePath))!;

        // 45 个 csp_ Java jar 爬虫：分类为 Node，且被 ToSources 全部过滤
        var csp = config.Sites.Where(s => s.Api.StartsWith("csp_", StringComparison.Ordinal)).ToList();
        Assert.Equal(45, csp.Count);
        Assert.All(csp, s => Assert.Equal(SourceRuntime.Node, s.Runtime));

        // 3 个 .js drpy 脚本站点存活，运行时判定为 QuickJs
        var sources = ConfigLoader.ToSources(config);
        Assert.Equal(3, sources.Count);
        Assert.All(sources, s => Assert.Equal(SourceRuntime.QuickJs, s.Runtime));
        Assert.All(new[] { "dr_兔小贝", "虎牙js", "斗鱼js" }, key => Assert.Contains(sources, s => s.Key == key));

        // 站点总数 = 45 过滤 + 3 存活，没有别的形态
        Assert.Equal(48, csp.Count + sources.Count);
    }

    /// <summary>独立解析饭太硬形态：最后一个 "**" 之后即纯 base64 载荷。</summary>
    private static string ExtractPayload(byte[] bytes)
    {
        var text = Encoding.ASCII.GetString(bytes);
        var marker = text.LastIndexOf("**", StringComparison.Ordinal);
        Assert.True(marker >= 0, "样本缺少 ** 分隔标记");
        var raw = text[(marker + 2)..];
        var b64 = new string(raw.Where(c => "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/=".Contains(c)).ToArray());
        b64 = b64[..(b64.Length - b64.Length % 4)];
        return Encoding.UTF8.GetString(Convert.FromBase64String(b64));
    }
}
