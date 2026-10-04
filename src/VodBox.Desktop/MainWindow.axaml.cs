using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using VodBox.Core;

namespace VodBox.Desktop;

public sealed partial class MainWindow : Window
{
    private readonly MainViewModel? _viewModel;
    private bool _closing;
    public MainWindow()
    {
        InitializeComponent();
        if (Design.IsDesignMode)
        {
            // The designer has no desktop compositor; give sample content a readable backdrop.
            Background = (IBrush)Resources["WindowFallbackBrush"]!;
            return;
        }
        _viewModel = new(); DataContext = _viewModel;
        _viewModel.Engine.Initialized += (_, _) => Dispatcher.UIThread.Invoke(() =>
        {
            VideoSurface.MediaPlayer = _viewModel.Engine.Player;
            if (_viewModel.Engine.Player is { } player)
            {
                player.Volume = (int)_viewModel.Volume;
                player.SetRate((float)_viewModel.Rate);
            }
        });
        Opened += async (_, _) => await _viewModel.InitializeAsync();
        Closing += async (_, e) =>
        {
            if (_closing) return;
            e.Cancel = true; _closing = true;
            try { VideoSurface.MediaPlayer = null; await _viewModel.DisposeAsync(); }
            finally { Close(); }
        };
        KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Escape) WindowState = WindowState.Normal;
            if (e.Key == Key.Space && e.Source is not TextBox) { await _viewModel.TogglePauseCommand.ExecuteAsync(null); e.Handled = true; }
        };
    }
    private async Task<string?> PickAsync(string title)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new() { Title = title, AllowMultiple = false });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }
    private async void OpenFileClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    { if (_viewModel is not null && await PickAsync("打开媒体") is { } path) await _viewModel.OpenFileAsync(path); }
    private async void OpenConfigClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    { if (_viewModel is not null && await PickAsync("打开 VodBox 配置") is { } path) { _viewModel.ConfigLocation = path; await _viewModel.LoadConfigCommand.ExecuteAsync(null); } }
    private async void AddSubtitleClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    { if (_viewModel is not null && await PickAsync("打开字幕") is { } path) await _viewModel.AddSubtitleAsync(path); }
    private void FullscreenClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => WindowState = WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen;
    private void SeekPressed(object? sender, PointerPressedEventArgs e) { if (_viewModel is not null) _viewModel.IsScrubbing = true; }
    private async void SeekReleased(object? sender, PointerReleasedEventArgs e) { if (_viewModel is not null) { await _viewModel.SeekAsync(); _viewModel.IsScrubbing = false; } }
    private async void EpisodeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    { if (_viewModel is not null && e.AddedItems.OfType<Episode>().FirstOrDefault() is { } episode) await _viewModel.PlayEpisodeCommand.ExecuteAsync(episode); }
    private async void LiveSelectionChanged(object? sender, SelectionChangedEventArgs e)
    { if (_viewModel is not null && e.AddedItems.OfType<LiveChannel>().FirstOrDefault() is { } channel) await _viewModel.PlayChannelCommand.ExecuteAsync(channel); }
    private async void HistorySelectionChanged(object? sender, SelectionChangedEventArgs e)
    { if (_viewModel is not null && e.AddedItems.OfType<HistoryEntry>().FirstOrDefault() is { } entry) await _viewModel.ResumeHistoryCommand.ExecuteAsync(entry); }
}
