using VodBox.Infrastructure;
using Xunit;

namespace VodBox.Tests;

public class LiveParserTests
{
    [Fact]
    public void ParsesM3uWithGroupsAndLogos()
    {
        const string m3u = """
        #EXTM3U
        #EXTINF:-1 tvg-id="cctv1" tvg-logo="https://logo/cctv1.png" group-title="央视",CCTV-1 综合
        http://stream.example.com/cctv1
        #EXTINF:-1 group-title="央视",CCTV-2 财经
        http://stream.example.com/cctv2
        #EXTINF:-1 group-title="卫视",湖南卫视
        http://stream.example.com/hntv
        """;
        var groups = M3uParser.ParseGroups(m3u);
        Assert.Equal(2, groups.Count);
        Assert.Equal("央视", groups[0].Name);
        Assert.Equal(2, groups[0].Channels.Count);
        var first = groups[0].Channels[0];
        Assert.Equal("CCTV-1 综合", first.Name);
        Assert.Equal("https://logo/cctv1.png", first.Logo);
        Assert.Equal("cctv1", first.TvgId);
        Assert.Equal(1, first.Number);
    }

    [Fact]
    public void ParsesTxtFormatWithGenres()
    {
        const string txt = """
        央视频道,#genre#
        CCTV-1,http://stream.example.com/cctv1
        CCTV-2,http://stream.example.com/cctv2
        卫视频道,#genre#
        湖南卫视,http://stream.example.com/hntv$http://backup.example.com/hntv
        """;
        var groups = TxtLiveParser.Parse(txt);
        Assert.Equal(2, groups.Count);
        Assert.Equal(2, groups[0].Channels.Count);
        // 多线路按 $ 分隔
        Assert.Equal(2, groups[1].Channels[0].Uris.Count);
        Assert.Equal("http://backup.example.com/hntv", groups[1].Channels[0].Uris[1]);
    }

    [Fact]
    public void SkipsCommentsAndEmptyLines()
    {
        const string txt = """
        // 注释行
        分组,#genre#

        频道,
        """;
        var groups = TxtLiveParser.Parse(txt);
        Assert.Empty(groups);
    }

    [Fact]
    public void PlaybackHeadersAreDecodedAndDoNotLeakToNextChannel()
    {
        const string m3u = """
        #EXTM3U
        #EXTINF:-1,第一台
        #EXTVLCOPT:http-user-agent=TV Player
        #KODIPROP:inputstream.adaptive.stream_headers=Referer=https%3A%2F%2Fsource.example%2F&Cookie=ignored
        https://stream.example/one
        #EXTINF:-1,第二台
        https://stream.example/two
        """;
        var channels = M3uParser.ParseGroups(m3u).SelectMany(x => x.Channels).ToArray();
        Assert.Equal("TV Player", channels[0].Headers["User-Agent"]);
        Assert.Equal("https://source.example/", channels[0].Headers["Referer"]);
        Assert.Equal(2, channels[0].Headers.Count);
        Assert.Empty(channels[1].Headers);
    }

    [Fact]
    public void EncodedHeaderInjectionIsRejected()
    {
        Assert.Throws<InvalidDataException>(() => M3uParser.ParseGroups("#EXTM3U\n#EXTINF:-1,X\n#KODIPROP:inputstream.adaptive.stream_headers=User-Agent=bad%0D%0AInjected\nhttps://stream.example"));
    }

    [Fact]
    public async Task TvBoxMultipleLiveListsMergeWithUniqueNumbersAndRelativeUrls()
    {
        var texts = new Dictionary<string, string>
        {
            ["https://example.com/config.json"] = "{\"lives\":[{\"name\":\"一\",\"url\":\"one.m3u\"},{\"name\":\"二\",\"url\":\"https://example.com/two.txt\"}]}",
            ["https://example.com/one.m3u"] = "#EXTM3U\n#EXTINF:-1 group-title=\"央视\",CCTV1\nhttps://stream.example/1",
            ["https://example.com/two.txt"] = "卫视,#genre#\n频道2,https://stream.example/2",
        };
        var groups = await new LiveSources((url, _) => Task.FromResult(texts[url])).LoadAsync("https://example.com/config.json", TestContext.Current.CancellationToken);
        Assert.Equal(2, groups.Count);
        Assert.Equal("一 · 央视", groups[0].Name);
        Assert.Equal(new[] { 1, 2 }, groups.SelectMany(group => group.Channels).Select(channel => channel.Number));
    }

    [Fact]
    public async Task MultiLiveSourcesUseIndependentUserAgentAndReportPartialFailure()
    {
        var agents = new List<string>();
        var loader = new LiveSources((url, headers, _) =>
        {
            if (url.EndsWith("config.json")) return Task.FromResult("{\"lives\":[{\"name\":\"好源\",\"url\":\"https://example.com/good\",\"ua\":\"TVClient\"},{\"name\":\"坏源\",\"url\":\"https://example.com/bad\"}]}");
            if (url.EndsWith("bad")) return Task.FromException<string>(new IOException("不可达"));
            agents.Add(headers!["User-Agent"]);
            return Task.FromResult("频道,https://stream.example/a");
        });
        var groups = await loader.LoadAsync("https://example.com/config.json", TestContext.Current.CancellationToken);
        Assert.Single(groups);
        Assert.Equal("TVClient", Assert.Single(agents));
        Assert.Contains("坏源", Assert.Single(loader.Warnings));
    }

    [Fact]
    public void PasswordGroupDisplayDoesNotContainRawPassword()
    {
        var group=Assert.Single(TxtLiveParser.Parse("私密组_test-pass,#genre#\n频道,https://stream.example/live"));
        Assert.Equal("私密组",group.Name);Assert.True(group.Locked);Assert.NotNull(group.PasswordHash);
        Assert.DoesNotContain("test-pass",group.Name);Assert.DoesNotContain("test-pass",group.PasswordHash);Assert.Equal("私密组",group.Channels[0].Group);
    }

    [Fact]
    public async Task PerChannelUserAgentOverridesLiveSourceDefaultWithoutLeakingToOtherSource()
    {
        var loader=new LiveSources((url,headers,_)=>Task.FromResult(url.EndsWith("config")
            ?"{\"lives\":[{\"name\":\"one\",\"url\":\"https://example.com/list\",\"ua\":\"SourceUA\"},{\"name\":\"two\",\"url\":\"https://example.com/other\"}]}"
            :url.EndsWith("list")?"#EXTM3U\n#EXTINF:-1,频道\n#EXTVLCOPT:http-user-agent=ChannelUA\nhttps://stream.example/a":"频道2,https://stream.example/b"));
        var groups=await loader.LoadAsync("https://example.com/config",TestContext.Current.CancellationToken);
        Assert.Equal("ChannelUA",groups[0].Channels[0].Headers["User-Agent"]);Assert.Empty(groups[1].Channels[0].Headers);
    }

    [Fact]
    public void DetectsM3uFormat()
    {
        Assert.True(M3uParser.IsM3u("#EXTM3U\n#EXTINF:-1,x\nhttp://a"));
        Assert.False(M3uParser.IsM3u("分组,#genre#\n频道,http://a"));
    }
}
