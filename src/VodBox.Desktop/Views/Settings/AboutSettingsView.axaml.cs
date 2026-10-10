using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using VodBox.Desktop.ViewModels;

namespace VodBox.Desktop.Views.Settings;

/// <summary>关于分区。</summary>
public partial class AboutSettingsView : UserControl
{
    public AboutSettingsView() => AvaloniaXamlLoader.Load(this);

    private SettingsViewModel VM => (SettingsViewModel)DataContext!;

    private async void OnCheckUpdate(object? sender, RoutedEventArgs e) => await VM.CheckUpdateAsync();

    private void OnCancelUpdate(object? sender, RoutedEventArgs e) => VM.CancelUpdate();

    private async void OnInstallUpdate(object? sender, RoutedEventArgs e)
    {
        if (VM.UpdateBusy || VM.AvailableUpdate is not { } offer) { VM.UpdateMessage = "请先检查更新"; return; }
        if (OperatingSystem.IsLinux()) { await VM.InstallUpdateAsync(); return; }
        var parent = TopLevel.GetTopLevel(this) as Window;
        if (parent is null) return;
        var confirm = new Window { Title = "确认更新 VodBox", Width = 440, Height = 180, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var yes = new Button { Content = "确认下载并安装" };
        var no = new Button { Content = "取消" };
        yes.Click += (_, _) => confirm.Close(true);
        no.Click += (_, _) => confirm.Close(false);
        confirm.Content = new StackPanel { Margin = new Avalonia.Thickness(18), Spacing = 16, Children =
        {
            new TextBlock { Text = $"将下载并校验 VodBox {offer.Version}，关闭应用后由独立更新器替换安装目录并重启。是否继续？", TextWrapping = Avalonia.Media.TextWrapping.Wrap },
            new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 10, Children = { yes, no } }
        } };
        if (await confirm.ShowDialog<bool>(parent)) await VM.InstallUpdateAsync();
    }
}
