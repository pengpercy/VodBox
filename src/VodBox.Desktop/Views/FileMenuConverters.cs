using System;
using System.Globalization;
using Avalonia.Data.Converters;
using VodBox.Desktop.ViewModels;

namespace VodBox.Desktop.Views;

/// <summary>文件行右键菜单的可见性判定（播放列表才显示“作为直播源打开”）。</summary>
public static class FileMenuConverters
{
    public static readonly IValueConverter CanOpenAsLive = new CanOpenAsLiveConverter();

    private sealed class CanOpenAsLiveConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is FileEntry entry && FilesViewModel.CanOpenAsLive(entry);

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
