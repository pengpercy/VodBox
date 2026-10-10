using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using VodBox.Desktop.ViewModels;

namespace VodBox.Desktop.Views;

/// <summary>独立设置窗口：左栏分区导航 + 右栏卡片内容（与 PlayerWindow 同为独立顶层窗口）。</summary>
public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        AvaloniaXamlLoader.Load(this);
        // 骨架阶段自带一个默认分区，保证窗口可以独立 new 出来并显示。
        DataContext = new SettingsWindowViewModel();
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Bubble);
    }

    /// <summary>接入主窗口的设置视图模型（迁移阶段由 MainWindow 调用）。</summary>
    public SettingsWindow(SettingsViewModel settings) : this()
    {
        ViewModel.Content = settings;
        // 新建窗口也必须落在请求的分区：右栏可见性由 IsSelected 驱动，
        // 只设 Content 会让「手机扫码推送」这类入口停在默认的「源与订阅」上。
        ViewModel.SelectSection(settings.Section);
    }

    /// <summary>窗口已存在时，把左栏选中项同步到设置视图模型的当前分区。</summary>
    internal void SyncSelectionFrom(SettingsViewModel settings) => ViewModel.SelectSection(settings.Section);

    internal SettingsWindowViewModel ViewModel => (SettingsWindowViewModel)DataContext!;

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        // Esc 关闭独立窗口；分区切换只走左栏，不需要拦截其他按键。
        if (e.Key != Key.Escape) return;
        Close();
        e.Handled = true;
    }
}
