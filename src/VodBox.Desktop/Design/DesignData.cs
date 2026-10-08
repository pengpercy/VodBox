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
        // 英雄区（对齐设计稿 p-home：剧情简介两行截断，运行时为元信息，S5 接真简介）
        main.Home.HeroTitle = "庆余年 第二季";
        main.Home.HeroRemarks = "更新至 36 集 · 豆瓣 8.2";
        main.Home.HeroDescription = "身世神秘的少年范闲，历经家族、江湖、庙堂的种种考验与锤炼，书写出一段人生传奇。张若昀、李沁领衔主演。";

        // 最近观看：6 张多样化卡（看剧中/已看完/直播回放，进度各异）——对齐设计稿
        var recent = new (string title, string remarks, long pos, long dur)[]
        {
            ("庆余年 第二季", "看到 第12集 · 23:41", 1281_000, 45 * 60_000),
            ("狐妖小红娘月红篇", "看到 第3集 · 08:12", 492_000, 45 * 60_000),
            ("九龙城寨之围城", "已看完", 45 * 60_000, 45 * 60_000),
            ("歌手 2024 直播回放", "看到 45:30", 2730_000, 90 * 60_000),
            ("与凤行", "看到 第8集", 2100_000, 45 * 60_000),
            ("承欢记", "看到 第1集", 300_000, 45 * 60_000),
        };
        for (var i = 0; i < recent.Length; i++)
        {
            var r = recent[i];
            main.Home.Recent.Add(new HistoryEntry
            {
                SourceKey = "lzi",
                SourceName = "量子资源",
                MediaId = $"recent{i + 1}",
                Title = r.title,
                Poster = null,
                Remarks = r.remarks,
                LineId = "lzm3u8",
                EpisodeId = $"ep{i + 1}",
                PositionMs = r.pos,
                DurationMs = r.dur,
                UpdatedAt = DateTimeOffset.Now.AddHours(-i),
            });
        }

        // 站点推荐：12 张（两行 × 6），徽标覆盖 更至/HD/完结/综艺/4K/动漫
        var recommendations = new (string title, string remarks, string year, string area)[]
        {
            ("庆余年 第二季", "更至36集", "2024", "大陆"),
            ("沙丘2", "HD", "2024", "欧美"),
            ("狐妖小红娘月红篇", "更至14集", "2024", "大陆"),
            ("九龙城寨之围城", "完结", "2024", "香港"),
            ("歌手 2024", "综艺", "2024", "大陆"),
            ("流浪地球3", "4K", "2027", "大陆"),
            ("凡人修仙传", "更至128集", "2024", "大陆"),
            ("繁花", "完结", "2023", "大陆"),
            ("与凤行", "更至36集", "2024", "大陆"),
            ("火星救援", "HD", "2015", "欧美"),
            ("中国奇谭", "动漫", "2023", "大陆"),
            ("周处除三害", "4K", "2024", "台湾"),
        };
        foreach (var r in recommendations)
            main.Home.Recommendations.Add(new MediaItem
            {
                Id = $"rec{main.Home.Recommendations.Count + 1}",
                Title = r.title,
                Poster = null,
                Remarks = r.remarks,
                Year = r.year,
                Area = r.area,
                TypeName = "剧集",
            });
    }

    private static void FillVod(MainViewModel main)
    {
        // 分类（对齐设计稿 p-vod：10 个分类横向滚动）
        foreach (var (id, name) in new[]
        {
            ("1", "全部"), ("2", "电影"), ("6", "电视剧"), ("3", "综艺"), ("4", "动漫"),
            ("5", "纪录片"), ("7", "短剧"), ("8", "少儿"), ("9", "美剧"), ("10", "韩剧"),
        })
            main.Vod.Categories.Add(new Category(id, name));
        main.Vod.SelectedCategory = main.Vod.Categories[0];

        // 12 张多样化卡片（徽标 + 年份/地区/类型各异——对齐设计稿）
        var items = new (string title, string remarks, string year, string area, string type)[]
        {
            ("庆余年 第二季", "更至36集", "2024", "大陆", "古装"),
            ("狐妖小红娘月红篇", "更至40集", "2024", "大陆", "奇幻"),
            ("九龙城寨之围城", "HD", "2024", "香港", "动作"),
            ("沙丘2", "4K", "2024", "美国", "科幻"),
            ("与凤行", "完结", "2024", "大陆", "仙侠"),
            ("承欢记", "更至28集", "2024", "大陆", "都市"),
            ("凡人修仙传", "更至20集", "2024", "大陆", "动漫"),
            ("繁花", "HD", "2023", "大陆", "剧情"),
            ("歌手 2024", "综艺", "2024", "大陆", "音乐"),
            ("我的后半生", "更至14集", "2024", "大陆", "都市"),
            ("中国奇谭", "动漫", "2023", "大陆", "动画"),
            ("周处除三害", "完结", "2024", "台湾", "动作"),
        };
        foreach (var it in items)
            main.Vod.Items.Add(new MediaItem
            {
                Id = $"vod{main.Vod.Items.Count + 1}",
                Title = it.title,
                Poster = null,
                Remarks = it.remarks,
                Year = it.year,
                Area = it.area,
                TypeName = it.type,
            });
        main.Vod.Page = 1;
        main.Vod.PageCount = 99;
    }

    private static void FillDetail(MainViewModel main)
    {
        // 36 集（设计稿：分段 1-20，倒序/分段控件为静态演示；DataTemplate 已带已看勾）
        var episodes = Enumerable.Range(1, 36)
            .Select(i => new Episode($"ep{i}", $"{i:D2}", $"https://cdn.example.com/s{i}.m3u8"))
            .ToList();
        main.Detail.Detail = new MediaDetail
        {
            Item = new MediaItem
            {
                Id = "vod1",
                Title = "庆余年 第二季",
                Poster = null,
                Remarks = "更新至 36 集 · 豆瓣 8.2",
                Year = "2024",
                Area = "中国大陆",
                TypeName = "古装 / 权谋",
            },
            Description = "该剧改编自猫腻同名畅销小说，承接上季，范闲率领使团回归途中，二皇子以费介、范思辙以及滕家遗孤的安危来威胁范闲，逼他向自己俯首称臣……",
            Director = "孙皓",
            Actor = "张若昀 / 李沁 / 陈道明 / 吴刚 / 郭麒麟",
            Lines =
            [
                new PlaybackLine("lzm3u8", "主线 ①", episodes),
                new PlaybackLine("yunque", "主线 ②", episodes),
                new PlaybackLine("bk4k", "备用 4K", episodes),
                new PlaybackLine("hw", "海外专线", episodes),
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
