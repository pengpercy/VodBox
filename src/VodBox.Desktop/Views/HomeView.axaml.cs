using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VodBox.Core;

namespace VodBox.Desktop.Views;

public sealed partial class HomeView : UserControl
{
    private MainViewModel? Model => DataContext as MainViewModel;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    public HomeView()
    {
        InitializeComponent();
        _clock.Tick += (_, _) => UpdateClock();
        AttachedToVisualTree += (_, _) => { UpdateClock(); _clock.Start(); };
        DetachedFromVisualTree += (_, _) => _clock.Stop();
        SizeChanged += (_, _) => Model?.UpdateHomeColumns(Bounds.Width);
        DataContextChanged += (_, _) => Model?.UpdateHomeColumns(Bounds.Width);
    }
    private void UpdateClock() => Clock.Text = DateTime.Now.ToString("MM/dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
    private void RecommendationClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    { if (Model is not null && sender is Button { DataContext: MediaCard card }) Model.SelectedCard = card; }
    private async void HistoryClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    { if (Model is not null && sender is Button { DataContext: HistoryEntry entry }) await Model.ResumeHistoryCommand.ExecuteAsync(entry); }
    private async void OpenLocalClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    { if (Model is not null && await ViewFilePicker.PickAsync(this, "打开本地媒体") is { } path) await Model.OpenFileAsync(path); }
    private readonly Dictionary<Image, MediaCard> _posterControls = [];
    private void PosterAttached(object? sender, Avalonia.VisualTreeAttachmentEventArgs e) => PosterContextChanged(sender, EventArgs.Empty);
    private void PosterDetached(object? sender, Avalonia.VisualTreeAttachmentEventArgs e)
    { if (sender is Image image && _posterControls.Remove(image, out var previous)) MainViewModel.ReleasePoster(previous); }
    private void PosterContextChanged(object? sender, EventArgs e)
    {
        if (sender is not Image image) return;
        if (_posterControls.Remove(image, out var previous)) MainViewModel.ReleasePoster(previous);
        if (Model is not null && image.IsAttachedToVisualTree() && image.DataContext is MediaCard card)
        { _posterControls[image] = card; Model.ActivatePoster(card); }
    }
}
