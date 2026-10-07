using System;
using System.Globalization;
using Avalonia.Data.Converters;
using VodBox.Desktop.ViewModels;

namespace VodBox.Desktop.Views;

/// <summary>AppPage 枚举 → bool（当前页高亮）。</summary>
public static class PageConverters
{
    public static readonly IValueConverter IsHome = new PageConverter(AppPage.Home);
    public static readonly IValueConverter IsVod = new PageConverter(AppPage.Vod);
    public static readonly IValueConverter IsLive = new PageConverter(AppPage.Live);
    public static readonly IValueConverter IsSearch = new PageConverter(AppPage.Search);
    public static readonly IValueConverter IsFavorites = new PageConverter(AppPage.Favorites);
    public static readonly IValueConverter IsHistory = new PageConverter(AppPage.History);
    public static readonly IValueConverter IsFiles = new PageConverter(AppPage.Files);
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

public static class StringConverters
{
    public static readonly IValueConverter IsNotNullOrEmpty = new NotNullOrEmptyConverter();

    private sealed class NotNullOrEmptyConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is string s && s.Length > 0;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
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

    private sealed class IsZeroConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is int count && count == 0;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
