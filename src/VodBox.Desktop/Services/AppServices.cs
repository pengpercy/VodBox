using VodBox.Core;
using VodBox.Infrastructure;
using VodBox.Playback.Mpv;

namespace VodBox.Desktop.Services;

/// <summary>应用服务组合根（AOT 安全：手写组合，无反射容器）。</summary>
public sealed class AppServices : IDisposable
{
    public string DataDir { get; }
    public LibraryStore Store { get; }
    public SourceRegistry Registry { get; }
    public MpvEngine Player { get; private set; }
    public IPreferences Prefs => Store;

    /// <summary>默认数据目录走平台规范（XDG / macOS ~/Library / Windows LocalApplicationData）。</summary>
    public AppServices() : this(AppPaths.DataDirectory)
    {
    }

    public AppServices(string overrideDataDir)
    {
        DataDir = overrideDataDir;
        Directory.CreateDirectory(DataDir);
        Store = new LibraryStore(Path.Combine(DataDir, "library.db"));
        Registry = new SourceRegistry();
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

    /// <summary>当前活跃点播配置 URL。</summary>
    public string? CurrentVodConfig
    {
        get => Prefs.GetString("config_vod");
        set => Prefs.Set("config_vod", value ?? "");
    }

    public string? CurrentLiveConfig
    {
        get => Prefs.GetString("config_live");
        set => Prefs.Set("config_live", value ?? "");
    }

    public void Dispose()
    {
        Registry.Dispose();
        Player.DisposeAsync().AsTask().GetAwaiter().GetResult();
        Store.Dispose();
    }
}
