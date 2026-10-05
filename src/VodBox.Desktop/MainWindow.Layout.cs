using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;

namespace VodBox.Desktop;

public sealed partial class MainWindow
{
    private MainViewModel? _layoutModel;
    private bool _fullscreenActive, _pageBeforeFullscreen;
    private void InitializeAdaptiveLayout(MainViewModel model)
    {
        _layoutModel = model; SizeChanged += LayoutSizeChanged;
        model.PropertyChanged += LayoutModelChanged; PropertyChanged += WindowLayoutChanged;
        Closed += (_, _) => { SizeChanged -= LayoutSizeChanged; model.PropertyChanged -= LayoutModelChanged; PropertyChanged -= WindowLayoutChanged; };
        UpdateAdaptiveLayout();
    }
    private void WindowLayoutChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
    {
        if (args.Property != WindowStateProperty || _layoutModel is null) return;
        bool fullscreen = WindowState == WindowState.FullScreen;
        if (fullscreen == _fullscreenActive) return;
        _fullscreenActive = fullscreen;
        if (fullscreen) { _pageBeforeFullscreen = _layoutModel.ShowPlaybackPage; _layoutModel.ShowPlaybackPage = true; }
        else _layoutModel.ShowPlaybackPage = _pageBeforeFullscreen;
        UpdateAdaptiveLayout();
    }
    private void LayoutSizeChanged(object? sender, SizeChangedEventArgs args) => UpdateAdaptiveLayout();
    private void LayoutModelChanged(object? sender, PropertyChangedEventArgs args)
    { if (args.PropertyName is nameof(MainViewModel.ShowPlaybackPage) or nameof(MainViewModel.ShowHome)) UpdateAdaptiveLayout(); }
    private void UpdateAdaptiveLayout()
    {
        bool fullscreen = _fullscreenActive;
        bool home = _layoutModel?.ShowHome == true && _layoutModel.ShowPlaybackPage == false;
        WindowLayout.RowDefinitions[0].Height = new(fullscreen ? 0 : 40);
        WindowTitleBar.IsVisible = !fullscreen;
        NavigationPane.IsVisible = !fullscreen && !home;
        WorkspaceHeader.IsVisible = !fullscreen && !home;
        AddressBar.IsVisible = !fullscreen && _layoutModel?.ShowPlaybackPage == true;
        StatusBar.IsVisible = !fullscreen && !home;
        Workspace.RowDefinitions[0].Height = fullscreen || home ? new(0) : GridLength.Auto;
        Workspace.RowDefinitions[1].Height = !AddressBar.IsVisible ? new(0) : GridLength.Auto;
        Workspace.RowDefinitions[3].Height = fullscreen || home ? new(0) : GridLength.Auto;
        Workspace.RowSpacing = fullscreen || home ? 0 : 16;
        PlaybackPane.SetFullscreenPresentation(fullscreen);
        PlaybackControls.SetFullscreenPresentation(fullscreen);
        AppShell.RowDefinitions[1].Height = fullscreen ? new(0) : GridLength.Auto;
        Grid.SetRow(PlaybackControls, fullscreen ? 0 : 1);
        PlaybackControls.VerticalAlignment = fullscreen ? Avalonia.Layout.VerticalAlignment.Bottom : Avalonia.Layout.VerticalAlignment.Stretch;
        _playbackControlsOverlay?.Update();
        bool compact = (Bounds.Width > 0 ? Bounds.Width : Width) < 1180;
        AppShell.ColumnDefinitions[0].Width = new(fullscreen || home ? 0 : compact ? 132 : 180);
        NavigationPane.Padding = new Thickness(compact ? 8 : 16, 28);
        Workspace.Margin = fullscreen ? new Thickness(0) : compact ? new Thickness(16, 20, 16, 12) : new Thickness(24, 24, 24, 16);
        bool playback = _layoutModel?.ShowPlaybackPage == true;
        BrowsePane.IsVisible = !playback; PlaybackPane.IsVisible = playback;
        ContentWorkspace.ColumnDefinitions[0].Width = new(1, GridUnitType.Star);
        ContentWorkspace.ColumnDefinitions[1].Width = new(0);
        ContentWorkspace.ColumnSpacing = 0;
        Grid.SetColumn(PlaybackPane, 0);
        BrowsePageButton.IsVisible = playback; PlaybackPageButton.IsVisible = !playback;
    }
    private void BrowsePageClick(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    { if (_layoutModel is not null) _layoutModel.ShowPlaybackPage = false; }
    private void PlaybackPageClick(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    { if (_layoutModel is not null) _layoutModel.ShowPlaybackPage = true; }
}
