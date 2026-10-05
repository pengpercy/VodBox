using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;

namespace VodBox.Desktop.Views;

public sealed partial class SettingsGeneralView : UserControl
{
    private MainViewModel? Model => DataContext as MainViewModel;
    public SettingsGeneralView() => InitializeComponent();
    private Task<string?> PickAsync(string title) => ViewFilePicker.PickAsync(this, title);
    private async void ExportBackupClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (Model is null) return;
        var file = await TopLevel.GetTopLevel(this)!.StorageProvider.SaveFilePickerAsync(new() { Title = "导出 VodBox 数据备份", SuggestedFileName = $"VodBox-{DateTime.Now:yyyyMMdd-HHmmss}.vodbox-backup.json", FileTypeChoices = [new("VodBox 数据备份") { Patterns = ["*.vodbox-backup.json"] }] });
        if (file?.TryGetLocalPath() is { } path) await Model.ExportBackupAsync(path);
    }
    private async void ImportBackupClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (Model is null) return;
        var files = await TopLevel.GetTopLevel(this)!.StorageProvider.OpenFilePickerAsync(new() { Title = "选择备份并合并数据", AllowMultiple = false, FileTypeFilter = [new("VodBox 数据备份") { Patterns = ["*.vodbox-backup.json"] }] });
        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path) await Model.ImportBackupAsync(path);
    }
    private async void ConfigureSourceClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (Model is null || TopLevel.GetTopLevel(this) is not Window owner) return;
        var dialog = new Window { Title = "播放源配置", Width = 560, Height = 340, MinWidth = 480, MinHeight = 320,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, DataContext = Model, Content = new SourceConfigurationView() };
        await dialog.ShowDialog(owner);
    }
    private void PlaybackSettingsClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    { this.GetVisualAncestors().OfType<SettingsView>().FirstOrDefault()?.ShowPlaybackSettings(); }
}
