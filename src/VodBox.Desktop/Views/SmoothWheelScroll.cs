using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace VodBox.Desktop.Views;

/// <summary>
/// 平滑滚轮：Avalonia 默认每格滚轮把偏移瞬间跳 50px，快速滚动时画面是一格一格地跳。
/// 这里把滚轮量累加成目标偏移，逐帧指数逼近（约 150ms 收敛），每格距离不变，只是把跳变变成连续运动。
/// 滚动条拖动、键盘或代码改了偏移时立即让位，不与之抢。
/// </summary>
public static class SmoothWheelScroll
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("IsEnabled", typeof(SmoothWheelScroll));

    /// <summary>与 Avalonia 非逻辑滚动的每格距离一致，滚动速度不变。</summary>
    internal const double WheelStep = 50;
    /// <summary>逼近的时间常数：每 TimeConstant 剩余距离缩为 1/e。</summary>
    internal static readonly TimeSpan TimeConstant = TimeSpan.FromMilliseconds(45);

    private static readonly AttachedProperty<Animator?> AnimatorProperty =
        AvaloniaProperty.RegisterAttached<Control, Animator?>("Animator", typeof(SmoothWheelScroll));

    static SmoothWheelScroll()
    {
        IsEnabledProperty.Changed.AddClassHandler<Control>((control, change) =>
        {
            control.RemoveHandler(InputElement.PointerWheelChangedEvent, OnWheel);
            if (change.GetNewValue<bool>())
                control.AddHandler(InputElement.PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel);
        });
    }

    public static bool GetIsEnabled(Control control) => control.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(Control control, bool value) => control.SetValue(IsEnabledProperty, value);

    private static void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        if (sender is not Control control || e.Delta.Y == 0) return;
        var scroller = control as ScrollViewer ?? control.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (scroller is null || TopLevel.GetTopLevel(control) is not { } top) return;
        var animator = control.GetValue(AnimatorProperty);
        if (animator is null) control.SetValue(AnimatorProperty, animator = new Animator());
        animator.Add(scroller, top, -e.Delta.Y * WheelStep);
        e.Handled = true;
    }

    internal sealed class Animator
    {
        private ScrollViewer? _scroller;
        private TopLevel? _top;
        private double _target;
        private double _written;
        private TimeSpan? _last;
        private bool _running;

        public void Add(ScrollViewer scroller, TopLevel top, double distance)
        {
            var current = scroller.Offset.Y;
            // 未在动画中、换了滚动目标、偏移被别处改过，或者反向滚动：从当前位置重新起算。
            if (!_running || !ReferenceEquals(scroller, _scroller) || Math.Abs(current - _written) > 0.5
                || Math.Sign(distance) != Math.Sign(_target - current))
                _target = current;
            _scroller = scroller;
            _top = top;
            _target = Math.Clamp(_target + distance, 0, MaxOffset(scroller));
            _written = current;
            if (_running) return;
            _running = true;
            _last = null;
            top.RequestAnimationFrame(Tick);
        }

        private static double MaxOffset(ScrollViewer scroller) => Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height);

        private void Tick(TimeSpan now)
        {
            if (_scroller is not { } scroller || _top is not { } top) { _running = false; return; }
            var current = scroller.Offset.Y;
            if (Math.Abs(current - _written) > 0.5) { _running = false; return; }
            // 首帧或时间戳未前进时按一帧 60Hz 计，避免步长退化为 0 而提前停住。
            var elapsed = _last is { } last && now > last ? (now - last).TotalMilliseconds : 16.7;
            _last = now;
            _target = Math.Clamp(_target, 0, MaxOffset(scroller));
            var next = current + (_target - current) * (1 - Math.Exp(-elapsed / TimeConstant.TotalMilliseconds));
            // 剩余不到半像素就直接落到目标，保证每格距离精确、多格累加不漂移。
            if (Math.Abs(_target - next) < 0.5) next = _target;
            scroller.Offset = new Vector(scroller.Offset.X, next);
            _written = scroller.Offset.Y;
            // 已落到目标，或滚动视图没接受这个偏移（到顶/到底被夹住）就停。
            if (next == _target || Math.Abs(_written - next) > 0.5) { _running = false; return; }
            top.RequestAnimationFrame(Tick);
        }
    }
}
