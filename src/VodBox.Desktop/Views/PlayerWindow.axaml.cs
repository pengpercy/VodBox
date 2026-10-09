using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using VodBox.Desktop.ViewModels;

namespace VodBox.Desktop.Views;

public partial class PlayerWindow : Window
{
    private bool _allowClose;
    private bool _closing;
    private bool _closed;
    internal PlayerOverlay Overlay => this.FindControl<PlayerOverlay>("PlayerView")!;

    public PlayerWindow()
    {
        AvaloniaXamlLoader.Load(this);
        AddHandler(KeyDownEvent, OnPlaybackKeyDown, RoutingStrategies.Bubble);
        Closing += OnClosing;
        Closed += (_, _) => _closed = true;
    }

    internal void CloseAfterPlayback()
    {
        if (_closed) return;
        _allowClose = true;
        Close();
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_allowClose || DataContext is not MainViewModel main || !main.Player.Visible) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true;
        try { await main.Player.CloseCommand.ExecuteAsync(null); }
        catch (Exception error) { main.Player.Error = error.Message; }
        finally
        {
            _closing = false;
            // A newer episode selected during shutdown wins: never close its reused window.
            if (!main.Player.Visible) CloseAfterPlayback();
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

}
