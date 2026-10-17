using Avalonia.Controls;
using Avalonia.VisualTree;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using VodBox.Core;
using VodBox.Desktop.ViewModels;

namespace VodBox.Desktop.Views;

public partial class SearchView : UserControl
{
    public SearchView()
    {
        InitializeComponent();
        // 历史下拉打开时，点页面上搜索框与下拉以外的任何地方都收起（结果卡片等不可聚焦，不会触发失焦）。
        AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (DataContext is not MainViewModel { Search.ShowSuggestions: true } main) return;
            if (e.Source is Avalonia.Visual source && (IsWithin(source, "SuggestionDropdown") || IsWithin(source, "SearchField"))) return;
            main.Search.CloseSuggestions();
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        // 看详情时 Esc 回到结果（搜索框里的 Esc 先用于收起历史下拉，已处理的不再返回）。
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.Escape || e.Handled || DataContext is not MainViewModel { Search.ShowingDetail: true } main) return;
            main.Search.CloseDetail();
            e.Handled = true;
        });
    }

    private bool IsWithin(Avalonia.Visual source, string name) =>
        this.FindControl<Control>(name) is { } target && (ReferenceEquals(source, target) || source.GetVisualAncestors().Contains(target));
    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnResultsSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (DataContext is MainViewModel main) main.Search.SetResultWidth(e.NewSize.Width);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is MainViewModel main && this.FindControl<ListBox>("ResultsList") is { } list)
            main.Search.SetResultWidth(list.Bounds.Width);
    }

    private SearchViewModel VM => ((MainViewModel)DataContext!).Search;

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Return) VM.RunSearchCommand.Execute(null);
        else if (e.Key == Key.Escape && VM.ShowSuggestions) { VM.CloseSuggestions(); e.Handled = true; }
    }

    /// <summary>聚焦搜索框即下拉全部搜索历史（对齐 FongMi/TV）。</summary>
    private void OnSearchFocused(object? sender, FocusChangedEventArgs e) => VM.OpenHistoryDropdown();

    /// <summary>焦点移到别的控件就收起下拉；下拉里的按钮不可聚焦，点它们不会走到这里。</summary>
    private void OnSearchLostFocus(object? sender, FocusChangedEventArgs e) => VM.CloseSuggestions();

    /// <summary>下拉里单条删除历史；不收起下拉，可连续删。</summary>
    private void OnDeleteSuggestion(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button { Tag: string word }) VM.DeleteSearchHistoryCommand.Execute(word);
        e.Handled = true;
    }

    private void OnSearchClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => VM.RunSearchCommand.Execute(null);

    private void OnOpenItem(object? sender, PointerPressedEventArgs e)
    {
        if ((sender as Control)?.Tag is MediaItem item) VM.OpenItemCommand.Execute(item);
    }

    /// <summary>点联想词：填入关键词并立即搜索。</summary>
    private void OnPickSuggestion(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Avalonia.Visual visual && (visual is Button || visual.GetVisualAncestors().Any(parent => parent is Button))) return;
        if ((sender as Control)?.Tag is SuggestItem item)
        {
            VM.Keyword = item.Text;
            VM.RunSearchCommand.Execute(null);
        }
    }

    /// <summary>点历史胶囊：回搜；点 × 区域删词（S5 接 Store）。</summary>
    private void OnDeleteHistory(object? sender,Avalonia.Interactivity.RoutedEventArgs e)
    {
        if(sender is Button {Tag:string word})VM.DeleteSearchHistoryCommand.Execute(word);
        e.Handled=true;
    }

    private void OnPickHistory(object? sender, PointerPressedEventArgs e)
    {
        if(e.Source is Avalonia.Visual visual && (visual is Button || visual.GetVisualAncestors().Any(parent=>parent is Button)))return;
        if ((sender as Control)?.Tag is string word)
        {
            VM.Keyword = word;
            VM.RunSearchCommand.Execute(null);
        }
    }

    /// <summary>站点列表行选中：右侧网格切到该站结果；取消选中即回到全部结果。</summary>
    private void OnSiteChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is MainViewModel main && sender is ListBox list)
            main.Search.SelectedSite = list.SelectedItem as SearchSiteResult;
    }

    /// <summary>侧栏「全部结果」：清掉站点选择，网格回到全部结果。</summary>
    private void OnShowAllSites(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (this.FindControl<ListBox>("SiteList") is { } list) list.SelectedItem = null;
        VM.SelectedSite = null;
    }
}
