using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace VodBox.Desktop.Views;

/// <summary>
/// 统计面板里的滚动条形图：最近 <see cref="Capacity"/> 次采样从左到右排开，最新的在最右；
/// 高度按窗口内最大值归一，柱顶描一道浅色细线，读出起伏。
/// </summary>
public sealed class StatsGraph : Control
{
    public const int Capacity = 100;
    public static readonly StyledProperty<IBrush?> FillProperty = AvaloniaProperty.Register<StatsGraph, IBrush?>(nameof(Fill));
    public static readonly StyledProperty<IBrush?> PeakProperty = AvaloniaProperty.Register<StatsGraph, IBrush?>(nameof(Peak));
    private static readonly IBrush Track = new SolidColorBrush(Color.FromArgb(0xCC, 0, 0, 0));
    private readonly double[] _values = new double[Capacity];
    private int _count;
    private int _next;

    static StatsGraph() => AffectsRender<StatsGraph>(FillProperty, PeakProperty);

    public IBrush? Fill { get => GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public IBrush? Peak { get => GetValue(PeakProperty); set => SetValue(PeakProperty, value); }
    internal int Count => _count;

    /// <summary>追加一次采样；缺失值记为 0（断流时图上表现为落到底）。</summary>
    public void Push(double? value)
    {
        _values[_next] = value is { } v && double.IsFinite(v) && v > 0 ? v : 0;
        _next = (_next + 1) % Capacity;
        _count = Math.Min(_count + 1, Capacity);
        InvalidateVisual();
    }

    public void Clear()
    {
        _count = 0;
        _next = 0;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(Track, bounds);
        if (_count == 0 || bounds.Width <= 0 || bounds.Height <= 0) return;
        var max = 0d;
        for (var i = 0; i < _count; i++) max = Math.Max(max, _values[i]);
        if (max <= 0) return;
        var step = bounds.Width / Capacity;
        // 最新采样贴右边，历史向左延伸。
        var start = bounds.Width - _count * step;
        for (var i = 0; i < _count; i++)
        {
            var value = _values[(_next - _count + i + Capacity) % Capacity];
            var height = Math.Max(1, value / max * bounds.Height);
            var x = start + i * step;
            var bar = new Rect(x, bounds.Height - height, step + .5, height);
            context.FillRectangle(Fill ?? Brushes.White, bar);
            context.FillRectangle(Peak ?? Brushes.White, new Rect(x, bar.Y, step + .5, Math.Min(2, height)));
        }
    }
}
