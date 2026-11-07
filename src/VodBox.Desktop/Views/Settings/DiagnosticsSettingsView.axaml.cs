using Avalonia.Controls;

namespace VodBox.Desktop.Views.Settings;

/// <summary>设置 › 诊断分区。</summary>
public partial class DiagnosticsSettingsView : UserControl
{
    public DiagnosticsSettingsView() => Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);
}
