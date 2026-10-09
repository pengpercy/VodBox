using Avalonia;

namespace VodBox.Desktop.Views;

/// <summary>
/// 播放控制条的唯一次寸来源：XAML 用 <c>{x:Static}</c> 引用这里的常量，窗口最小宽度也由同一组
/// 数值推导，因此不会再出现「XAML 改了尺寸、最小宽度没跟着算」的脱节（曾出现 16×28 的瘦长按钮）。
/// 数值来自控件实测（见 PlayerPanelMinimumWidth 回归里的 DesiredSize 校验）。
/// </summary>
public static class PlayerLayout
{
    // ── 顶栏 ────────────────────────────────────────────────────────────────
    /// <summary>顶栏置顶键边长（正方形）。</summary>
    public const double TitleBarButtonSize = 28;

    // ── 图标键（比例/小窗/插件/列表/倍速/全屏/设置/静音）────────────────────
    /// <summary>图标键边长，宽高必须相等。20px 图标居中，四周各留 2px。</summary>
    public const double IconButtonSize = 20;
    /// <summary>图标键内的图标尺寸。</summary>
    public const double IconGlyphSize = 18;
    /// <summary>图标键之间的间距；0 表示按键宽紧贴排列。</summary>
    public const double IconButtonSpacing = 0;
    /// <summary>倍速键宽度（装“1.25x”角标的自适应结果）；实测值，参与最小宽度算式。</summary>
    public const double RateButtonWidth = 56;

    // ── 左侧音量 ────────────────────────────────────────────────────────────
    public const double VolumeSliderWidth = 64;
    public const double VolumeSpacing = 4;

    // ── 中央三键（上一集/播放/下一集）──────────────────────────────────────
    /// <summary>三键边长，刻意大于图标键以保持主次层次。</summary>
    public const double TransportButtonSize = 34;
    public const double TransportGlyphSize = 18;
    public const double TransportSpacing = 8;

    // ── 控制条容器 ──────────────────────────────────────────────────────────
    /// <summary>上下两行之间的间距。</summary>
    public const double RowSpacing = 7;
    /// <summary>控制条内边距（左、上、右、下）。</summary>
    public static readonly Thickness PanelPadding = new(16, 12, 16, 13);
    /// <summary>控制条外边距（仅底部留白）。</summary>
    public static readonly Thickness PanelMargin = new(0, 0, 0, 13);

    // ── 由上述尺寸推导的布局约束 ────────────────────────────────────────────
    /// <summary>左侧组：静音键 + 间距 + 音量滑杆。</summary>
    public const double LeftGroupWidth = IconButtonSize + VolumeSpacing + VolumeSliderWidth;
    /// <summary>右侧组：六个等宽图标键 + 自适应的倍速键。</summary>
    public const double RightGroupWidth = 6 * IconButtonSize + RateButtonWidth;
    /// <summary>中央三键整组宽度。</summary>
    public const double TransportWidth = 3 * TransportButtonSize + 2 * TransportSpacing;
    /// <summary>紧凑模式左侧只剩静音键。</summary>
    public const double CompactLeftGroupWidth = IconButtonSize;
    /// <summary>紧凑模式右侧只剩小窗/列表/倍速/全屏/设置。</summary>
    public const double CompactRightGroupWidth = 4 * IconButtonSize + RateButtonWidth;
    /// <summary>紧凑模式中央只剩播放键。</summary>
    public const double CompactTransportWidth = TransportButtonSize;
    /// <summary>三键与左右组之间必须保留的最小空隙。</summary>
    public const double GroupGap = 12;
    /// <summary>控制条左右内边距合计。</summary>
    public const double HorizontalPadding = 16 + 16;
    /// <summary>控制条相对窗口左右各留出的余量。</summary>
    public const double WindowMargin = 20;

    /// <summary>
    /// 面板内容宽 C 需满足：三键居中占 [C/2−半宽, C/2+半宽]，两侧组不得与其相交，
    /// 故 C ≥ 2 × (较宽一侧组 + 三键半宽 + 空隙) + 左右内边距。
    /// </summary>
    public static double MinimumPanelWidth(bool compact) =>
        2 * (Math.Max(compact ? CompactLeftGroupWidth : LeftGroupWidth,
                      compact ? CompactRightGroupWidth : RightGroupWidth)
             + (compact ? CompactTransportWidth : TransportWidth) / 2
             + GroupGap) + HorizontalPadding;

    /// <summary>窗口最小宽度：面板需求再加左右余量。</summary>
    public static double MinimumWindowWidth(bool compact) => MinimumPanelWidth(compact) + WindowMargin;
}
