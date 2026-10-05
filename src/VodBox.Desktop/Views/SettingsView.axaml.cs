using Avalonia.Controls;

namespace VodBox.Desktop.Views;

public sealed partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();
    public void ShowPlaybackSettings() => SettingsTabs.SelectedIndex = 1;
}
