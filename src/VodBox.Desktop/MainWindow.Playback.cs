using Avalonia.Threading;
using VodBox.Application;
using VodBox.Core;
using VodBox.Playback.LibVlc;
using VodBox.Playback.Mpv;

namespace VodBox.Desktop;

public sealed partial class MainWindow
{
    private readonly HashSet<IPlaybackEngine> _observedEngines = [];
    private readonly CancellationTokenSource _surfaceLifetime = new();
    private MpvVideoSurface? _mpvSurface;
    private volatile bool _mpvReady;
    private volatile Exception? _mpvError;
    public long MpvRenderedFrames => _mpvSurface?.RenderedFrames ?? 0;
    public int ActiveDanmakuCount => _viewModel?.Engine.ActiveKind == PlaybackEngineKind.Mpv ? DanmakuPreview.ActiveCount : _danmakuOverlay?.ActiveCount ?? 0;

    private void EngineChanged(object? sender, ActivePlaybackEngine choice) => Dispatcher.UIThread.Invoke(() =>
    {
        if (_closing || _viewModel is null || !ReferenceEquals(_viewModel.Engine.ActiveEngine, choice.Engine)) return;
        if (_observedEngines.Add(choice.Engine))
        {
            if (choice.Engine is LibVlcEngine vlc) vlc.Initialized += NativeInitialized;
            if (choice.Engine is MpvEngine mpv) mpv.Initialized += NativeInitialized;
        }
        BindSurface(choice.Engine);
    });
    private void NativeInitialized(object? sender, EventArgs args) => Dispatcher.UIThread.Invoke(() =>
    {
        if (!_closing && sender is IPlaybackEngine engine && ReferenceEquals(_viewModel?.Engine.ActiveEngine, engine)) BindSurface(engine);
    });
    private void BindSurface(IPlaybackEngine engine)
    {
        bool isVlc = engine is LibVlcEngine;
        VideoSurface.MediaPlayer = isVlc ? ((LibVlcEngine)engine).Player : null;
        VideoSurface.IsVisible = isVlc;
        DanmakuPreview.IsVisible = !isVlc;
        if (engine is MpvEngine mpv && mpv.Client is MpvClient client && _mpvSurface is null)
        {
            _mpvSurface = new(client);
            _mpvSurface.Ready += (_, _) => { _mpvReady = true; mpv.NotifyVideoSurfaceReady(); };
            _mpvSurface.Failed += (_, error) => { _mpvError = error; mpv.NotifyVideoSurfaceFailure(error); _viewModel?.Engine.ReportSurfaceFailure(mpv, error); };
            VideoContainer.Children.Insert(0, _mpvSurface);
            _ = WatchRendererAsync(mpv);
        }
        // Reuse the render context across switches: a client supports only one render context.
        if (_mpvSurface is not null) _mpvSurface.IsVisible = !isVlc;
        if (!isVlc && _mpvError is { } error) _viewModel?.Engine.ReportSurfaceFailure(engine, error);
    }
    private async Task WatchRendererAsync(MpvEngine engine)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(8), _surfaceLifetime.Token);
            if (!_mpvReady && !_closing)
            {
                _mpvError = new NotSupportedException("当前图形后端未能创建 mpv OpenGL 视频表面。");
                _viewModel?.Engine.ReportSurfaceFailure(engine, _mpvError);
            }
        }
        catch (OperationCanceledException) { }
    }
}
