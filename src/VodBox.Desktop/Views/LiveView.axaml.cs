using Avalonia.Controls;
using VodBox.Core;

namespace VodBox.Desktop.Views;

public sealed partial class LiveView : UserControl
{
    private MainViewModel? Model => DataContext as MainViewModel;
    public LiveView() => InitializeComponent();
    private async void LiveSelectionChanged(object? sender, SelectionChangedEventArgs e)
    { if (Model is not null && e.AddedItems.OfType<LiveChannel>().FirstOrDefault() is { } channel) await Model.PlayChannelCommand.ExecuteAsync(channel); }
}
