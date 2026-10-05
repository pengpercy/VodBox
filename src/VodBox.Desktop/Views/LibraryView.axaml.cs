using Avalonia.Controls;
using Avalonia.VisualTree;
using VodBox.Core;

namespace VodBox.Desktop.Views;

public sealed partial class LibraryView : UserControl
{
    private MainViewModel? Model => DataContext as MainViewModel;
    public LibraryView()
    {
        InitializeComponent();
        SizeChanged += (_, _) => QueueCardLayout();
    }
    private bool _cardLayoutQueued;
    private void QueueCardLayout()
    {
        if (_cardLayoutQueued) return;
        _cardLayoutQueued = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _cardLayoutQueued = false;
            if (this.IsAttachedToVisualTree()) Model?.UpdateCardColumns(Bounds.Width - 24);
        }, Avalonia.Threading.DispatcherPriority.Background);
    }
    private readonly Dictionary<Image, MediaCard> _posterControls = [];
    private void PosterAttached(object? sender, Avalonia.VisualTreeAttachmentEventArgs e) => PosterContextChanged(sender, EventArgs.Empty);
    private void PosterDetached(object? sender, Avalonia.VisualTreeAttachmentEventArgs e)
    { if (sender is Image image && _posterControls.Remove(image, out var previous)) MainViewModel.ReleasePoster(previous); }
    private void PosterContextChanged(object? sender, EventArgs e)
    {
        if (sender is not Image image) return;
        if (_posterControls.Remove(image, out var previous)) MainViewModel.ReleasePoster(previous);
        if (Model is not null && image.IsAttachedToVisualTree() && image.DataContext is MediaCard card)
        { _posterControls[image] = card; Model.ActivatePoster(card); }
    }
    private async void SearchSelectionChanged(object? sender, SelectionChangedEventArgs e)
    { if (Model is not null && e.AddedItems.Count > 0 && e.AddedItems[0] is SearchHit hit) await Model.OpenSearchResultCommand.ExecuteAsync(hit); }
    private void MediaCardClick(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    { if (Model is not null && sender is Button { DataContext: MediaCard card }) Model.SelectedCard = card; }

}
