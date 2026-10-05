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
    [ObservableProperty] private bool _isSelected;
    private CancellationTokenSource? _load;
    public bool IsDisposed { get; private set; }
    public bool IsActive => _load is not null;
    public CancellationToken LoadToken => _load?.Token ?? new CancellationToken(true);
    public bool Activate(CancellationToken lifetime)
    {
        if (IsDisposed || IsActive) return false;
        _load = CancellationTokenSource.CreateLinkedTokenSource(lifetime); return true;
    }
    public void Deactivate()
    {
        _load?.Cancel(); _load?.Dispose(); _load = null;
        var previous = Poster; Poster = null; previous?.Dispose();
    }
    public void Dispose() { IsDisposed = true; Deactivate(); }
}
