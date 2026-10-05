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
    public ObservableCollection<MediaCardRow> CardRows { get; } = [];
    public int CardColumns { get; private set; } = 3;
    public void UpdateCardColumns(double width)
    {
        if (width <= 0) return;
        int columns = Math.Clamp((int)(width / 160), 2, 10);
        if (columns == CardColumns) return;
        CardColumns = columns; RebuildCardRows();
    }
    private void RebuildCardRows()
    {
        CardRows.Clear();
        foreach (var card in Cards) AppendCardRow(card);
    }
    private void AppendCardRow(MediaCard card)
    {
        if (CardRows.Count == 0 || CardRows[^1].Cards.Count == CardColumns)
            CardRows.Add(new(CardColumns));
        CardRows[^1].Cards.Add(card);
    }
    [ObservableProperty] private MediaCard? _selectedCard;
    partial void OnSelectedCardChanged(MediaCard? oldValue, MediaCard? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) { newValue.IsSelected = true; SelectedItem = newValue.Item; }
    }
    private void ItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        { foreach (var card in Cards) card.Dispose(); Cards.Clear(); CardRows.Clear(); SelectedCard = null; SelectedItem = null; return; }
        if (e.OldItems is not null)
            foreach (MediaItem item in e.OldItems)
            { var old = Cards.FirstOrDefault(x => ReferenceEquals(x.Item, item)); if (old is not null) { if (ReferenceEquals(SelectedCard, old)) { SelectedCard = null; SelectedItem = null; } Cards.Remove(old); old.Dispose(); } }
        if (e.OldItems is not null) RebuildCardRows();
        if (e.NewItems is null) return;
        foreach (MediaItem item in e.NewItems)
        {
            var card = new MediaCard(item); Cards.Add(card); AppendCardRow(card);

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
