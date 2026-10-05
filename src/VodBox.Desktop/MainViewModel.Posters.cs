using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using VodBox.Core;
using VodBox.Infrastructure;

namespace VodBox.Desktop;

public partial class MainViewModel
{
    private readonly PosterCache _posters;
    private readonly List<Task> _posterTasks = [];
    public ObservableCollection<MediaCard> Cards { get; } = [];
    [ObservableProperty] private MediaCard? _selectedCard;
    partial void OnSelectedCardChanged(MediaCard? value) { if (value is not null) SelectedItem = value.Item; }
    private void ItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        { foreach (var card in Cards) card.Dispose(); Cards.Clear(); SelectedCard = null; SelectedItem = null; return; }
        if (e.OldItems is not null)
            foreach (MediaItem item in e.OldItems)
            { var old = Cards.FirstOrDefault(x => ReferenceEquals(x.Item, item)); if (old is not null) { Cards.Remove(old); old.Dispose(); } }
        if (e.NewItems is null) return;
        foreach (MediaItem item in e.NewItems)
        {
            var card = new MediaCard(item); Cards.Add(card);

        }
    }
    public void ActivatePoster(MediaCard card)
    {
        if (_designMode || _disposed || string.IsNullOrWhiteSpace(card.Item.Poster) || !card.Activate(_lifetime.Token)) return;
        _posterTasks.RemoveAll(x => x.IsCompleted); _posterTasks.Add(LoadPosterAsync(card, card.LoadToken));
    }
    public static void ReleasePoster(MediaCard card) => card.Deactivate();
    private async Task LoadPosterAsync(MediaCard card, CancellationToken token)
    {
        Bitmap? bitmap = null;
        try
        {
            string? path = await _posters.GetAsync(card.Item.Poster, token);
            if (path is null || card.IsDisposed || token.IsCancellationRequested) return;
            bitmap = await Task.Run(() => { using var stream = File.OpenRead(path); return Bitmap.DecodeToWidth(stream, 160); }, token);
            await Dispatcher.UIThread.InvokeAsync(() =>
            { if (!card.IsDisposed && card.IsActive && !token.IsCancellationRequested && !_disposed) { card.Poster = bitmap; bitmap = null; } });
        }
        catch (Exception) { /* Invalid or missing posters retain the placeholder. */ }
        finally { bitmap?.Dispose(); }
    }
}
