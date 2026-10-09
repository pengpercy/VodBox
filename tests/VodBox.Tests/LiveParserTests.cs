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
    public void UnescapesHtmlEntitiesInStreamUrlAndLogo()
    {
        // 网页导出的 m3u 会把 & 写成 &amp;；未还原会让带查询串的播放地址失效。
        const string m3u = """
        #EXTM3U
        #EXTINF:-1 tvg-logo="https://logo/a.png?a=1&amp;b=2" group-title="组",频道A
        http://host:9901/live/a.m3u8?key=txiptv&amp;playlive=1&amp;authid=0
        """;
        var channel = Assert.Single(M3uParser.ParseGroups(m3u)[0].Channels);
        Assert.Equal("http://host:9901/live/a.m3u8?key=txiptv&playlive=1&authid=0", channel.Uris[0]);
        Assert.Equal("https://logo/a.png?a=1&b=2", channel.Logo);
    }

    [Fact]
    public void ExtGrpDirectiveGroupsItsOwnChannelOnly()
    {
        // #EXTGRP 是频道级指令（与 #EXTVLCOPT 同级）：只作用于紧随其后的那个频道，
        // 不声明的频道仍归入“未分组”。
        const string m3u = """
        #EXTM3U
        #EXTINF:-1,频道A
        #EXTGRP:央视
        http://stream.example.com/a
        #EXTINF:-1,频道B
        http://stream.example.com/b
        """;
        var groups = M3uParser.ParseGroups(m3u);
        var grouped = Assert.Single(groups, g => g.Name == "央视");
        Assert.Equal("频道A", Assert.Single(grouped.Channels).Name);
        Assert.Equal("央视", grouped.Channels[0].Group);
        var ungrouped = Assert.Single(groups, g => g.Name == "未分组");
        Assert.Equal("频道B", Assert.Single(ungrouped.Channels).Name);
    }

    [Fact]
    public void MissingLogoAttributeLeavesChannelWithoutLogo()
    {
        const string m3u = """
        #EXTM3U
        #EXTINF:-1 group-title="组",无台标频道
        http://stream.example.com/a
        """;
        Assert.Null(Assert.Single(M3uParser.ParseGroups(m3u)[0].Channels).Logo);
    }

    [Theory]
    [InlineData("a.m3u", LocalFileKind.Playlist)]
    [InlineData("a.M3U", LocalFileKind.Playlist)]
    [InlineData("a.m3u8", LocalFileKind.Playlist)]
    [InlineData("a.mp4", LocalFileKind.Media)]
    [InlineData("a.MKV", LocalFileKind.Media)]
    [InlineData("a.txt", LocalFileKind.Other)]
    [InlineData("a", LocalFileKind.Other)]
    [InlineData("", LocalFileKind.Other)]
    public void ClassifiesLocalFilesByExtension(string path, LocalFileKind expected) =>
        Assert.Equal(expected, LocalMedia.Classify(path));

    [Fact]
    public void ResolvesLocalPathsAndFileUrisButRejectsRemote()
    {
        var file = Path.Combine(Path.GetTempPath(), $"vodbox-m3u-{Guid.NewGuid():N}.m3u");
        File.WriteAllText(file, "#EXTM3U");
        try
        {
            Assert.True(LocalMedia.TryResolveFile(file, out var plain));
            Assert.Equal(Path.GetFullPath(file), plain);
            Assert.True(LocalMedia.TryResolveFile(new Uri(file).AbsoluteUri, out var fromUri));
            Assert.Equal(Path.GetFullPath(file), fromUri);
            Assert.False(LocalMedia.TryResolveFile("http://host/list.m3u", out _));
            Assert.False(LocalMedia.TryResolveFile("https://host/list.m3u", out _));
            Assert.False(LocalMedia.TryResolveFile(Path.Combine(Path.GetTempPath(), "missing.m3u"), out _));
            Assert.False(LocalMedia.TryResolveFile("", out _));
            Assert.False(LocalMedia.TryResolveFile(null, out _));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task LoadsChannelListFromLocalPlaylistFile()
    {
        var file = Path.Combine(Path.GetTempPath(), $"vodbox-live-{Guid.NewGuid():N}.m3u");
        await File.WriteAllTextAsync(file, """
        #EXTM3U
        #EXTINF:-1 tvg-logo="https://logo/cctv1.png" group-title="央视",CCTV-1 综合
        http://host:9901/live/0001.m3u8?key=txiptv&amp;playlive=1
        #EXTINF:-1 group-title="卫视",湖南卫视
        http://host:9901/live/0022.m3u8?key=txiptv&amp;playlive=1
        """, TestContext.Current.CancellationToken);
        try
        {
            // 本地播放列表不经 HTTP：加载器必须直接读盘。
            var loader = new LiveSources((_, _) => throw new InvalidOperationException("本地播放列表不应发起网络请求。"));
            var groups = await loader.LoadAsync(file, TestContext.Current.CancellationToken);
            Assert.Equal(2, groups.Count);
            Assert.Equal("央视", groups[0].Name);
            Assert.Equal("https://logo/cctv1.png", groups[0].Channels[0].Logo);
            Assert.Equal("http://host:9901/live/0001.m3u8?key=txiptv&playlive=1", groups[0].Channels[0].Uris[0]);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task LocalPlaylistInsideJsonConfigIsReadFromDisk()
    {
        var file = Path.Combine(Path.GetTempPath(), $"vodbox-live-conf-{Guid.NewGuid():N}.m3u");
        await File.WriteAllTextAsync(file, "#EXTM3U\n#EXTINF:-1,频道A\nhttp://host/a.m3u8\n",
            TestContext.Current.CancellationToken);
        // 手写 JSON：JsonSerializer 在裁剪/AOT 下需要源生成，测试里没必要引入。
        var json = "{\"lives\":[{\"name\":\"本地\",\"type\":0,\"url\":\""
            + file.Replace("\\", "/") + "\"}]}";
        try
        {
            var loader = new LiveSources((_, _) => Task.FromResult(json));
            var groups = await loader.LoadAsync("http://config.example.com/live.json",
                TestContext.Current.CancellationToken);
            var group = Assert.Single(groups);
            Assert.Equal("频道A", Assert.Single(group.Channels).Name);
            Assert.Empty(loader.Warnings);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void RealChannelListFixtureParsesWithGroupAndLogos()
    {
        // 真实场景样例（河北保定酒店源，52 个频道）：文件名与频道名都含中文，
        // 图标地址来自 taksssss/tv 的 iconList_default.json。
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "channels-sample.m3u");
        var groups = M3uParser.ParseGroups(File.ReadAllText(path));
        var group = Assert.Single(groups);
        Assert.Equal("河北保定酒店 河北联通", group.Name);
        Assert.Equal(52, group.Channels.Count);
        Assert.False(group.Locked);
        // 台标齐全（清单里没有的 4 个本地台已移除坏链，走文字占位而不是 404 图片）。
        var withLogo = group.Channels.Where(c => !string.IsNullOrWhiteSpace(c.Logo)).ToArray();
        Assert.Equal(48, withLogo.Length);
        Assert.All(withLogo, c => Assert.DoesNotContain("/icon/.png", c.Logo));
        Assert.All(withLogo, c => Assert.StartsWith("https://", c.Logo));
        Assert.Equal(4, group.Channels.Count(c => string.IsNullOrWhiteSpace(c.Logo)));
        // 归一化匹配：CCTV-1综合 → CCTV1.png，CCTV5+体育赛事 → CCTV5+.png。
        Assert.Equal("https://gcore.jsdelivr.net/gh/taksssss/tv/icon/CCTV1.png",
            group.Channels.Single(c => c.Name == "CCTV-1综合").Logo);
        Assert.Equal("https://gcore.jsdelivr.net/gh/taksssss/tv/icon/CCTV5+.png",
            group.Channels.Single(c => c.Name == "CCTV5+体育赛事").Logo);
        Assert.Equal("https://gcore.jsdelivr.net/gh/taksssss/tv/icon/湖南卫视.png",
            group.Channels.Single(c => c.Name == "湖南卫视").Logo);
        // 地址里的 &amp; 必须还原，否则播放请求会落到错误路径。
        Assert.All(group.Channels, c => Assert.DoesNotContain("&amp;", c.Uris[0]));
        Assert.DoesNotContain("&amp;", group.Channels[0].Uris[0]);
        // 每个频道都有文字台标兜底，且编号连续。
        Assert.All(group.Channels, c => Assert.False(string.IsNullOrWhiteSpace(c.Badge)));
        Assert.Equal(Enumerable.Range(1, 52), group.Channels.Select(c => c.Number));
    }

    [Fact]
    public void ChannelBadgeFallsBackToNameNotGenericPlaceholder()
    {
        var groups = M3uParser.ParseGroups("#EXTM3U\n#EXTINF:-1,湖南卫视\nhttp://h/a\n");
        var channel = groups[0].Channels[0];
        Assert.Equal("湖南", channel.Badge);
        Assert.NotEqual("TV", channel.Badge);
        // 同一名字永远得到同一种颜色（列表重绘不应变色）。
        Assert.Equal(channel.BadgeColor, M3uParser.BadgeColorFor("湖南卫视"));
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
