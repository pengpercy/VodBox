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
    private bool _shutdownStarted;
    private bool _shutdownComplete;
    public MainWindow()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnPlaybackKeyDown, RoutingStrategies.Bubble);
        Closing+=OnClosing;
    }

    private async void OnClosing(object? sender,WindowClosingEventArgs e)
    {
        if(_shutdownComplete||Avalonia.Application.Current is not App||DataContext is not MainViewModel main)return;
        e.Cancel=true;
        if(_shutdownStarted)return;
        _shutdownStarted=true;
        try { await main.ShutdownAsync(); }
        catch(Exception error) { System.Diagnostics.Debug.WriteLine($"[shutdown] {error.Message}"); }
        finally
        {
            try { App.Services.Dispose(); }
            catch(Exception error){System.Diagnostics.Debug.WriteLine($"[shutdown] release failed: {error.Message}");}
            _shutdownComplete=true;Close();
        }
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
        var episodeNumber = e.Key switch
        {
            Key.D1 or Key.NumPad1 => 1, Key.D2 or Key.NumPad2 => 2, Key.D3 or Key.NumPad3 => 3,
            Key.D4 or Key.NumPad4 => 4, Key.D5 or Key.NumPad5 => 5, Key.D6 or Key.NumPad6 => 6,
            Key.D7 or Key.NumPad7 => 7, Key.D8 or Key.NumPad8 => 8, Key.D9 or Key.NumPad9 => 9,
            _ => 0,
        };
        if (episodeNumber > 0)
        {
            if (e.KeyModifiers != KeyModifiers.None || episodeNumber > player.Playlist.Count) return;
            _ = player.SelectEpisodeNumberAsync(episodeNumber);
            e.Handled = true;
            return;
        }
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
