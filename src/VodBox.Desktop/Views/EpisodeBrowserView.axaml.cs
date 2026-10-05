using Avalonia.Controls;

namespace VodBox.Desktop.Views;

public sealed partial class EpisodeBrowserView : UserControl
{
    public EpisodeBrowserView() => InitializeComponent();
    private async void EpisodeClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is MainViewModel model && sender is Button { DataContext: VodBox.Core.Episode episode })
            await model.PlayEpisodeCommand.ExecuteAsync(episode);
    }
}
