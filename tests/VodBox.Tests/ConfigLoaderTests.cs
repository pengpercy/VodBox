using VodBox.Core;
using VodBox.Infrastructure;
using Xunit;

namespace VodBox.Tests;

public class ConfigLoaderTests
{
    [Fact]
    public void SitePlayUrlSurvivesImportIntoMacCmsRuntime()
    {
        var sources=ConfigLoader.ToSources(new TvBoxConfig{Sites=[new TvBoxSite{Key="site",Name="站点",Api="https://example.com/api",Type=1,PlayUrl="https://parse.example/?url="}]});
        var site=Assert.Single(sources);Assert.Equal("https://parse.example/?url=",site.PlayUrl);
        using var http=new DefaultHttp();var source=new MacCmsSource(site,http);Assert.Equal(site.PlayUrl,source.ParseEndpoint);
    }

    [Fact]
    public void ParsesStandardTvBoxConfig()
    {
        const string json = """
        {
          "spider": "https://example.com/spider.jar",
          "sites": [
            {"key": "niba", "name": "泥巴影视", "type": 0, "api": "https://api.example.com/provide/vod", "searchable": 1},
            {"key": "js1", "name": "脚本站", "type": 3, "api": "https://example.com/spider.js"}
          ],
          "lives": [{"name": "直播", "type": 0, "url": "https://example.com/live.m3u"}],
          "parses": [{"name": "json1", "type": 1, "url": "https://example.com/parse"}]
        }
        """;
        var config = ConfigLoader.Parse(json);
        Assert.NotNull(config);
        Assert.Equal(2, config.Sites.Count);
        Assert.Equal("泥巴影视", config.Sites[0].Name);
        Assert.Equal("https://example.com/live.m3u", config.Lives[0].Url);
        Assert.Single(config.Parses);
    }

    [Fact]
    public void ParsesJsWrappedConfig()
    {
        const string raw = "var config = {\"sites\":[{\"key\":\"a\",\"name\":\"A站\",\"api\":\"https://a.com/api.php/provide/vod\"}]}";
        var config = ConfigLoader.Parse(raw);
        Assert.NotNull(config);
        Assert.Single(config.Sites);
    }

    [Fact]
    public void ReturnsNullOnGarbage()
    {
        Assert.Null(ConfigLoader.Parse("not a config at all"));
    }

    [Fact]
    public void ToSourcesFiltersCspAndKeepsMacCms()
    {
        var config = new TvBoxConfig
        {
            Sites =
            [
                new TvBoxSite { Key = "mac", Name = "采集站", Api = "https://a.com/api.php/provide/vod", Type = 0 },
                new TvBoxSite { Key = "jar", Name = "爬虫站", Api = "csp_XXX", Type = 3 },
                new TvBoxSite { Key = "js", Name = "JS站", Api = "https://a.com/s.js", Type = 3 },
            ],
        };
        var sources = ConfigLoader.ToSources(config);
        Assert.Equal(2, sources.Count); // csp_ 被过滤
        Assert.Equal(SourceRuntime.MacCms, sources[0].Runtime);
        Assert.Equal(SourceRuntime.QuickJs, sources[1].Runtime);
    }
}
