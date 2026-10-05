using Avalonia.Controls;

namespace VodBox.Desktop.Views;

public sealed partial class SettingsView : UserControl
{
    private bool _ready;
    public SettingsView() { InitializeComponent(); _ready = true; UpdateSection(); }
    public void ShowPlaybackSettings() => SettingsMenu.SelectedIndex = 1;
    private void MenuSelectionChanged(object? sender, SelectionChangedEventArgs args)
    { if (_ready) UpdateSection(); }
    private void UpdateSection()
    {
        int index = SettingsMenu.SelectedIndex;
        GeneralPage.IsVisible = index == 0;
        PlaybackPage.IsVisible = index == 1;
        DanmakuPage.IsVisible = index == 2;
        SectionTitle.Text = index switch { 1 => "播放设置", 2 => "弹幕设置", _ => "通用设置" };
    }
}
