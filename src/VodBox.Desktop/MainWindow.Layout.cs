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
    { if (args.PropertyName is nameof(MainViewModel.ShowPlaybackPage) or nameof(MainViewModel.ShowHome) or nameof(MainViewModel.ShowSearch) or nameof(MainViewModel.ShowHistory) or nameof(MainViewModel.ShowFavorites) or nameof(MainViewModel.ShowLibrary)) UpdateAdaptiveLayout(); }
    private void UpdateAdaptiveLayout()
    {
        bool fullscreen = _fullscreenActive;
        WindowLayout.RowDefinitions[0].Height = new(fullscreen ? 0 : 40);
        WindowTitleBar.IsVisible = !fullscreen;
        StatusBar.IsVisible = !fullscreen && _layoutModel?.ShowPlaybackPage == true;
        Workspace.RowDefinitions[1].Height = StatusBar.IsVisible ? GridLength.Auto : new(0);
        Workspace.RowSpacing = StatusBar.IsVisible ? 12 : 0;
        PlaybackPane.SetFullscreenPresentation(fullscreen);
        PlaybackControls.SetFullscreenPresentation(fullscreen);
        _playbackControlsOverlay?.Update();
        bool compact = (Bounds.Width > 0 ? Bounds.Width : Width) < 1180;
        Workspace.Margin = fullscreen ? new Thickness(0) : compact ? new Thickness(16, 20, 16, 12) : new Thickness(24, 24, 24, 16);
        bool playback = _layoutModel?.ShowPlaybackPage == true;
        BrowsePane.IsVisible = !playback; PlaybackPane.IsVisible = playback;
        Grid.SetColumn(PlaybackPane, 0);
    }
}
