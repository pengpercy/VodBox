using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using VodBox.Desktop.Services;
using VodBox.Desktop.ViewModels;

namespace VodBox.Desktop.Views;

public partial class UpdateWindow : Window
{
    private readonly UpdateViewModel _viewModel;
    private bool _closing;
    private bool _closeComplete;

    // Parameterless constructor keeps the compiled XAML reachable for previews/runtime loading.
    public UpdateWindow() : this(new UpdateViewModel(new UpdateOffer("未知", "", new Uri("https://github.com/pengpercy/VodBox/releases"), "", "https://github.com/pengpercy/VodBox/releases"), "未知", _ => { }))
    {
    }

    public UpdateWindow(UpdateViewModel viewModel)
    {
        _viewModel = viewModel;
        AvaloniaXamlLoader.Load(this);
        DataContext = _viewModel;
        Closing += OnClosing;
    }

    private void OnCancelDownload(object? sender, RoutedEventArgs e) => _viewModel.CancelDownload();
    private void OnOpenDownloadPage(object? sender, RoutedEventArgs e) => _viewModel.OpenDownloadPage();

    /// <summary>后台下载、退出时安装：窗口随即关闭，会话由 <see cref="UpdateWindowService"/> 保留。</summary>
    private void OnInstallOnQuit(object? sender, RoutedEventArgs e)
    {
        _viewModel.ScheduleInstallOnQuit();
        if (_viewModel.InstallOnQuit) Close();
    }

    private async void OnInstallNow(object? sender, RoutedEventArgs e)
    {
        if (await _viewModel.InstallNowAsync()) App.ShutdownForUpdate();
        else if (_viewModel.InstallOnQuit) Close(); // 下载中改选了「退出时安装」
    }

    private async void OnSkip(object? sender, RoutedEventArgs e)
    {
        await _viewModel.SkipAsync();
        Close();
    }

    private void OnReleasePage(object? sender, RoutedEventArgs e)
    {
        try
        {
            _ = Process.Start(new ProcessStartInfo { FileName = _viewModel.ReleaseUrl, UseShellExecute = true }) ?? throw new InvalidOperationException("无法打开浏览器。");
        }
        catch (Exception error) { _viewModel.StatusText = $"打开发行页面失败：{error.Message}"; }
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeComplete) return;
        if (!_viewModel.CanClose) { e.Cancel = true; return; }
        // 退出时安装的会话继续后台下载；已结束的会话无需清理。
        if (_viewModel.InstallOnQuit || _viewModel.IsFinished) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true;
        await _viewModel.WindowClosedAsync();
        _closeComplete = true;
        Close();
    }

    /// <summary>应用退出：直接收起，会话的安装或清理由 <see cref="UpdateWindowService.CloseAsync"/> 负责。</summary>
    internal void CloseForShutdown()
    {
        _closeComplete = true;
        Close();
    }
}
