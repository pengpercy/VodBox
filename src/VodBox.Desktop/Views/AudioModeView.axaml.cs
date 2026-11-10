using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using VodBox.Desktop.ViewModels;

namespace VodBox.Desktop.Views;

/// <summary>
/// 听视频页。一个逐帧循环驱动全部持续动画，每个量都朝目标值做指数缓动：
/// 唱片转速（播放时 33⅓ 转的观感、暂停时惯性停下）、唱臂落下/抬起、光晕呼吸、频谱起伏、背景漂移。
/// 入场/退场是透明度 + 轻微缩放的过渡。页面不可见时循环停止，不占用渲染。
/// </summary>
public partial class AudioModeView : UserControl
{
    /// <summary>频谱竖条数。</summary>
    internal const int BarCount = 28;
    /// <summary>播放时每秒转过的角度（约 6 秒一圈，比真实黑胶慢，海报看得清）。</summary>
    private const double SpinDegreesPerSecond = 60;
    private const double ArmOnAngle = 7, ArmOffAngle = -24;
    private const double FadeMs = 420;
    private const double DiscDesignSize = 280, DeckDesignSize = 360;

    private readonly Border[] _bars = new Border[BarCount];
    private readonly double[] _barLevels = new double[BarCount];
    private readonly double[] _barSeeds = new double[BarCount];
    private readonly DispatcherTimer _hideTimer = new() { Interval = TimeSpan.FromMilliseconds(FadeMs + 30) };
    private PlayerViewModel? _player;
    private bool _active;
    private bool _looping;
    private TimeSpan? _lastFrame;
    private double _time;
    private double _angle;
    private double _spin;
    private double _energy;
    private double _arm = ArmOffAngle;

    public AudioModeView()
    {
        AvaloniaXamlLoader.Load(this);
        IsVisible = false;
        Opacity = 0;
        Transitions = [new Avalonia.Animation.DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(FadeMs), Easing = new Avalonia.Animation.Easings.CubicEaseOut() }];
        if (this.FindControl<Panel>("Stage") is { } stage)
            stage.Transitions = [new Avalonia.Animation.TransformOperationsTransition { Property = RenderTransformProperty, Duration = TimeSpan.FromMilliseconds(FadeMs + 120), Easing = new Avalonia.Animation.Easings.CubicEaseOut() }];
        var random = new Random(7);
        var bars = this.FindControl<StackPanel>("Bars")!;
        for (var i = 0; i < BarCount; i++)
        {
            _barSeeds[i] = random.NextDouble() * Math.PI * 2;
            _bars[i] = new Border
            {
                Width = 4, Height = 34, CornerRadius = new CornerRadius(2), VerticalAlignment = VerticalAlignment.Bottom,
                RenderTransformOrigin = new RelativePoint(.5, 1, RelativeUnit.Relative),
                RenderTransform = new ScaleTransform(1, .08),
                Background = new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(.5, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(.5, 1, RelativeUnit.Relative),
                    GradientStops = { new GradientStop(Color.Parse("#FFFFC48A"), 0), new GradientStop(Color.Parse("#FFE5536B"), 1) },
                },
            };
            bars.Children.Add(_bars[i]);
        }
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            if (!_active) IsVisible = false;
        };
        DataContextChanged += (_, _) => Observe(DataContext as PlayerViewModel);
        DetachedFromVisualTree += (_, _) => { Observe(null); _looping = false; };
        AttachedToVisualTree += (_, _) => Observe(DataContext as PlayerViewModel);
    }

    /// <summary>是否处于听视频模式（测试与 PlayerOverlay 读取）。</summary>
    internal bool IsActive => _active;
    internal double SpinSpeed => _spin;
    internal double DiscAngle => _angle;

    private void Observe(PlayerViewModel? player)
    {
        if (_player is not null) _player.PropertyChanged -= OnPlayerChanged;
        _player = player;
        if (_player is not null) _player.PropertyChanged += OnPlayerChanged;
        SetActive(_player is { AudioOnly: true, Visible: true });
    }

    private void OnPlayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PlayerViewModel.AudioOnly) or nameof(PlayerViewModel.Visible))
            SetActive(_player is { AudioOnly: true, Visible: true });
        else if (e.PropertyName == nameof(PlayerViewModel.State)) StartLoop();
    }

    private void SetActive(bool active)
    {
        if (_active == active) return;
        _active = active;
        var stage = this.FindControl<Panel>("Stage");
        if (active)
        {
            _hideTimer.Stop();
            // 先摆到起点再显示，下一渲染帧再给终点，过渡才会真正播放。
            Opacity = 0;
            if (stage is not null) stage.RenderTransform = TransformOperations.Parse("scale(0.9)");
            IsVisible = true;
            Dispatcher.UIThread.Post(() =>
            {
                if (!_active) return;
                Opacity = 1;
                if (stage is not null) stage.RenderTransform = TransformOperations.Parse("scale(1)");
            }, DispatcherPriority.Render);
            StartLoop();
        }
        else
        {
            Opacity = 0;
            if (stage is not null) stage.RenderTransform = TransformOperations.Parse("scale(1.04)");
            _hideTimer.Start();
        }
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        ApplyLayout(e.NewSize);
    }

    /// <summary>宽窗口左右排布，窄或竖窗口上下排布；唱片尺寸跟随可用空间。</summary>
    internal void ApplyLayout(Size size)
    {
        if (this.FindControl<StackPanel>("Layout") is not { } content || this.FindControl<Viewbox>("DeckBox") is not { } deck ||
            this.FindControl<StackPanel>("Info") is not { } info) return;
        var width = Math.Max(0, size.Width - 48);
        var height = Math.Max(0, size.Height - 176);
        var horizontal = width >= 620 && width > height * 1.25;
        content.Orientation = horizontal ? Orientation.Horizontal : Orientation.Vertical;
        content.Spacing = horizontal ? 24 : 0;
        var disc = horizontal ? Math.Min(height * .92, width * .42) : Math.Min(width * .7, (height - 150) * .95);
        disc = Math.Clamp(disc, 120, 420);
        // DeckBox 含四周留白：唱片本体占设计框的 280/360。
        deck.Width = deck.Height = disc * DeckDesignSize / DiscDesignSize;
        info.HorizontalAlignment = horizontal ? HorizontalAlignment.Left : HorizontalAlignment.Center;
        var align = horizontal ? HorizontalAlignment.Left : HorizontalAlignment.Center;
        var textAlign = horizontal ? TextAlignment.Left : TextAlignment.Center;
        foreach (var name in new[] { "TitleText", "SubtitleText", "StatusText" })
            if (this.FindControl<TextBlock>(name) is { } text) { text.TextAlignment = textAlign; text.HorizontalAlignment = align; }
        if (this.FindControl<Control>("Badge") is { } badge) badge.HorizontalAlignment = align;
        if (this.FindControl<Control>("Bars") is { } bars) bars.HorizontalAlignment = align;
        info.MaxWidth = horizontal ? Math.Clamp(width - deck.Width - 52, 200, 420) : Math.Min(width, 460);
    }

    private void StartLoop()
    {
        if (_looping || !IsVisible || TopLevel.GetTopLevel(this) is not { } top) return;
        if (Step(0)) return;
        _looping = true;
        _lastFrame = null;
        top.RequestAnimationFrame(OnFrame);
    }

    private void OnFrame(TimeSpan now)
    {
        if (!_looping) return;
        var dt = _lastFrame is { } last ? Math.Clamp((now - last).TotalSeconds, 0, .1) : 0;
        _lastFrame = now;
        var settled = Step(dt);
        // 不可见或所有量都已静止（暂停/退场完成）：停循环，状态变化时再起。
        if (!IsVisible || settled)
        {
            _looping = false;
            return;
        }
        if (TopLevel.GetTopLevel(this) is { } top) top.RequestAnimationFrame(OnFrame);
        else _looping = false;
    }

    private static double Approach(double value, double target, double dt, double tau) =>
        target + (value - target) * Math.Exp(-dt / tau);

    /// <summary>推进一帧；返回各动画量是否都已落到静止目标。</summary>
    internal bool Step(double dt)
    {
        var playing = _active && _player?.State == VodBox.Core.PlaybackState.Playing;
        var spinning = _active && _player?.State is VodBox.Core.PlaybackState.Playing or VodBox.Core.PlaybackState.Buffering;
        _time += dt;
        // 起转快、停转慢：像唱盘的电机与惯性。
        _spin = Approach(_spin, spinning ? SpinDegreesPerSecond : 0, dt, spinning ? .45 : .9);
        _angle = (_angle + _spin * dt) % 360;
        _arm = Approach(_arm, spinning ? ArmOnAngle : ArmOffAngle, dt, .35);
        _energy = Approach(_energy, playing ? 1 : 0, dt, playing ? .3 : .5);

        if (this.FindControl<Panel>("Disc")?.RenderTransform is RotateTransform disc) disc.Angle = _angle;
        if (this.FindControl<Panel>("Arm")?.RenderTransform is RotateTransform arm) arm.Angle = _arm;
        if (this.FindControl<Control>("Glow") is { } glow)
        {
            var breath = .5 + .5 * Math.Sin(_time * 1.6);
            glow.Opacity = _energy * (.55 + .35 * breath);
            if (glow.RenderTransform is ScaleTransform scale) scale.ScaleX = scale.ScaleY = .94 + .08 * _energy * breath;
        }
        if (this.FindControl<Control>("Backdrop")?.RenderTransform is ScaleTransform backdrop)
            backdrop.ScaleX = backdrop.ScaleY = 1.2 + .06 * Math.Sin(_time * .15);

        var barsSettled = true;
        for (var i = 0; i < BarCount; i++)
        {
            var seed = _barSeeds[i];
            // 几路不同频率的正弦叠加：低频段整体更高，避免看起来像均匀噪声。
            var wave = (Math.Sin(_time * (5.3 + i * .21) + seed) + Math.Sin(_time * (8.9 - i * .13) + seed * 1.7) * .6 + Math.Sin(_time * 2.1 + i * .5) * .4 + 2) / 4;
            var weight = 1 - .45 * i / BarCount;
            var target = .08 + .92 * _energy * wave * weight;
            _barLevels[i] = Approach(_barLevels[i], target, dt, .07);
            if (Math.Abs(_barLevels[i] - .08) > .005) barsSettled = false;
            if (_bars[i].RenderTransform is ScaleTransform bar) bar.ScaleY = _barLevels[i];
        }
        return _spin < .5 && _energy < .005 && Math.Abs(_arm - ArmOffAngle) < .2 && barsSettled;
    }
}
