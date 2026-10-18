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
        // 看详情时 Esc 回到结果（搜索框里的 Esc 先用于收起历史下拉，已处理的不再返回）。
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.Escape || e.Handled || DataContext is not MainViewModel { Search.ShowingDetail: true } main) return;
            main.Search.CloseDetail();
            e.Handled = true;
        });
    }

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
        if (e.Key is Key.Enter or Key.Return && !e.Handled) VM.RunSearchCommand.Execute(null);
    }

    /// <summary>历史下拉选了一条：词已写回搜索框（同步到 Keyword），直接搜。</summary>
    private void OnSearchHistoryPicked(object? sender, string word) => VM.RunSearchCommand.Execute(null);

    private void OnSearchClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => VM.RunSearchCommand.Execute(null);

    private void OnOpenItem(object? sender, PointerPressedEventArgs e)
    {
        if ((sender as Control)?.Tag is MediaItem item) VM.OpenItemCommand.Execute(item);
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
