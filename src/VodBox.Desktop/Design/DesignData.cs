using VodBox.Core;
using VodBox.Desktop.Services;
using VodBox.Desktop.ViewModels;

namespace VodBox.Desktop.Design;

/// <summary>
/// XAML 设计时预览数据（Design.DataContext 用）。
/// 纯内存构造，不触网、不访问 SQLite / libmpv / 配置、不枚举文件系统、不启动计时器——保证设计器秒开。
/// </summary>
public static class DesignData
{
    /// <summary>共享单例：每个视图都绑定 Main 的同一子 VM；FillX 只在初始化时执行一次，填充后不再变更。</summary>
    public static MainViewModel Main { get; } = CreateMain();

    /// <summary>设计器专用入口：以 object 类型公开同一实例，匹配 Design.DataContext 附加属性。</summary>
    public static object? MainForDesign => Main;

    private static MainViewModel CreateMain()
    {
        // 设计时安全路径：空 MpvEngine（永不 P/Invoke）、临时目录 SQLite、Files 停在空根目录。
        var main = new MainViewModel(AppServices.CreateDesignTime(), designTime: true);
        FillPlayer(main);
        FillHome(main);
        FillVod(main);
        FillDetail(main);
        FillLive(main);
        FillSearch(main);
        FillFavorites(main);
        FillHistory(main);
        FillSettings(main);
        return main;
    }

    private static void FillPlayer(MainViewModel main)
    {
        // Player 在设计时构造之后再赋值：保证 Detail/播放相关 XAML 能绑定到非空 VM。
        var player = new PlayerViewModel(AppServices.CreateDesignTime(), main);
        player.Title = "庆余年 第二季 · 第03集";
        player.Visible = true;
        player.State = PlaybackState.Paused;
        player.Duration = TimeSpan.FromMinutes(45);
        player.Position = TimeSpan.FromMinutes(12).Add(TimeSpan.FromSeconds(34));
        player.Volume = 80;
        player.Rate = 1.0;
        main.Player = player;
    }

    private static void FillHome(MainViewModel main)
    {
        main.Home.HeroTitle = "庆余年 第二季";
        main.Home.HeroRemarks = "更新至 36 集 · 豆瓣 8.2";
        main.Home.HeroDescription = "2024 · 大陆 · 古装 / 权谋";
        foreach (var entry in History(3))
            main.Home.Recent.Add(entry);
        foreach (var item in Items(12))
            main.Home.Recommendations.Add(item);
    }

    private static void FillVod(MainViewModel main)
    {
        foreach (var (id, name) in new[] { ("1", "全部"), ("2", "电影"), ("6", "连续剧"), ("3", "综艺"), ("4", "动漫") })
            main.Vod.Categories.Add(new Category(id, name));
        main.Vod.SelectedCategory = main.Vod.Categories[0];
        foreach (var item in Items(12))
            main.Vod.Items.Add(item);
        main.Vod.Page = 1;
        main.Vod.PageCount = 99;
    }

    private static void FillDetail(MainViewModel main)
    {
        var episodes = Enumerable.Range(1, 12)
            .Select(i => new Episode($"ep{i}", $"第{i:D2}集", $"https://cdn.example.com/s{i}.m3u8"))
            .ToList();
        main.Detail.Detail = new MediaDetail
        {
            Item = Items(1)[0],
            Description = "该剧改编自猫腻同名畅销小说。范闲率领使团北齐归来后，依然身处京都尔虞我诈的棋局之中……",
            Director = "孙皓",
            Actor = "张若昀 / 李沁 / 陈道明 / 吴刚",
            Lines =
            [
                new PlaybackLine("lzm3u8", "量子专线", episodes),
                new PlaybackLine("yunque", "云雀备用", episodes),
            ],
        };
        foreach (var line in main.Detail.Detail.Lines) main.Detail.Lines.Add(line);
        main.Detail.SelectedLine = main.Detail.Lines[0];
        main.Detail.IsFavorite = true;
    }

    private static void FillLive(MainViewModel main)
    {
        var channels = new List<LiveChannel>
        {
            new() { Name = "CCTV-1 综合", Group = "央视频道", Number = 1, Logo = "https://live.fanmingming.cn/tv/CCTV1.png", Uris = ["http://iptv.example.com/cctv1.m3u8"] },
            new() { Name = "CCTV-2 财经", Group = "央视频道", Number = 2, Logo = "https://live.fanmingming.cn/tv/CCTV2.png", Uris = ["http://iptv.example.com/cctv2.m3u8"] },
            new() { Name = "CCTV-3 综艺", Group = "央视频道", Number = 3, Logo = "https://live.fanmingming.cn/tv/CCTV3.png", Uris = ["http://iptv.example.com/cctv3.m3u8"] },
            new() { Name = "湖南卫视", Group = "卫视频道", Number = 101, Uris = ["http://iptv.example.com/hntv.m3u8"] },
            new() { Name = "东方卫视", Group = "卫视频道", Number = 102, Uris = ["http://iptv.example.com/dftv.m3u8"] },
        };
        main.Live.Groups.Add(new LiveGroup("央视频道", channels.Where(c => c.Group == "央视频道").ToList(), false));
        main.Live.Groups.Add(new LiveGroup("卫视频道", channels.Where(c => c.Group == "卫视频道").ToList(), false));
        main.Live.SelectedGroup = main.Live.Groups[0];
        main.Live.CurrentChannel = main.Live.Groups[0].Channels[0];
    }

    private static void FillSearch(MainViewModel main)
    {
        main.Search.Keyword = "庆余年";
        main.Search.Summary = "「庆余年」在 3 个站点找到 12 条结果";
        var site = new SearchSiteResult("量子资源", "lzi");
        foreach (var item in Items(6)) site.Items.Add(item);
        main.Search.SiteResults.Add(site);
        foreach (var item in Items(10)) main.Search.AllResults.Add(item);
    }

    private static void FillFavorites(MainViewModel main)
    {
        foreach (var item in Items(6))
            main.Favorites.Vod.Add(new FavoriteEntry
            {
                Kind = FavoriteKind.Vod,
                SourceKey = "lzi", SourceName = "量子资源",
                MediaId = item.Id, Title = item.Title, Poster = item.Poster, Remarks = item.Remarks,
            });
    }

    private static void FillHistory(MainViewModel main)
    {
        foreach (var entry in History(6))
            main.History.Entries.Add(entry);
    }

    private static void FillSettings(MainViewModel main)
    {
        main.Settings.VodConfigUrl = "https://example.com/tvbox.json";
        main.Settings.LiveConfigUrl = "https://example.com/live.m3u";
        main.Settings.Sites.Add(new SourceInfo { Key = "lzi", Name = "量子资源", Runtime = SourceRuntime.MacCms, Api = "https://cj.lziapi.com/api.php/provide/vod", Type = 0 });
        main.Settings.Sites.Add(new SourceInfo { Key = "fty", Name = "饭太硬", Runtime = SourceRuntime.QuickJs, Api = "https://example.com/spider.js", Type = 3 });
        main.Settings.Message = "设计时预览数据";
    }

    private static List<MediaItem> Items(int count)
    {
        string[] titles = ["庆余年 第二季", "狐妖小红娘", "九龙城寨之围城", "沙丘2", "与凤行", "承欢记", "哈尔滨一九四四", "繁花", "歌手2024", "凡人修仙传", "我的后半生", "成家"];
        string[] remarks = ["更至36集", "完结", "HD", "4K", "综艺", "动漫"];
        string[] areas = ["大陆", "香港", "美国", "大陆", "大陆", "大陆"];
        return [.. Enumerable.Range(0, count).Select(i => new MediaItem
        {
            Id = $"vod{i + 1}",
            Title = titles[i % titles.Length],
            Poster = null,
            Remarks = remarks[i % remarks.Length],
            Year = "2024",
            Area = areas[i % areas.Length],
            TypeName = "古装",
        })];
    }

    private static IEnumerable<HistoryEntry> History(int count) => Enumerable.Range(0, count).Select(i => new HistoryEntry
    {
        SourceKey = "lzi",
        SourceName = "量子资源",
        MediaId = $"vod{i + 1}",
        Title = new[] { "庆余年 第二季", "沙丘2", "歌手2024", "与凤行", "繁花", "凡人修仙传" }[i % 6],
        Poster = null,
        Remarks = $"看到 第{i + 1}集",
        LineId = "lzm3u8",
        EpisodeId = $"ep{i + 1}",
        PositionMs = (i + 1) * 5 * 60_000 + 41_000,
        DurationMs = 45 * 60_000,
        UpdatedAt = DateTimeOffset.Now.AddHours(-i),
    });
}
