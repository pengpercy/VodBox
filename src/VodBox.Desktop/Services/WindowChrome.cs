using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.VisualTree;
using VodBox.Infrastructure;

namespace VodBox.Desktop.Services;

/// <summary>
/// 自绘标题栏（Avalonia 12 的 WindowDrawnDecorations）在各平台上的统一修正。
/// </summary>
internal static class WindowChrome
{
    /// <summary>「使用自绘标题栏」偏好键（仅 Linux 有意义）。</summary>
    internal const string LinuxDrawnTitleBarKey = "ui.linux-drawn-titlebar";

    /// <summary>
    /// Linux 上是否由 Avalonia 自己画标题栏（否则由窗口管理器画）。跟随设置「使用自绘标题栏」（默认开），
    /// VODBOX_LINUX_CSD=0/1 可临时覆盖。X11 选项在 Avalonia 启动时就固定了，所以只读一次，改了要重启。
    /// </summary>
    public static bool UsesLinuxDrawnDecorations { get; } = DetectLinuxDrawnDecorations();

    /// <summary>
    /// 客户区右上角是否有自绘的最小化/最大化/关闭按钮：Windows 恒有；Linux 只在自绘标题栏时有
    /// （窗口管理器画标题栏时按钮在客户区外）；macOS 是左上角的交通灯。
    /// </summary>
    public static bool HasClientCaptionButtons { get; } =
        OperatingSystem.IsWindows() || (OperatingSystem.IsLinux() && UsesLinuxDrawnDecorations);

    private static bool DetectLinuxDrawnDecorations()
    {
        if (!OperatingSystem.IsLinux()) return false;
        switch (Environment.GetEnvironmentVariable("VODBOX_LINUX_CSD"))
        {
            case "0": return false;
            case "1": return true;
        }
        try
        {
            // 平台初始化早于 AppServices，这里单独开一次库读偏好。
            using var store = new LibraryStore(Path.Combine(AppPaths.DataDirectory, "library.db"));
            return store.GetBool(LinuxDrawnTitleBarKey, true);
        }
        catch
        {
            return true;
        }
    }

    /// <summary>在 App.Initialize 里调用一次：修正之后每个窗口加载出来的标题栏模板。</summary>
    public static void Install()
    {
        Control.LoadedEvent.AddClassHandler<Panel>((panel, _) =>
        {
            if (panel.TemplatedParent is not WindowDrawnDecorations) return;
            switch (panel.Name)
            {
                // Fluent 模板给按钮面板写死了 Spacing="2"，:has-border / :has-shadow 样式又按 1px 边框和
                // （X11 上）8px 阴影把它往里缩；这些都压过 App.axaml 的样式。我们的内容铺满整个窗口、
                // 不理会 WindowDecorationMargin，所以按钮要贴紧右上角，悬停底色才能连成一片、碰到窗口边缘。
                case "PART_OverlayPanel" when panel is StackPanel stack:
                    stack.Spacing = 0;
                    stack.Margin = default;
                    if (stack.GetVisualParent() is Panel { Name: "PART_OverlayWrapper" } wrapper)
                        wrapper.Margin = default;
                    break;
                // 扩展进标题栏的窗口都自己画标题（设置的分区标题、播放窗口的片名），
                // 模板左上角那份小标题会重复，隐藏掉；未扩展的窗口（更新窗口）保留。
                case "PART_TitleTextPanel" when TopLevel.GetTopLevel(panel) is Window { ExtendClientAreaToDecorationsHint: true }:
                    foreach (var text in panel.Children.OfType<TextBlock>()) text.IsVisible = false;
                    break;
            }
        });
    }
}
