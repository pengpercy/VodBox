using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;

namespace VodBox.Desktop;

public sealed partial class MainWindow
{
    private MainViewModel? _layoutModel;
    private void InitializeAdaptiveLayout(MainViewModel model)
    {
        _layoutModel = model; SizeChanged += LayoutSizeChanged;
        model.PropertyChanged += LayoutModelChanged;
        Closed += (_, _) => { SizeChanged -= LayoutSizeChanged; model.PropertyChanged -= LayoutModelChanged; };
        UpdateAdaptiveLayout();
    }
    private void LayoutSizeChanged(object? sender, SizeChangedEventArgs args) => UpdateAdaptiveLayout();
    private void LayoutModelChanged(object? sender, PropertyChangedEventArgs args)
    { if (args.PropertyName == nameof(MainViewModel.ShowPlaybackPage)) UpdateAdaptiveLayout(); }
    private void UpdateAdaptiveLayout()
    {
        bool compact = (Bounds.Width > 0 ? Bounds.Width : Width) < 1180;
        AppShell.ColumnDefinitions[0].Width = new(compact ? 132 : 180);
        NavigationPane.Padding = new Thickness(compact ? 8 : 16, 28);
        Workspace.Margin = compact ? new Thickness(16, 20, 16, 12) : new Thickness(24, 24, 24, 16);
        bool playback = _layoutModel?.ShowPlaybackPage == true;
        BrowsePane.IsVisible = !compact || !playback; PlaybackPane.IsVisible = !compact || playback;
        ContentWorkspace.ColumnDefinitions[0].Width = new(5, GridUnitType.Star);
        ContentWorkspace.ColumnDefinitions[1].Width = compact ? new(0) : new(4, GridUnitType.Star);
        ContentWorkspace.ColumnSpacing = compact ? 0 : 20;
        Grid.SetColumn(PlaybackPane, compact ? 0 : 1);
        BrowsePageButton.IsVisible = compact && playback; PlaybackPageButton.IsVisible = compact && !playback;
    }
    private void BrowsePageClick(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    { if (_layoutModel is not null) _layoutModel.ShowPlaybackPage = false; }
    private void PlaybackPageClick(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    { if (_layoutModel is not null) _layoutModel.ShowPlaybackPage = true; }
}
