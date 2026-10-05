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
    private bool _closing;
    private readonly Dictionary<Image, MediaCard> _posterControls = [];
    public MainWindow() : this(preview: false) { }
    public MainWindow(bool preview)
    {
        InitializeComponent();
        if (Design.IsDesignMode || preview)
        {
            // The designer has no desktop compositor; give sample content a readable backdrop.
            DataContext = new DesignMainViewModel(); VideoSurface.IsVisible = false;
            Background = new SolidColorBrush(Color.Parse("#111317"));
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
            if (e.Key == Key.Escape) { WindowState = WindowState.Normal; e.Handled = true; return; }
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
                case Key.F11: WindowState = WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen; break;
                case Key.PageDown: await _viewModel.NextEpisodeCommand.ExecuteAsync(null); break;
                case Key.PageUp: await _viewModel.PreviousEpisodeCommand.ExecuteAsync(null); break;
                default: return;
            }
            e.Handled = true;
        };
    }
    private void PosterAttached(object? sender, Avalonia.VisualTreeAttachmentEventArgs e) => PosterContextChanged(sender, EventArgs.Empty);
    private void PosterDetached(object? sender, Avalonia.VisualTreeAttachmentEventArgs e)
    { if (sender is Image image && _posterControls.Remove(image, out var previous)) MainViewModel.ReleasePoster(previous); }
    private void PosterContextChanged(object? sender, EventArgs e)
    {
        if (sender is not Image image) return;
        if (_posterControls.Remove(image, out var previous)) MainViewModel.ReleasePoster(previous);
        if (_viewModel is not null && image.IsAttachedToVisualTree() && image.DataContext is MediaCard card)
        { _posterControls[image] = card; _viewModel.ActivatePoster(card); }
    }
    private async void ExportBackupClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        var file = await StorageProvider.SaveFilePickerAsync(new() { Title = "导出 VodBox 数据备份", SuggestedFileName = $"VodBox-{DateTime.Now:yyyyMMdd-HHmmss}.vodbox-backup.json", FileTypeChoices = [new("VodBox 数据备份") { Patterns = ["*.vodbox-backup.json"] }] });
        if (file?.TryGetLocalPath() is { } path) await _viewModel.ExportBackupAsync(path);
    }
    private async void ImportBackupClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        var files = await StorageProvider.OpenFilePickerAsync(new() { Title = "选择备份并合并数据", AllowMultiple = false, FileTypeFilter = [new("VodBox 数据备份") { Patterns = ["*.vodbox-backup.json"] }] });
        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path) await _viewModel.ImportBackupAsync(path);
    }
    private async void SnapshotClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        var file = await StorageProvider.SaveFilePickerAsync(new() { Title = "保存视频截图", SuggestedFileName = $"VodBox-{DateTime.Now:yyyyMMdd-HHmmss}.png", DefaultExtension = "png", FileTypeChoices = [new("PNG 图片") { Patterns = ["*.png"] }] });
        if (file?.TryGetLocalPath() is { } path) await _viewModel.TakeSnapshotAsync(path);
    }
    private async void DeleteHistoryClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    { if (_viewModel is not null && sender is Button { DataContext: HistoryEntry entry }) { e.Handled = true; await _viewModel.DeleteHistoryAsync(entry); } }
    private async void FavoriteSelectionChanged(object? sender, SelectionChangedEventArgs e)
    { if (_viewModel is not null && e.AddedItems.Count > 0 && e.AddedItems[0] is FavoriteEntry entry) await _viewModel.OpenFavoriteCommand.ExecuteAsync(entry); }
    private async void SearchSelectionChanged(object? sender, SelectionChangedEventArgs e)
    { if (_viewModel is not null && e.AddedItems.Count > 0 && e.AddedItems[0] is SearchHit hit) await _viewModel.OpenSearchResultCommand.ExecuteAsync(hit); }
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
