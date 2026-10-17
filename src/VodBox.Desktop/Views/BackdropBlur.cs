using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;

namespace VodBox.Desktop.Views;

/// <summary>
/// 毛玻璃底：对自身区域下方已绘制的内容（视频画面、弹幕）做高斯模糊，放在半透明面板背后即成毛玻璃。
/// 视频由 OpenGlControlBase 在合成器内绘制，因此 Skia 背景滤镜能取到画面；拿不到 Skia 租约（如 Headless）时不绘制。
/// </summary>
public sealed class BackdropBlur : Control
{
    public static readonly StyledProperty<double> BlurRadiusProperty =
        AvaloniaProperty.Register<BackdropBlur, double>(nameof(BlurRadius), 24);

    public static readonly StyledProperty<CornerRadius> CornerRadiusProperty =
        Border.CornerRadiusProperty.AddOwner<BackdropBlur>();

    static BackdropBlur() => AffectsRender<BackdropBlur>(BlurRadiusProperty, CornerRadiusProperty);

    /// <summary>模糊半径（DIP），换算为高斯 sigma 时取一半。</summary>
    public double BlurRadius
    {
        get => GetValue(BlurRadiusProperty);
        set => SetValue(BlurRadiusProperty, value);
    }

    public CornerRadius CornerRadius
    {
        get => GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        if (BlurRadius > 0 && Bounds.Width > 0 && Bounds.Height > 0)
            context.Custom(new BlurOperation(new Rect(Bounds.Size), CornerRadius.TopLeft, BlurRadius / 2));
    }

    private sealed record BlurOperation(Rect Bounds, double CornerRadius, double Sigma) : ICustomDrawOperation
    {
        public bool HitTest(Point p) => false;

        public bool Equals(ICustomDrawOperation? other) => other is BlurOperation blur && Equals(blur);

        public void Render(ImmediateDrawingContext context)
        {
            if (context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } feature) return;
            using var lease = feature.Lease();
            var canvas = lease.SkCanvas;
            var rect = new SKRect(0, 0, (float)Bounds.Width, (float)Bounds.Height);
            using var clip = new SKRoundRect(rect, (float)CornerRadius);
            using var filter = SKImageFilter.CreateBlur((float)Sigma, (float)Sigma, SKShaderTileMode.Clamp);
            canvas.Save();
            canvas.ClipRoundRect(clip, antialias: true);
            canvas.SaveLayer(new SKCanvasSaveLayerRec { Bounds = rect, Backdrop = filter });
            canvas.Restore();
            canvas.Restore();
        }

        public void Dispose() { }
    }
}
