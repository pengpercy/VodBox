using Avalonia.Controls;
using Avalonia.Input;
namespace VodBox.Desktop.Views;
public sealed partial class SearchView : UserControl
{
    private MainViewModel? Model => DataContext as MainViewModel;
    public SearchView() => InitializeComponent();
    private async void KeywordKeyDown(object? sender, KeyEventArgs args)
    { if (args.Key == Key.Enter && Model is not null) { args.Handled = true; await Model.SubmitSearchCommand.ExecuteAsync(null); } }
    private async void KeywordClick(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    { if (Model is not null && sender is Button { DataContext: string keyword }) await Model.SubmitSearchCommand.ExecuteAsync(keyword); }
}
