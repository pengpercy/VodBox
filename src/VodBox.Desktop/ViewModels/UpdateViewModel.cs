using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using VodBox.Core;
using VodBox.Desktop.Services;

namespace VodBox.Desktop.ViewModels;

/// <summary>发现新版本 → 下载 → 解压验证 → 可以安装 → 更新器已启动。</summary>
public enum UpdateState { Available, Downloading, Extracting, Ready, Installed }

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
/// 一次更新会话：可挂在更新窗口上，也可（自动更新 / 就绪后关闭窗口）脱离窗口继续存在。
/// 「安装更新」= 下载 + 校验 + 解压验证，就绪后由「安装并重启应用」启动独立更新器；
/// 就绪后关闭窗口或自动更新则在 VodBox 退出时安装、不重启。就绪前关闭窗口会取消并清理临时文件。
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
    private StagedUpdate? _staged;
    private bool _closed;

    public string CurrentVersion { get; }
    public string NewVersion => _offer.Version;
    public string ReleaseUrl => _offer.ReleaseUrl;
    public string ReleaseNotes => string.IsNullOrWhiteSpace(_offer.ReleaseNotes) ? "此版本未提供发行说明，可打开 GitHub 发行页面查看。" : _offer.ReleaseNotes;
    public string OfferText => $"VodBox {NewVersion} 可供下载，您现在的版本是 {CurrentVersion}。" +
        (UsesExternalDownload ? OperatingSystem.IsLinux() ? "请下载安装包后通过系统包管理器安装。" : "开发模式下不能原地更新，可打开发行页面下载。" : "要现在下载吗？");
    public bool UsesExternalDownload => OperatingSystem.IsLinux() || !AppUpdater.IsPackaged(_processPath);
    public bool SupportsInPlaceUpdate => !UsesExternalDownload;

    /// <summary>安装推迟到 VodBox 退出时（窗口关闭后会话继续存在）。</summary>
    [ObservableProperty] private bool _installOnQuit;
    [ObservableProperty] private UpdateState _state;
    [ObservableProperty] private string _statusText = "";
    /// <summary>进度窗口的标题行：正在下载更新… / 正在解压更新… / 可以开始安装了。</summary>
    [ObservableProperty] private string _phaseText = "";
    /// <summary>进度条下方的说明，例如「8.0 MB / 32.3 MB」。</summary>
    [ObservableProperty] private string _detailText = "";
    /// <summary>上一次下载或解压失败的原因，回到发现新版本页面时显示。</summary>
    [ObservableProperty] private string _errorText = "";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _progressIndeterminate;

    public string WindowTitle => IsOffer ? "软件更新" : "正在更新 VodBox";
    public bool IsOffer => State == UpdateState.Available;
    public bool IsInProgress => State is not UpdateState.Available;
    public bool IsReady => State == UpdateState.Ready;
    public bool HasError => ErrorText.Length > 0;
    public bool HasDetail => DetailText.Length > 0;
    /// <summary>解压与启动更新器期间不能中断。</summary>
    public bool CanClose => State is not UpdateState.Extracting;
    public bool CanCancel => !_closed && State is UpdateState.Downloading;
    public bool ShowCancel => State is UpdateState.Downloading or UpdateState.Extracting;
    public bool CanSkip => !_closed && State == UpdateState.Available;
    public bool CanInstall => !_closed && SupportsInPlaceUpdate && State == UpdateState.Available;
    public bool CanInstallNow => !_closed && State == UpdateState.Ready;
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
    partial void OnErrorTextChanged(string value) => OnPropertyChanged(nameof(HasError));
    partial void OnDetailTextChanged(string value) => OnPropertyChanged(nameof(HasDetail));

    private void RaiseCommandState()
    {
        foreach (var name in new[] { nameof(WindowTitle), nameof(IsOffer), nameof(IsInProgress), nameof(IsReady), nameof(CanClose), nameof(CanCancel), nameof(ShowCancel),
                     nameof(CanSkip), nameof(CanInstall), nameof(CanInstallNow), nameof(CanOpenDownloadPage), nameof(IsFinished) })
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
        catch (Exception error) { ErrorText = $"打开下载页面失败：{error.Message}"; Report(ErrorText); }
    }

    /// <summary>安装更新：下载、校验并解压验证，完成后停在「可以开始安装了」。</summary>
    public Task InstallAsync()
    {
        if (!CanInstall) return Task.CompletedTask;
        return _operation = PrepareCoreAsync();
    }

    /// <summary>自动更新：后台完成下载与解压，VodBox 退出时安装（不重启）。</summary>
    public void ScheduleInstallOnQuit()
    {
        if (!CanInstall) return;
        InstallOnQuit = true;
        _operation = PrepareCoreAsync();
    }

    /// <summary>安装并重启应用：启动独立更新器；返回 true 表示应关闭 VodBox。</summary>
    public async Task<bool> InstallNowAsync()
    {
        if (!CanInstallNow) return false;
        InstallOnQuit = false;
        var launch = LaunchCoreAsync(relaunch: true);
        _operation = launch;
        return await launch;
    }

    /// <summary>跳过这个版本：自动检查不再提示它（手动检查仍会显示），并结束会话。</summary>
    public async Task SkipAsync()
    {
        if (!CanSkip) return;
        _preferences.SkippedUpdateVersion = NewVersion;
        await CloseAsync();
        Report($"已跳过版本 {NewVersion}；手动检查更新时仍可安装。");
    }

    private static string Megabytes(long bytes) => $"{bytes / (1024d * 1024):F1} MB";

    private async Task PrepareCoreAsync()
    {
        ErrorText = "";
        State = UpdateState.Downloading;
        PhaseText = "正在下载更新…";
        DetailText = "";
        Progress = 0;
        ProgressIndeterminate = true;
        Report($"正在下载 {NewVersion}…");
        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        _cache ??= Path.Combine(Path.GetTempPath(), "vodbox-update-" + Guid.NewGuid().ToString("N"));
        try
        {
            var download = new Progress<UpdateDownloadProgress>(value =>
            {
                if (_closed || !ReferenceEquals(_cancellation, cancellation) || cancellation.IsCancellationRequested || State != UpdateState.Downloading) return;
                ProgressIndeterminate = value.IsVerifying || value.TotalBytes is not > 0;
                Progress = value.TotalBytes is > 0 ? Math.Clamp(value.BytesReceived * 100d / value.TotalBytes.Value, 0, 100) : 0;
                PhaseText = value.IsVerifying ? "正在校验更新…" : "正在下载更新…";
                DetailText = value.IsVerifying ? "SHA256 校验中"
                    : value.TotalBytes is > 0 ? $"{Megabytes(value.BytesReceived)} / {Megabytes(value.TotalBytes.Value)}"
                    : $"已下载 {Megabytes(value.BytesReceived)}";
            });
            var archive = await AppUpdater.DownloadAsync(_offer, Path.Combine(_cache, "payload"), cancellation.Token, download);
            cancellation.Token.ThrowIfCancellationRequested();

            State = UpdateState.Extracting;
            PhaseText = "正在解压更新…";
            DetailText = "";
            Progress = 0;
            ProgressIndeterminate = false;
            Report($"正在解压 {NewVersion}…");
            var extract = new Progress<double>(value => { if (State == UpdateState.Extracting) Progress = value * 100; });
            // Extraction and signature verification must not freeze the UI.
            _staged = await Task.Run(() => AppUpdater.StageAsync(archive, _processPath, cancellation.Token, extract), cancellation.Token);
            // 解压完成即可删除压缩包，只保留待替换的目录。
            File.Delete(archive);
            if (_closed) { Discard(); return; }

            State = UpdateState.Ready;
            Progress = 100;
            PhaseText = "可以开始安装了";
            Report(InstallOnQuit ? $"VodBox {NewVersion} 已准备就绪，将在退出 VodBox 时安装。" : $"VodBox {NewVersion} 已准备就绪，可以开始安装了。");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Fail("", "更新已取消；临时文件已清理。");
        }
        catch (Exception error)
        {
            VodBoxLog.Error("update", "准备更新失败", error);
            Fail(State == UpdateState.Extracting ? $"解压更新失败：{error.Message}" : $"下载更新失败：{error.Message}", null);
        }
        finally { _cancellation = null; }
    }

    private void Fail(string error, string? message)
    {
        State = UpdateState.Available;
        InstallOnQuit = false;
        ProgressIndeterminate = false;
        ErrorText = error;
        Report(message ?? error + "；可重试。");
        Cleanup();
    }

    public void CancelDownload()
    {
        if (!CanCancel) return;
        Report("正在取消下载并清理临时文件…");
        _cancellation?.Cancel();
    }

    private async Task<bool> LaunchCoreAsync(bool relaunch)
    {
        try
        {
            await AppUpdater.LaunchAsync(_staged!, _cache!, relaunch);
            State = UpdateState.Installed;
            Report(relaunch ? "独立更新器已启动；正在关闭 VodBox，随后替换并重启。" : $"独立更新器已启动；VodBox 退出后将安装 {NewVersion}。");
            return true;
        }
        catch (Exception error)
        {
            VodBoxLog.Error("update", "启动更新器失败", error);
            ErrorText = $"启动安装失败：{error.Message}";
            Report(ErrorText + "；尚未安装。");
            return false;
        }
    }

    /// <summary>应用退出：已就绪且安排了退出时安装 → 启动不重启的更新器；其余情况取消并清理。</summary>
    public async Task ShutdownAsync()
    {
        if (_closed || State == UpdateState.Installed) return;
        if (InstallOnQuit && State == UpdateState.Ready)
        {
            var launch = LaunchCoreAsync(relaunch: false);
            _operation = launch;
            if (await launch) return;
        }
        await CloseAsync();
    }

    /// <summary>更新窗口被关闭：已就绪 → 改为退出时安装，会话继续；后台准备中的自动更新继续；其余情况取消并清理。</summary>
    public Task WindowClosedAsync()
    {
        if (State == UpdateState.Ready && !_closed)
        {
            InstallOnQuit = true;
            Report($"VodBox {NewVersion} 将在退出 VodBox 时安装。");
            return Task.CompletedTask;
        }
        return InstallOnQuit || State == UpdateState.Installed ? Task.CompletedTask : CloseAsync();
    }

    public Task CloseAsync() => _closeOperation ??= CloseCoreAsync();

    private async Task CloseCoreAsync()
    {
        var wasPreparing = State is UpdateState.Downloading or UpdateState.Extracting;
        _closed = true;
        RaiseCommandState();
        _cancellation?.Cancel();
        if (_operation is { } operation) await operation;
        if (State != UpdateState.Installed)
        {
            Report(wasPreparing ? "已取消更新；临时文件已清理，未安装更新。"
                : State == UpdateState.Ready ? "已取消安装；临时文件已清理，未安装更新。" : "已关闭更新窗口；未安装更新。");
            Cleanup();
        }
    }

    private void Discard()
    {
        try { if (_staged is { } staged) AppUpdater.Discard(staged); }
        catch (Exception error) { VodBoxLog.Error("update", "清理已解压的更新失败", error); }
        _staged = null;
    }

    private void Cleanup()
    {
        Discard();
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
