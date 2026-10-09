using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.Data.Converters;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Avalonia.Markup.Xaml;
using Avalonia.Layout;
using Avalonia.Media;
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
    private MpvEngine? _observedEngine;
    internal long RenderedFrames=>_surface?.RenderedFrames??0;
    private Window? _compactWindow;
    private Window? _sizedWindow;
    private (double Width,double Height,double MinWidth,double MinHeight,double MaxWidth,double MaxHeight,Avalonia.PixelPoint Position) _originalWindow;
    private double? _appliedVideoRatio;
    private (double Width,double Height,double MinWidth,double MinHeight,double MaxWidth,double MaxHeight) _compactSize;
    private (double Width, double Height, double MinWidth, double MinHeight, Avalonia.PixelPoint Position, bool Topmost, WindowState State) _windowSnapshot;
    private readonly DispatcherTimer _controlsTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private long _lastActivity = Environment.TickCount64;
    private bool _pointerOutside;
    private bool _menuOpen;
    private bool _drawerOpen;
    private readonly DispatcherTimer _drawerHideTimer =
        new() { Interval = TimeSpan.FromMilliseconds(PlayerLayout.PlaylistDrawerSlideMs + 40) };
    private ContextMenu? _activeMenu;
    private PlayerViewModel? _observedPlayer;
    private static readonly Cursor HiddenCursor = new(StandardCursorType.None);


    public PlayerOverlay()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => ObservePlayer();
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
        AddHandler(PointerPressedEvent, OnVideoPointerPressed, RoutingStrategies.Tunnel, true);
        PointerEntered += (_, _) => { _pointerOutside = false; RevealControls(); };
        PointerExited += (_, _) => { _pointerOutside = true; _lastActivity = Environment.TickCount64; Cursor = null; };
        AddHandler(KeyDownEvent, (_, _) => RevealControls(), RoutingStrategies.Tunnel, true);
        _drawerHideTimer.Tick += (_, _) =>
        {
            _drawerHideTimer.Stop();
            this.FindControl<Border>("PlaylistDrawer")?.IsVisible = false;
        };
        AttachedToVisualTree += OnAttached;
        DetachedFromVisualTree += OnDetached;
    }

    private static bool IsSeekKey(Key key) => key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown;

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>当前 Player 子 VM；DataContext 未装载（XAML 初始化早期/设计时）时为 null，调用方需判空。</summary>
    private PlayerViewModel? VM => DataContext is MainViewModel main ? main.Player : null;

    private void ObservePlayer()
    {
        if (_observedPlayer is not null) _observedPlayer.PropertyChanged -= OnPlayerChanged;
        _observedPlayer = VM;
        if (_observedPlayer is not null) _observedPlayer.PropertyChanged += OnPlayerChanged;
    }

    private void OnPlayerChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if(e.PropertyName==nameof(PlayerViewModel.VideoAspectRatio))FitWindowToVideo();
        if(e.PropertyName==nameof(PlayerViewModel.Visible)&&VM?.Visible==false)RestorePlayerWindow();
        if(e.PropertyName is nameof(PlayerViewModel.PlaylistIndex) or nameof(PlayerViewModel.Visible) or nameof(PlayerViewModel.Title))
        {
            _activeMenu?.Close();_activeMenu=null;_menuOpen=false;ClosePlaylistDrawer(animate:false);
        }
        if (e.PropertyName is nameof(PlayerViewModel.Position) or nameof(PlayerViewModel.Danmaku) or nameof(PlayerViewModel.DanmakuEnabled) or nameof(PlayerViewModel.DanmakuOpacity) or nameof(PlayerViewModel.DanmakuLimit) or nameof(PlayerViewModel.Visible))
            this.FindControl<DanmakuLayer>("DanmakuCanvas")?.InvalidateVisual();
    }

    private void OnAttached(object? sender, Avalonia.VisualTreeAttachmentEventArgs e)
    {
        ObservePlayer();
        RevealControls();
        if(Avalonia.Application.Current is App)
        {
            _observedEngine=App.Services.Player;_observedEngine.Initialized+=OnEngineInitialized;
        }
        TryInstallSurface();
        FitWindowToVideo();
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
        var show = !vm.Visible || !IsVisible || vm.IsSeeking || _menuOpen || _drawerOpen || hovering ||
                   (!_pointerOutside && vm.State != PlaybackState.Playing) ||
                   idle < (_pointerOutside ? TimeSpan.FromMilliseconds(350) : TimeSpan.FromMilliseconds(1500));
        vm.ControlsVisible = show;
        Cursor = show ? null : HiddenCursor;
        if (!vm.Visible || (!_pointerOutside && vm.State != PlaybackState.Playing)) _lastActivity = Environment.TickCount64;
    }

    private void OnVideoPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        RevealControls();
        if (e.Handled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed ||
            TopLevel.GetTopLevel(this) is not Window window) return;
        if (e.Source is Avalonia.Visual visual &&
            (visual is Button or Slider or MenuItem || visual.GetVisualAncestors().Any(v => v is Button or Slider or MenuItem))) return;
        if (_drawerOpen && !IsInsidePlaylistDrawer(e.Source as Avalonia.Visual))
        {
            ClosePlaylistDrawer();
            e.Handled = true;
            return;
        }
        if (e.ClickCount == 2)
        {
            window.WindowState = window.WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen;
            e.Handled = true;
        }
        else if (window.WindowState != WindowState.FullScreen)
        {
            window.BeginMoveDrag(e);
            e.Handled = true;
        }
    }

    private void OnToggleTopmost(object? sender, RoutedEventArgs e)
    {
        if (VM is not { } vm || TopLevel.GetTopLevel(this) is not Window window) return;
        window.Topmost = !window.Topmost;
        vm.AlwaysOnTop = window.Topmost;
    }

    private void OpenControlMenu(Button button, ContextMenu menu)
    {
        _activeMenu?.Close();_activeMenu=menu;
        _menuOpen = true;
        RevealControls();
        menu.Closed += (_, _) => { if(ReferenceEquals(_activeMenu,menu))_activeMenu=null;_menuOpen = false; RevealControls(); };
        button.ContextMenu = menu;
        menu.Open(button);
    }

    private void OnEngineInitialized(object? sender,EventArgs e)=>Dispatcher.UIThread.Post(TryInstallSurface);

    private void OnDetached(object? sender, Avalonia.VisualTreeAttachmentEventArgs e)
    {
        if(_observedEngine is not null)_observedEngine.Initialized-=OnEngineInitialized;
        _observedEngine=null;
        if (_observedPlayer is not null) _observedPlayer.PropertyChanged -= OnPlayerChanged;
        _observedPlayer = null;
        _activeMenu?.Close();_activeMenu=null;_menuOpen=false;ClosePlaylistDrawer(animate:false);
        RestoreWindow();RestorePlayerWindow();
        _controlsTimer.Stop();
        VM?.CancelSeek();
        RevealControls();
        // 控件卸载时丢渲染面；重新入树时重建（OpenGlControlBase 会重新走 Init 流程）。
        if (_surface is not null)
        {
            _surface.Ready -= OnSurfaceReady;
            _surface.Failed -= OnSurfaceFailed;
            this.FindControl<Panel>("VideoHost")?.Children.Remove(_surface);
            _surface = null;
        }
    }

    /// <summary>引擎触发懒初始化后拿到真实 MpvClient，装渲染面；重复调用幂等。</summary>
    private void TryInstallSurface()
    {
        if (_surface is not null || Avalonia.Application.Current is not App) return;
        var engine = App.Services.Player;
        if (engine.Client is not MpvClient client) return;
        var host=this.FindControl<Panel>("VideoHost");
        if(host is null)
        {
            var error=new InvalidOperationException("播放器视频宿主未加载。");
            engine.NotifyVideoSurfaceFailure(error);if(VM is {} vm)vm.Error=error.Message;return;
        }
        _surface = new MpvVideoSurface(client);
        _surface.Ready += OnSurfaceReady;
        _surface.Failed += OnSurfaceFailed;
        host.Children.Add(_surface);
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
    private void OnClose(object? sender, RoutedEventArgs e)
    {
        RestoreWindow();
        if (TopLevel.GetTopLevel(this) is Window window && window.WindowState == WindowState.FullScreen)
            window.WindowState = WindowState.Normal;
        VM?.CloseCommand.Execute(null);
    }

    protected override void OnPropertyChanged(Avalonia.AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty && !IsVisible) { RestoreWindow();RestorePlayerWindow(); }
    }

    private void FitWindowToVideo()
    {
        if(VM is not {Visible:true,VideoAspectRatio: >0} vm||TopLevel.GetTopLevel(this) is not Window window||window.WindowState!=WindowState.Normal)return;
        var ratio=vm.VideoAspectRatio!.Value;
        if(_appliedVideoRatio==ratio)return;
        if(_sizedWindow is null)
        {
            _sizedWindow=window;_originalWindow=(window.Width,window.Height,window.MinWidth,window.MinHeight,window.MaxWidth,window.MaxHeight,window.Position);
        }
        ApplyVideoSize(window,ratio,_compactWindow is not null);
        _appliedVideoRatio=ratio;
    }

    private void ApplyVideoSize(Window window,double ratio,bool compact)
    {
        var screen=window.Screens.ScreenFromWindow(window)??window.Screens.Primary;
        var width=screen is null?1280:Math.Max(1,screen.WorkingArea.Width/screen.Scaling-32);
        var height=screen is null?800:Math.Max(1,screen.WorkingArea.Height/screen.Scaling-48);
        var fit=PlayerWindowSizing.Fit(ratio,width,height,compact);
        window.MinWidth=0;window.MinHeight=0;window.MaxWidth=double.PositiveInfinity;window.MaxHeight=double.PositiveInfinity;
        window.Width=fit.Size.Width;window.Height=fit.Size.Height;
        window.MinWidth=fit.Minimum.Width;window.MinHeight=fit.Minimum.Height;window.MaxWidth=fit.Maximum.Width;window.MaxHeight=fit.Maximum.Height;
        if(screen is not null)window.Position=ClampRestoredPosition(window.Position,screen.WorkingArea,fit.Size.Width,fit.Size.Height,screen.Scaling);
    }

    private void RestorePlayerWindow()
    {
        if(_sizedWindow is not {} window)return;
        RestoreWindow();_sizedWindow=null;_appliedVideoRatio=null;
        var saved=_originalWindow;
        window.MinWidth=0;window.MinHeight=0;window.MaxWidth=double.PositiveInfinity;window.MaxHeight=double.PositiveInfinity;
        window.Width=saved.Width;window.Height=saved.Height;window.MinWidth=saved.MinWidth;window.MinHeight=saved.MinHeight;window.MaxWidth=saved.MaxWidth;window.MaxHeight=saved.MaxHeight;
        try
        {
            var screen=window.Screens.ScreenFromWindow(window)??window.Screens.Primary;
            window.Position=screen is null?saved.Position:ClampRestoredPosition(saved.Position,screen.WorkingArea,saved.Width,saved.Height,screen.Scaling);
        }
        catch(ObjectDisposedException) { /* native close already released the screen owner */ }
    }

    internal void ToggleCompactWindow(Window window)
    {
        if (_compactWindow is not null) { RestoreWindow(); return; }
        _windowSnapshot = (window.Width, window.Height, window.MinWidth, window.MinHeight, window.Position, window.Topmost, window.WindowState);
        _compactSize=(window.Width,window.Height,window.MinWidth,window.MinHeight,window.MaxWidth,window.MaxHeight);
        _compactWindow = window;
        window.WindowState = WindowState.Normal;
        ApplyVideoSize(window,VM?.VideoAspectRatio??16d/9,true);
        window.Topmost=true;
        if (VM is { } vm) { vm.CompactMode = true; vm.AlwaysOnTop = window.Topmost; }
        RevealControls();
    }

    internal void RestoreWindow()
    {
        if (_compactWindow is not { } window) return;
        _compactWindow = null;
        var saved = _windowSnapshot;
        window.MinWidth=0;window.MinHeight=0;window.MaxWidth=double.PositiveInfinity;window.MaxHeight=double.PositiveInfinity;
        window.Width=_compactSize.Width;window.Height=_compactSize.Height;
        window.MinWidth=_compactSize.MinWidth;window.MinHeight=_compactSize.MinHeight;window.MaxWidth=_compactSize.MaxWidth;window.MaxHeight=_compactSize.MaxHeight;
        try
        {
            var screens=window.Screens.All;
            var target=screens.FirstOrDefault(screen=>saved.Position.X>=screen.WorkingArea.X&&saved.Position.X<screen.WorkingArea.Right&&saved.Position.Y>=screen.WorkingArea.Y&&saved.Position.Y<screen.WorkingArea.Bottom)
                ??window.Screens.Primary;
            window.Position=target is null?saved.Position:ClampRestoredPosition(saved.Position,target.WorkingArea,saved.Width,saved.Height,target.Scaling);
        }
        catch(ObjectDisposedException) { /* native window already closed */ }
        window.Topmost = saved.Topmost;
        window.WindowState = saved.State;
        if (VM is { } vm) { vm.CompactMode = false; vm.AlwaysOnTop = window.Topmost; }
    }

    internal static Avalonia.PixelPoint ClampRestoredPosition(Avalonia.PixelPoint position,Avalonia.PixelRect area,double width,double height,double scaling)
    {
        var pixelWidth=(int)Math.Ceiling(width*scaling);var pixelHeight=(int)Math.Ceiling(height*scaling);
        var x=Math.Clamp(position.X,area.X,Math.Max(area.X,area.Right-pixelWidth));
        var y=Math.Clamp(position.Y,area.Y,Math.Max(area.Y,area.Bottom-pixelHeight));
        return new Avalonia.PixelPoint(x,y);
    }

    private static readonly double[] Rates = [0.5, 0.75, 1.0, 1.25, 1.5, 2.0];

    private void OnSelectRate(object? sender, RoutedEventArgs e)
    {
        if (VM is not { } vm || sender is not Button button) return;
        var menu = new ContextMenu();
        foreach (var rate in Rates)
        {
            var item = new MenuItem { Header = (Math.Abs(vm.Rate - rate) < .001 ? "当前 · " : "") + $"{rate:0.##}×" };
            item.Click += (_, _) => vm.Rate = rate;
            menu.Items.Add(item);
        }
        OpenControlMenu(button, menu);
    }

    private void OnTogglePip(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is Window window) ToggleCompactWindow(window);
    }

    private void OnTogglePlugin(object? sender, RoutedEventArgs e)
    {
        if (VM is { } vm) vm.FlashToast("插件（S3 接）");
    }

    private void OnTogglePlaylist(object? sender, RoutedEventArgs e)
    {
        if (VM is null) return;
        if (_drawerOpen) ClosePlaylistDrawer();
        else OpenPlaylistDrawer();
    }

    /// <summary>从窗口右侧滑入播放列表抽屉。</summary>
    internal void OpenPlaylistDrawer()
    {
        if (VM is not { } vm || this.FindControl<Border>("PlaylistDrawer") is not { } drawer) return;
        _drawerHideTimer.Stop();
        BuildPlaylistItems(vm);
        // 先置于窗口外，显示后再滑到位移 0，过渡动画才会真正播放。
        drawer.RenderTransform = new TranslateTransform(PlayerLayout.PlaylistDrawerWidth(Bounds.Width), 0);
        drawer.IsVisible = true;
        Dispatcher.UIThread.Post(() =>
        {
            if (_drawerOpen && drawer.IsVisible) drawer.RenderTransform = new TranslateTransform(0, 0);
        }, DispatcherPriority.Render);
        _drawerOpen = true;
        RevealControls();
    }

    /// <summary>滑出后隐藏抽屉；<paramref name="animate"/> 为假时立即隐藏（关闭播放器/卸载控件）。</summary>
    internal void ClosePlaylistDrawer(bool animate = true)
    {
        if (!_drawerOpen) return;
        _drawerOpen = false;
        if (this.FindControl<Border>("PlaylistDrawer") is not { } drawer) return;
        _drawerHideTimer.Stop();
        if (!animate) { drawer.IsVisible = false; drawer.RenderTransform = new TranslateTransform(0, 0); return; }
        drawer.RenderTransform = new TranslateTransform(PlayerLayout.PlaylistDrawerWidth(Bounds.Width), 0);
        _drawerHideTimer.Start();
    }

    private void BuildPlaylistItems(PlayerViewModel vm)
    {
        if (this.FindControl<StackPanel>("PlaylistItems") is not { } items) return;
        if (this.FindControl<TextBlock>("PlaylistCount") is { } count)
            count.Text = vm.Playlist.Count > 0 ? $"共 {vm.Playlist.Count} 集" : "";
        items.Children.Clear();
        if (vm.Playlist.Count == 0)
        {
            items.Children.Add(new TextBlock
            {
                Text = "当前媒体没有剧集列表", FontSize = 12, Margin = new Thickness(8, 10, 8, 10),
                Foreground = new SolidColorBrush(Color.Parse("#80FFFFFF")),
            });
            return;
        }
        var session = vm.CurrentSessionId;
        for (var i = 0; i < vm.Playlist.Count; i++)
        {
            var index = i;
            var current = i == vm.PlaylistIndex;
            var item = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Background = new SolidColorBrush(Color.Parse(current ? "#2EFFFFFF" : "#00000000")),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 6, 10, 6),
                Cursor = new Cursor(StandardCursorType.Hand),
                Content = new TextBlock
                {
                    Text = (current ? "正在播放 · " : "") + vm.Playlist[i].Title,
                    FontSize = 12,
                    FontWeight = current ? FontWeight.SemiBold : FontWeight.Normal,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Foreground = new SolidColorBrush(Color.Parse(current ? "#FFFFFFFF" : "#D0FFFFFF")),
                },
            };
            item.Click += async (_, _) =>
            {
                if (session != vm.CurrentSessionId) return;
                ClosePlaylistDrawer();
                await vm.PlayPlaylistIndexAsync(index);
            };
            items.Children.Add(item);
        }
    }

    private bool IsInsidePlaylistDrawer(Avalonia.Visual? source)
    {
        if (source is null || this.FindControl<Border>("PlaylistDrawer") is not { } drawer) return false;
        for (Avalonia.Visual? node = source; node is not null; node = node.GetVisualParent())
            if (ReferenceEquals(node, drawer)) return true;
        return false;
    }

    private void OnToggleSettings(object? sender, RoutedEventArgs e)
    {
        if (VM is not { } vm || sender is not Button button) return;
        var menu = new ContextMenu();
        var autoNext = new MenuItem { Header = vm.AutoNext ? "自动下一集：开" : "自动下一集：关" };
        autoNext.Click += (_, _) => vm.AutoNext = !vm.AutoNext;
        menu.Items.Add(autoNext);
        var incognito = new MenuItem { Header = vm.Incognito ? "不记录播放历史：开" : "不记录播放历史：关" };
        incognito.Click += (_, _) => vm.Incognito = !vm.Incognito;
        menu.Items.Add(incognito);
        var danmaku = new MenuItem { Header = vm.DanmakuEnabled ? "弹幕：开" : "弹幕：关" };
        danmaku.Click += (_, _) => vm.DanmakuEnabled = !vm.DanmakuEnabled;
        menu.Items.Add(danmaku);
        if (vm.AvailableParsers.Count > 0)
        {
            var parsers = new MenuItem { Header = "优先解析线路（下次播放生效）" };
            var options = new List<MenuItem>();
            var automatic = new MenuItem { Header = vm.PreferredParser.Length == 0 ? "当前 · 自动" : "自动" };
            automatic.Click += (_, _) => vm.PreferredParser = "";
            options.Add(automatic);
            foreach (var parse in vm.AvailableParsers)
            {
                var option = new MenuItem { Header = (vm.PreferredParser == parse.Url ? "当前 · " : "") + parse.Name };
                option.Click += (_, _) => vm.PreferredParser = parse.Url;
                options.Add(option);
            }
            parsers.ItemsSource = options;
            menu.Items.Add(parsers);
        }
        foreach (var opening in new[] { true, false })
        {
            var group = new MenuItem { Header = opening ? "跳过片头" : "跳过片尾" };
            var options = new List<MenuItem>();
            foreach (var seconds in new[] { 0, 30, 60, 90, 120 })
            {
                var current = opening ? vm.OpeningSkipSeconds : vm.EndingSkipSeconds;
                var option = new MenuItem { Header = (current == seconds ? "当前 · " : "") + (seconds == 0 ? "关闭" : $"{seconds} 秒") };
                option.Click += (_, _) =>
                {
                    if (opening) vm.OpeningSkipSeconds = seconds;
                    else vm.EndingSkipSeconds = seconds;
                };
                options.Add(option);
            }
            group.ItemsSource = options;
            menu.Items.Add(group);
        }
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
                var load = new MenuItem { Header = "加载本地字幕…" };
                load.Click += async (_, _) =>
                {
                    var top = TopLevel.GetTopLevel(this);
                    if (top?.StorageProvider.CanOpen != true) { vm.FlashToast("当前环境不能选择本地文件"); return; }
                    try
                    {
                        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                        {
                            Title = "选择字幕文件", AllowMultiple = false,
                            FileTypeFilter = [new FilePickerFileType("字幕") { Patterns = ["*.srt", "*.ass", "*.ssa", "*.vtt", "*.sub"] }],
                        });
                        if (files.Count == 0 || sessionId != vm.CurrentSessionId) return;
                        var path = files[0].TryGetLocalPath();
                        if (path is null) { vm.FlashToast("请选择本地字幕文件"); return; }
                        await vm.LoadSubtitleAsync(path, sessionId);
                    }
                    catch (Exception error)
                    {
                        if (sessionId == vm.CurrentSessionId) vm.FlashToast($"选择字幕失败：{error.Message}");
                    }
                };
                items.Add(load);
                var earlier = new MenuItem { Header = "字幕提前 0.5 秒" };
                earlier.Click += (_, _) => {if(sessionId==vm.CurrentSessionId)vm.SubtitleDelay -= .5;};
                var later = new MenuItem { Header = "字幕延后 0.5 秒" };
                later.Click += (_, _) => {if(sessionId==vm.CurrentSessionId)vm.SubtitleDelay += .5;};
                var reset = new MenuItem { Header = $"恢复同步（当前 {vm.SubtitleDelay:+0.0;-0.0;0.0}s）" };
                reset.Click += (_, _) => {if(sessionId==vm.CurrentSessionId)vm.SubtitleDelay = 0;};
                items.Add(earlier); items.Add(later); items.Add(reset);
                foreach (var size in new[] { 28, 40, 52 })
                {
                    var style = new MenuItem { Header = (vm.SubtitleFontSize == size ? "当前 · " : "") + $"字幕字号 {size}" };
                    style.Click += (_, _) => {if(sessionId==vm.CurrentSessionId)vm.SubtitleFontSize = size;};
                    items.Add(style);
                }
            }
            if (items.Count == 0) items.Add(new MenuItem { Header = "暂无可用轨道", IsEnabled = false });
            group.ItemsSource = items;
            menu.Items.Add(group);
        }
        OpenControlMenu(button, menu);
    }

    private void OnToggleAspectRatio(object? sender, RoutedEventArgs e)
    {
        if (VM is not { } vm || sender is not Button button) return;
        var menu = new ContextMenu();
        var session = vm.CurrentSessionId;
        foreach (var (label, ratio) in new (string, double?)[] { ("原始比例", null), ("16:9", 16d / 9), ("4:3", 4d / 3), ("1:1", 1d) })
        {
            var item = new MenuItem { Header = (vm.AspectRatio == ratio ? "当前 · " : "") + label };
            item.Click += async (_, _) => { if (session == vm.CurrentSessionId) await vm.SetAspectRatioAsync(ratio); };
            menu.Items.Add(item);
        }
        OpenControlMenu(button, menu);
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

public static class PlayerLayoutConverters
{
    /// <summary>播放列表抽屉宽度：按窗口宽度收窄，上下限见 PlayerLayout。</summary>
    public static readonly IValueConverter PlaylistDrawerWidth = new DrawerWidthConverter();

    private sealed class DrawerWidthConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => PlayerLayout.PlaylistDrawerWidth(value is double width ? width : 0);

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>面板宽度 = max(视频宽 60%, 该模式下的内容需求)，且不超出视频宽度。</summary>
    public static readonly IMultiValueConverter ControlWidth = new ControlWidthConverter();

    private sealed class ControlWidthConverter : IMultiValueConverter
    {
        public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
        {
            if (values.Count < 2 || values[0] is not double width || !double.IsFinite(width) || width <= 0) return 0d;
            var compact = values[1] as bool? ?? false;
            return Math.Min(width, Math.Max(width * .6, PlayerLayout.MinimumPanelWidth(compact)));
        }

        public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
