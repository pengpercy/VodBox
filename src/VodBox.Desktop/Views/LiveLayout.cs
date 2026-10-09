namespace VodBox.Desktop.Views;

/// <summary>
/// 频道行的几何常量。台标多为宽幅品牌图（实测约 2.2:1），用正方形框会让图缩得很小，
/// 因此这里用宽框；XAML 与对齐回归共用同一组数值。
/// </summary>
public static class LiveLayout
{
    /// <summary>台标框宽（宽幅台标按 Uniform 缩放后仍能看清）。</summary>
    public const double BadgeWidth = 44;
    /// <summary>台标框高。</summary>
    public const double BadgeHeight = 28;
    /// <summary>台标与频道名之间的间距。</summary>
    public const double BadgeGap = 10;
    /// <summary>频道名列宽。</summary>
    public const double NameColumnWidth = 176;
    /// <summary>频道名左边距。</summary>
    public static readonly Avalonia.Thickness NameMargin = new(BadgeGap, 0, 0, 0);
    /// <summary>EPG 行左边距（上方多 2px 行距）。</summary>
    public static readonly Avalonia.Thickness EpgMargin = new(BadgeGap, 2, 0, 0);
    /// <summary>整行内容宽：台标 + 间距 + 名称列。</summary>
    public const double RowWidth = BadgeWidth + BadgeGap + NameColumnWidth;
}
