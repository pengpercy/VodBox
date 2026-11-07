using VodBox.Core;
using VodBox.Infrastructure;
using VodBox.Playback.Mpv;

namespace VodBox.Desktop.Services;

/// <summary>应用服务组合根（AOT 安全：手写组合，无反射容器）。</summary>
public sealed class AppServices : IDisposable
{
    private int _disposed;
    public string DataDir { get; }
    public LibraryStore Store { get; }
    /// <summary>源配置（采集源 / 自定义分类 / 直播源 / 站点设置 / 订阅），LunaTV AdminConfig 的桌面版。</summary>
    public SourceConfigStore SourceConfig { get; }
    /// <summary>点播目录：采集客户端 + 聚合搜索 + 换源 + 源检测。</summary>
    public VodCatalog Catalog { get; }
    public DoubanClient Douban { get; }
    public BangumiClient Bangumi { get; }
    /// <summary>豆瓣浏览（首页/点播页分类数据源）。</summary>
    public DoubanBrowseSource DoubanBrowse { get; }
    public SourceSpeedTester SpeedTester { get; }
    public ConfigSubscriptionService Subscription { get; }
    public MpvEngine Player { get; private set; }
    public IPreferences Prefs => Store;
    public LocalMediaServer MediaServer { get; }

    /// <summary>默认数据目录走平台规范（XDG / macOS ~/Library / Windows LocalApplicationData）。</summary>
    public AppServices() : this(AppPaths.DataDirectory)
    {
    }

    public AppServices(string overrideDataDir)
    {
        DataDir = overrideDataDir;
        Directory.CreateDirectory(DataDir);
        MediaServer=new LocalMediaServer(Path.Combine(DataDir,"media-cache"));
        Store = new LibraryStore(Path.Combine(DataDir, "library.db"));
        // 播放源解析参照 LunaTV：订阅 config.json（api_site 苹果 CMS 采集源）+ 豆瓣数据浏览 + 聚合搜索换源。
        SourceConfig = new SourceConfigStore(Path.Combine(DataDir, "source-config.json"));
        Catalog = new VodCatalog(SourceConfig);
        Douban = new DoubanClient(() => SourceConfig.Settings);
        Bangumi = new BangumiClient();
        DoubanBrowse = new DoubanBrowseSource(Douban, Bangumi, () => SourceConfig.EnabledCategories);
        SpeedTester = new SourceSpeedTester();
        Subscription = new ConfigSubscriptionService(SourceConfig);
        // GUI 模式 + 等渲染面：loadfile 不早于 render-context 创建，消除静默丢画面竞态。
        Player = new MpvEngine(waitForVideoSurface: true);
    }

    /// <summary>设计时服务组合根：空引擎（永不加载 libmpv）、不触碰文件系统、不启动计时器。属性集在假数据填充之前完成。</summary>
    public static AppServices CreateDesignTime()
    {
        var services = new AppServices(DesignTimeDataDir());
        services.Player = MpvEngine.DesignDisabled();
        return services;
    }

    /// <summary>设计时数据目录：优先进程内临时目录（预览器可写），不可用时退回 OS 临时目录。</summary>
    private static string DesignTimeDataDir()
    {
        try
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "vodbox-design-preview");
            Directory.CreateDirectory(dir);
            return dir;
        }
        catch (Exception)
        {
            return Path.Combine(Path.GetTempPath(), "vodbox-design-preview");
        }
    }

    public string? CurrentLiveConfig
    {
        get => Prefs.GetString("config_live");
        set => Prefs.Set("config_live", value ?? "");
    }

    public void Dispose()
    {
        if(Interlocked.Exchange(ref _disposed,1)!=0)return;
        MediaServer.DisposeAsync().AsTask().GetAwaiter().GetResult();
        MediaServer.Proxy.Dispose();
        Catalog.Dispose();
        Douban.Dispose();
        Bangumi.Dispose();
        SpeedTester.Dispose();
        Subscription.Dispose();
        Player.DisposeAsync().AsTask().GetAwaiter().GetResult();
        Store.Dispose();
    }
}
