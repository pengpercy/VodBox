using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using Avalonia.Threading;
using VodBox.Core;
using VodBox.Desktop.ViewModels;

namespace VodBox.Desktop.Views;

/// <summary>
/// 「统计信息」面板：显示时每 500ms 向引擎采样一次，更新文字与网速/缓冲两条滚动图；
/// 隐藏后停止采样、清空曲线，下次打开重新开始。
/// </summary>
public partial class StatsPanel : UserControl
{
    internal static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(500);
    private readonly DispatcherTimer _timer = new() { Interval = SampleInterval };
    private PlayerViewModel? _player;
    private long _sampleSession = -1;
    private bool _sampling;
    private Point? _dragStart;
    private Thickness _dragOrigin;

    public StatsPanel()
    {
        AvaloniaXamlLoader.Load(this);
        IsVisible = false;
        _timer.Tick += async (_, _) => await SampleAsync();
        DataContextChanged += (_, _) => Observe(DataContext as PlayerViewModel);
        AttachedToVisualTree += (_, _) => Observe(DataContext as PlayerViewModel);
        DetachedFromVisualTree += (_, _) => { Observe(null); _timer.Stop(); };
        // 拖动：按住面板任意空白处移动，限制在视频区域内；关闭按钮照常点击。
        PointerPressed += OnDragStart;
        PointerMoved += OnDragMove;
        PointerReleased += (_, e) => EndDrag(e.Pointer);
        PointerCaptureLost += (_, _) => _dragStart = null;
        Cursor = new Cursor(StandardCursorType.SizeAll);
    }

    private void OnDragStart(object? sender, PointerPressedEventArgs e)
    {
        if (Parent is not Visual parent || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.Source is Visual source && (source is Button || source.GetVisualAncestors().Any(v => v is Button))) return;
        _dragStart = e.GetPosition(parent);
        _dragOrigin = Margin;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    private void OnDragMove(object? sender, PointerEventArgs e)
    {
        if (_dragStart is not { } start || Parent is not Visual parent) return;
        var delta = e.GetPosition(parent) - start;
        MoveTo(_dragOrigin.Left + delta.X, _dragOrigin.Top + delta.Y);
        e.Handled = true;
    }

    private void EndDrag(IPointer pointer)
    {
        if (_dragStart is null) return;
        _dragStart = null;
        pointer.Capture(null);
    }

    /// <summary>把面板左上角移到 (left, top)，并夹在父容器（视频区域）之内。</summary>
    internal void MoveTo(double left, double top)
    {
        var area = (Parent as Visual)?.Bounds.Size ?? default;
        var size = Bounds.Size;
        if (area.Width > 0) left = Math.Clamp(left, 0, Math.Max(0, area.Width - size.Width));
        if (area.Height > 0) top = Math.Clamp(top, 0, Math.Max(0, area.Height - size.Height));
        Margin = new Thickness(left, top, 0, 0);
    }

    /// <summary>视频区域缩小（退出全屏、画中画）时把面板拉回可见范围。</summary>
    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (IsVisible) Dispatcher.UIThread.Post(() => MoveTo(Margin.Left, Margin.Top), DispatcherPriority.Background);
    }

    internal void KeepInside() { if (IsVisible) MoveTo(Margin.Left, Margin.Top); }

    private void Observe(PlayerViewModel? player)
    {
        if (_player is not null) _player.PropertyChanged -= OnPlayerChanged;
        _player = player;
        if (_player is not null) _player.PropertyChanged += OnPlayerChanged;
        Apply();
    }

    private void OnPlayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PlayerViewModel.StatsVisible) or nameof(PlayerViewModel.Visible)) Apply();
    }

    private void Apply()
    {
        var show = _player is { StatsVisible: true, Visible: true } && VisualRoot is not null;
        IsVisible = show;
        if (!show)
        {
            _timer.Stop();
            this.FindControl<StatsGraph>("SpeedGraph")?.Clear();
            this.FindControl<StatsGraph>("BufferGraph")?.Clear();
            return;
        }
        if (_timer.IsEnabled) return;
        _timer.Start();
        _ = SampleAsync();
    }

    /// <summary>采样一次并刷新显示；测试直接调用。</summary>
    internal async Task SampleAsync()
    {
        if (_sampling || _player is not { } player || !IsVisible) return;
        _sampling = true;
        try
        {
            PlaybackStats? stats = null;
            try { stats = await player.GetStatsAsync(); }
            catch (Exception error) { VodBoxLog.Info("player", $"统计信息采样失败：{error.Message}"); }
            if (!IsVisible || !ReferenceEquals(player, _player)) return;
            // 换集/换源后曲线从头画，不和上一个媒体的数据接在一起。
            if (_sampleSession != player.CurrentSessionId)
            {
                _sampleSession = player.CurrentSessionId;
                this.FindControl<StatsGraph>("SpeedGraph")?.Clear();
                this.FindControl<StatsGraph>("BufferGraph")?.Clear();
            }
            Show(player, stats);
        }
        finally { _sampling = false; }
    }

    private void Show(PlayerViewModel player, PlaybackStats? stats)
    {
        var top = TopLevel.GetTopLevel(this);
        var viewport = top?.ClientSize ?? default;
        var rows = StatsFormatter.Rows(player.Title, player.SourceName, player.MediaUri, stats, viewport, top?.RenderScaling ?? 1,
            player.Volume, player.Rate, player.State, player.AudioOnly, DateTime.Now);
        Set("MediaText", rows.Media);
        Set("UriText", rows.Uri);
        Set("ViewportText", rows.Viewport);
        Set("ResolutionText", rows.Resolution);
        Set("VolumeText", rows.Volume);
        Set("CodecText", rows.Codec);
        Set("ColorText", rows.Color);
        Set("BitrateText", rows.Bitrate);
        Set("SpeedText", rows.Speed);
        Set("BufferText", rows.Buffer);
        Set("ContainerText", rows.Container);
        Set("DateText", rows.Date);
        if (this.FindControl<TextBlock>("UriText") is { } uri) ToolTip.SetTip(uri, player.MediaUri);
        this.FindControl<StatsGraph>("SpeedGraph")?.Push(stats?.NetworkSpeed);
        this.FindControl<StatsGraph>("BufferGraph")?.Push(stats?.BufferSeconds);
    }

    private void Set(string name, string text)
    {
        if (this.FindControl<TextBlock>(name) is { } block) block.Text = text;
    }
}

/// <summary>统计面板各行文字。</summary>
internal sealed record StatsRows(string Media, string Uri, string Viewport, string Resolution, string Volume, string Codec,
    string Color, string Bitrate, string Speed, string Buffer, string Container, string Date);

internal static class StatsFormatter
{
    private const string Missing = "—";
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private static readonly CultureInfo Chinese = CultureInfo.GetCultureInfo("zh-CN");

    public static StatsRows Rows(string title, string? source, string? uri, PlaybackStats? stats, Size viewport, double scaling,
        int volume, double rate, PlaybackState state, bool audioOnly, DateTime now)
    {
        var media = string.IsNullOrWhiteSpace(source) ? title : $"{title} / {source}";
        var view = viewport.Width > 0 ? $"{viewport.Width:0}×{viewport.Height:0}*{scaling.ToString("0.##", Invariant)}" : Missing;
        var dropped = stats?.DroppedFrames is { } drops
            ? $"丢弃 {drops + (stats.DecoderDroppedFrames ?? 0)} 帧" + (stats.FrameNumber is { } frames ? $"，共 {frames} 帧" : "")
            : Missing;
        string resolution;
        if (audioOnly) resolution = "听视频模式（视频轨已关闭）";
        else if (stats is { VideoWidth: { } w, VideoHeight: { } h })
            resolution = $"{w}×{h}" + (stats.FramesPerSecond is { } fps ? $"@{fps.ToString("0.##", Invariant)}" : "");
        else resolution = Missing;
        var codec = Join(" / ",
            stats?.VideoCodec is { } video ? video + (stats.HardwareDecoder is { } hw && hw != "no" ? $"（硬解 {hw}）" : "（软解）") : null,
            stats?.AudioCodec is { } audio ? audio + Audio(stats) : null);
        var color = Join(" / ", stats?.ColorMatrix, stats?.ColorPrimaries, stats?.PixelFormat);
        var bitrate = Join(" · ",
            stats?.VideoBitrate is > 0 and var vb ? $"视频 {Rate(vb)}" : null,
            stats?.AudioBitrate is > 0 and var ab ? $"音频 {Rate(ab)}" : null);
        var buffer = stats?.BufferSeconds is { } seconds ? $"{seconds.ToString("0.00", Invariant)} s" : Missing;
        var status = state switch
        {
            PlaybackState.Playing => "播放中", PlaybackState.Paused => "已暂停", PlaybackState.Buffering => "缓冲中",
            PlaybackState.Loading or PlaybackState.Resolving => "加载中", PlaybackState.Ended => "已结束",
            PlaybackState.Failed => "播放失败", _ => "空闲",
        };
        return new StatsRows(media, string.IsNullOrWhiteSpace(uri) ? Missing : uri, $"{view} / {dropped}", resolution,
            $"{volume}% / {rate.ToString("0.##", Invariant)}×", codec, color, bitrate,
            stats?.NetworkSpeed is { } speed ? Rate(speed) : Missing, buffer,
            Join(" / ", stats?.Container, status), now.ToString("yyyy-MM-dd dddd HH:mm:ss", Chinese));
    }

    private static string Audio(PlaybackStats stats)
    {
        var parts = new List<string>();
        if (stats.AudioSampleRate is { } rate) parts.Add($"{(rate / 1000d).ToString("0.#", Invariant)} kHz");
        if (stats.AudioChannels is { } channels) parts.Add($"{channels} 声道");
        return parts.Count == 0 ? "" : $"（{string.Join(" ", parts)}）";
    }

    /// <summary>bit/s → 「850 Kbps」「12.4 Mbps」。</summary>
    public static string Rate(double bitsPerSecond) => bitsPerSecond >= 10_000_000
        ? $"{(bitsPerSecond / 1_000_000).ToString("0.0", Invariant)} Mbps"
        : $"{(bitsPerSecond / 1000).ToString("0", Invariant)} Kbps";

    private static string Join(string separator, params string?[] parts)
    {
        var present = parts.Where(part => !string.IsNullOrWhiteSpace(part)).ToArray();
        return present.Length == 0 ? Missing : string.Join(separator, present);
    }
}
