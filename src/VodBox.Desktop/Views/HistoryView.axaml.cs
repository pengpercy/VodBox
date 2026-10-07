using System;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using VodBox.Core;
using VodBox.Desktop.ViewModels;

namespace VodBox.Desktop.Views;

/// <summary>毫秒 → mm:ss / hh:mm:ss。</summary>
public static class MillisecondConverters
{
    public static readonly IValueConverter ToTime = new ToTimeConverter();

    private sealed class ToTimeConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is not long ms) return "00:00";
            var span = TimeSpan.FromMilliseconds(Math.Max(0, ms));
            return span.TotalHours >= 1
                ? $"{(int)span.TotalHours:00}:{span.Minutes:00}:{span.Seconds:00}"
                : $"{span.Minutes:00}:{span.Seconds:00}";
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}

public partial class HistoryView : UserControl
{
    public HistoryView() => InitializeComponent();
    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private HistoryViewModel VM => ((MainViewModel)DataContext!).History;

    private void OnResume(object? sender, PointerPressedEventArgs e)
    {
        if ((sender as Control)?.Tag is HistoryEntry entry) VM.ResumeCommand.Execute(entry);
    }

    private void OnResumeClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is HistoryEntry entry) VM.ResumeCommand.Execute(entry);
    }

    private void OnDelete(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is HistoryEntry entry) VM.DeleteCommand.Execute(entry);
    }
}
