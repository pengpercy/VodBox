using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia.Data.Converters;
using VodBox.Desktop.ViewModels;

namespace VodBox.Desktop.Views;

/// <summary>AppPage 枚举 → bool（当前页高亮）。</summary>
public static class PageConverters
{
    public static readonly IValueConverter IsHome = new PageConverter(AppPage.Home);
    public static readonly IValueConverter IsVod = new PageConverter(AppPage.Vod);
    public static readonly IValueConverter IsLive = new PageConverter(AppPage.Live);
    public static readonly IValueConverter IsFavorites = new PageConverter(AppPage.Favorites);
    public static readonly IValueConverter IsHistory = new PageConverter(AppPage.History);
    public static readonly IValueConverter IsSettings = new PageConverter(AppPage.Settings);
    public static readonly IValueConverter IsDetail = new PageConverter(AppPage.Detail);

    private sealed class PageConverter(AppPage target) : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is AppPage page && page == target;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}

/// <summary>直播历史使用台标缓存与频道名回退，点播历史保留海报显示。</summary>
public static class HistoryConverters
{
    public static readonly IValueConverter ChannelName = new FuncValueConverter<VodBox.Core.HistoryEntry?, string?>(
        entry => entry?.SourceKey == "live" ? entry.Title : null);
    public static readonly IValueConverter IsLive = new FuncValueConverter<VodBox.Core.HistoryEntry?, bool>(
        entry => entry?.SourceKey == "live");
    public static readonly IValueConverter IsNotLive = new FuncValueConverter<VodBox.Core.HistoryEntry?, bool>(
        entry => entry?.SourceKey != "live");
    public static readonly IValueConverter CoverStretch = new FuncValueConverter<VodBox.Core.HistoryEntry?, Avalonia.Media.Stretch>(
        entry => entry?.SourceKey == "live" ? Avalonia.Media.Stretch.Uniform : Avalonia.Media.Stretch.UniformToFill);
}

public static class StringConverters
{
    public static readonly IValueConverter IsNotNullOrEmpty = new NotNullOrEmptyConverter();
    public static readonly IValueConverter IsNullOrEmpty = new NullOrEmptyConverter();

    private sealed class NotNullOrEmptyConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is string s && s.Length > 0;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    private sealed class NullOrEmptyConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is not string s || s.Length == 0;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}

/// <summary>骨架屏占位槽位（XAML 经 x:Static 绑定，避免每个 VM 重复定义占位集合）。</summary>
public static class SkeletonData
{
    /// <summary>海报/结果卡占位（覆盖 6 列 × 3 行的常见首屏吞吐）。</summary>
    public static IReadOnlyList<int> Cards { get; } = Enumerable.Range(0, 18).ToArray();
    /// <summary>详情页选集骨架槽位（20 个，约两行）。</summary>
    public static IReadOnlyList<int> Episodes { get; } = Enumerable.Range(0, 20).ToArray();
    /// <summary>站点/线路等横向短条骨架。</summary>
    public static IReadOnlyList<int> Bars { get; } = Enumerable.Range(0, 5).ToArray();
    /// <summary>首页「最近观看」横向卡骨架槽位（6 张）。</summary>
    public static IReadOnlyList<int> Recent { get; } = Enumerable.Range(0, 6).ToArray();
    /// <summary>收藏/历史列表行骨架槽位（4 行）。</summary>
    public static IReadOnlyList<int> Rows { get; } = Enumerable.Range(0, 4).ToArray();
}

/// <summary>进度毫秒 → 0..1 比例。</summary>
public static class ProgressConverters
{
    public static readonly IValueConverter Ratio = new RatioConverter();

    private sealed class RatioConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is long position && parameter is long duration && duration > 0)
                return Math.Clamp((double)position / duration, 0, 1);
            return 0.0;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}


public static class CountConverters
{
    public static readonly IValueConverter IsZero = new IsZeroConverter();
    public static readonly IValueConverter IsNotZero = new IsNotZeroConverter();

    private sealed class IsZeroConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is int count && count == 0;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    private sealed class IsNotZeroConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is int count && count != 0;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}

/// <summary>hex 字符串 → IBrush（台标底色等设计数据）。</summary>
public static class BrushConverters
{
    public static readonly IValueConverter FromHex = new FromHexConverter();

    private sealed class FromHexConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is string hex && Avalonia.Media.Color.TryParse(hex, out var color)
                ? new Avalonia.Media.SolidColorBrush(color)
                : Avalonia.Media.Brushes.DarkSlateBlue;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}

/// <summary>bool → 取反。</summary>
public static class BoolConverters
{
    public static readonly IValueConverter Not = new NotConverter();

    private sealed class NotConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is not true;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    public static readonly IValueConverter ToSelectedRow = new ToSelectedRowConverter();

    private sealed class ToSelectedRowConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is true ? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromUInt32(0xFF0078D4)) : Avalonia.Media.Brushes.Transparent;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}

/// <summary>收藏页点播/直播切换：当前 Tab 是否为该项（选中样式由 favTab.selected 提供，颜色跟随主题）。</summary>
public static class TabConverters
{
    public static readonly IValueConverter IsVod = new FuncValueConverter<int, bool>(tab => tab == 0);
    public static readonly IValueConverter IsLive = new FuncValueConverter<int, bool>(tab => tab == 1);
}

public static class TimeAgoConverters
{
    public static readonly IValueConverter ToAgo = new ToAgoConverter();

    private sealed class ToAgoConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is not DateTimeOffset time) return "";
            var days = (DateTimeOffset.Now - time).TotalDays;
            return days switch
            {
                < 1 => "今天",
                < 2 => "昨天",
                < 7 => $"{(int)days} 天前",
                < 14 => "上周",
                < 30 => $"{(int)(days / 7)} 周前",
                < 60 => "1 个月前",
                _ => $"{(int)(days / 30)} 个月前",
            };
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}

/// <summary>设置页分组导航（int 对比 parameter）→ 样式。</summary>
public static class SectionConverters
{
    public static readonly IValueConverter NavBackground = new NavBackgroundConverter();
    public static readonly IValueConverter NavForeground = new NavForegroundConverter();

    private sealed class NavBackgroundConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is int section && int.TryParse(parameter?.ToString(), out var p) && section == p
                ? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromUInt32(0x1F808080))
                : Avalonia.Media.Brushes.Transparent;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    private sealed class NavForegroundConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            // 文字色跟随主题（不再用浅天蓝区分选中），选中态只由浅中性底色表达。
            => Avalonia.AvaloniaProperty.UnsetValue;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}

/// <summary>媒体卡片的次要信息行：年份 · 地区 · 类型，只拼非空项，缺项时不留孤立的「·」。</summary>
public static class MediaConverters
{
    public static readonly IValueConverter Meta = new FuncValueConverter<VodBox.Core.MediaItem?, string>(item =>
        item is null ? "" : string.Join(" · ", new[] { item.Year, item.Area, item.TypeName }.Where(value => !string.IsNullOrWhiteSpace(value))));
}

/// <summary>数值 → 是否大于 0（例如尺寸为 0 时直接隐藏元素）。</summary>
public static class NumberConverters
{
    public static readonly IValueConverter IsPositive = new FuncValueConverter<double, bool>(value => value > 0);
}
