using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace VodBox.Desktop.Views;

public sealed partial class SettingsDanmakuView : UserControl
{
    private MainViewModel? Model => DataContext as MainViewModel;
    public SettingsDanmakuView() => InitializeComponent();
    private Task<string?> PickAsync(string title) => ViewFilePicker.PickAsync(this, title);
    private async void OpenDanmakuClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    { if (Model is not null && await PickAsync("打开 XML / JSON 弹幕") is { } path) await Model.LoadDanmakuAsync(path); }
}
