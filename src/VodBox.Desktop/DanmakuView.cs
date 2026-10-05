using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using VodBox.Core;

namespace VodBox.Desktop;

/// <summary>One drawing surface, cached glyph layouts and a 30 Hz media-clock update.</summary>
public sealed class DanmakuView : Control, IDisposable
{
    private static readonly IBrush Backplate = new ImmutableSolidColorBrush(Color.FromArgb(110, 0, 0, 0));
    private readonly DanmakuTimeline _timeline = new();
    private readonly Dictionary<DanmakuComment, TextLayout> _layouts = [];
    private readonly Queue<DanmakuComment> _layoutOrder = [];
    private readonly DispatcherTimer _timer;
    private MainViewModel? _viewModel;
    private bool _preview, _disposed;
    private long _samplePosition = -1, _sampleAt;
    private PlaybackState _sampleState;
    private IReadOnlyList<DanmakuComment>? _configured;
    private Size _configuredSize;
    private double _font, _coverage;
    public int ActiveCount => _timeline.Placements.Count;
    public DanmakuView()
    {
        IsHitTestVisible = false; ClipToBounds = true;
        _timer = new() { Interval = TimeSpan.FromMilliseconds(1000d / 30) };
        _timer.Tick += (_, _) => RefreshFrame();
        AttachedToVisualTree += (_, _) => RefreshFrame();
        DetachedFromVisualTree += (_, _) => _timer.Stop();
        SizeChanged += (_, _) => RefreshFrame();
    }
    public void Attach(MainViewModel viewModel, bool preview = false)
    {
        if (_viewModel is not null) _viewModel.PropertyChanged -= ModelChanged;
        _viewModel = viewModel; _preview = preview; _configured = null;
        viewModel.PropertyChanged += ModelChanged; RefreshFrame();
    }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty) RefreshFrame();
    }
    private void ModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.DanmakuComments) or nameof(MainViewModel.DanmakuEnabled) or nameof(MainViewModel.DanmakuFontSize) or nameof(MainViewModel.DanmakuCoverage) or nameof(MainViewModel.DanmakuOpacity) or nameof(MainViewModel.DanmakuDelayMs) or nameof(MainViewModel.Position) or nameof(MainViewModel.PlayerState) or nameof(MainViewModel.NowPlaying)) RefreshFrame();
    }
    private TextLayout Layout(DanmakuComment comment)
    {
        if (_layouts.TryGetValue(comment, out var layout)) return layout;
        layout = new(comment.Text, new Typeface(FontFamily.Default), _font, new SolidColorBrush(Color.FromUInt32(0xFF000000 | comment.Color)), maxLines: 1);
        _layouts.Add(comment, layout); _layoutOrder.Enqueue(comment);
        while (_layouts.Count > 256 && _layoutOrder.TryDequeue(out var old))
            if (_layouts.Remove(old, out var previous)) previous.Dispose();
        return layout;
    }
    public void RefreshFrame()
    {
        if (_disposed || _viewModel is not { } model) return;
        var snapshot = _preview ? new PlaybackSnapshot(PlaybackState.Paused, TimeSpan.FromMilliseconds(model.Position), TimeSpan.FromMilliseconds(model.Duration), true) : model.Engine.Snapshot;
        long position = (long)snapshot.Position.TotalMilliseconds;
        long now = Stopwatch.GetTimestamp();
        if (position != _samplePosition || snapshot.State != _sampleState)
        { _samplePosition = position; _sampleAt = now; _sampleState = snapshot.State; }
        if (snapshot.State == PlaybackState.Playing)
            position += (long)(Math.Min(500, Stopwatch.GetElapsedTime(_sampleAt, now).TotalMilliseconds) * model.Rate);
        var comments = (_preview || IsEffectivelyVisible) && model.DanmakuEnabled && position >= model.DanmakuDelayMs && snapshot.State is PlaybackState.Playing or PlaybackState.Paused or PlaybackState.Buffering ? model.DanmakuComments : Array.Empty<DanmakuComment>();
        double font = Math.Clamp(model.DanmakuFontSize, 16, 48), coverage = Math.Clamp(model.DanmakuCoverage, .25, 1);
        if (!ReferenceEquals(_configured, comments) || _configuredSize != Bounds.Size || _font != font || _coverage != coverage)
        {
            if (_font != font) ClearLayouts();
            _configured = comments; _configuredSize = Bounds.Size; _font = font; _coverage = coverage;
            _timeline.Configure(comments, Bounds.Width, Bounds.Height, font, coverage);
        }
        Opacity = Math.Clamp(model.DanmakuOpacity, .1, 1);
        _timeline.Update(Math.Max(0, position - model.DanmakuDelayMs), comment => Layout(comment).Width);
        if (model.DanmakuEnabled && comments.Count > 0 && snapshot.State == PlaybackState.Playing && VisualRoot is not null) _timer.Start(); else _timer.Stop();
        InvalidateVisual();
    }
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        using var clip = context.PushClip(new Rect(Bounds.Size));
        foreach (var placement in _timeline.Placements)
        {
            var layout = Layout(placement.Comment);
            context.DrawRectangle(Backplate, null, new Rect(placement.X - 2, placement.Y, placement.Width + 4, _font * 1.3), 2, 2);
            layout.Draw(context, new(placement.X, placement.Y));
        }
    }
    private void ClearLayouts()
    { foreach (var layout in _layouts.Values) layout.Dispose(); _layouts.Clear(); _layoutOrder.Clear(); }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true; _timer.Stop();
        if (_viewModel is not null) _viewModel.PropertyChanged -= ModelChanged;
        ClearLayouts();
    }
}
