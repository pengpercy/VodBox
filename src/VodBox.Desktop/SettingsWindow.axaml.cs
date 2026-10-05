using Avalonia.Controls;
using Avalonia.Media;

namespace VodBox.Desktop;

public sealed partial class SettingsWindow : Window
{
    public Views.SettingsView SettingsView => SettingsPage;
    public SettingsWindow()
    {
        InitializeComponent();
        if (Design.IsDesignMode) Background = new SolidColorBrush(Color.Parse("#111317"));
    }
    public SettingsWindow(MainViewModel model) : this() => DataContext = model;
}
