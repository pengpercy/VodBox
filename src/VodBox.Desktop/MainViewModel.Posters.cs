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
            if (_designMode || string.IsNullOrWhiteSpace(item.Poster)) continue;
            _posterTasks.RemoveAll(x => x.IsCompleted); _posterTasks.Add(LoadPosterAsync(card));
        }
    }
    private async Task LoadPosterAsync(MediaCard card)
    {
        Bitmap? bitmap = null;
        try
        {
            string? path = await _posters.GetAsync(card.Item.Poster, _lifetime.Token);
            if (path is null || card.IsDisposed) return;
            bitmap = await Task.Run(() => { using var stream = File.OpenRead(path); return Bitmap.DecodeToWidth(stream, 160); }, _lifetime.Token);
            await Dispatcher.UIThread.InvokeAsync(() =>
            { if (!card.IsDisposed && !_disposed) { card.Poster = bitmap; bitmap = null; } });
        }
        catch (Exception) { /* Invalid or missing posters retain the placeholder. */ }
        finally { bitmap?.Dispose(); }
    }
}
