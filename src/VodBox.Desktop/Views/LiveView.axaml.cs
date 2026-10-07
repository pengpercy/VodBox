using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using VodBox.Core;
using VodBox.Desktop.ViewModels;

namespace VodBox.Desktop.Views;

public partial class LiveView : UserControl
{
    public LiveView() => InitializeComponent();
    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private LiveViewModel VM => ((MainViewModel)DataContext!).Live;

    private void OnPlayChannel(object? sender, PointerPressedEventArgs e)
    {
        if ((sender as Control)?.Tag is LiveChannel channel) VM.PlayChannelCommand.Execute(channel);
    }

    private void OnTabGroups(object? sender, Avalonia.Interactivity.RoutedEventArgs e) { }
    private void OnTabFavorites(object? sender, Avalonia.Interactivity.RoutedEventArgs e) { }
    private void OnTabHistory(object? sender, Avalonia.Interactivity.RoutedEventArgs e) { }
}
