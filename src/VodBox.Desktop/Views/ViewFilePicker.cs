using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace VodBox.Desktop.Views;

internal static class ViewFilePicker
{
    public static async Task<string?> PickAsync(Control owner, string title)
    {
        var storage = TopLevel.GetTopLevel(owner)?.StorageProvider;
        if (storage is null) return null;
        var files = await storage.OpenFilePickerAsync(new() { Title = title, AllowMultiple = false });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }
}
