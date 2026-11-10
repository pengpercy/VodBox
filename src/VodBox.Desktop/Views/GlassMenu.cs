using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Media;
using Avalonia.Threading;

namespace VodBox.Desktop.Views;

/// <summary>
/// 玻璃质感菜单：弹出层是独立的原生窗口，窗口透明，面板用半透明渐变底 + 顶部受光的细描边 + 圆角，
/// 画面在菜单下隐约透出。
/// 不用系统背景模糊：macOS 上 Avalonia 的模糊材质几乎不透明（压在纯色画面上也不透色），
/// 且铺满整个矩形窗口，圆角外会露出方角。系统不支持透明窗口（部分 Linux 桌面）时换成实色底，保证可读。
/// 子菜单也是各自的弹出窗口，展开时同样处理。
/// </summary>
internal static class GlassMenu
{
    /// <summary>半透明底：上浅下深，像一块受顶光的深色玻璃。</summary>
    internal static IBrush GlassBackground() => new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.FromArgb(0xF5, 0x2E, 0x30, 0x37), 0),
            new GradientStop(Color.FromArgb(0xFA, 0x1A, 0x1B, 0x20), 1),
        },
    };

    /// <summary>描边：顶边更亮，读出玻璃边缘的高光。</summary>
    internal static IBrush GlassBorder() => new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.FromArgb(0x4D, 0xFF, 0xFF, 0xFF), 0),
            new GradientStop(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF), 1),
        },
    };

    internal static readonly Color SolidTint = Color.FromArgb(0xFF, 0x24, 0x25, 0x2B);

    public static ContextMenu Apply(ContextMenu menu)
    {
        menu.Classes.Add("glass");
        menu.Background = GlassBackground();
        menu.BorderBrush = GlassBorder();
        menu.BorderThickness = new Thickness(1);
        menu.CornerRadius = new CornerRadius(10);
        menu.Padding = new Thickness(5);
        // 子菜单弹层的模板从这些资源取底色/描边/圆角，与主菜单保持一致。
        menu.Resources["MenuFlyoutPresenterBackground"] = GlassBackground();
        menu.Resources["MenuFlyoutPresenterBorderBrush"] = GlassBorder();
        menu.Resources["MenuFlyoutPresenterBorderThemeThickness"] = new Thickness(1);
        menu.Resources["OverlayCornerRadius"] = new CornerRadius(10);
        menu.Resources["MenuFlyoutItemBackgroundPointerOver"] = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
        menu.Resources["MenuFlyoutItemBackgroundPressed"] = new SolidColorBrush(Color.FromArgb(0x3D, 0xFF, 0xFF, 0xFF));
        menu.Resources["MenuFlyoutSeparatorBackground"] = new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
        menu.Opened += (_, _) => Clear(menu, menu);
        // 子菜单：SubmenuOpened 触发时弹出窗口还没建好，等它真正打开后再处理。
        menu.AddHandler(MenuItem.SubmenuOpenedEvent, (_, e) =>
        {
            if (e.Source is not MenuItem item || item.GetTemplateDescendants().OfType<Popup>().FirstOrDefault() is not { } popup) return;
            if (popup.IsOpen && popup.Child is Control open) Clear(menu, open);
            Dispatcher.UIThread.Post(() => { if (popup.IsOpen && popup.Child is Control child) Clear(menu, child); }, DispatcherPriority.Loaded);
        });
        return menu;
    }

    /// <summary>
    /// 弹出窗口本身默认就是透明的（主题给 PopupRoot 的提示即 Transparent），这里只清掉窗口底色；
    /// 平台没给透明（部分 Linux 桌面）时换成实色底。不重设提示：重设会让实际透明度短暂回落为 None。
    /// </summary>
    private static void Clear(ContextMenu menu, Control content)
    {
        if (TopLevel.GetTopLevel(content) is not { } root || root is Window) return;
        root.Background = Brushes.Transparent;
        if (root.ActualTransparencyLevel != WindowTransparencyLevel.None) return;
        var solid = new SolidColorBrush(SolidTint);
        if (ReferenceEquals(content, menu)) menu.Background = solid;
        else if (content is Border border) border.Background = solid;
    }
}
