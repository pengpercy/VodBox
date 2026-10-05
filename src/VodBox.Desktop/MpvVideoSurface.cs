using Avalonia;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Threading;
using VodBox.Playback.Mpv;

namespace VodBox.Desktop;

// GPU surface shared by the desktop player and the isolated render probe.
public sealed class MpvVideoSurface(MpvClient client) : OpenGlControlBase
{
    private MpvRenderContext? _renderer;
    private readonly DispatcherTimer _updates = new() { Interval = TimeSpan.FromMilliseconds(16) };
    public event EventHandler? Ready;
    public event EventHandler<Exception>? Failed;
    public long RenderedFrames { get; private set; }

    protected override void OnOpenGlInit(GlInterface gl)
    {
        try
        {
            _renderer = new(client, gl.GetProcAddress);
            _updates.Tick += OnUpdate;
            _updates.Start();
            Ready?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception error) { Failed?.Invoke(this, error); }
    }
    private void OnUpdate(object? sender, EventArgs args)
    {
        if (_renderer?.HasUpdate == true && IsEffectivelyVisible) RequestNextFrameRendering();
    }
    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        if (_renderer is null) return;
        var scale = Avalonia.Controls.TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        try
        {
            _renderer.Render(fb, Math.Max(1, (int)(Bounds.Width * scale)), Math.Max(1, (int)(Bounds.Height * scale)));
            RenderedFrames++;
        }
        catch (Exception error) { _updates.Stop(); Failed?.Invoke(this, error); }
    }
    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        _updates.Stop(); _updates.Tick -= OnUpdate;
        _renderer?.Dispose(); _renderer = null;
    }
    protected override void OnOpenGlLost()
    {
        _updates.Stop();
        Failed?.Invoke(this, new InvalidOperationException("mpv OpenGL context was lost; recreate the playback surface."));
    }
}
