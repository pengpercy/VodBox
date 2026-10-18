using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using VodBox.Desktop.ViewModels;

namespace VodBox.Desktop.Views;

/// <summary>
/// 挂在任意搜索框下的搜索历史下拉（侧栏、点播、搜索结果三处共用同一份 <see cref="SearchViewModel.SearchHistory"/>）。
/// 打开：聚焦 / 点击搜索框 / ↓；关闭：选词、Esc、回车、搜索框失焦、点下拉与搜索框以外任意处（light dismiss）、窗口失活。
/// 选中一条会把词写回搜索框并触发 <see cref="Picked"/>，由宿主执行各自的搜索。
/// </summary>
public partial class SearchHistoryPopup : Popup
{
    public static readonly StyledProperty<TextBox?> TargetBoxProperty =
        AvaloniaProperty.Register<SearchHistoryPopup, TextBox?>(nameof(TargetBox));

    public static readonly StyledProperty<SearchViewModel?> HistoryProperty =
        AvaloniaProperty.Register<SearchHistoryPopup, SearchViewModel?>(nameof(History));

    /// <summary>挂靠的搜索框：下拉贴着它的下沿弹出，点它本身不算「外部点击」。</summary>
    public TextBox? TargetBox { get => GetValue(TargetBoxProperty); set => SetValue(TargetBoxProperty, value); }

    /// <summary>搜索历史来源（全局共享的 <see cref="SearchViewModel"/>）。</summary>
    public SearchViewModel? History { get => GetValue(HistoryProperty); set => SetValue(HistoryProperty, value); }

    /// <summary>用户选了一条历史；搜索框里已经是这个词。</summary>
    public event EventHandler<string>? Picked;

    private readonly List<SuggestItem> _items = [];
    private bool _listAll;
    private bool _writingText;
    private bool _suppressNextFocus;
    private Window? _window;
    private SearchViewModel? _observed;

    public SearchHistoryPopup() => AvaloniaXamlLoader.Load(this);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TargetBoxProperty)
        {
            Detach(change.GetOldValue<TextBox?>());
            Attach(change.GetNewValue<TextBox?>());
        }
        else if (change.Property == HistoryProperty)
        {
            if (_observed is not null) _observed.SearchHistory.CollectionChanged -= OnHistoryChanged;
            _observed = change.GetNewValue<SearchViewModel?>();
            if (_observed is not null) _observed.SearchHistory.CollectionChanged += OnHistoryChanged;
        }
    }

    private void Attach(TextBox? box)
    {
        PlacementTarget = box;
        OverlayInputPassThroughElement = box;
        if (box is null) return;
        box.GotFocus += OnBoxGotFocus;
        box.LostFocus += OnBoxLostFocus;
        box.PropertyChanged += OnBoxPropertyChanged;
        box.AddHandler(KeyDownEvent, OnBoxKeyDown, RoutingStrategies.Tunnel);
        box.AddHandler(PointerPressedEvent, OnBoxPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        box.AttachedToVisualTree += OnBoxAttached;
        box.DetachedFromVisualTree += OnBoxDetached;
        if (box.IsAttachedToVisualTree()) HookWindow(box);
    }

    private void Detach(TextBox? box)
    {
        if (box is null) return;
        box.GotFocus -= OnBoxGotFocus;
        box.LostFocus -= OnBoxLostFocus;
        box.PropertyChanged -= OnBoxPropertyChanged;
        box.RemoveHandler(KeyDownEvent, OnBoxKeyDown);
        box.RemoveHandler(PointerPressedEvent, OnBoxPointerPressed);
        box.AttachedToVisualTree -= OnBoxAttached;
        box.DetachedFromVisualTree -= OnBoxDetached;
        UnhookWindow();
    }

    private void OnBoxAttached(object? sender, VisualTreeAttachmentEventArgs e) { if (sender is TextBox box) HookWindow(box); }
    private void OnBoxDetached(object? sender, VisualTreeAttachmentEventArgs e) { UnhookWindow(); Dismiss(); }

    private void HookWindow(TextBox box)
    {
        UnhookWindow();
        _window = TopLevel.GetTopLevel(box) as Window;
        if (_window is not null) _window.Deactivated += OnWindowDeactivated;
    }

    private void UnhookWindow()
    {
        if (_window is not null) _window.Deactivated -= OnWindowDeactivated;
        _window = null;
    }

    /// <summary>切到别的应用/窗口：收起；回来时焦点自动还给搜索框，不因此再弹出（再点一下搜索框才打开）。</summary>
    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        Dismiss();
        if (TargetBox?.IsFocused == true) _suppressNextFocus = true;
    }

    private void OnBoxGotFocus(object? sender, FocusChangedEventArgs e)
    {
        if (_suppressNextFocus) { _suppressNextFocus = false; return; }
        Show(all: true);
    }

    private void OnBoxLostFocus(object? sender, FocusChangedEventArgs e) => Dismiss();

    /// <summary>已聚焦的搜索框再点一下（如 Esc 收起后）重新列出全部历史。</summary>
    private void OnBoxPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _suppressNextFocus = false;
        if (!IsOpen && TargetBox?.IsFocused == true) Show(all: true);
    }

    private void OnBoxPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == BoundsProperty && IsOpen) UpdatePanelWidth();
        if (e.Property != TextBox.TextProperty || _writingText || TargetBox?.IsFocused != true) return;
        Show(all: false);
    }

    private void OnBoxKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape when IsOpen:
                Dismiss(); e.Handled = true; break;
            case Key.Down when !IsOpen:
                Show(all: true); e.Handled = IsOpen; break;
            case Key.Down or Key.Up when IsOpen:
                MoveSelection(e.Key == Key.Down ? 1 : -1); e.Handled = true; break;
            case Key.Enter or Key.Return:
                // 有键盘选中的历史就搜它；否则收起下拉，回车交给宿主按输入内容搜索。
                if (IsOpen && _items.FirstOrDefault(item => item.IsSelected) is { } selected) { Pick(selected.Text); e.Handled = true; }
                else Dismiss();
                break;
        }
    }

    private void OnHistoryChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (IsOpen) Show(_listAll);
    }

    private void Show(bool all)
    {
        if (TargetBox is not { } box || History is not { } history) return;
        _listAll = all;
        var selected = _items.FirstOrDefault(item => item.IsSelected)?.Text;
        _items.Clear();
        _items.AddRange(SearchViewModel.FilterHistory(history.SearchHistory, box.Text ?? "", all)
            .Select(text => new SuggestItem { Text = text, IsSelected = text == selected }));
        if (_items.Count == 0) { Dismiss(); return; }
        if (this.FindControl<ItemsControl>("HistoryItems") is { } list) { list.ItemsSource = null; list.ItemsSource = _items; }
        UpdatePanelWidth();
        IsOpen = true;
    }

    private void UpdatePanelWidth()
    {
        if (TargetBox is { } box && this.FindControl<Border>("HistoryPanel") is { } panel)
            panel.Width = panel.MaxWidth = box.Bounds.Width;
    }

    private void Dismiss()
    {
        foreach (var item in _items) item.IsSelected = false;
        IsOpen = false;
    }

    private void MoveSelection(int step)
    {
        if (_items.Count == 0) return;
        int current = _items.FindIndex(item => item.IsSelected);
        int next = current < 0 ? (step > 0 ? 0 : _items.Count - 1) : Math.Clamp(current + step, 0, _items.Count - 1);
        for (int i = 0; i < _items.Count; i++) _items[i].IsSelected = i == next;
    }

    private void Pick(string word)
    {
        Dismiss();
        if (TargetBox is { } box)
        {
            _writingText = true;
            try { box.Text = word; box.CaretIndex = word.Length; }
            finally { _writingText = false; }
        }
        Picked?.Invoke(this, word);
    }

    private void OnPick(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Visual visual && (visual is Button || visual.GetVisualAncestors().Any(parent => parent is Button))) return;
        if ((sender as Control)?.Tag is SuggestItem item) { e.Handled = true; Pick(item.Text); }
    }

    /// <summary>单条删除：不收起下拉，可连续删。</summary>
    private void OnDelete(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string word }) History?.DeleteSearchHistoryCommand.Execute(word);
        e.Handled = true;
    }

    private void OnClear(object? sender, RoutedEventArgs e)
    {
        History?.ClearSearchHistoryCommand.Execute(null);
        Dismiss();
        e.Handled = true;
    }
}
