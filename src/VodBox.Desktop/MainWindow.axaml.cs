using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VodBox.Core;

namespace VodBox.Desktop;

public sealed partial class MainWindow : Window
{
    private readonly MainViewModel? _viewModel;
    public Views.PlaybackView PlaybackView => PlaybackPane;
    public Views.LibraryView LibraryView => LibraryPage;
    private Views.PlaybackControlsView PlaybackControls => PlaybackPane.ControlsView;
    private Grid VideoContainer => PlaybackPane.VideoContainer;
    private VlcVideoSurface VideoSurface => PlaybackPane.VideoSurface;
    private DanmakuView DanmakuPreview => PlaybackPane.DanmakuPreview;
    private bool _closing;
    private readonly DanmakuOverlay? _danmakuOverlay;
    private readonly PlaybackControlsOverlay? _playbackControlsOverlay;
    public MainWindow() : this(preview: false) { }
    public MainWindow(bool preview) : this(preview, null, true) { }
    public MainWindow(MainViewModel model, bool initialize = true) : this(false, model, initialize) { }
    private MainWindow(bool preview, MainViewModel? model, bool initialize)
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Escape && WindowState == WindowState.FullScreen) { ToggleFullscreen(); e.Handled = true; }
            else if (e.Key == Key.F11 && e.KeyModifiers == KeyModifiers.None) { FullscreenClick(this, e); e.Handled = true; }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        if (Design.IsDesignMode || preview)
        {
            // The designer has no desktop compositor; give sample content a readable backdrop.
            var design = new DesignMainViewModel(); DataContext = design; PlaybackPane.ConfigureDesignPreview(design);
            InitializeAdaptiveLayout(design); InitializeSettingsWindow(design);
            Closed += (_, _) => DanmakuPreview.Dispose();
            Background = new SolidColorBrush(Color.Parse("#111317"));
            return;
        }
        _viewModel = model ?? new(); DataContext = _viewModel;
        InitializeAdaptiveLayout(_viewModel); InitializeSettingsWindow(_viewModel);
        DanmakuPreview.Attach(_viewModel);
        _danmakuOverlay = new(this, VideoContainer, _viewModel);
        _playbackControlsOverlay = new(this, VideoContainer, PlaybackControls, _viewModel);
        _viewModel.Engine.ActiveEngineChanged += EngineChanged;
        VideoSurface.DrawableReady += (_, _) =>
        {
            if (_viewModel.Engine.ActiveEngine is VodBox.Playback.LibVlc.LibVlcEngine vlc && ReferenceEquals(vlc.Player, VideoSurface.MediaPlayer)) vlc.NotifyVideoSurfaceReady();
        };
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, (_, e) =>
        { e.DragEffects = e.DataTransfer.Contains(DataFormat.File) || e.DataTransfer.Contains(DataFormat.Text) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; });
        AddHandler(DragDrop.DropEvent, async (_, e) =>
        {
            e.Handled = true;
            var file = e.DataTransfer.TryGetFiles()?.FirstOrDefault()?.TryGetLocalPath();
            if (file is not null)
            {
                string extension = Path.GetExtension(file).ToLowerInvariant();
                if (extension is ".srt" or ".ass" or ".ssa" or ".vtt" or ".sub") await _viewModel.AddSubtitleAsync(file);
                else if (extension == ".json") { _viewModel.ConfigLocation = file; await _viewModel.LoadConfigCommand.ExecuteAsync(null); }
                else await _viewModel.OpenFileAsync(file);
            }
            else if (e.DataTransfer.TryGetText() is { } text && Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            { _viewModel.Url = uri.AbsoluteUri; await _viewModel.OpenUrlCommand.ExecuteAsync(null); }
        });
        if (initialize) Opened += async (_, _) => await _viewModel.InitializeAsync();
        Closing += async (_, e) =>
        {
            if (_closing) return;
            e.Cancel = true; _closing = true; SettingsWindow?.Close();
            try
            {
                _surfaceLifetime.Cancel(); _viewModel.Engine.ActiveEngineChanged -= EngineChanged;
                foreach (var engine in _observedEngines)
                {
                    if (engine is VodBox.Playback.LibVlc.LibVlcEngine vlc) vlc.Initialized -= NativeInitialized;
                    if (engine is VodBox.Playback.Mpv.MpvEngine mpv) mpv.Initialized -= NativeInitialized;
                }
                // Save progress and stop decoding before invalidating native drawables.
                await _viewModel.StopCommand.ExecuteAsync(null);
                _playbackControlsOverlay?.Dispose(); _danmakuOverlay?.Dispose(); DanmakuPreview.Dispose(); VideoSurface.MediaPlayer = null;
                if (_mpvSurface is not null) VideoContainer.Children.Remove(_mpvSurface);
                await _viewModel.DisposeAsync();
            }
            catch (Exception error) { Console.Error.WriteLine(error); }
            finally { _surfaceLifetime.Dispose(); Close(); }
        };
        KeyDown += async (_, e) =>
        {
            if (e.Handled) return;
            if ((e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0 && e.Key == Key.O)
            { if (await PickAsync("打开媒体") is { } path) await _viewModel.OpenFileAsync(path); e.Handled = true; return; }
            if (e.KeyModifiers != KeyModifiers.None || e.Source is Avalonia.Visual visual && visual.GetVisualAncestors().Prepend(visual).Any(x => x is TextBox or ComboBox or Slider or NumericUpDown or Button)) return;
            switch (e.Key)
            {
                case Key.Space: await _viewModel.TogglePauseCommand.ExecuteAsync(null); break;
                case Key.Left: await _viewModel.SeekRelativeAsync(-10); break;
                case Key.Right: await _viewModel.SeekRelativeAsync(10); break;
                case Key.Up: _viewModel.Volume = Math.Min(100, _viewModel.Volume + 5); break;
                case Key.Down: _viewModel.Volume = Math.Max(0, _viewModel.Volume - 5); break;
                case Key.M: _viewModel.ToggleMute(); break;
                case Key.PageDown: await _viewModel.NextEpisodeCommand.ExecuteAsync(null); break;
                case Key.PageUp: await _viewModel.PreviousEpisodeCommand.ExecuteAsync(null); break;
                default: return;
            }
            e.Handled = true;
        };
    }

    private async Task<string?> PickAsync(string title)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new() { Title = title, AllowMultiple = false });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }
    private async void OpenFileClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    { if (_viewModel is not null && await PickAsync("打开媒体") is { } path) await _viewModel.OpenFileAsync(path); }

    private WindowState _windowBeforeFullscreen = WindowState.Normal;
    public void ToggleFullscreen()
    {
        if (IsVisible) Activate();
        if (WindowState == WindowState.FullScreen) WindowState = _windowBeforeFullscreen;
        else { _windowBeforeFullscreen = WindowState; WindowState = WindowState.FullScreen; }
    }
    private void FullscreenClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => ToggleFullscreen();

}
