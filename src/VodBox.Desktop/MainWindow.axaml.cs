using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using VodBox.Desktop.Services;
using VodBox.Desktop.ViewModels;

namespace VodBox.Desktop;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnPlaybackKeyDown, RoutingStrategies.Bubble);
    }

    private MainViewModel VM => (MainViewModel)DataContext!;

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is MainViewModel vm && Avalonia.Application.Current is App)
        {
            vm.AttachDispatcher(Avalonia.Threading.Dispatcher.UIThread);
            // 启动时若有历史配置则自动加载
            Loaded += async (_, _) =>
            {
                if (App.Services.CurrentVodConfig is { Length: > 0 } url)
                {
                    try
                    {
                        await App.Services.Registry.LoadConfigAsync(url);
                        vm.UpdateSourceName();
                        await vm.Home.LoadAsync();
                    }
                    catch
                    {
                        vm.StatusMessage = "上次配置加载失败，请到设置中重新配置";
                    }
                }
                if (App.Services.CurrentLiveConfig is { Length: > 0 })
                {
                    await vm.Live.LoadAsync();
                }
            };
        }
    }

    private void OnPlaybackKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || DataContext is not MainViewModel main || !main.Player.Visible) return;
        // 输入框和下拉框的按键属于编辑/选择，不能触发播放操作。
        if (e.Source is Avalonia.Visual visual &&
            (visual is TextBox or ComboBox || visual.GetVisualAncestors().Any(v => v is TextBox or ComboBox))) return;
        if ((e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta)) != 0) return;
        var player = main.Player;
        switch (e.Key)
        {
            case Key.Space: player.TogglePlayPauseCommand.Execute(null); break;
            case Key.Left: player.SeekBy(-5); break;
            case Key.Right: player.SeekBy(5); break;
            case Key.Up: player.Volume = Math.Clamp(player.Volume + 5, 0, 100); break;
            case Key.Down: player.Volume = Math.Clamp(player.Volume - 5, 0, 100); break;
            case Key.F:
            case Key.Enter:
                WindowState = WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen;
                break;
            case Key.Escape:
                if (WindowState == WindowState.FullScreen) WindowState = WindowState.Normal;
                else player.CloseCommand.Execute(null);
                break;
            default: return;
        }
        e.Handled = true;
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Return) VM.SubmitSearchCommand.Execute(null);
    }
}
