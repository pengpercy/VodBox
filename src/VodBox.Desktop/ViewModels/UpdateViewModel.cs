using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using VodBox.Core;
using VodBox.Desktop.Services;

namespace VodBox.Desktop.ViewModels;

public enum UpdateState { Available, Downloading, Ready, Installing, Installed }

/// <summary>更新偏好：自动下载并在退出时安装、跳过的版本。</summary>
public interface IUpdatePreferences
{
    bool AutoInstallUpdates { get; set; }
    string SkippedUpdateVersion { get; set; }
}

/// <summary>未接设置时（预览/测试）只在内存里记。</summary>
public sealed class MemoryUpdatePreferences : IUpdatePreferences
{
    public bool AutoInstallUpdates { get; set; }
    public string SkippedUpdateVersion { get; set; } = "";
}

/// <summary>
/// 一次更新会话：可挂在更新窗口上，也可（退出时安装 / 自动更新）在窗口关闭后继续后台下载。
/// 「安装并重启应用」= 下载 + 校验后立即安装重启；「退出时安装」= 后台下载，VodBox 退出时替换、不重启。
/// 未选择退出时安装时关闭窗口会取消下载并清理临时包。
/// </summary>
public sealed partial class UpdateViewModel : ObservableObject
{
    private readonly UpdateOffer _offer;
    private readonly Action<string> _report;
    private readonly IUpdatePreferences _preferences;
    private readonly string _processPath = Environment.ProcessPath ?? "";
    private CancellationTokenSource? _cancellation;
    private Task? _operation;
    private Task? _closeOperation;
    private string? _cache;
    private string? _archive;
    private bool _closed;
    private bool _restartAfterDownload;

    public string CurrentVersion { get; }
    public string NewVersion => _offer.Version;
    public string ReleaseUrl => _offer.ReleaseUrl;
    public string ReleaseNotes => string.IsNullOrWhiteSpace(_offer.ReleaseNotes) ? "此版本未提供发行说明，可打开 GitHub 发行页面查看。" : _offer.ReleaseNotes;
    public bool UsesExternalDownload => OperatingSystem.IsLinux() || !AppUpdater.IsPackaged(_processPath);
    public bool SupportsInPlaceUpdate => !UsesExternalDownload;
    public string PolicyText => OperatingSystem.IsLinux()
        ? "Linux 下载由浏览器处理，请通过系统包管理器安装。"
        : UsesExternalDownload ? "当前是开发模式：仅打开正式发行包下载页面，不会原地更新。"
        : "更新包下载后会校验 SHA256。「安装并重启应用」会关闭 VodBox 并由独立更新器替换后重启；「退出时安装」在你退出 VodBox 时替换。";

    /// <summary>安装是否推迟到退出时（窗口关闭后会话继续存在）。</summary>
    [ObservableProperty] private bool _installOnQuit;
    [ObservableProperty] private UpdateState _state;
    [ObservableProperty] private string _statusText = "发现新版本，可查看发行说明后选择安装方式。";
    [ObservableProperty] private double _downloadProgress;
    [ObservableProperty] private bool _progressIndeterminate;

    public bool IsDownloading => State == UpdateState.Downloading;
    public bool IsReady => State == UpdateState.Ready;
    public bool CanClose => State is not UpdateState.Installing;
    public bool HasProgress => State is UpdateState.Downloading or UpdateState.Installing || State == UpdateState.Ready && !InstallOnQuit;
    public bool CanSkip => !_closed && State is UpdateState.Available or UpdateState.Downloading or UpdateState.Ready;
    public bool CanInstallOnQuit => !_closed && SupportsInPlaceUpdate && !InstallOnQuit && State is UpdateState.Available or UpdateState.Downloading or UpdateState.Ready;
    public bool CanInstallNow => !_closed && SupportsInPlaceUpdate && (State is UpdateState.Available or UpdateState.Ready || State == UpdateState.Downloading && InstallOnQuit);
    public bool CanOpenDownloadPage => !_closed && UsesExternalDownload && State == UpdateState.Available;
    /// <summary>会话已结束（关闭、跳过或安装器已启动），服务应丢弃它。</summary>
    public bool IsFinished => _closed || State == UpdateState.Installed;

    public bool AutoInstall
    {
        get => _preferences.AutoInstallUpdates;
        set { if (_preferences.AutoInstallUpdates == value) return; _preferences.AutoInstallUpdates = value; OnPropertyChanged(); }
    }

    public UpdateViewModel(UpdateOffer offer, string currentVersion, Action<string> report, IUpdatePreferences? preferences = null)
    {
        _offer = offer;
        CurrentVersion = currentVersion;
        _report = report;
        _preferences = preferences ?? new MemoryUpdatePreferences();
    }

    partial void OnStateChanged(UpdateState value) => RaiseCommandState();
    partial void OnInstallOnQuitChanged(bool value) => RaiseCommandState();

    private void RaiseCommandState()
    {
        foreach (var name in new[] { nameof(IsDownloading), nameof(IsReady), nameof(CanClose), nameof(HasProgress), nameof(CanSkip),
                     nameof(CanInstallOnQuit), nameof(CanInstallNow), nameof(CanOpenDownloadPage), nameof(IsFinished) })
            OnPropertyChanged(name);
    }

    private void Report(string message) { StatusText = message; _report(message); }

    /// <summary>Linux / 开发模式：交给浏览器下载。</summary>
    public void OpenDownloadPage()
    {
        if (!CanOpenDownloadPage) return;
        try
        {
            var url = OperatingSystem.IsLinux() ? _offer.Url.AbsoluteUri : _offer.ReleaseUrl;
            _ = Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }) ?? throw new InvalidOperationException("无法打开浏览器。");
            Report(OperatingSystem.IsLinux() ? "已打开下载页面，请通过系统包管理器安装。" : "已打开正式发行页面；开发模式不会原地更新。");
        }
        catch (Exception error) { Report($"打开下载页面失败：{error.Message}"); }
    }

    /// <summary>退出时安装：后台下载并校验，VodBox 退出时由独立更新器替换（不重启）。窗口可以随即关闭。</summary>
    public void ScheduleInstallOnQuit()
    {
        if (!CanInstallOnQuit) return;
        InstallOnQuit = true;
        _restartAfterDownload = false;
        if (State == UpdateState.Ready) Report($"VodBox {NewVersion} 已下载并校验，将在退出 VodBox 时安装。");
        else if (State == UpdateState.Available) _operation = DownloadCoreAsync();
    }

    /// <summary>安装并重启应用：未下载则先下载校验，完成后立即准备安装；返回 true 表示更新器已启动、应关闭 VodBox。</summary>
    public async Task<bool> InstallNowAsync()
    {
        if (!CanInstallNow) return false;
        InstallOnQuit = false;
        _restartAfterDownload = true;
        if (State == UpdateState.Available) _operation = DownloadCoreAsync();
        if (State == UpdateState.Downloading && _operation is { } download) await download;
        if (_closed || State != UpdateState.Ready || !_restartAfterDownload) return false;
        var installation = InstallCoreAsync(relaunch: true);
        _operation = installation;
        return await installation;
    }

    /// <summary>跳过这个版本：自动检查不再提示它（手动检查仍会显示），并结束会话。</summary>
    public async Task SkipAsync()
    {
        if (!CanSkip) return;
        _preferences.SkippedUpdateVersion = NewVersion;
        await CloseAsync();
        Report($"已跳过版本 {NewVersion}；手动检查更新时仍可安装。");
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
                    : value.TotalBytes is > 0 ? $"正在下载 {NewVersion}：{received:F1} / {value.TotalBytes.Value / (1024d * 1024):F1} MiB（{DownloadProgress:F0}%）"
                    : $"正在下载 {NewVersion}：已收到 {received:F1} MiB（服务器未提供总大小）");
            });
            _archive = await AppUpdater.DownloadAsync(_offer, Path.Combine(_cache, "payload"), cancellation.Token, progress);
            cancellation.Token.ThrowIfCancellationRequested();
            DownloadProgress = 100;
            ProgressIndeterminate = false;
            State = UpdateState.Ready;
            Report(InstallOnQuit ? $"VodBox {NewVersion} 已下载并校验，将在退出 VodBox 时安装。"
                : "下载完成且 SHA256 校验通过，正在准备安装…");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            State = UpdateState.Available;
            InstallOnQuit = false;
            Report("下载已取消；临时包已清理。");
            Cleanup();
        }
        catch (Exception error)
        {
            State = UpdateState.Available;
            InstallOnQuit = false;
            Report($"下载失败：{error.Message}；可重试。");
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

    private async Task<bool> InstallCoreAsync(bool relaunch)
    {
        State = UpdateState.Installing;
        ProgressIndeterminate = true;
        Report(relaunch ? "正在安全解压、验证并准备独立更新器，请勿关闭窗口…" : $"正在准备在退出后安装 VodBox {NewVersion}…");
        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        try
        {
            if (!await AppUpdater.MatchesAsync(_archive!, _offer.Sha256, cancellation.Token))
                throw new InvalidDataException("更新包 SHA256 校验失败，请重新下载。");
            // Extraction and signature verification must not freeze the UI.
            await Task.Run(() => AppUpdater.PrepareAndLaunchAsync(_archive!, _processPath, _cache!, cancellation.Token, relaunch), cancellation.Token);
            State = UpdateState.Installed;
            Report(relaunch ? "独立更新器已启动；正在关闭 VodBox，随后替换并重启。" : $"独立更新器已启动；VodBox 退出后将安装 {NewVersion}。");
            return true;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { Report("安装准备已取消，未启动更新器。"); }
        catch (Exception error) { Report($"安装准备失败：{error.Message}；尚未安装。"); }
        finally { _cancellation = null; ProgressIndeterminate = false; }
        State = UpdateState.Ready;
        return false;
    }

    /// <summary>
    /// 应用退出：已选择退出时安装且包已就绪 → 启动不重启的更新器；其余情况取消并清理。
    /// </summary>
    public async Task ShutdownAsync()
    {
        if (_closed || State == UpdateState.Installed) return;
        if (InstallOnQuit && State == UpdateState.Ready && !_closed)
        {
            _operation = InstallCoreAsync(relaunch: false);
            await _operation;
            if (State == UpdateState.Installed) return;
        }
        await CloseAsync();
    }

    /// <summary>更新窗口被关闭：选择了退出时安装则会话继续，否则取消并清理。</summary>
    public Task WindowClosedAsync() => InstallOnQuit || State == UpdateState.Installed ? Task.CompletedTask : CloseAsync();

    public Task CloseAsync() => _closeOperation ??= CloseCoreAsync();

    private async Task CloseCoreAsync()
    {
        var wasDownloading = IsDownloading;
        _closed = true;
        RaiseCommandState();
        _cancellation?.Cancel();
        if (_operation is { } operation) await operation;
        if (State != UpdateState.Installed)
        {
            Report(wasDownloading ? "已取消下载；临时包已清理，未安装更新。"
                : State == UpdateState.Ready ? "已取消安装；下载包已清理，未安装更新。" : "已关闭更新窗口；未安装更新。");
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
