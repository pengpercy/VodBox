using Avalonia.Controls;

namespace VodBox.Desktop.Views;

public sealed partial class SourceConfigurationView : UserControl
{
    private MainViewModel? Model => DataContext as MainViewModel;
    public SourceConfigurationView() => InitializeComponent();
    private async void ChooseFileClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    { if (Model is not null && await ViewFilePicker.PickAsync(this, "选择播放源配置") is { } path) Model.ConfigLocation = path; }
    private async void LoadClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (Model is null) return;
        int version = Model.ConfigurationLoadVersion;
        await Model.LoadConfigCommand.ExecuteAsync(null);
        if (Model.ConfigurationLoadVersion != version && TopLevel.GetTopLevel(this) is Window window) window.Close();
    }
}
