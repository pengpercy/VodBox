using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using VodBox.Core;
using VodBox.Desktop.ViewModels;

namespace VodBox.Desktop.Views;

public partial class DetailView : UserControl
{
    public DetailView() => InitializeComponent();
    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private DetailViewModel VM => ((MainViewModel)DataContext!).Detail;

    private void OnPickLine(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is PlaybackLine line) VM.SelectedLine = line;
    }

    private void OnPickEpisode(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is Episode episode)
        {
            VM.SelectedEpisode = episode;
            VM.PlayCommand.Execute(null);
        }
    }
}
