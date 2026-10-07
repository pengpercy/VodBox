using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using VodBox.Core;
using VodBox.Desktop.ViewModels;

namespace VodBox.Desktop.Views;

public partial class SearchView : UserControl
{
    public SearchView() => InitializeComponent();
    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private SearchViewModel VM => ((MainViewModel)DataContext!).Search;

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Return) VM.RunSearchCommand.Execute(null);
    }

    private void OnSearchClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => VM.RunSearchCommand.Execute(null);

    private void OnOpenItem(object? sender, PointerPressedEventArgs e)
    {
        if ((sender as Control)?.Tag is MediaItem item) VM.OpenItemCommand.Execute(item);
    }
}
