using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Avalonia.Markup.Xaml;
using VodBox.Core;
using VodBox.Desktop.ViewModels;

namespace VodBox.Desktop.Views;

public static class CategoryConverters
{
    /// <summary>SelectedCategory == item → 选中态。</summary>
    public static readonly IMultiValueConverter Matches = new MatchesConverter();

    private sealed class MatchesConverter : IMultiValueConverter
    {
        public object Convert(System.Collections.Generic.IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
            => values.Count == 2 && Equals(values[0], values[1]);
    }
}

public partial class VodView : UserControl
{
    /// <summary>距底部不足该像素时预取下一页；VM 内部再按代次/去重/终点门控，重复触发是安全的。</summary>
    private const double PrefetchDistance = 320;

    private static readonly Avalonia.Animation.Animation FilterReveal = new()
    {
        Duration = TimeSpan.FromMilliseconds(240),
        Easing = new Avalonia.Animation.Easings.CubicEaseOut(),
        FillMode = Avalonia.Animation.FillMode.Both,
        Children =
        {
            new Avalonia.Animation.KeyFrame { Cue = new Avalonia.Animation.Cue(0), Setters = { new Avalonia.Styling.Setter(OpacityProperty, 0d), new Avalonia.Styling.Setter(Avalonia.Media.TranslateTransform.YProperty, -10d) } },
            new Avalonia.Animation.KeyFrame { Cue = new Avalonia.Animation.Cue(1), Setters = { new Avalonia.Styling.Setter(OpacityProperty, 1d), new Avalonia.Styling.Setter(Avalonia.Media.TranslateTransform.YProperty, 0d) } },
        },
    };

    public VodView()
    {
        InitializeComponent();
        // 「更多筛选」展开：筛选组从上方淡入下滑，不是瞬间撑开。
        if (this.FindControl<ItemsControl>("FilterGroups") is { } filters)
        {
            filters.RenderTransform = new Avalonia.Media.TranslateTransform();
            filters.PropertyChanged += (_, e) =>
            {
                if (e.Property == IsVisibleProperty && e.NewValue is true) _ = FilterReveal.RunAsync(filters);
            };
        }
        // 网格宽度决定列数；宿主铺到右边缘，扣除移入内容层的 28 留白。
        // 网格与骨架互斥显示，两个宿主都上报宽度：
        // 首次加载时只有骨架可见，靠 GridHost 也能算出正确列数（SetGridWidth 忽略 0 宽度）。
        foreach (var name in new[] { "GridHost", "PosterList" })
            if (this.FindControl<Control>(name) is { } host)
                host.SizeChanged += (_, _) =>
                { if (DataContext is MainViewModel main) main.Vod.SetGridWidth(host.Bounds.Width - 28); };
        // 无限滚动：ListBox 内部 ScrollViewer 滚到底部附近时取下一页。
        // 用冒泡的 ScrollChanged 事件监听，不依赖模板子控件；VM 做并发/去重/取消/终点门控。
        if (this.FindControl<ListBox>("PosterList") is { } list)
            list.AddHandler(ScrollViewer.ScrollChangedEvent, OnPosterScrolled);
    }

    /// <summary>滚到接近底部时预取下一页；到顶/无溢出/未到底都不触发。</summary>
    private void OnPosterScrolled(object? sender, ScrollChangedEventArgs e)
    {
        if (e.Source is not ScrollViewer scroll) return;
        if (scroll.Extent.Height <= 0 || scroll.Extent.Height <= scroll.Viewport.Height) return;
        if (scroll.Offset.Y + scroll.Viewport.Height < scroll.Extent.Height - PrefetchDistance) return;
        if (DataContext is not MainViewModel main) return;
        // 上一次翻页失败：停在页脚的「重试」状态，等用户显式点击，不在滚动时反复重发。
        if (main.Vod.FooterCanRetry) return;
        main.Vod.LoadMoreCommand.Execute(null);
    }
    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private VodViewModel VM => ((MainViewModel)DataContext!).Vod;

    private void OnOpenItem(object? sender, PointerPressedEventArgs e)
    {
        if ((sender as Control)?.Tag is MediaItem item) VM.OpenItemCommand.Execute(item);
    }

    private void OnCategoryClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is Category category) VM.SelectedCategory = category;
    }

    private void OnPickFilter(object? sender,RoutedEventArgs e)
    {
        if(sender is Button{Tag:FilterChip chip})VM.SelectFilter(chip.Key,chip.Value);
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Return && !e.Handled) VM.RunSearchCommand.Execute(null);
    }

    /// <summary>点播搜索框的历史下拉选了一条：词已写回搜索框，按它做跨站搜索。</summary>
    private void OnSearchHistoryPicked(object? sender, string word) => VM.RunSearchCommand.Execute(null);
}
