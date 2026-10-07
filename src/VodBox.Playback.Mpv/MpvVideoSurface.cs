using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;

using VodBox.Core;

namespace VodBox.Playback.Mpv;

/// <summary>libmpv 视频渲染面（占位实现：libmpv render API 需要 OpenGL 上下文接入 Avalonia 的 IGlPlatformSurface）。</summary>
public sealed class MpvVideoSurface : Control
{
    private readonly MpvEngine _engine;

    public MpvVideoSurface(MpvEngine engine)
    {
        _engine = engine;
        ClipToBounds = true;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        // 视频 GL 渲染经 NativeControlHost / render-context 接入（后续接入 RenderApi）
        if (_engine.Snapshot is { State: PlaybackState.Idle or PlaybackState.Failed })
        {
            context.FillRectangle(Brushes.Black, new Rect(Bounds.Size));
        }
    }
}
