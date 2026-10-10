using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using VodBox.Core;
using VodBox.Desktop.ViewModels;

namespace VodBox.Desktop.Views;

public partial class HomeView : UserControl
{
    private readonly DispatcherTimer _heroTimer = new() { Interval = TimeSpan.FromSeconds(6) };
    private bool _attached;
    private bool _heroHovered;
    internal bool IsHeroRotationRunning => _heroTimer.IsEnabled;

    public HomeView()
    {
        InitializeComponent();
        _heroTimer.Tick += (_, _) => RotateHero();
    }

    internal void RotateHero()
    {
        if (!_heroHovered && IsEffectivelyVisible && DataContext is MainViewModel { Home.Loading: false } main)
            main.Home.NextHeroCommand.Execute(null);
    }

    private void UpdateHeroTimer()
    {
        _heroTimer.Stop();
        if (_attached && IsEffectivelyVisible && !_heroHovered) _heroTimer.Start();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e); _attached = true; UpdateHeroTimer();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false; _heroTimer.Stop(); base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty && _heroTimer is not null) UpdateHeroTimer();
    }

    private void OnHeroPointerEntered(object? sender, PointerEventArgs e) { _heroHovered = true; UpdateHeroTimer(); }
    private void OnHeroPointerExited(object? sender, PointerEventArgs e) { _heroHovered = false; UpdateHeroTimer(); }
    private void OnSelectHero(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: int index }) VM.SelectHero(index);
        e.Handled = true; UpdateHeroTimer();
    }
    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private HomeViewModel VM => ((MainViewModel)DataContext!).Home;

    private void OnHeroClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => VM.OpenHeroCommand.Execute(null);
    private void OnHeroPlay(object? sender, RoutedEventArgs e) => VM.OpenHeroCommand.Execute(null);
    private void OnHeroPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Button || e.Source is Control child && child.GetVisualAncestors().OfType<Button>().Any()) return;
        VM.OpenHeroCommand.Execute(null);
    }

    private void OnResumeHistory(object? sender, PointerPressedEventArgs e)
    {
        if ((sender as Control)?.Tag is HistoryEntry entry) VM.ResumeCommand.Execute(entry);
    }

    private void OnOpenRecommend(object? sender, PointerPressedEventArgs e)
    {
        if ((sender as Control)?.Tag is MediaItem item) VM.OpenItemCommand.Execute(item);
    }

    private void OnShowAllHistory(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainViewModel main) main.GoHistoryCommand.Execute(null);
    }

    private void OnSwitchSource(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainViewModel main) main.GoSettingsCommand.Execute(null);
    }
}
