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

    /// <summary>大图（<see cref="Url"/>）尚未就绪时先显示、加载失败时改用的地址，按普通海报尺寸解码。</summary>
    public static readonly StyledProperty<string?> FallbackUrlProperty =
        AvaloniaProperty.Register<RemotePoster, string?>(nameof(FallbackUrl));
    public string? FallbackUrl { get => GetValue(FallbackUrlProperty); set => SetValue(FallbackUrlProperty, value); }

    /// <summary>整宽横幅：<see cref="Url"/> 按大尺寸解码，避免小图被放大发糊。</summary>
    public static readonly StyledProperty<bool> LargeProperty =
        AvaloniaProperty.Register<RemotePoster, bool>(nameof(Large));
    public bool Large { get => GetValue(LargeProperty); set => SetValue(LargeProperty, value); }

    public static readonly StyledProperty<string?> ChannelNameProperty =
        AvaloniaProperty.Register<RemotePoster, string?>(nameof(ChannelName));
    public string? ChannelName { get => GetValue(ChannelNameProperty); set => SetValue(ChannelNameProperty, value); }

    public static readonly StyledProperty<bool> ChannelIsScrollingProperty =
        AvaloniaProperty.Register<RemotePoster, bool>(nameof(ChannelIsScrolling));
    public bool ChannelIsScrolling { get => GetValue(ChannelIsScrollingProperty); set => SetValue(ChannelIsScrollingProperty, value); }
    private readonly DispatcherTimer _logoFadeTimer = new() { Interval = TimeSpan.FromMilliseconds(30) };
    private long _logoFadeStarted;
    internal bool IsLogoFading => _logoFadeTimer.IsEnabled;

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
    private readonly bool _useSharedLogoCache;
    private CancellationTokenSource? _load;
    private PosterCache.Lease? _lease;
    private bool _attached;
    internal Task LoadingTask { get; private set; } = Task.CompletedTask;
    public string? Url { get => GetValue(UrlProperty); set => SetValue(UrlProperty, value); }

    public RemotePoster() : this(PosterCache.Shared) { _useSharedLogoCache = true; }

    internal RemotePoster(PosterCache cache)
    {
        _cache = cache;
        _logoFadeTimer.Tick += (_, _) =>
        {
            if (!_attached || !HasImage || ChannelIsScrolling)
            {
                FinishLogoFade();
                return;
            }
            var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(_logoFadeStarted).TotalMilliseconds;
            Opacity = Math.Clamp(elapsed / 180, 0, 1);
            if (Opacity >= 1) _logoFadeTimer.Stop();
        };
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
        if (change.Property == ChannelIsScrollingProperty)
        {
            if (ChannelIsScrolling)
            {
                FinishLogoFade();
                // 滚动时保留已显示的图像，但暂停仍在等待/下载的图像，避免渲染线程不断收到新位图。
                var previous = _load;
                _load = null;
                previous?.Cancel();
            }
            else if (_attached && !HasImage && !string.IsNullOrWhiteSpace(ChannelName)) StartLoad();
        }
        if ((change.Property == UrlProperty || change.Property == FallbackUrlProperty || change.Property == LargeProperty
             || change.Property == ChannelNameProperty) && _attached) StartLoad();
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

    private void FinishLogoFade()
    {
        _logoFadeTimer.Stop();
        if (!string.IsNullOrWhiteSpace(ChannelName)) Opacity = HasImage ? 1 : 0;
    }

    private void ClearLoad()
    {
        _logoFadeTimer.Stop();
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
        var channelName = ChannelName;
        var isLogo = !string.IsNullOrWhiteSpace(channelName);
        if (isLogo) Transitions = null;
        Uri? primary = Uri.TryCreate(Url, UriKind.Absolute, out var candidate) && candidate.Scheme is "https" or "http"
            ? candidate : null;
        if (primary is null && !isLogo)
        {
            LoadingTask = Task.CompletedTask;
            return;
        }
        var cache = isLogo && _useSharedLogoCache ? PosterCache.ChannelLogos : Large && _useSharedLogoCache ? PosterCache.Hero : _cache;
        Uri? fallbackPoster = !isLogo && Uri.TryCreate(FallbackUrl, UriKind.Absolute, out var fallbackCandidate)
            && fallbackCandidate.Scheme is "https" or "http" && fallbackCandidate != primary ? fallbackCandidate : null;
        // M3U 台标近期已失败时按没有台标处理：直接用内置索引（已解码则同步上屏，滚动中也不闪占位），
        // 不再每次停下滚动都先请求一遍坏地址。
        if (isLogo && primary is not null && cache.HasRecentlyFailed(primary)) primary = null;
        var cached = primary is not null ? cache.TryAcquireCached(primary) : null;
        // 没有 M3U 台标时，索引读取也保持后台执行；已初始化的索引可直接匹配缓存。
        if (cached is null && isLogo && Services.BuiltInChannelLogos.IsReady)
        {
            var fallback = Services.BuiltInChannelLogos.Find(channelName);
            if (fallback is not null && (primary is null || primary == fallback)) cached = cache.TryAcquireCached(fallback);
        }
        if (cached is not null)
        {
            _lease = cached;
            Source = cached.Bitmap;
            HasImage = true;
            Opacity = 1;
            LoadingTask = Task.CompletedTask;
            return;
        }
        // 大图还没解码：已缓存的原海报先顶上（同步上屏，不闪空），大图到了再替换。
        if (fallbackPoster is not null && _cache.TryAcquireCached(fallbackPoster) is { } small)
        {
            _lease = small;
            Source = small.Bitmap;
            HasImage = true;
            Opacity = 1;
            if (primary is null)
            {
                LoadingTask = Task.CompletedTask;
                return;
            }
        }
        if (isLogo && ChannelIsScrolling)
        {
            LoadingTask = Task.CompletedTask;
            return;
        }
        var scope = new CancellationTokenSource();
        _load = scope;
        LoadingTask = LoadAsync(primary, channelName, cache, HasImage ? null : fallbackPoster, scope);
    }

    private async Task LoadAsync(Uri? primary, string? channelName, PosterCache cache, Uri? fallbackPoster, CancellationTokenSource scope)
    {
        try
        {
            var isLogo = !string.IsNullOrWhiteSpace(channelName);
            // 快速滚动/虚拟化复用时，取消尚未开始的台标任务；下载与解码继续在后台执行。
            if (isLogo) await Task.Delay(100, scope.Token).ConfigureAwait(false);
            var fallback = isLogo ? Services.BuiltInChannelLogos.Find(channelName) : fallbackPoster;
            // 主地址优先；缺失、下载失败、超时或图片解码失败时，台标改用内置索引 URL，海报改用原尺寸地址。
            foreach (var uri in new[] { primary, fallback }.Where(uri => uri is not null).Distinct())
            {
                var source = uri == fallbackPoster ? _cache : cache;
                PosterCache.Lease? lease = null;
                try
                {
                    lease = await source.AcquireAsync(uri!, scope.Token).ConfigureAwait(false);
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (!_attached || scope.IsCancellationRequested || !ReferenceEquals(_load, scope)) return;
                        var previous = _lease;
                        _lease = lease;
                        Source = lease.Bitmap;
                        HasImage = true;
                        if (isLogo && !ChannelIsScrolling)
                        {
                            Opacity = 0;
                            _logoFadeStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                            _logoFadeTimer.Start();
                        }
                        else Opacity = 1;
                        // 先换上新图再归还占位的小图租约（位图只由缓存释放）。
                        previous?.Dispose();
                        lease = null;
                    });
                    return;
                }
                catch (OperationCanceledException) when (scope.IsCancellationRequested) { return; }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    System.Diagnostics.Debug.WriteLine($"[poster] {error.Message}");
                }
                finally { lease?.Dispose(); }
            }
        }
        catch (OperationCanceledException) when (scope.IsCancellationRequested) { }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (ReferenceEquals(_load, scope)) _load = null;
                scope.Dispose();
            });
        }
    }
}
