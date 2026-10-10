using System.Xml;
using VodBox.Core;
using VodBox.Infrastructure;
using Xunit;

namespace VodBox.Tests;

public sealed class MacCmsXmlTests
{
    private const string Sample = """
        <?xml version="1.0" encoding="utf-8"?>
        <rss version="5.1">
          <class><ty id="7">测试分类</ty></class>
          <list page="2" pagecount="5" recordcount="42">
            <video><id>42</id><name><![CDATA[示例 & 标题]]></name><pic>https://example.invalid/poster.png</pic>
              <type>测试分类</type><year>2026</year><area>中国</area><note>完结</note>
              <director>导演</director><actor>演员</actor><des><![CDATA[<p>第一段</p><p>第二段</p>]]></des>
              <dl><dd flag="empty"></dd><dd flag="mp4"><![CDATA[第一集$https://example.invalid/1.mp4?a=1&b=2#第二集$https://example.invalid/2.mp4]]></dd></dl>
            </video>
          </list>
        </rss>
        """;

    [Fact]
    public async Task XmlSupportsCategoriesListsSearchAndDetail()
    {
        var source = new MacCmsSource(new SourceInfo { Runtime = SourceRuntime.MacCms, Key = "xml", Name = "XML", Api = "https://example.invalid/api", Type = 0 }, (_, _) => Task.FromResult(Sample));
        Assert.Equal("7", Assert.Single(await source.GetCategoriesAsync(TestContext.Current.CancellationToken)).Id);
        var page = await source.GetItemsAsync("7", 2, null, TestContext.Current.CancellationToken);
        Assert.Equal(2, page.Page);
        Assert.Equal(5, page.PageCount);
        Assert.Equal("示例 & 标题", Assert.Single(page.Items).Title);
        Assert.Equal(5, (await source.SearchAsync("示例", 2, TestContext.Current.CancellationToken)).PageCount);
        var detail = await source.GetDetailAsync("42", TestContext.Current.CancellationToken);
        var line = Assert.Single(detail.Lines);
        Assert.Equal("mp4", line.Name);
        Assert.Equal(2, line.Episodes.Count);
        Assert.Contains("a=1&b=2", line.Episodes[0].Uri);
    }

    [Fact]
    public async Task HomeRequestsAllCategoriesAndRetainsExistingApiQuery()
    {
        string? requested = null;
        var source = new MacCmsSource(new SourceInfo { Runtime = SourceRuntime.MacCms, Key = "xml", Name = "XML", Api = "https://example.invalid/api?format=xml", Type = 0 }, (url, _) => { requested = url; return Task.FromResult(Sample); });
        await source.GetHomeAsync(TestContext.Current.CancellationToken);
        Assert.Contains("format=xml&ac=videolist", requested);
        Assert.DoesNotContain("&t=", requested);
    }

    [Fact]
    public async Task JsonSearchPreservesReportedPagination()
    {
        var source = new MacCmsSource(new SourceInfo { Runtime = SourceRuntime.MacCms, Key = "json", Name = "JSON", Api = "https://example.invalid/api", Type = 1 }, (_, _) => Task.FromResult("""{"page":"2","pagecount":"8","list":[{"vod_id":1,"vod_name":"测试"}]}"""));
        var page = await source.SearchAsync("测试", 2, TestContext.Current.CancellationToken);
        Assert.Equal(2, page.Page);
        Assert.Equal(8, page.PageCount);
    }

    [Fact]
    public void RejectsDtdAndExternalEntities()
    {
        Assert.Throws<XmlException>(() => MacCmsXml.Parse("""<!DOCTYPE rss [<!ENTITY x SYSTEM "file:///etc/passwd">]><rss><list><video><name>&x;</name></video></list></rss>"""));
    }

    [Fact]
    public void RejectsNonProtocolXmlAndOversizedDocuments()
    {
        Assert.Throws<InvalidDataException>(() => MacCmsXml.Parse("<html><body>challenge</body></html>"));
        Assert.Throws<XmlException>(() => MacCmsXml.Parse("<rss><list>" + new string('x', 8 * 1024 * 1024) + "</list></rss>"));
    }

    [Fact]
    public void EmptyLineSlotDoesNotShiftFollowingLineName()
    {
        var detail = MacCmsSource.ToDetail(new MacCmsVod { VodId = "1", VodName = "测试", VodPlayFrom = "empty$$$mp4", VodPlayUrl = "$$$第一集$https://example.invalid/1.mp4" });
        Assert.Equal("mp4", Assert.Single(detail.Lines).Name);
    }
}
