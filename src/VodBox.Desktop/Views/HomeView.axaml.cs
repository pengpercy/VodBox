using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using VodBox.Core;
using VodBox.Desktop.ViewModels;

namespace VodBox.Desktop.Views;

public partial class HomeView : UserControl
{
    public HomeView() => InitializeComponent();
    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private HomeViewModel VM => ((MainViewModel)DataContext!).Home;

    private void OnHeroClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => VM.OpenHeroCommand.Execute(null);
    private void OnHeroPlay(object? sender, RoutedEventArgs e) => VM.OpenHeroCommand.Execute(null);
    private void OnHeroPressed(object? sender, PointerPressedEventArgs e) => VM.OpenHeroCommand.Execute(null);

    private void OnResumeHistory(object? sender, PointerPressedEventArgs e)
    {
        if ((sender as Control)?.Tag is HistoryEntry entry) VM.ResumeCommand.Execute(entry);
    }

    private void OnOpenRecommend(object? sender, PointerPressedEventArgs e)
    {
        if ((sender as Control)?.Tag is MediaItem item) VM.OpenItemCommand.Execute(item);
    }

    private void OnShowAllHistory(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainViewModel main) main.GoHistoryCommand.Execute(null);
    }

    private void OnSwitchSource(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainViewModel main) main.GoSettingsCommand.Execute(null);
    }
}
