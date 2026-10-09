using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using VodBox.Desktop.ViewModels;

namespace VodBox.Desktop.Views;

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();
    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private SettingsViewModel VM => ((MainViewModel)DataContext!).Settings;

    private async void OnPickWallpaper(object? sender,RoutedEventArgs e)
    {
        try
        {
            var storage=TopLevel.GetTopLevel(this)?.StorageProvider;if(storage?.CanOpen!=true)return;
            var files=await storage.OpenFilePickerAsync(new FilePickerOpenOptions{Title="选择本地壁纸",AllowMultiple=false,FileTypeFilter=[new FilePickerFileType("图片"){Patterns=["*.png","*.jpg","*.jpeg","*.webp"]}]});
            if(files.Count==0)return;var path=files[0].TryGetLocalPath();if(path is null)throw new InvalidDataException("请选择本地文件。");VM.SetWallpaper(path);
        }
        catch(Exception error){VM.Message=$"壁纸设置失败：{error.Message}";}
    }

    private async void OnExportBackup(object? sender, RoutedEventArgs e)
    {
        try
        {
            var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (storage?.CanSave != true) return;
            var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions { Title = "导出备份", SuggestedFileName = "vodbox-backup.json" });
            if (file is null) return;
            var json = await VM.ExportBackupAsync();
            await using var output = await file.OpenWriteAsync();
            output.SetLength(0);
            await using var writer = new StreamWriter(output);
            await writer.WriteAsync(json);
            VM.Message = "备份已导出；文件包含订阅与偏好，请妥善保管";
        }
        catch (Exception error) { VM.Message = $"导出失败：{error.Message}"; }
    }

    private async void OnRestoreBackup(object? sender, RoutedEventArgs e)
    {
        try
        {
            var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (storage?.CanOpen != true) return;
            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "合并备份（不会删除现有记录）", AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("备份JSON") { Patterns = ["*.json"] }],
            });
            if (files.Count == 0) return;
            await using var input = await files[0].OpenReadAsync();
            using var reader = new StreamReader(input);
            var buffer = new char[8192]; var text = new System.Text.StringBuilder(); int count;
            while ((count = await reader.ReadAsync(buffer)) > 0)
            {
                if (text.Length + count > 16 * 1024 * 1024) throw new InvalidDataException("备份文件过大。");
                text.Append(buffer, 0, count);
            }
            await VM.RestoreBackupAsync(text.ToString());
        }
        catch (Exception error) { VM.Message = $"恢复失败：{error.Message}"; }
    }

    private void OnSelectSection(object? sender,RoutedEventArgs e)
    {
        if(sender is Button { Tag: string value }&&int.TryParse(value,out var section))VM.Section=section;
    }

}
