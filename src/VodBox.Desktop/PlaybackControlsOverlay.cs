using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using VodBox.Core;
using VodBox.Desktop.Views;

namespace VodBox.Desktop;

/// <summary>Interactive transparent controls above LibVLC's native video surface.</summary>
internal sealed class PlaybackControlsOverlay : IDisposable
{
    private readonly MainWindow _owner;
    private readonly Control _surface;
    private readonly PlaybackControlsView _inline;
    private readonly MainViewModel _model;
    private readonly Window _window;
    private readonly PlaybackControlsView _controls;
    public PlaybackControlsOverlay(MainWindow owner, Control surface, PlaybackControlsView inline, MainViewModel model)
    {
        _owner = owner; _surface = surface; _inline = inline; _model = model;
        var controls = new PlaybackControlsView { DataContext = model, OwnerWindow = owner, Margin = new Thickness(12) };
        _controls = controls;
        _window = new Window
        {
            WindowDecorations = WindowDecorations.None, ShowActivated = false, ShowInTaskbar = false, CanResize = false,
            Background = Brushes.Transparent, TransparencyBackgroundFallback = Brushes.Transparent,
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent], SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.Manual, Content = controls, MinWidth = 0, MinHeight = 0
        };
        _window.AddHandler(Avalonia.Input.InputElement.KeyDownEvent, (_, e) =>
        {
            if (e.Key is Avalonia.Input.Key.Escape or Avalonia.Input.Key.F11)
            { owner.ToggleFullscreen(); e.Handled = true; }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _window.SizeChanged += Changed;
        owner.LayoutUpdated += Changed; owner.PositionChanged += Changed;
        owner.PropertyChanged += OwnerChanged; model.PropertyChanged += ModelChanged;
    }
    private void Changed(object? sender, EventArgs e) => Update();
    private void OwnerChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    { if (e.Property == Window.WindowStateProperty || e.Property == Visual.IsVisibleProperty) Update(); }
    private void ModelChanged(object? sender, PropertyChangedEventArgs e)
    { if (e.PropertyName is nameof(MainViewModel.ShowSettings) or nameof(MainViewModel.ActiveEngineText) or nameof(MainViewModel.ShowPlaybackPage)) Update(); }
    public void Update()
    {
        bool visible = !_model.ShowSettings && _owner.IsVisible && _owner.WindowState != WindowState.Minimized
            && _surface.IsEffectivelyVisible && _surface.IsAttachedToVisualTree()
            && _model.Engine.ActiveKind == PlaybackEngineKind.LibVlc;
        _controls.SetFullscreenPresentation(_owner.WindowState == WindowState.FullScreen);
        _inline.IsVisible = _model.ShowPlaybackPage && !visible;
        if (!visible || _surface.Bounds.Width < 1) { if (_window.IsVisible) _window.Hide(); return; }
        if (_window.Width != _surface.Bounds.Width) _window.Width = _surface.Bounds.Width;
        var position = _surface.PointToScreen(new Point(0, Math.Max(0, _surface.Bounds.Height - _window.Bounds.Height)));
        if (_window.Position != position) _window.Position = position;
        if (!_window.IsVisible) _window.Show(_owner);
    }
    public void Dispose()
    {
        _window.SizeChanged -= Changed;
        _owner.LayoutUpdated -= Changed; _owner.PositionChanged -= Changed;
        _owner.PropertyChanged -= OwnerChanged; _model.PropertyChanged -= ModelChanged;
        _window.Close();
    }
}
