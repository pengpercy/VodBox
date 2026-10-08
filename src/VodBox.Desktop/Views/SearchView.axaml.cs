using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using VodBox.Core;
using VodBox.Desktop.ViewModels;

namespace VodBox.Desktop.Views;

public partial class SearchView : UserControl
{
    public SearchView() => InitializeComponent();
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
    }

    private void OnSearchClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => VM.RunSearchCommand.Execute(null);

    private void OnOpenItem(object? sender, PointerPressedEventArgs e)
    {
        if ((sender as Control)?.Tag is MediaItem item) VM.OpenItemCommand.Execute(item);
    }

    /// <summary>点联想词：填入关键词并立即搜索。</summary>
    private void OnPickSuggestion(object? sender, PointerPressedEventArgs e)
    {
        if ((sender as Control)?.Tag is SuggestItem item)
        {
            VM.Keyword = item.Text;
            VM.RunSearchCommand.Execute(null);
        }
    }

    /// <summary>点历史胶囊：回搜；点 × 区域删词（S5 接 Store）。</summary>
    private void OnPickHistory(object? sender, PointerPressedEventArgs e)
    {
        if ((sender as Control)?.Tag is string word)
        {
            VM.Keyword = word;
            VM.RunSearchCommand.Execute(null);
        }
    }

    /// <summary>站点列表行选中：右侧网格切到该站结果（S5 接，当前显示全部）。</summary>
    private void OnSiteChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is MainViewModel main && sender is ListBox list)
            main.Search.SelectedSite = list.SelectedItem as SearchSiteResult;
    }
}
