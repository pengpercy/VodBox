using System;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using VodBox.Desktop.ViewModels;

namespace VodBox.Desktop.Views;

/// <summary>TimeSpan → mm:ss。</summary>
public static class TimeConverters
{
    public static readonly IValueConverter ToTime = new ToTimeConverter();

    private sealed class ToTimeConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is not TimeSpan span) return "00:00";
            return span.TotalHours >= 1
                ? $"{(int)span.TotalHours:00}:{span.Minutes:00}:{span.Seconds:00}"
                : $"{span.Minutes:00}:{span.Seconds:00}";
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}

public partial class PlayerOverlay : UserControl
{
    public PlayerOverlay() => InitializeComponent();
    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private PlayerViewModel VM => ((MainViewModel)DataContext!).Player;

    private void OnTogglePlay(object? sender, RoutedEventArgs e) => VM.TogglePlayPauseCommand.Execute(null);

    private void OnBack10(object? sender, RoutedEventArgs e) => VM.SeekByCommand.Execute(-10d);
    private void OnForward10(object? sender, RoutedEventArgs e) => VM.SeekByCommand.Execute(10d);
    private void OnClose(object? sender, RoutedEventArgs e) => VM.CloseCommand.Execute(null);

    private void OnRateChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { SelectedIndex: var index } && VM is { })
        {
            VM.Rate = index switch
            {
                0 => 0.5, 1 => 0.75, 2 => 1.0, 3 => 1.25, 4 => 1.5, 5 => 2.0, _ => 1.0,
            };
        }
    }
}
