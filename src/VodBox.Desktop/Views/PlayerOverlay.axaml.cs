using System;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.Data.Converters;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using VodBox.Desktop.Services;
using VodBox.Desktop.ViewModels;
using VodBox.Core;
using VodBox.Playback.Mpv;

namespace VodBox.Desktop.Views;

/// <summary>TimeSpan → mm:ss。</summary>
public static class TimeConverters
{
    public static readonly IValueConverter ToTime = new ToTimeConverter();

    private sealed class ToTimeConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is not TimeSpan span) return "00:00";
            return span.TotalHours >= 1
                ? $"{(int)span.TotalHours:00}:{span.Minutes:00}:{span.Seconds:00}"
                : $"{span.Minutes:00}:{span.Seconds:00}";
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}

public partial class PlayerOverlay : UserControl
{
    private MpvVideoSurface? _surface;
    private readonly DispatcherTimer _controlsTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private long _lastActivity = Environment.TickCount64;
    private bool _menuOpen;
    private static readonly Cursor HiddenCursor = new(StandardCursorType.None);


    public PlayerOverlay()
    {
        InitializeComponent();
        if (this.FindControl<Slider>("ProgressSlider") is { } progress)
        {
            progress.AddHandler(PointerPressedEvent, (_, _) => VM?.BeginSeek(), RoutingStrategies.Tunnel, true);
            progress.AddHandler(PointerReleasedEvent, (_, _) => VM?.CommitSeek(), RoutingStrategies.Bubble, true);
            progress.AddHandler(KeyDownEvent, (_, e) =>
            {
                if (IsSeekKey(e.Key)) VM?.BeginSeek();
            }, RoutingStrategies.Tunnel, true);
            progress.AddHandler(KeyUpEvent, (_, e) =>
            {
                if (IsSeekKey(e.Key)) VM?.CommitSeek();
            }, RoutingStrategies.Bubble, true);
            progress.LostFocus += (_, _) => VM?.CancelSeek();
        }

        _controlsTimer.Tick += (_, _) => UpdateControls(TimeSpan.FromMilliseconds(Environment.TickCount64 - _lastActivity));
        AddHandler(PointerMovedEvent, (_, _) => RevealControls(), RoutingStrategies.Tunnel, true);
        AddHandler(PointerPressedEvent, (_, _) => RevealControls(), RoutingStrategies.Tunnel, true);
        AddHandler(KeyDownEvent, (_, _) => RevealControls(), RoutingStrategies.Tunnel, true);
        AttachedToVisualTree += OnAttached;
        DetachedFromVisualTree += OnDetached;
    }

    private static bool IsSeekKey(Key key) => key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown;

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>当前 Player 子 VM；DataContext 未装载（XAML 初始化早期/设计时）时为 null，调用方需判空。</summary>
    private PlayerViewModel? VM => DataContext is MainViewModel main ? main.Player : null;

    private void OnAttached(object? sender, Avalonia.VisualTreeAttachmentEventArgs e)
    {
        RevealControls();
        TryInstallSurface();
        if (Avalonia.Application.Current is App) _controlsTimer.Start();
    }

    private void RevealControls()
    {
        _lastActivity = Environment.TickCount64;
        if (VM is { } vm) vm.ControlsVisible = true;
        Cursor = null;
    }

    internal void UpdateControls(TimeSpan idle)
    {
        if (VM is not { } vm) return;
        var hovering = this.FindControl<Control>("TopControls")?.IsPointerOver == true ||
                       this.FindControl<Control>("BottomControls")?.IsPointerOver == true;
        var show = !vm.Visible || !IsVisible || vm.State != PlaybackState.Playing || vm.IsSeeking ||
                   _menuOpen || hovering || idle < TimeSpan.FromSeconds(3);
        vm.ControlsVisible = show;
        Cursor = show ? null : HiddenCursor;
        if (!vm.Visible || vm.State != PlaybackState.Playing) _lastActivity = Environment.TickCount64;
    }

    private void OpenControlMenu(Button button, ContextMenu menu)
    {
        _menuOpen = true;
        RevealControls();
        menu.Closed += (_, _) => { _menuOpen = false; RevealControls(); };
        button.ContextMenu = menu;
        menu.Open(button);
    }

    private void OnDetached(object? sender, Avalonia.VisualTreeAttachmentEventArgs e)
    {
        _controlsTimer.Stop();
        VM?.CancelSeek();
        RevealControls();
        // 控件卸载时丢渲染面；重新入树时重建（OpenGlControlBase 会重新走 Init 流程）。
        if (_surface is not null)
        {
            _surface.Ready -= OnSurfaceReady;
            _surface.Failed -= OnSurfaceFailed;
            VideoHost.Children.Remove(_surface);
            _surface = null;
        }
    }

    /// <summary>引擎触发懒初始化后拿到真实 MpvClient，装渲染面；重复调用幂等。</summary>
    private void TryInstallSurface()
    {
        if (_surface is not null || Avalonia.Application.Current is not App) return;
        var engine = App.Services.Player;
        if (engine.Client is not MpvClient client) return;
        _surface = new MpvVideoSurface(client);
        _surface.Ready += OnSurfaceReady;
        _surface.Failed += OnSurfaceFailed;
        VideoHost.Children.Add(_surface);
    }

    private void OnSurfaceReady(object? sender, EventArgs e)
    {
        if (_surface is not null) _surface.Ready -= OnSurfaceReady;
        if (Avalonia.Application.Current is App) App.Services.Player.NotifyVideoSurfaceReady();
    }

    private void OnSurfaceFailed(object? sender, Exception error)
    {
        if (_surface is not null) _surface.Failed -= OnSurfaceFailed;
        if (Avalonia.Application.Current is App) App.Services.Player.NotifyVideoSurfaceFailure(error);
        if (DataContext is MainViewModel main) main.Player.Error = error.Message;
    }

    private void OnTogglePlay(object? sender, RoutedEventArgs e) => VM?.TogglePlayPauseCommand.Execute(null);

    private void OnBack10(object? sender, RoutedEventArgs e) => VM?.SeekByCommand.Execute(-10d);
    private void OnForward10(object? sender, RoutedEventArgs e) => VM?.SeekByCommand.Execute(10d);
    private void OnClose(object? sender, RoutedEventArgs e) => VM?.CloseCommand.Execute(null);

    private void OnRateChanged(object? sender, SelectionChangedEventArgs e)
    {
        // XAML 装载期（SelectedIndex 在 EndInit 时触发）DataContext 尚未来得及赋值，直接忽略。
        if (sender is ComboBox { SelectedIndex: var index } && VM is { } vm)
        {
            vm.Rate = index switch
            {
                0 => 0.5, 1 => 1.0, 2 => 1.5, 3 => 2.0, _ => 1.0,
            };
        }
    }

    private static readonly double[] Rates = [0.5, 1.0, 1.25, 1.5, 2.0];

    private void OnCycleRate(object? sender, RoutedEventArgs e)
    {
        if (VM is not { } vm) return;
        var index = Array.IndexOf(Rates, vm.Rate);
        vm.Rate = Rates[(index + 1) % Rates.Length];

    }

    private void OnTogglePip(object? sender, RoutedEventArgs e)
    {
        if (VM is { } vm) vm.FlashToast("画中画（S2 接）");
    }

    private void OnTogglePlugin(object? sender, RoutedEventArgs e)
    {
        if (VM is { } vm) vm.FlashToast("插件（S3 接）");
    }

    private void OnTogglePlaylist(object? sender, RoutedEventArgs e)
    {
        if (VM is not { } vm || sender is not Button button) return;
        var menu = new ContextMenu();
        var session = vm.CurrentSessionId;
        if (vm.Playlist.Count == 0) menu.Items.Add(new MenuItem { Header = "当前媒体没有剧集列表", IsEnabled = false });
        for (var i = 0; i < vm.Playlist.Count; i++)
        {
            var index = i;
            var item = new MenuItem { Header = (i == vm.PlaylistIndex ? "当前 · " : "") + vm.Playlist[i].Title };
            item.Click += async (_, _) => { if (session == vm.CurrentSessionId) await vm.PlayPlaylistIndexAsync(index); };
            menu.Items.Add(item);
        }
        OpenControlMenu(button, menu);
    }

    private void OnToggleSettings(object? sender, RoutedEventArgs e)
    {
        if (VM is not { } vm || sender is not Button button) return;
        var menu = new ContextMenu();
        var autoNext = new MenuItem { Header = vm.AutoNext ? "自动下一集：开" : "自动下一集：关" };
        autoNext.Click += (_, _) => vm.AutoNext = !vm.AutoNext;
        menu.Items.Add(autoNext);
        foreach (var (kind, label) in new[] { (TrackKind.Audio, "音轨"), (TrackKind.Subtitle, "字幕") })
        {
            var group = new MenuItem { Header = label };
            var items = new List<MenuItem>();
            var tracks = vm.GetTracks(kind);
            foreach (var track in tracks)
            {
                var item = new MenuItem { Header = (track.IsSelected ? "当前 · " : "") + track.Name };
                var sessionId = vm.CurrentSessionId;
                item.Click += async (_, _) =>
                {
                    if (vm.CurrentSessionId == sessionId) await vm.SelectTrackAsync(kind, track.Id);
                };
                items.Add(item);
            }
            if (kind == TrackKind.Subtitle)
            {
                var off = new MenuItem { Header = "关闭字幕" };
                var sessionId = vm.CurrentSessionId;
                off.Click += async (_, _) =>
                {
                    if (vm.CurrentSessionId == sessionId) await vm.SelectTrackAsync(kind, "no");
                };
                items.Add(off);
            }
            if (items.Count == 0) items.Add(new MenuItem { Header = "暂无可用轨道", IsEnabled = false });
            group.ItemsSource = items;
            menu.Items.Add(group);
        }
        OpenControlMenu(button, menu);
    }

    private void OnToggleAspectRatio(object? sender, RoutedEventArgs e)
    {
        if (VM is { } vm) vm.FlashToast("画面比例（S2 接）");
    }

    private void OnToggleFullscreen(object? sender, RoutedEventArgs e)
    {
        // 全屏由宿主 Window 处理（VM 不持控件引用）
        if (Avalonia.Controls.TopLevel.GetTopLevel(this) is Avalonia.Controls.Window window)
            window.WindowState = window.WindowState == Avalonia.Controls.WindowState.FullScreen
                ? Avalonia.Controls.WindowState.Normal
                : Avalonia.Controls.WindowState.FullScreen;
    }
}
