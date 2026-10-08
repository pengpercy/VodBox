using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using VodBox.Core;
using VodBox.Desktop.ViewModels;

namespace VodBox.Desktop.Views;

public partial class FavoritesView : UserControl
{
    public FavoritesView() => InitializeComponent();
    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private FavoritesViewModel VM => ((MainViewModel)DataContext!).Favorites;

    private void OnOpenFavorite(object? sender, PointerPressedEventArgs e)
    {
        if ((sender as Control)?.Tag is FavoriteEntry entry) VM.OpenCommand.Execute(entry);
    }

    private void OnRemoveFavorite(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is FavoriteEntry entry)
        {
            e.Handled = true;
            VM.RemoveCommand.Execute(entry);
        }
    }

    private void OnTabVod(object? sender, RoutedEventArgs e) => VM.Tab = 0;

    private void OnTabLive(object? sender, RoutedEventArgs e) => VM.Tab = 1;
}
