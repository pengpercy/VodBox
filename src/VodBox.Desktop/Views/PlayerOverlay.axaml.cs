using System;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using VodBox.Desktop.Services;
using VodBox.Desktop.ViewModels;
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

    public PlayerOverlay()
    {
        InitializeComponent();
        AttachedToVisualTree += OnAttached;
        DetachedFromVisualTree += OnDetached;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>当前 Player 子 VM；DataContext 未装载（XAML 初始化早期/设计时）时为 null，调用方需判空。</summary>
    private PlayerViewModel? VM => DataContext is MainViewModel main ? main.Player : null;

    private void OnAttached(object? sender, Avalonia.VisualTreeAttachmentEventArgs e) => TryInstallSurface();

    private void OnDetached(object? sender, Avalonia.VisualTreeAttachmentEventArgs e)
    {
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
