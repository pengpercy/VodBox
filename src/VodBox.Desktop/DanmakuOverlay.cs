using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using VodBox.Core;

namespace VodBox.Desktop;

/// <summary>Owned transparent window avoids the native video's airspace without copying decoded frames.</summary>
public sealed class DanmakuOverlay : IDisposable
{
    private readonly Window _owner;
    private readonly Control _surface;
    private readonly MainViewModel _model;
    private readonly Window _window;
    private readonly DanmakuView _view = new();
    private bool _disposed, _unsupported;
    public DanmakuOverlay(Window owner, Control surface, MainViewModel model)
    {
        _owner = owner; _surface = surface; _model = model;
        _window = new()
        {
            WindowDecorations = WindowDecorations.None, ShowActivated = false, ShowInTaskbar = false, CanResize = false,
            Background = Brushes.Transparent, TransparencyBackgroundFallback = Brushes.Transparent,
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent], SizeToContent = SizeToContent.Manual,
            WindowStartupLocation = WindowStartupLocation.Manual, Content = _view, MinWidth = 0, MinHeight = 0
        };
        _view.Attach(model);
        // Video has no interactive controls in this rectangle. Return focus and keys to the owner.
        _window.PointerPressed += (_, _) => _owner.Activate();
        _window.KeyDown += (_, e) => { _owner.RaiseEvent(e); };
        owner.LayoutUpdated += PositionChanged; owner.PositionChanged += PositionChanged;
        owner.PropertyChanged += OwnerChanged; model.PropertyChanged += ModelChanged;
    }
    private void PositionChanged(object? sender, EventArgs e) => Update();
    private void OwnerChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    { if (e.Property == Window.WindowStateProperty || e.Property == Visual.IsVisibleProperty) Update(); }
    private void ModelChanged(object? sender, PropertyChangedEventArgs e)
    { if (e.PropertyName is nameof(MainViewModel.DanmakuEnabled) or nameof(MainViewModel.DanmakuComments) or nameof(MainViewModel.Position) or nameof(MainViewModel.PlayerState) or nameof(MainViewModel.NowPlaying)) Update(); }
    public void Update()
    {
        if (_disposed || _unsupported) return;
        var state = _model.Engine.Snapshot.State;
        bool visible = _owner.IsVisible && _owner.WindowState != WindowState.Minimized && _surface.IsEffectivelyVisible && _surface.IsAttachedToVisualTree()
            && _model.DanmakuEnabled && _model.DanmakuComments.Count > 0 && state is PlaybackState.Playing or PlaybackState.Paused or PlaybackState.Buffering;
        if (!visible || _surface.Bounds.Width < 1 || _surface.Bounds.Height < 1) { if (_window.IsVisible) _window.Hide(); return; }
        var position = _surface.PointToScreen(default);
        if (_window.Position != position) _window.Position = position;
        if (_window.Width != _surface.Bounds.Width) _window.Width = _surface.Bounds.Width;
        if (_window.Height != _surface.Bounds.Height) _window.Height = _surface.Bounds.Height;
        if (!_window.IsVisible)
        {
            _window.Show(_owner);
            if (_window.ActualTransparencyLevel != WindowTransparencyLevel.Transparent)
            { _window.Hide(); _unsupported = true; _model.DanmakuStatus = "当前窗口系统未提供透明叠层，弹幕显示已停用。"; }
        }
        _view.RefreshFrame();
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        _owner.LayoutUpdated -= PositionChanged; _owner.PositionChanged -= PositionChanged;
        _owner.PropertyChanged -= OwnerChanged; _model.PropertyChanged -= ModelChanged;
        _view.Dispose(); _window.Close();
    }
}
