using Avalonia.Controls;
using VodBox.Desktop.ViewModels;
using VodBox.Desktop.Views;

namespace VodBox.Desktop.Services;

/// <summary>
/// One update window and one update session at a time; automatic offers are deduplicated only within this session.
/// 会话可脱离窗口存在（退出时安装 / 自动下载），应用退出时由 <see cref="CloseAsync"/> 决定安装还是清理。
/// </summary>
public sealed class UpdateWindowService
{
    private UpdateWindow? _currentWindow;
    private UpdateViewModel? _session;
    private readonly HashSet<string> _automaticOffers = new(StringComparer.Ordinal);

    public bool ActivateExisting()
    {
        if (_currentWindow is not { } window) return false;
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
        return true;
    }

    public void Show(UpdateOffer offer, string currentVersion, Window owner, bool automatic, Action<string> report, IUpdatePreferences? preferences = null)
    {
        if (_currentWindow is not null)
        {
            if (!automatic) ActivateExisting();
            return;
        }
        if (automatic && _automaticOffers.Contains(offer.Version)) return;
        var window = new UpdateWindow(Session(offer, currentVersion, report, preferences));
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_currentWindow, window)) _currentWindow = null;
        };
        _currentWindow = window;
        try
        {
            window.Show(owner);
            _automaticOffers.Add(offer.Version);
        }
        catch { _currentWindow = null; throw; }
    }

    /// <summary>自动更新：不弹窗，后台下载并安排退出时安装。已有进行中的会话时不重复开始。</summary>
    public bool StartBackground(UpdateOffer offer, string currentVersion, Action<string> report, IUpdatePreferences preferences)
    {
        if (_currentWindow is not null || _session is { IsFinished: false } || _automaticOffers.Contains(offer.Version)) return false;
        var session = Session(offer, currentVersion, report, preferences);
        if (!session.CanInstallOnQuit) return false;
        session.ScheduleInstallOnQuit();
        _automaticOffers.Add(offer.Version);
        return true;
    }

    /// <summary>同一版本复用进行中的会话（窗口重开能看到后台下载进度）；更新的版本替换旧会话。</summary>
    private UpdateViewModel Session(UpdateOffer offer, string currentVersion, Action<string> report, IUpdatePreferences? preferences)
    {
        if (_session is { IsFinished: false } existing)
        {
            if (existing.NewVersion == offer.Version) return existing;
            _ = existing.CloseAsync();
        }
        return _session = new UpdateViewModel(offer, currentVersion, report, preferences);
    }

    /// <summary>应用退出：收起窗口；选择了退出时安装且包已就绪则启动更新器，否则取消并清理。</summary>
    public async Task CloseAsync()
    {
        _currentWindow?.CloseForShutdown();
        if (_session is { } session) await session.ShutdownAsync();
    }
}
