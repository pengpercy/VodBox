using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
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
    public VodView()
    {
        InitializeComponent();
        if(this.FindControl<ListBox>("PosterList") is {} grid)grid.SizeChanged+=(_,_)=>
        {if(DataContext is MainViewModel main)main.Vod.SetGridWidth(grid.Bounds.Width);};
    }
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

    private void OnPickFilter(object? sender,RoutedEventArgs e)
    {
        if(sender is Button{Tag:FilterValue value} button)
        {
            var group=button.GetVisualAncestors().OfType<Control>().Select(control=>control.DataContext).OfType<FilterGroup>().FirstOrDefault();
            if(group is not null)VM.SelectFilter(group.Key,value.Value);
        }
    }

    private void OnSwitchSource(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        var menu = new ContextMenu();
        foreach (var source in VM.AvailableSources)
        {
            var item = new MenuItem { Header = source.Name };
            item.Click += async (_, _) => await VM.SwitchSourceAsync(source.Key);
            menu.Items.Add(item);
        }
        if (menu.Items.Count == 0) menu.Items.Add(new MenuItem { Header = "请先添加配置", IsEnabled = false });
        button.ContextMenu = menu;
        menu.Open(button);
    }
}
