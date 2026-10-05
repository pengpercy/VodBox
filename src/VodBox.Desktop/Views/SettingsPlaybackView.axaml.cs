using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace VodBox.Desktop.Views;

public sealed partial class SettingsPlaybackView : UserControl
{
    private MainViewModel? Model => DataContext as MainViewModel;
    public SettingsPlaybackView() => InitializeComponent();
    private Task<string?> PickAsync(string title) => ViewFilePicker.PickAsync(this, title);
    private async void SnapshotClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (Model is null) return;
        var file = await TopLevel.GetTopLevel(this)!.StorageProvider.SaveFilePickerAsync(new() { Title = "保存视频截图", SuggestedFileName = $"VodBox-{DateTime.Now:yyyyMMdd-HHmmss}.png", DefaultExtension = "png", FileTypeChoices = [new("PNG 图片") { Patterns = ["*.png"] }] });
        if (file?.TryGetLocalPath() is { } path) await Model.TakeSnapshotAsync(path);
    }
}
