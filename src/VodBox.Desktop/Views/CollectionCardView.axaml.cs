using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace VodBox.Desktop.Views;

public sealed partial class CollectionCardView : UserControl
{
    private MediaCard? _active;
    public CollectionCardView()
    {
        InitializeComponent();
        Artwork.SizeChanged += (_, _) => { if (Artwork.Bounds.Width > 0) Artwork.Height = Artwork.Bounds.Width * 4 / 3; };
        AttachedToVisualTree += (_, _) => UpdateCard();
        DetachedFromVisualTree += (_, _) => Release();
        DataContextChanged += (_, _) => UpdateCard();
    }
    private void Release() { if (_active is not null) MainViewModel.ReleasePoster(_active); _active = null; }
    private void UpdateCard()
    {
        Release(); if (DataContext is not CollectionCard entry) return;
        bool search = entry.SearchHit is not null;
        Width = double.NaN;
        CardLayout.ColumnDefinitions = new(search ? "150,*" : "*");
        Grid.SetRow(CardText, search ? 0 : 1); Grid.SetColumn(CardText, search ? 1 : 0);
        Artwork.Height = search ? 200 : 240;
        CardText.VerticalAlignment = VerticalAlignment.Stretch;
        Artwork.CornerRadius = search ? new CornerRadius(8,0,0,8) : new CornerRadius(8,8,0,0);
        CardText.Height = search ? double.NaN : 44;
        CardText.Margin = search ? new Thickness(16,12) : new Thickness(8,0);
        CardTitle.VerticalAlignment = search ? VerticalAlignment.Top : VerticalAlignment.Center;
        HistoryCaption.IsVisible = !search && entry.History is not null && entry.Caption.Length > 0;
        CardTitle.HorizontalAlignment = search ? HorizontalAlignment.Stretch : HorizontalAlignment.Center;
        CardCaption.IsVisible = search && entry.Caption.Length > 0;
        if (VisualRoot is not null && TopLevel.GetTopLevel(this)?.DataContext is MainViewModel model)
        { _active = entry.Card; model.ActivatePoster(entry.Card); }
    }
    private async void OpenClick(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    {
        if (DataContext is not CollectionCard entry || TopLevel.GetTopLevel(this)?.DataContext is not MainViewModel model) return;
        if (entry.History is not null) await model.ResumeHistoryCommand.ExecuteAsync(entry.History);
        else if (entry.Favorite is not null) await model.OpenFavoriteCommand.ExecuteAsync(entry.Favorite);
        else if (entry.SearchHit is not null) await model.OpenSearchResultCommand.ExecuteAsync(entry.SearchHit);
    }
}
