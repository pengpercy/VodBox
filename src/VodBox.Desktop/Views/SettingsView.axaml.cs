using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using VodBox.Desktop.ViewModels;

namespace VodBox.Desktop.Views;

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();
    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private SettingsViewModel VM => ((MainViewModel)DataContext!).Settings;

    private void OnSectionSources(object? sender, PointerPressedEventArgs e) => VM.Section = 0;
    private void OnSectionPlayback(object? sender, PointerPressedEventArgs e) => VM.Section = 1;
    private void OnSectionData(object? sender, PointerPressedEventArgs e) => VM.Section = 2;
    private void OnSectionAbout(object? sender, PointerPressedEventArgs e) => VM.Section = 3;
}
