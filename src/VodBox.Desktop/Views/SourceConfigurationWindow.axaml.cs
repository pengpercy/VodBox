using Avalonia.Controls;
using Avalonia.Media;

namespace VodBox.Desktop.Views;

public sealed partial class SourceConfigurationWindow : Window
{
    public SourceConfigurationWindow()
    {
        InitializeComponent();
        if (Design.IsDesignMode) Background = new SolidColorBrush(Color.Parse("#111317"));
    }
    public SourceConfigurationWindow(MainViewModel model) : this() => DataContext = model;
}
