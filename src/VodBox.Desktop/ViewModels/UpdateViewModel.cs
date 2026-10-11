using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using VodBox.Core;
using VodBox.Desktop.Services;

namespace VodBox.Desktop.ViewModels;

public enum UpdateState { Available, Downloading, Ready, Installing }

/// <summary>Download and installation require separate consent; closing never applies an update.</summary>
public sealed partial class UpdateViewModel : ObservableObject
{
    private readonly UpdateOffer _offer;
    private readonly Action<string> _report;
    private readonly string _processPath = Environment.ProcessPath ?? "";
    private CancellationTokenSource? _cancellation;
    private Task? _operation;
    private Task? _closeOperation;
    private string? _cache;
    private string? _archive;
    private bool _closed;
    private bool _updaterLaunched;

    public string CurrentVersion { get; }
    public string NewVersion => _offer.Version;
    public string ReleaseNotes => string.IsNullOrWhiteSpace(_offer.ReleaseNotes) ? "此版本未提供发行说明，可打开 GitHub 发行页面查看。" : _offer.ReleaseNotes;
    public bool UsesExternalDownload => OperatingSystem.IsLinux() || !AppUpdater.IsPackaged(_processPath);
    public string DownloadLabel => UsesExternalDownload ? "打开下载页面" : "确认下载";
    public string PolicyText => OperatingSystem.IsLinux()
        ? "Linux 下载由浏览器处理，请通过系统包管理器安装；应用不会显示或模拟浏览器下载进度。"
        : UsesExternalDownload ? "当前是开发模式：仅打开正式发行包下载页面，不会原地更新。"
        : "确认下载后校验 SHA256；下载完成后仍需确认安装。安装将关闭 VodBox，由独立更新器替换并重启。";
    public string CloseHint => "下载中关闭窗口会取消下载并清理临时包；下载完成后关闭也会清理，下次需重新下载。";
    public bool CanDownload => !_closed && State == UpdateState.Available;
    public bool IsDownloading => State == UpdateState.Downloading;
    public bool IsReady => State == UpdateState.Ready;
    public bool CanClose => State != UpdateState.Installing;
    public bool HasProgress => State != UpdateState.Available;

    [ObservableProperty] private UpdateState _state;
    [ObservableProperty] private string _statusText = "发现新版本，请查看发行说明后选择是否下载。";
    [ObservableProperty] private double _downloadProgress;
    [ObservableProperty] private bool _progressIndeterminate;

    public UpdateViewModel(UpdateOffer offer, string currentVersion, Action<string> report)
    {
        _offer = offer;
        CurrentVersion = currentVersion;
        _report = report;
    }

    partial void OnStateChanged(UpdateState value)
    {
        foreach (var name in new[] { nameof(CanDownload), nameof(IsDownloading), nameof(IsReady), nameof(CanClose), nameof(HasProgress) })
            OnPropertyChanged(name);
    }

    private void Report(string message) { StatusText = message; _report(message); }

    public async Task DownloadAsync()
    {
        if (!CanDownload) return;
        if (UsesExternalDownload)
        {
            try
            {
                var url = OperatingSystem.IsLinux() ? _offer.Url.AbsoluteUri : _offer.ReleaseUrl;
                _ = Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }) ?? throw new InvalidOperationException("无法打开浏览器。");
                Report(OperatingSystem.IsLinux() ? "已打开下载页面，请通过系统包管理器安装。" : "已打开正式发行页面；开发模式不会原地更新。");
            }
            catch (Exception error) { Report($"打开下载页面失败：{error.Message}"); }
            return;
        }
        _operation = DownloadCoreAsync();
        await _operation;
    }

    private async Task DownloadCoreAsync()
    {
        State = UpdateState.Downloading;
        DownloadProgress = 0;
        ProgressIndeterminate = true;
        Report("正在连接下载服务器…");
        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        _cache ??= Path.Combine(Path.GetTempPath(), "vodbox-update-" + Guid.NewGuid().ToString("N"));
        try
        {
            var progress = new Progress<UpdateDownloadProgress>(value =>
            {
                if (_closed || !ReferenceEquals(_cancellation, cancellation) || cancellation.IsCancellationRequested || State != UpdateState.Downloading) return;
                ProgressIndeterminate = value.IsVerifying || value.TotalBytes is not > 0;
                DownloadProgress = value.TotalBytes is > 0 ? Math.Clamp(value.BytesReceived * 100d / value.TotalBytes.Value, 0, 100) : 0;
                var received = value.BytesReceived / (1024d * 1024);
                Report(value.IsVerifying ? "下载完成，正在校验 SHA256…"
                    : value.TotalBytes is > 0 ? $"正在下载：{received:F1} / {value.TotalBytes.Value / (1024d * 1024):F1} MiB（{DownloadProgress:F0}%）"
                    : $"正在下载：已收到 {received:F1} MiB（服务器未提供总大小）");
            });
            _archive = await AppUpdater.DownloadAsync(_offer, Path.Combine(_cache, "payload"), cancellation.Token, progress);
            cancellation.Token.ThrowIfCancellationRequested();
            DownloadProgress = 100;
            ProgressIndeterminate = false;
            State = UpdateState.Ready;
            Report("下载完成且 SHA256 校验通过。确认安装将关闭 VodBox 并重启；尚未安装。");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            State = UpdateState.Available;
            Report("下载已取消；临时包将清理，可重新下载。");
            Cleanup();
        }
        catch (Exception error)
        {
            State = UpdateState.Available;
            Report($"下载失败：{error.Message}；可重试下载。");
            Cleanup();
        }
        finally { _cancellation = null; }
    }

    public void CancelDownload()
    {
        if (!IsDownloading) return;
        Report("正在取消下载并清理临时包…");
        _cancellation?.Cancel();
    }

    public async Task<bool> InstallAsync()
    {
        if (_closed || !IsReady || _archive is null || _cache is null) return false;
        var installation = InstallCoreAsync();
        _operation = installation;
        return await installation;
    }

    private async Task<bool> InstallCoreAsync()
    {
        State = UpdateState.Installing;
        ProgressIndeterminate = true;
        Report("正在安全解压、验证并准备独立更新器，请勿关闭窗口…");
        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        try
        {
            if (!await AppUpdater.MatchesAsync(_archive!, _offer.Sha256, cancellation.Token))
                throw new InvalidDataException("更新包 SHA256 校验失败，请关闭窗口后重新下载。");
            // Extraction and signature verification must not freeze the UI.
            await Task.Run(() => AppUpdater.PrepareAndLaunchAsync(_archive!, _processPath, _cache!, cancellation.Token), cancellation.Token);
            _updaterLaunched = true;
            Report("独立更新器已启动；正在关闭 VodBox，随后替换并重启。");
            return true;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { Report("安装准备已取消，未启动更新器。"); }
        catch (Exception error) { Report($"安装准备失败：{error.Message}；尚未安装，可重试或关闭窗口。"); }
        finally { _cancellation = null; ProgressIndeterminate = false; }
        State = UpdateState.Ready;
        return false;
    }

    public Task CloseAsync() => _closeOperation ??= CloseCoreAsync();

    private async Task CloseCoreAsync()
    {
        var wasDownloading = IsDownloading;
        _closed = true;
        _cancellation?.Cancel();
        if (_operation is { } operation) await operation;
        if (!_updaterLaunched)
        {
            Report(wasDownloading ? "已关闭更新窗口并取消下载；临时包将清理，未安装更新。"
                : State == UpdateState.Ready ? "已取消安装；下载包将清理，未安装更新。" : "已关闭更新窗口；未安装更新。");
            Cleanup();
        }
    }

    private void Cleanup()
    {
        _archive = null;
        try
        {
            if (_cache is { } cache && Directory.Exists(cache)) Directory.Delete(cache, true);
            _cache = null;
        }
        catch (Exception error)
        {
            VodBoxLog.Error("update", "清理临时更新包失败", error);
            Report($"{StatusText} 临时包清理失败：{error.Message}");
        }
    }
}
