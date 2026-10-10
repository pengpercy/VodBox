using Avalonia;
using Avalonia.Controls;
using Avalonia.Animation;
using Avalonia.Media;
using Avalonia.Threading;

namespace VodBox.Desktop.Views;

/// <summary>随视图生命周期租用共享海报；过期响应不覆盖新地址。</summary>
public sealed class RemotePoster : Image
{
    public static readonly StyledProperty<string?> UrlProperty =
        AvaloniaProperty.Register<RemotePoster, string?>(nameof(Url));

    /// <summary>是否已经拿到图像。台标占位（文字/底色）据此在加载成功后让位，加载失败则保留占位。</summary>
    public static readonly DirectProperty<RemotePoster, bool> HasImageProperty =
        AvaloniaProperty.RegisterDirect<RemotePoster, bool>(nameof(HasImage), o => o.HasImage);
    private bool _hasImage;
    public bool HasImage
    {
        get => _hasImage;
        private set => SetAndRaise(HasImageProperty, ref _hasImage, value);
    }
    private readonly PosterCache _cache;
    private CancellationTokenSource? _load;
    private PosterCache.Lease? _lease;
    private bool _attached;
    internal Task LoadingTask { get; private set; } = Task.CompletedTask;
    public string? Url { get => GetValue(UrlProperty); set => SetValue(UrlProperty, value); }

    public RemotePoster() : this(PosterCache.Shared) { }

    internal RemotePoster(PosterCache cache)
    {
        _cache = cache;
        Stretch = Stretch.UniformToFill;
        Opacity = 0;
        Transitions = new Transitions
        {
            new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(180) }
        };
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == UrlProperty && _attached) StartLoad();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        StartLoad();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        ClearLoad();
        base.OnDetachedFromVisualTree(e);
    }

    private void ClearLoad()
    {
        var previous = _load;
        _load = null;
        previous?.Cancel();
        // Clear the UI reference before releasing its lease; only the cache owns Bitmap disposal.
        Opacity = 0;
        Source = null;
        HasImage = false;
        _lease?.Dispose();
        _lease = null;
    }

    private void StartLoad()
    {
        ClearLoad();
        if (!Uri.TryCreate(Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")) return;
        var scope = new CancellationTokenSource();
        _load = scope;
        LoadingTask = LoadAsync(uri, scope);
    }

    private async Task LoadAsync(Uri uri, CancellationTokenSource scope)
    {
        PosterCache.Lease? lease = null;
        try
        {
            lease = await _cache.AcquireAsync(uri, scope.Token).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!_attached || scope.IsCancellationRequested || !ReferenceEquals(_load, scope)) return;
                _lease = lease;
                Source = lease.Bitmap;
                HasImage = true;
                Opacity = 1;
                lease = null;
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            System.Diagnostics.Debug.WriteLine($"[poster] {error.Message}");
        }
        finally
        {
            lease?.Dispose();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (ReferenceEquals(_load, scope)) _load = null;
                scope.Dispose();
            });
        }
    }
}
