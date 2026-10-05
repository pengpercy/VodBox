using Avalonia.Controls;
using VodBox.Core;

namespace VodBox.Desktop.Views;

public sealed partial class HistoryView : UserControl
{
    private MainViewModel? Model => DataContext as MainViewModel;
    public HistoryView() => InitializeComponent();
    private async void DeleteHistoryClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    { if (Model is not null && sender is Button { DataContext: HistoryEntry entry }) { e.Handled = true; await Model.DeleteHistoryAsync(entry); } }
    private async void HistorySelectionChanged(object? sender, SelectionChangedEventArgs e)
    { if (Model is not null && e.AddedItems.OfType<HistoryEntry>().FirstOrDefault() is { } entry) await Model.ResumeHistoryCommand.ExecuteAsync(entry); }
}
