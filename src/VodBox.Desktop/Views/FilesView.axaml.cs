using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using VodBox.Core;
using VodBox.Desktop.ViewModels;

namespace VodBox.Desktop.Views;

public partial class FilesView : UserControl
{
    public FilesView() => InitializeComponent();
    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private FilesViewModel VM => ((MainViewModel)DataContext!).Files;

    private void OnOpen(object? sender, PointerPressedEventArgs e)
    {
        if ((sender as Control)?.Tag is FileEntry entry) VM.OpenCommand.Execute(entry);
    }
}
