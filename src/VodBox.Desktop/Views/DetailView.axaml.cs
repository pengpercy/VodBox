using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using VodBox.Core;
using VodBox.Desktop.ViewModels;

namespace VodBox.Desktop.Views;

public partial class DetailView : UserControl
{
    public DetailView()
    {
        InitializeComponent();
        // 防御：根元素持有本地值，不继承宿主的 DataContext（MainViewModel 若流进按 DetailViewModel 编译的绑定会强转失败）；
        // 实际由 OnDataContextChanged 赋值，DetailViewBindsCleanlyInBothHosts 覆盖两种宿主与两种赋值顺序。
        this.FindControl<ScrollViewer>("DetailRoot")!.DataContext = null;
    }
    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// 两种宿主：主窗口（DataContext 为 MainViewModel，显示其 Detail）与搜索窗口（直接给一个独立的 DetailViewModel）。
    /// 绑定都按 DetailViewModel 编译，所以把解析出的详情 VM 设在根元素上，不让 MainViewModel 流进按 DetailViewModel 编译的绑定。
    /// </summary>
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (this.FindControl<ScrollViewer>("DetailRoot") is { } root)
            root.DataContext = DataContext switch { MainViewModel main => main.Detail, DetailViewModel detail => detail, _ => null };
    }

    private DetailViewModel VM => (DetailViewModel)this.FindControl<ScrollViewer>("DetailRoot")!.DataContext!;

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
