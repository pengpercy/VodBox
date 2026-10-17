using Avalonia.Controls;
using VodBox.Desktop.ViewModels;
using VodBox.Desktop.Views;

namespace VodBox.Desktop.Services;

/// <summary>
/// 独立搜索结果窗口的生命周期管理。
/// <para>
/// 去重/激活：同一时刻只允许一扇搜索窗口——重复提交会复用并激活已有窗口，而不会再开一扇。
/// 因为窗口与主 VM 共享同一份 <see cref="MainViewModel.Search"/>，去重正是「避免多窗口共享状态互串」的关键。
/// </para>
/// <para>
/// 清理：窗口关闭（含主窗口关闭时的主动关闭）会取消在途搜索；配合 <see cref="SearchViewModel"/> 的代次门控，
/// 旧请求的结果绝不会写进之后的新请求。
/// </para>
/// </summary>
public sealed class SearchWindowService
{
    private readonly MainViewModel _main;
    private SearchWindow? _window;

    public SearchWindowService(MainViewModel main) => _main = main;

    /// <summary>当前已打开的搜索窗口（null = 未打开）。只读观察用，不会顺手创建一个。</summary>
    internal SearchWindow? Window => _window;

    /// <summary>是否已有一扇搜索窗口；重复提交靠它去重。</summary>
    public bool IsOpen => _window is not null;

    /// <summary>当前窗口（必要时先创建并显示，但不发起搜索）——UI smoke 用，验证窗口能渲染。</summary>
    internal SearchWindow? EnsureWindow()
    {
        if (_window is { } existing) return existing;
        var window = new SearchWindow { DataContext = _main };
        MacMenus.Attach(window, _main);
        _window = window;
        window.Closed += (_, _) => { if (ReferenceEquals(_window, window)) _window = null; };
        // 非模态、不设 Owner：与设置/播放窗口一致，主窗口仍可独立操作；主窗口关闭时由此服务主动关闭。
        window.Show();
        return window;
    }

    /// <summary>已开则还原最小化并激活，返回是否复用了已有窗口。</summary>
    internal bool ActivateExisting()
    {
        if (_window is not { } window) return false;
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        if (!window.IsActive) window.Activate();
        return true;
    }

    /// <summary>
    /// 提交一次搜索：窗口已存在则复用它（去重 + 激活）并在其中发起新请求；否则新建一扇独立窗口。
    /// 无论哪条路径，关键词都会先写进共享的 <c>Search</c>，再触发一次搜索（代次门控保证旧结果不回填）。
    /// </summary>
    /// <returns>true = 复用了已有窗口，false = 新建了窗口。</returns>
    public bool Submit(string keyword)
    {
        var query = keyword.Trim();
        if (query.Length == 0) return false;
        _main.Search.Keyword = query;
        var reused = ActivateExisting();
        EnsureWindow();
        _main.Search.RunSearchCommand.Execute(null);
        return reused;
    }

    /// <summary>主窗口关闭清理：关闭搜索窗口（其 Closed 回调会取消在途搜索）。</summary>
    public void Close()
    {
        var window = _window;
        _window = null;
        window?.Close();
    }
}
