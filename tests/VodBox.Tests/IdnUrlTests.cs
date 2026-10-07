using System.Globalization;
using VodBox.Infrastructure;
using Xunit;

namespace VodBox.Tests;

/// <summary>
/// 中文/IDN 域名回归（测试源 `https://宝盒接口.top`、`http://www.饭太硬.net/tv`）。
/// 锁定 .NET 的真实行为：<see cref="Uri.Host"/> 保留 Unicode，只有 <see cref="Uri.IdnHost"/> 是 Punycode，
/// 以及 DefaultHttp 的显式归一化（两种写法等价、幂等）。
/// </summary>
public class IdnUrlTests
{
    [Theory]
    [InlineData("http://www.饭太硬.net/tv", "www.饭太硬.net", "www.xn--sss604efuw.net")]
    [InlineData("https://宝盒接口.top", "宝盒接口.top", "xn--6orr3pi6g9uu.top")]
    [InlineData("https://动态壁纸.饭.eu.org/", "动态壁纸.饭.eu.org", "xn--6fru5lc1gxo8a.xn--t75a.eu.org")]
    public void UriHostKeepsUnicodeWhileIdnHostIsPunycode(string url, string expectedHost, string expectedIdnHost)
    {
        var uri = new Uri(url);
        // 关键：Host 不是 punycode（.NET 不会自动改写），别指望用 Host 做 ASCII 比较
        Assert.Equal(expectedHost, uri.Host);
        Assert.Equal(expectedIdnHost, uri.IdnHost);
        Assert.Equal(expectedIdnHost, new IdnMapping().GetAscii(expectedHost));
    }

    [Theory]
    [InlineData("http://www.饭太硬.net/tv", "http://www.xn--sss604efuw.net/tv")]
    [InlineData("https://宝盒接口.top", "https://xn--6orr3pi6g9uu.top/")]
    [InlineData("https://动态壁纸.饭.eu.org/", "https://xn--6fru5lc1gxo8a.xn--t75a.eu.org/")]
    [InlineData("https://user:pass@饭太硬.net:8443/p?x=1", "https://user:pass@xn--sss604efuw.net:8443/p?x=1")]
    public void NormalizeIdnConvertsUnicodeHostToPunycode(string url, string expected)
    {
        Assert.Equal(expected, DefaultHttp.NormalizeIdn(url));
    }

    [Theory]
    [InlineData("http://www.xn--sss604efuw.net/tv")]     // 已是 punycode：幂等
    [InlineData("https://example.com/tv")]
    [InlineData("http://[::1]:8080/x")]
    [InlineData("http://193.123.86.190:14888/TV/iptv.php")]
    public void NormalizeIdnLeavesAsciiUrlsUnchanged(string url)
    {
        Assert.Equal(url, DefaultHttp.NormalizeIdn(url));
        Assert.Equal(url, DefaultHttp.NormalizeIdn(DefaultHttp.NormalizeIdn(url)));
    }

    [Fact]
    public void NormalizeIdnReturnsGarbageUnchanged()
    {
        Assert.Equal("not a url", DefaultHttp.NormalizeIdn("not a url"));
        Assert.Equal("", DefaultHttp.NormalizeIdn(""));
    }
}
