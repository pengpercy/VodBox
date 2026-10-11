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

    private async void OnCheckUpdate(object? sender, RoutedEventArgs e) =>
        await VM.CheckUpdateAsync(TopLevel.GetTopLevel(this) as Window);
}
