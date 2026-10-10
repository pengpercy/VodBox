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
        var player = main.Player;
        player.Title = "庆余年 第二季";
        player.Subtitle = "第12集 · 抱月楼风波";
        player.ShowToast = true;
        player.ToastText = "已开启 1.5x 倍速 · 片头跳过 00:00—01:30";
        player.Visible = true;
        player.State = PlaybackState.Paused;
        player.Duration = TimeSpan.FromMinutes(45);
        player.Position = TimeSpan.FromMinutes(12).Add(TimeSpan.FromSeconds(34));
        player.Volume = 80;
        player.Rate = 1.0;
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
        // 分块行（6 张/行，虚拟化网格数据源）
        for (var i = 0; i < main.Vod.Items.Count; i += 6)
            main.Vod.Rows.Add(new MediaRow(main.Vod.Items.Skip(i).Take(6).ToList()));

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
        // 对齐设计稿 p-live：18 央视 + 24 卫视示例量级取前几个；台标短字 + 彩色底；EPG 单行。
        (string name, string badge, string color, string epg, int no)[] cctv =
        [
            ("CCTV-1 综合", "央1", "#C0392B", "正在播出：新闻联播重播", 1),
            ("CCTV-2 财经", "央2", "#8E44AD", "正在播出：经济半小时", 2),
            ("CCTV-3 综艺", "央3", "#2471A3", "正在播出：开门大吉", 3),
            ("CCTV-4 中文国际", "央4", "#148F77", "正在播出：今日亚洲", 4),
            ("CCTV-5 体育", "央5", "#D68910", "正在播出：体育新闻", 5),
            ("CCTV-6 电影", "央6", "#7D3C98", "正在播出：流金岁月", 6),
        ];
        (string name, string badge, string color, string epg, int no)[] satellite =
        [
            ("湖南卫视", "芒果", "#27AE60", "正在播出：歌手2024", 101),
            ("东方卫视", "番茄", "#E67E22", "正在播出：今晚80后", 102),
            ("浙江卫视", "蓝莓", "#2E86C1", "正在播出：奔跑吧", 103),
            ("江苏卫视", "荔枝", "#CB4335", "正在播出：非诚勿扰", 104),
        ];
        var all = new List<LiveChannel>();
        foreach (var c in cctv)
            all.Add(new LiveChannel { Name = c.name, Group = "央视频道", Number = c.no, Badge = c.badge, BadgeColor = c.color, EpgNow = c.epg, EpgNext = "黄金档剧场 20:06", Uris = [$"http://iptv.example.com/c{c.no}.m3u8", "http://iptv2.example.com/backup.m3u8"] });
        foreach (var c in satellite)
            all.Add(new LiveChannel { Name = c.name, Group = "卫视频道", Number = c.no, Badge = c.badge, BadgeColor = c.color, EpgNow = c.epg, EpgNext = "晚间剧场 21:40", Uris = [$"http://iptv.example.com/s{c.no}.m3u8"] });
        main.Live.Groups.Add(new LiveGroup("央视频道", all.Where(c => c.Group == "央视频道").ToList(), false));
        main.Live.Groups.Add(new LiveGroup("卫视频道", all.Where(c => c.Group == "卫视频道").ToList(), false));
        main.Live.Groups.Add(new LiveGroup("影视频道（密码分组）", [], true, DisplayCount: 36));
        main.Live.Groups.Add(new LiveGroup("地方频道", [], false, DisplayCount: 58));
        main.Live.SelectedGroup = main.Live.Groups[0];
        // 选中 CCTV-1 并点亮 LIVE 徽标 + EPG 时间轴
        var first = main.Live.Groups[0].Channels[0];
        main.Live.SetCurrent(first); // 仅预览填充示例节目，运行时不生成假 EPG。
        var now = DateTimeOffset.Now;
        main.Live.EpgTimeline.Add(new LiveEpgCard { Title = "新闻联播", Start = now.AddMinutes(-45), End = now.AddMinutes(-15), IsPast = true });
        main.Live.EpgTimeline.Add(new LiveEpgCard { Title = "焦点访谈", Start = now.AddMinutes(-15), End = now.AddMinutes(15), IsNow = true });
        main.Live.EpgTimeline.Add(new LiveEpgCard { Title = "黄金档剧场", Start = now.AddMinutes(15), End = now.AddMinutes(105) });
    }

    private static void FillSearch(MainViewModel main)
    {
        main.Search.Keyword = "庆余年";
        main.Search.Summary = "「庆余年」在 8 个站点找到结果";
        // 站点列表：数量/加载中/0 三态（对齐设计稿 ②）
        var sites = new (string name, string key, int count, bool loading)[]
        {
            ("泥巴影视", "niba", 12, false),
            ("金牌影院", "jinpa", 9, false),
            ("量子影视", "lzi", 7, false),
            ("天堂影视", "tiantang", 5, false),
            ("卧龙资源", "wolong", 0, true),
            ("天天视频", "tiantian", 0, false),
        };
        foreach (var s in sites)
        {
            var site = new SearchSiteResult(s.name, s.key) { Loading = s.loading };
            foreach (var item in Items(Math.Min(s.count, 6))) site.Items.Add(item);
            main.Search.SiteResults.Add(site);
        }
        foreach (var item in Items(10)) main.Search.AllResults.Add(item);
        // 分块行（5 张/行）
        for (var i = 0; i < main.Search.AllResults.Count; i += 5)
            main.Search.ResultRows.Add(new ResultRow(main.Search.AllResults.Skip(i).Take(5).ToList()));
        // 联想列表（热词带 🔥）
        foreach (var (text, hot) in new[] { ("庆余年 第二季", true), ("庆余年 第一季", false), ("庆余年 动画版", false), ("庆余年 有声书", false) })
            main.Search.Suggestions.Add(new SuggestItem { Text = text, Hot = hot });
        main.Search.ShowSuggestions = true;
        foreach (var word in new[] { "庆余年", "繁花", "歌手" })
            main.Search.SearchHistory.Add(word);
    }

    private static void FillFavorites(MainViewModel main)
    {
        // 对齐设计稿 p-keep：多站点 + 时间梯度（3 天前 … 1 个月前）+ 直播频道 Tab 数据
        var sources = new[] { ("niba", "泥巴影视"), ("jinpa", "金牌影院"), ("niba", "泥巴影视"), ("lzi", "量子影视"), ("niba", "泥巴影视"), ("tiantang", "天堂影视") };
        var days = new[] { 3, 8, 10, 16, 22, 32 };
        var items = Items(6);
        for (var i = 0; i < items.Count; i++)
        {
            main.Favorites.Vod.Add(new FavoriteEntry
            {
                Kind = FavoriteKind.Vod,
                SourceKey = sources[i].Item1, SourceName = sources[i].Item2,
                MediaId = items[i].Id, Title = items[i].Title, Poster = items[i].Poster,
                Remarks = items[i].Remarks,
                CreatedAt = DateTimeOffset.Now.AddDays(-days[i]),
            });
        }
        main.Favorites.Live.Add(new FavoriteEntry
        {
            Kind = FavoriteKind.Live,
            SourceKey = "live", SourceName = "直播",
            MediaId = "cctv1", Title = "CCTV-1 综合", Remarks = "常看",
            CreatedAt = DateTimeOffset.Now.AddDays(-1),
        });
        main.Favorites.RebuildVodRows();
    }

    private static void FillHistory(MainViewModel main)
    {
        // 对齐设计稿 p-keep：历史 · 今天 = 3 张卡（点播×2 + 直播 CCTV-1），更早 = 3 条
        var today = new (string title, string remarks, string source, long pos, long dur)[]
        {
            ("庆余年 第二季", "第12集 · 23:41", "泥巴影视", 1421_000, 45 * 60_000),
            ("沙丘2", "45:30", "泥巴影视", 2730_000, 155 * 60_000),
            ("CCTV-1 综合", "直播 · 看了 32 分钟", "直播", 32 * 60_000, 0),
        };
        foreach (var h in today)
            main.History.Entries.Add(new HistoryEntry
            {
                SourceKey = h.source == "直播" ? "live" : "niba",
                SourceName = h.source,
                MediaId = $"today-{h.title}",
                Title = h.title,
                Remarks = h.remarks,
                PositionMs = h.pos,
                DurationMs = h.dur,
                UpdatedAt = DateTimeOffset.Now.AddMinutes(-30),
            });
        foreach (var entry in History(3))
            main.History.Entries.Add(entry);
        main.History.SplitHistory();
    }

    private static void FillSettings(MainViewModel main)
    {
        main.Settings.VodConfigUrl = "https://example.com/tvbox.json";
        main.Settings.LiveConfigUrl = "https://example.com/live.m3u";
        main.Settings.Sites.Add(new SourceInfo { Key = "lzi", Name = "量子资源", Runtime = SourceRuntime.MacCms, Api = "https://cj.lziapi.com/api.php/provide/vod", Type = 0 });
        main.Settings.Sites.Add(new SourceInfo { Key = "fty", Name = "饭太硬", Runtime = SourceRuntime.QuickJs, Api = "https://example.com/spider.js", Type = 3 });
        main.Settings.Message = "设计时预览数据";
        // 配置弹窗演示：当前使用中 + 可切换/删除历史（对齐设计稿 ②）
        main.Settings.ConfigDialogOpen = true;
        main.Settings.DialogUrl = "https://mirror.ghproxy.com/.../tvbox.json";
        main.Settings.ConfigHistory.Add(new ConfigHistoryEntry { Name = "tvbox.json（当前）", Url = "https://example.com/tvbox.json", Current = true });
        main.Settings.ConfigHistory.Add(new ConfigHistoryEntry { Name = "https://.../fm.json", Url = "https://example.com/fm.json" });
        main.Settings.ConfigHistory.Add(new ConfigHistoryEntry { Name = "clan://localhost/.../duo.json", Url = "clan://localhost/.../duo.json" });
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
        UpdatedAt = DateTimeOffset.Now.AddDays(-1).AddHours(-i),
    });
}
