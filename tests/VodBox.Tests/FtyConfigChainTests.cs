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

    /// <summary>
    /// csp_ 站点的桌面端可用性锚点：45 个 csp_ 入口中已用 C# 原生重写的
    /// 提升为 <see cref="SourceRuntime.NativeSpider"/> 并存活，
    /// 其余仍为 Node 并被过滤（桌面端无 JVM）；3 个 .js drpy 站点归 QuickJs。
    /// S3b 每新增一个原生爬虫，此处的存活数应相应增长——数字必须精确。
    /// </summary>
    [Fact]
    public void CspSitesPromoteToNativeSpiderWhenImplementedAndOthersAreFilteredOut()
    {
        var config = ConfigLoader.ParseBytes(File.ReadAllBytes(FixturePath))!;

        // 45 个 csp_ Java jar 爬虫，全部在模型层分类为 Node（提升发生在 ToSources）
        var csp = config.Sites.Where(s => s.Api.StartsWith("csp_", StringComparison.Ordinal)).ToList();
        Assert.Equal(45, csp.Count);
        Assert.All(csp, s => Assert.Equal(SourceRuntime.Node, s.Runtime));

        var sources = ConfigLoader.ToSources(config);

        // 已有 C# 原生实现的饭太硬 csp_ 入口（新增原生爬虫时在此登记）+ 3 个按 key 适配的 .js 入口。
        string[] implemented = ["csp_AiDjGuard", "csp_AllliveGuard", "csp_App99Guard", "csp_AppSxGuard", "csp_AppTTGuard", "csp_AppgzGuard", "csp_AueteGuard", "csp_BiliGuard", "csp_BttwooGuard", "csp_Dm84Guard", "csp_DouDouGuard", "csp_DoubaoGuard", "csp_FirstAidGuard", "csp_JPJGuard", "csp_JpysGuard", "csp_KanqiuGuard", "csp_LibvioGuard", "csp_LiveGzGuard", "csp_MusicGuard", "csp_NewCzGuard", "csp_NmyswvGuard", "csp_SixVGuard", "csp_T4Guard", "csp_Tingshu275Guard", "csp_XPathGuard", "csp_YCyzGuard", "csp_YGPGuard"];
        var native = sources.Where(s => s.Runtime == SourceRuntime.NativeSpider).ToList();
        var nativeCsp = native.Where(s => s.Api?.StartsWith("csp_", StringComparison.Ordinal) == true).ToList();
        Assert.Equal(implemented, nativeCsp.Select(s => s.Api).Distinct().Order(StringComparer.Ordinal));
        Assert.Equal(7, native.Count(s => s.Api == "csp_BiliGuard"));
        Assert.Equal(38, native.Count);
        Assert.Equal(2, native.Count(s => s.Api == "csp_AppSxGuard"));
        Assert.Equal(2, native.Count(s => s.Api == "csp_T4Guard"));
        Assert.Equal(["dr_兔小贝", "斗鱼js", "虎牙js"], native.Except(nativeCsp).Select(s => s.Key).Order(StringComparer.Ordinal));

        // 全部 .js 入口已有原生实现；暂无 QuickJs 存活源
        Assert.DoesNotContain(sources, s => s.Runtime == SourceRuntime.QuickJs);

        // 未实现的 csp_ 全部被过滤，存活源全是原生实现；站点总数 48 = 过滤 + 存活
        Assert.DoesNotContain(sources, s => s.Runtime == SourceRuntime.Node);
        Assert.Equal(native.Count, sources.Count);
        Assert.Equal(48, sources.Count + (csp.Count - nativeCsp.Count));
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
