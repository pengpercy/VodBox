using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using VodBox.Core;
using VodBox.Desktop.ViewModels;

namespace VodBox.Desktop.Views;

public static class CategoryConverters
{
    /// <summary>SelectedCategory == item → 选中态。</summary>
    public static readonly IMultiValueConverter Matches = new MatchesConverter();

    private sealed class MatchesConverter : IMultiValueConverter
    {
        public object Convert(System.Collections.Generic.IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
            => values.Count == 2 && Equals(values[0], values[1]);
    }
}

public partial class VodView : UserControl
{
    public VodView() => InitializeComponent();
    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private VodViewModel VM => ((MainViewModel)DataContext!).Vod;

    private void OnOpenItem(object? sender, PointerPressedEventArgs e)
    {
        if ((sender as Control)?.Tag is MediaItem item) VM.OpenItemCommand.Execute(item);
    }

    private void OnCategoryClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is Category category) VM.SelectedCategory = category;
    }

    private void OnSwitchSource(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel main) main.GoSettingsCommand.Execute(null);
    }
}
