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

    public VodView()
    {
        InitializeComponent();
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
        if(sender is Button{Tag:FilterValue value} button)
        {
            var group=button.GetVisualAncestors().OfType<Control>().Select(control=>control.DataContext).OfType<FilterGroup>().FirstOrDefault();
            if(group is not null)VM.SelectFilter(group.Key,value.Value);
        }
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Return) VM.RunSearchCommand.Execute(null);
    }

    private void OnSwitchSource(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        // 复用 XAML 里定义的弹层（固定宽度省略号 + 中性色的站点名样式挂在那里）。
        var menu = button.ContextMenu ?? this.FindControl<ContextMenu>("SourceMenu");
        if (menu is null) return;
        menu.Items.Clear();
        foreach (var source in VM.AvailableSources)
        {
            var item = new MenuItem { Header = source.Name };
            item.Click += async (_, _) => await VM.SwitchSourceAsync(source.Key);
            menu.Items.Add(item);
        }
        if (menu.Items.Count == 0) menu.Items.Add(new MenuItem { Header = VM.SourceSelectionHint, IsEnabled = false });
        // 固定在当前站点按钮下方展开（控件锚点），不跟随鼠标点位定位。
        menu.PlacementTarget = button;
        menu.Open(button);
    }
}
