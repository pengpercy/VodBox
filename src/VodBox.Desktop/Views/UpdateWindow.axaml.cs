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
    private readonly UpdateOffer _offer;
    private bool _closing;
    private bool _closeComplete;

    // Parameterless constructor keeps the compiled XAML reachable for previews/runtime loading.
    public UpdateWindow() : this(new UpdateOffer("未知", "", new Uri("https://github.com/pengpercy/VodBox/releases"), "", "https://github.com/pengpercy/VodBox/releases"), "未知", _ => { })
    {
    }

    public UpdateWindow(UpdateOffer offer, string currentVersion, Action<string> report)
    {
        _offer = offer;
        _viewModel = new UpdateViewModel(offer, currentVersion, report);
        AvaloniaXamlLoader.Load(this);
        DataContext = _viewModel;
        Closing += OnClosing;
        Closed += (_, _) => _ = _viewModel.CloseAsync();
    }

    private async void OnDownload(object? sender, RoutedEventArgs e) => await _viewModel.DownloadAsync();
    private void OnCancelDownload(object? sender, RoutedEventArgs e) => _viewModel.CancelDownload();
    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private async void OnInstall(object? sender, RoutedEventArgs e)
    {
        var mainWindow = (Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (mainWindow is null) return;
        if (await _viewModel.InstallAsync())
            mainWindow.Close(); // MainWindow owns graceful player and service shutdown.
    }

    private void OnReleasePage(object? sender, RoutedEventArgs e)
    {
        try
        {
            _ = Process.Start(new ProcessStartInfo { FileName = _offer.ReleaseUrl, UseShellExecute = true }) ?? throw new InvalidOperationException("无法打开浏览器。");
        }
        catch (Exception error) { _viewModel.StatusText = $"打开发行页面失败：{error.Message}"; }
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeComplete) return;
        e.Cancel = true;
        if (_closing || !_viewModel.CanClose) return;
        await CloseForShutdownAsync();
    }

    internal async Task CloseForShutdownAsync()
    {
        if (_closeComplete) return;
        _closing = true;
        await _viewModel.CloseAsync();
        _closeComplete = true;
        Close();
    }
}
