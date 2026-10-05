using Avalonia.Controls;
using VodBox.Core;

namespace VodBox.Desktop.Views;

public sealed partial class FavoritesView : UserControl
{
    private MainViewModel? Model => DataContext as MainViewModel;
    public FavoritesView() => InitializeComponent();
    private async void FavoriteSelectionChanged(object? sender, SelectionChangedEventArgs e)
    { if (Model is not null && e.AddedItems.Count > 0 && e.AddedItems[0] is FavoriteEntry entry) await Model.OpenFavoriteCommand.ExecuteAsync(entry); }

}
