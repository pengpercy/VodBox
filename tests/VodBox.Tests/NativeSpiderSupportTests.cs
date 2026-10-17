using VodBox.Core;
using VodBox.Infrastructure;
using Xunit;

namespace VodBox.Tests;

/// <summary>阶段 4 独立入口的注册与 ext 门控：非法 ext 必须保留 JAR 回退，不被原生冒充。</summary>
public sealed class NativeSpiderSupportTests
{
    public static IEnumerable<object[]> RegisteredEntries()
    {
        yield return ["csp_AueteGuard"];
        yield return ["csp_LibvioGuard"];
        yield return ["csp_NewCzGuard"];
        yield return ["csp_YCyzGuard"];
        yield return ["csp_JPJGuard"];
        yield return ["csp_JpysGuard"];
        yield return ["csp_Jpys"];
        yield return ["csp_AllliveGuard"];
        yield return ["csp_MusicGuard"];
        yield return ["csp_NmyswvGuard"];
        yield return ["csp_DoubaoGuard"];
        yield return ["csp_AppgzGuard"];
        yield return ["csp_AiDjGuard"];
    }

    [Theory]
    [MemberData(nameof(RegisteredEntries))]
    public void EveryStageFourEntryIsRegistered(string api)
    {
        Assert.True(NativeSpiders.IsSupported(api));
        Assert.True(NativeSpiders.TryGetFactory(api, out _));
    }

    [Fact]
    public void UnknownFieldExtKeepsTheJarFallbackForGuardedEntries()
    {
        Assert.False(NativeSpiders.IsSupported(SyntheticSourceHelpers.TvSite("auete", "csp_AueteGuard", """{"siteUrl":"https://au.example","unknown":1}""")));
        Assert.False(NativeSpiders.IsSupported(SyntheticSourceHelpers.TvSite("lib", "csp_LibvioGuard", """{"siteUrl":"https://lib.example","token":"x"}""")));
        Assert.False(NativeSpiders.IsSupported(SyntheticSourceHelpers.TvSite("newcz", "csp_NewCzGuard", """{"login":"user"}""")));
        Assert.False(NativeSpiders.IsSupported(SyntheticSourceHelpers.TvSite("ycyz", "csp_YCyzGuard", """{"a":1}""")));
        Assert.False(NativeSpiders.IsSupported(SyntheticSourceHelpers.TvSite("jpj", "csp_JPJGuard", """{"a":1}""")));
        Assert.False(NativeSpiders.IsSupported(SyntheticSourceHelpers.TvSite("jpys", "csp_JpysGuard", "file:///tmp")));
        Assert.False(NativeSpiders.IsSupported(SyntheticSourceHelpers.TvSite("music", "csp_MusicGuard", "file:///tmp")));
        Assert.False(NativeSpiders.IsSupported(SyntheticSourceHelpers.TvSite("nmyswv", "csp_NmyswvGuard", "https://vip.example/path")));
        Assert.False(NativeSpiders.IsSupported(SyntheticSourceHelpers.TvSite("doubao", "csp_DoubaoGuard", "file:///tmp")));
        Assert.False(NativeSpiders.IsSupported(SyntheticSourceHelpers.TvSite("appgz", "csp_AppgzGuard", """{"user":"x"}""")));
        Assert.False(NativeSpiders.IsSupported(SyntheticSourceHelpers.TvSite("aidj", "csp_AiDjGuard", """{"modulePath":"http://x"}""")));
    }

    [Fact]
    public void ValidExtsAreAcceptedForGuardedEntries()
    {
        Assert.True(NativeSpiders.IsSupported(SyntheticSourceHelpers.TvSite("auete", "csp_AueteGuard", """{"siteUrl":"https://au.example"}""")));
        Assert.True(NativeSpiders.IsSupported(SyntheticSourceHelpers.TvSite("lib", "csp_LibvioGuard", """{"Cloud-drive":"https://cloud.example","siteUrl":"https://lib.example"}""")));
        Assert.True(NativeSpiders.IsSupported(SyntheticSourceHelpers.TvSite("jpys", "csp_Jpys", "https://www.ghw9zwp5.com")));
        Assert.True(NativeSpiders.IsSupported(SyntheticSourceHelpers.TvSite("doubao", "csp_DoubaoGuard", "https://www.doubaozhibo.com")));
        Assert.True(NativeSpiders.IsSupported(SyntheticSourceHelpers.TvSite("appgz", "csp_AppgzGuard", null)));
        Assert.True(NativeSpiders.IsSupported(SyntheticSourceHelpers.TvSite("aidj", "csp_AiDjGuard", """{"type":"video"}""")));
    }
}
