using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using VodBox.Core;

namespace VodBox.Desktop;

public sealed partial class MediaCard(MediaItem item) : ObservableObject, IDisposable
{
    public MediaItem Item { get; } = item;
    public string Title => Item.Title;
    public string? Remarks => Item.Remarks;
    [ObservableProperty] private Bitmap? _poster;
    public bool IsDisposed { get; private set; }
    public void Dispose() { IsDisposed = true; Poster?.Dispose(); Poster = null; }
}
