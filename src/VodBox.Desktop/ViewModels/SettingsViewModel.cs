using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Input;
using VodBox.Core;
using VodBox.Desktop.Services;

namespace VodBox.Desktop.ViewModels;

/// <summary>设置：配置订阅（点播/直播 URL）+ 站点列表 + 关于。</summary>
public sealed partial class SettingsViewModel : ObservableObject, IUpdatePreferences
{
    private readonly AppServices _services;
    private readonly MainViewModel _main;
    private readonly SemaphoreSlim _applyConfiguration = new(1,1);
    private readonly SemaphoreSlim _subscriptionChanges = new(1, 1);

    public ObservableCollection<SourceInfo> Sites { get; } = [];
    public ObservableCollection<ConfigHistoryEntry> LiveConfigHistory { get; } = [];
    public void RefreshSites()
    {
        Sites.Clear();
        foreach (var site in _services.Registry.Sources) Sites.Add(site);
        _main.UpdateSourceName();
    }

    [RelayCommand] private void ToggleSiteHidden(SourceInfo site) { _services.Registry.SetHidden(site.Key, !_services.Registry.IsHidden(site.Key)); RefreshSites(); Message = _services.Registry.IsHidden(site.Key) ? "站点已隐藏" : "站点已显示"; }
    [RelayCommand] private void ToggleSiteSearch(SourceInfo site) { _services.Registry.SetSearchable(site.Key, !_services.Registry.CanSearch(site)); Message = _services.Registry.CanSearch(site) ? "站点搜索已开启" : "站点搜索已关闭"; }
    [RelayCommand] private void ToggleSiteChange(SourceInfo site){_services.Registry.SetChangeable(site.Key,!_services.Registry.CanChange(site));Message=_services.Registry.CanChange(site)?"站点换源已开启":"站点换源已关闭";}
    [RelayCommand] private void ToggleSiteStar(SourceInfo site) { _services.Registry.SetStarred(site.Key, !_services.Registry.IsStarred(site.Key)); RefreshSites(); }
    [RelayCommand] private void MoveSiteUp(SourceInfo site) { _services.Registry.MoveUp(site.Key); RefreshSites(); }
    [RelayCommand] private void MoveSiteToTop(SourceInfo site) { _services.Registry.MoveToTop(site.Key); RefreshSites(); }

    [ObservableProperty] private string _vodConfigUrl = "";
    [ObservableProperty] private string _liveConfigUrl = "";
    [ObservableProperty] private string _epgSubscriptionUrl = "";
    [ObservableProperty] private bool _lanControl;
    [ObservableProperty] private string _pairingInformation="";
    [ObservableProperty] private Avalonia.Media.Imaging.Bitmap? _pairingQrImage;
    public void ClearPairingPresentation()
    {
        var image=PairingQrImage;PairingQrImage=null;image?.Dispose();PairingInformation="";
    }
    [ObservableProperty] private bool _proxyPushedMedia;
    partial void OnProxyPushedMediaChanged(bool value) => _services.Prefs.Set("push.use-proxy", value);
    [ObservableProperty] private bool _applying;
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private string _updateMessage = "尚未检查更新";
    [ObservableProperty] private bool _autoCheckUpdates = true;
    partial void OnAutoCheckUpdatesChanged(bool value)
    {
        _services.Prefs.Set("updates.auto-check", value);
        if (!value) AutoInstallUpdates = false;
    }
    /// <summary>以后自动下载并安装更新：自动检查发现新版本时后台下载，退出 VodBox 时安装，不再弹窗。</summary>
    [ObservableProperty] private bool _autoInstallUpdates;
    partial void OnAutoInstallUpdatesChanged(bool value)
    {
        _services.Prefs.Set("updates.auto-install", value);
        if (value) AutoCheckUpdates = true;
    }
    /// <summary>「跳过这个版本」记下的版本：自动检查不再提示它，手动检查仍会显示。</summary>
    public string SkippedUpdateVersion
    {
        get => _services.Prefs.GetString("updates.skipped-version");
        set => _services.Prefs.Set("updates.skipped-version", value);
    }
    public string AppVersion => typeof(App).Assembly.GetName().Version?.ToString(3) ?? "未知";
    public UpdateOffer? AvailableUpdate { get; private set; }
    [ObservableProperty] private bool _updateBusy;
    private readonly UpdateWindowService _updateWindows = new();
    private CancellationTokenSource? _updateCheckCancellation;
    private bool _updatesStopped;

    public async Task CheckUpdateAsync(Avalonia.Controls.Window? owner = null, bool automatic = false)
    {
        if (_updatesStopped || (!automatic && _updateWindows.ActivateExisting()) || UpdateBusy) return;
        UpdateBusy = true;
        using var cancellation = new CancellationTokenSource();
        _updateCheckCancellation = cancellation;
        UpdateMessage = "正在检查更新…";
        owner ??= (Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        // 手动检查：先弹出「正在检查更新…」，结果在同一窗口给出；自动检查保持静默。
        var checking = !automatic && owner is { IsVisible: true } ? new Views.UpdateCheckWindow() : null;
        if (checking is not null)
        {
            checking.CancelRequested += cancellation.Cancel;
            checking.Show(owner!);
        }
        try
        {
            AvailableUpdate = await AppUpdater.CheckAsync(AppVersion, cancellation.Token);
            if (_updatesStopped || cancellation.IsCancellationRequested) return;
            UpdateMessage = AvailableUpdate is { } offer ? $"发现新版本 {offer.Version}" : "当前已是最新版本，或暂无可用的安全更新包";
            if (AvailableUpdate is not { } update)
            {
                checking?.ShowResult("VodBox 已是最新版本", $"VodBox {AppVersion} 是目前的最新版本。");
                return;
            }
            checking?.Dismiss();
            checking = null;
            if (automatic && !AutoCheckUpdates) return;
            if (automatic && update.Version == SkippedUpdateVersion) { UpdateMessage = $"发现新版本 {update.Version}（已跳过此版本）"; return; }
            void Report(string message) => UpdateMessage = message;
            if (automatic && AutoInstallUpdates && _updateWindows.StartBackground(update, AppVersion, Report, this)) return;
            if (owner is { IsVisible: true }) _updateWindows.Show(update, AppVersion, owner, automatic, Report, this);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { UpdateMessage = "已取消检查更新"; }
        catch (Exception error)
        {
            UpdateMessage = $"检查更新失败：{error.Message}";
            checking?.ShowResult("检查更新失败", error.Message);
            checking = null;
        }
        finally
        {
            _updateCheckCancellation = null;
            UpdateBusy = false;
            // 取消或停机时收起仍在「检查中」的窗口；已显示结果的窗口留给用户关闭。
            if (cancellation.IsCancellationRequested) checking?.Dismiss();
        }
    }

    public async Task StopUpdatesAsync()
    {
        _updatesStopped = true;
        _updateCheckCancellation?.Cancel();
        await _updateWindows.CloseAsync();
    }


    [ObservableProperty] private string _subtitleCredential="";
    [ObservableProperty] private string _subtitleQuery="";
    [ObservableProperty] private bool _subtitleSearching;
    public ObservableCollection<OnlineSubtitle> OnlineSubtitles {get;}=[];
    public ObservableCollection<OnlineSubtitleFile> SubtitleFiles {get;}=[];
    private CancellationTokenSource? _subtitleRequest;
    private long _subtitleGeneration;
    [ObservableProperty] private int _posterDensity=1;
    partial void OnPosterDensityChanged(int value)
    {
        _services.Prefs.Set("ui.poster-density",Math.Clamp(value,0,2));_main.Vod.RefreshPosterDensity();
    }
    [ObservableProperty] private int _themeIndex;
    public bool IsLinux => OperatingSystem.IsLinux();
    [ObservableProperty][NotifyPropertyChangedFor(nameof(LinuxTitleBarRestartRequired))] private bool _linuxDrawnTitleBar;
    /// <summary>X11 的标题栏模式在 Avalonia 启动时固定，开关和当前进程不一致时提示重启。</summary>
    public bool LinuxTitleBarRestartRequired => IsLinux && LinuxDrawnTitleBar != WindowChrome.UsesLinuxDrawnDecorations;
    partial void OnLinuxDrawnTitleBarChanged(bool value) => _services.Prefs.Set(WindowChrome.LinuxDrawnTitleBarKey, value);
    partial void OnThemeIndexChanged(int value)
    {
        _services.Prefs.Set("ui.theme", Math.Clamp(value, 0, 2));
        if (Avalonia.Application.Current is { } app) app.RequestedThemeVariant = value switch
        {
            1 => Avalonia.Styling.ThemeVariant.Light, 2 => Avalonia.Styling.ThemeVariant.Dark, _ => Avalonia.Styling.ThemeVariant.Default,
        };
    }

    // ---- 设置分组导航（设计稿 p-setting 左栏） ----
    /// <summary>当前设置分组：0=源与订阅 1=播放 2=数据 3=关于。</summary>
    [ObservableProperty] private int _section = 0;
    public bool IsSourcesSection=>Section==0;
    public bool IsPlaybackSection=>Section==1;
    public bool IsDanmakuSection=>Section==2;
    public bool IsSubtitleSection=>Section==3;
    public bool IsInterfaceSection=>Section==4;
    public bool IsDataSection=>Section==5;
    public bool IsRemoteSection=>Section==6;
    public bool IsAboutSection=>Section==7;
    public bool IsDiagnosticsSection=>Section==8;
    public PlayerViewModel PlayerSettings=>_main.Player;

    // ---- 内置节目表（源与订阅 → 节目订阅）----
    /// <summary>当前生效的节目表来源状态：内置 / 手动订阅 / 失败文案，与直播页状态入口一致。</summary>
    public string EpgStatusText => _main.Live.EpgStatusLabel;
    /// <summary>状态详情：状态 / 最近成功获取 / 数据覆盖，未知显示未知。</summary>
    public string EpgStatusDetailText => _main.Live.EpgStatusDetail;
    /// <summary>手动订阅覆盖生效时为 true：此时内置来源让位，「恢复内置节目表」才有意义。</summary>
    public bool EpgManualOverride => _main.Live.EpgManualOverride;

    /// <summary>节目表状态变化后刷新设置页展示（由 LiveViewModel 的属性通知驱动）。</summary>
    private void RefreshEpgStatus()
    {
        OnPropertyChanged(nameof(EpgStatusText));
        OnPropertyChanged(nameof(EpgStatusDetailText));
        OnPropertyChanged(nameof(EpgManualOverride));
    }

    /// <summary>
    /// 「恢复内置节目表」：清除手动订阅地址与手动导入数据，回到内置主源 + 补充源。
    /// 不触发播放、切台或换线；只在手动覆盖存在时可用。
    /// </summary>
    [RelayCommand]
    private async Task RestoreBuiltInEpg()
    {
        try
        {
            Message = "正在恢复内置节目表…";
            await _main.Live.RestoreBuiltInEpgAsync();
            EpgSubscriptionUrl = "";
            Message = "已恢复内置节目表；失败或过期时保留有效缓存";
        }
        catch (Exception error) { Message = $"恢复内置节目表失败：{error.Message}"; }
        finally { RefreshEpgStatus(); }
    }

    // ---- 诊断（日志）----
    private bool _logEnabled = true;
    /// <summary>日志开关。关闭后不再写入，用于日常降低噪音；排障时打开。</summary>
    public bool LogEnabled
    {
        get => _logEnabled;
        set
        {
            if (SetProperty(ref _logEnabled, value))
            {
                VodBox.Core.VodBoxLog.SetEnabled(value);
                _services.Prefs.Set("diagnostics.log", value ? 1 : 0);
                RefreshLog();
            }
        }
    }

    public string LogDirectory => VodBox.Core.VodBoxLog.LogDirectory ?? "（尚未初始化）";
    public string LogFile => VodBox.Core.VodBoxLog.CurrentFile is { } file ? Path.GetFileName(file) : "（本次运行还没有日志文件）";
    public string LogPath => VodBox.Core.VodBoxLog.CurrentFile ?? "";
    [ObservableProperty] private string _logTail = "";
    [ObservableProperty] private string _diagnosticsMessage = "";

    /// <summary>重新读取日志尾部；点“刷新”或切到本区时调用。</summary>
    public void RefreshLog()
    {
        var lines = VodBox.Core.VodBoxLog.ReadTail(80);
        LogTail = lines.Count == 0 ? "（暂无日志。执行一次播放后刷新即可看到记录。）" : string.Join(Environment.NewLine, lines);
        OnPropertyChanged(nameof(LogDirectory));
        OnPropertyChanged(nameof(LogFile));
        OnPropertyChanged(nameof(LogPath));
    }

    [RelayCommand]
    public void RefreshLogs() { RefreshLog(); DiagnosticsMessage = $"已刷新（{VodBox.Core.VodBoxLog.ReadTail(80).Count} 行）"; }

    [RelayCommand]
    public void OpenLogFolder()
    {
        try
        {
            var directory = VodBox.Core.VodBoxLog.LogDirectory;
            if (string.IsNullOrEmpty(directory)) { DiagnosticsMessage = "日志目录尚未初始化"; return; }
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo { FileName = directory, UseShellExecute = true });
            DiagnosticsMessage = "已打开日志目录";
        }
        catch (Exception error) { DiagnosticsMessage = $"打开失败：{error.Message}"; }
    }

    [RelayCommand]
    public void ClearLogs()
    {
        try
        {
            VodBox.Core.VodBoxLog.Clear();
            RefreshLog();
            DiagnosticsMessage = "日志已清空";
        }
        catch (Exception error) { DiagnosticsMessage = $"清空失败：{error.Message}"; }
    }

    [RelayCommand]
    public void CopyLogPath()
    {
        try
        {
            if (string.IsNullOrEmpty(LogPath)) { DiagnosticsMessage = "还没有日志文件"; return; }
            // 剪贴板需窗口，这里退化为把路径显示出来，避免破坏无窗口调用。
            DiagnosticsMessage = LogPath;
        }
        catch (Exception error) { DiagnosticsMessage = error.Message; }
    }

    partial void OnSectionChanged(int value)
    {
        foreach(var property in new[]{nameof(IsSourcesSection),nameof(IsPlaybackSection),nameof(IsDanmakuSection),nameof(IsSubtitleSection),nameof(IsInterfaceSection),nameof(IsDataSection),nameof(IsRemoteSection),nameof(IsAboutSection),nameof(IsDiagnosticsSection),nameof(PlayerSettings)}) OnPropertyChanged(property);
    }

    // ---- 更改点播配置弹窗（设计稿 ②） ----
    [ObservableProperty] private bool _configDialogOpen;
    [ObservableProperty] private string _dialogUrl = "";
    /// <summary>历史配置（当前 + 可切换/删除），S5 持久化到 Config 表。</summary>
    public ObservableCollection<ConfigHistoryEntry> ConfigHistory { get; } = [];

    public SettingsViewModel(AppServices services, MainViewModel main)
    {
        _services = services;
        AutoCheckUpdates = services.Prefs.GetBool("updates.auto-check", true);
        _autoInstallUpdates = services.Prefs.GetBool("updates.auto-install", false);
        _main = main;
        VodConfigUrl = services.CurrentVodConfig ?? "";
        LiveConfigUrl = services.CurrentLiveConfig ?? "";
        EpgSubscriptionUrl = services.Prefs.GetString("live.epg-url");
        _proxyPushedMedia = services.Prefs.GetBool("push.use-proxy", false);
        _themeIndex = Math.Clamp(services.Prefs.GetInt("ui.theme", 0), 0, 2);
        _linuxDrawnTitleBar = services.Prefs.GetBool(WindowChrome.LinuxDrawnTitleBarKey, true);
        _posterDensity=Math.Clamp(services.Prefs.GetInt("ui.poster-density",1),0,2);
        // 节目表状态入口由直播页维护；设置页转发它的变化，避免两处状态各说各话。
        _main.Live.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(LiveViewModel.EpgStatusLabel)
                or nameof(LiveViewModel.EpgStatusDetail)
                or nameof(LiveViewModel.EpgManualOverride)) RefreshEpgStatus();
        };
    }

    [RelayCommand]
    public async Task ApplyVodConfig()
    {
        if (string.IsNullOrWhiteSpace(VodConfigUrl))
        {
            Message = "请输入 TVBox 配置地址";
            return;
        }
        var requestedUrl=VodConfigUrl;
        await _applyConfiguration.WaitAsync();
        Applying = true;
        Message = "正在加载配置…";
        try
        {
            await _services.Registry.LoadConfigAsync(requestedUrl);
            _services.CurrentVodConfig = requestedUrl;
            RefreshSites();
            _main.UpdateSourceName();
            await RecordSubscriptionAsync(requestedUrl);
            Message = $"加载成功：{_services.Registry.Sources.Count} 个站点"+(_services.Registry.ImportWarnings.Count>0?"；导入警告："+string.Join("；",_services.Registry.ImportWarnings):"");
            // 换配置后旧的分类/海报属于已失效的站点，进点播页必须重新加载而不是复用缓存。
            _main.Vod.Invalidate();
            await _main.Home.LoadAsync();
        }
        catch (Exception error)
        {
            Message = $"配置加载失败：{error.Message}";
        }
        finally
        {
            Applying = false;
            _applyConfiguration.Release();
        }
    }

    [RelayCommand]
    private async Task RefreshEpgSubscription()
    {
        try
        {
            Message = "正在刷新节目订阅…";
            await _main.Live.RefreshEpgSubscriptionAsync(EpgSubscriptionUrl);
            Message = "节目订阅刷新成功，缓存已保存；已覆盖内置节目表";
        }
        catch (Exception error) { Message = $"节目订阅刷新失败，保留原缓存：{error.Message}"; }
        finally { RefreshEpgStatus(); }
    }

    [RelayCommand]
    public async Task ApplyLiveConfig()
    {
        if (string.IsNullOrWhiteSpace(LiveConfigUrl))
        {
            Message = "请输入直播源地址（m3u/txt）";
            return;
        }
        Applying = true;
        Message = "正在加载直播源…";
        try
        {
            if (await _main.Live.ApplyConfigurationAsync(LiveConfigUrl))
            {
                await RecordSubscriptionAsync(LiveConfigUrl, ConfigKind.Live);
                Message = $"直播源加载成功：{_main.Live.Groups.Count} 个分组" + (_main.Live.ImportWarnings.Count > 0 ? "；部分源失败：" + string.Join("；", _main.Live.ImportWarnings) : "");
            }
            else Message = _main.StatusMessage;
        }
        catch (Exception error)
        {
            Message = $"直播源加载失败：{error.Message}";
        }
        finally
        {
            Applying = false;
        }
    }

    [RelayCommand]
    private async Task ClearHistory()
    {
        try { await _services.Store.ClearHistoryAsync(); Message = "观看历史已清空"; }
        catch (Exception error) { Message = $"清空历史失败：{error.Message}"; }
    }

    [RelayCommand]
    public async Task LoadLiveConfigHistory()
    {
        var entries = await _services.Store.ListAsync(ConfigKind.Live);
        await _main.RunOnUiAsync(() =>
        {
            LiveConfigHistory.Clear();
            foreach (var entry in entries) LiveConfigHistory.Add(new ConfigHistoryEntry { Id = entry.Id, Name = entry.Name, Url = entry.Url, Current = entry.Active });
        });
    }

    [RelayCommand]
    private async Task SwitchLiveConfig(ConfigHistoryEntry entry)
    {
        LiveConfigUrl = entry.Url;
        await ApplyLiveConfig();
    }

    [RelayCommand]
    private async Task DeleteLiveConfig(ConfigHistoryEntry entry)
    {
        if (entry.Current) { Message = "请先切换直播配置，再删除当前订阅"; return; }
        try { await _services.Store.RemoveAsync(entry.Id); await LoadLiveConfigHistory(); }
        catch (Exception error) { Message = $"删除直播订阅失败：{error.Message}"; }
    }

    public async Task LoadConfigHistoryAsync()
    {
        var entries = await _services.Store.ListAsync(ConfigKind.Vod);
        await _main.RunOnUiAsync(() =>
        {
            ConfigHistory.Clear();
            foreach (var entry in entries)
                ConfigHistory.Add(new ConfigHistoryEntry { Id = entry.Id, Url = entry.Url, Name = entry.Name, Current = entry.Active });
        });
    }

    private async Task RecordSubscriptionAsync(string url, ConfigKind kind = ConfigKind.Vod)
    {
        await _subscriptionChanges.WaitAsync();
        try
        {
            var existing = await _services.Store.ListAsync(kind);
            if (!existing.Any(entry => entry.Url == url))
                await _services.Store.AddAsync(new ConfigSubscription { Url = url, Name = url, Kind = kind });
            var entry = (await _services.Store.ListAsync(kind)).First(item => item.Url == url);
            await _services.Store.SetActiveAsync(kind, entry.Id);
            if (kind == ConfigKind.Live) await LoadLiveConfigHistory(); else await LoadConfigHistoryAsync();
        }
        finally { _subscriptionChanges.Release(); }
    }

    public void CancelSubtitleSearch()
    {++_subtitleGeneration;_subtitleRequest?.Cancel();_subtitleRequest=null;SubtitleSearching=false;SubtitleCredential="";}

    [RelayCommand]
    private async Task SearchOnlineSubtitles()
    {
        _subtitleRequest?.Cancel();using var scope=new CancellationTokenSource();_subtitleRequest=scope;
        var generation=++_subtitleGeneration;SubtitleSearching=true;OnlineSubtitles.Clear();SubtitleFiles.Clear();
        try
        {
            using var http=new VodBox.Infrastructure.DefaultHttp();
            var query=string.IsNullOrWhiteSpace(SubtitleQuery)?_main.Player.Title:SubtitleQuery;
            var result=await new VodBox.Infrastructure.AssrtSubtitles(http).SearchAsync(query,SubtitleCredential,scope.Token);
            await _main.RunOnUiAsync(()=>{if(generation!=_subtitleGeneration||scope.IsCancellationRequested)return;foreach(var item in result)OnlineSubtitles.Add(item);Message=$"找到 {result.Count} 条字幕结果";});
        }
        catch(OperationCanceledException)when(scope.IsCancellationRequested){}
        catch(Exception error){if(generation==_subtitleGeneration)Message=$"字幕搜索失败：{error.Message}";}
        finally{if(generation==_subtitleGeneration){_subtitleRequest=null;SubtitleSearching=false;}}
    }

    [RelayCommand]
    private async Task SelectOnlineSubtitle(OnlineSubtitle subtitle)
    {
        _subtitleRequest?.Cancel();using var scope=new CancellationTokenSource();_subtitleRequest=scope;var generation=++_subtitleGeneration;
        SubtitleFiles.Clear();
        try
        {
            using var http=new VodBox.Infrastructure.DefaultHttp();
            var files=await new VodBox.Infrastructure.AssrtSubtitles(http).FilesAsync(subtitle.Id,SubtitleCredential,scope.Token);
            await _main.RunOnUiAsync(()=>{if(generation!=_subtitleGeneration||scope.IsCancellationRequested)return;foreach(var file in files)SubtitleFiles.Add(file);Message=files.Count==0?"无支持的独立字幕文件（压缩包未自动解压）":$"可加载 {files.Count} 个字幕文件";});
        }
        catch(OperationCanceledException)when(scope.IsCancellationRequested){}
        catch(Exception error){if(generation==_subtitleGeneration)Message=$"字幕详情失败：{error.Message}";}
        finally{if(generation==_subtitleGeneration)_subtitleRequest=null;}
    }

    [RelayCommand]
    private async Task LoadOnlineSubtitle(OnlineSubtitleFile file)
    {
        var player=_main.Player;var intent=player.CurrentSessionId;
        if(!player.Visible){Message="请先播放媒体，再加载在线字幕";return;}
        _subtitleRequest?.Cancel();using var scope=new CancellationTokenSource();_subtitleRequest=scope;
        var generation=++_subtitleGeneration;string? savedPath=null;
        try
        {
            if(!Uri.TryCreate(file.Url,UriKind.Absolute,out var address)||address.Scheme!="https")throw new InvalidDataException("字幕下载地址无效。");
            var extension=Path.GetExtension(file.Name).ToLowerInvariant();
            if(extension is not (".srt" or ".ass" or ".ssa" or ".vtt"))throw new InvalidDataException("字幕类型不支持。");
            using var http=new VodBox.Infrastructure.DefaultHttp(allowRedirect:false);
            var bytes=await http.GetBoundedAsync(file.Url,2*1024*1024,scope.Token);
            if(intent!=player.CurrentSessionId||generation!=_subtitleGeneration){Message="播放已切换，丢弃旧字幕下载";return;}
            var directory=Path.Combine(_services.DataDir,"subtitles");Directory.CreateDirectory(directory);
            var owned=new DirectoryInfo(directory).EnumerateFiles().Where(item=>Guid.TryParseExact(Path.GetFileNameWithoutExtension(item.Name),"N",out _)).OrderBy(item=>item.LastWriteTimeUtc).ToList();
            if(owned.Count>=100)throw new InvalidOperationException("字幕缓存已达100项，请关闭播放器后清理字幕缓存。");
            savedPath=Path.Combine(directory,Guid.NewGuid().ToString("N")+extension);
            await VodBox.Infrastructure.AtomicFile.WriteBytesAsync(savedPath,bytes,scope.Token);
            scope.Token.ThrowIfCancellationRequested();
            if(intent!=player.CurrentSessionId||generation!=_subtitleGeneration)return;
            if(await player.LoadSubtitleAsync(savedPath,intent))
            {savedPath=null;Message="字幕已下载并加载到当前播放器";}
            else Message="字幕未加载成功，下载临时文件已清理";
        }
        catch(OperationCanceledException)when(scope.IsCancellationRequested){}
        catch(Exception error){if(generation==_subtitleGeneration)Message=$"字幕加载失败：{error.Message}";}
        finally
        {
            if(savedPath is not null&&File.Exists(savedPath))File.Delete(savedPath);
            if(ReferenceEquals(_subtitleRequest,scope))_subtitleRequest=null;
        }
    }

    [RelayCommand]
    public void OpenRemoteSettings()
    {ConfigDialogOpen=false;_main.OpenSettings();Section=6;}

    [RelayCommand]
    private async Task StartLocalControl()
    {
        try
        {
            await _services.LocalControl.StartAsync(command => _main.RunOnUiAsync(() =>
            {
                var player = _main.Player;
                switch (command)
                {
                    case "toggle": player.TogglePlayPauseCommand.Execute(null); break;
                    case "stop": player.CloseCommand.Execute(null); break;
                    case "previous": player.PreviousEpisodeCommand.Execute(null); break;
                    case "next": player.NextEpisodeCommand.Execute(null); break;
                    case "back": player.SeekBy(-10); break;
                    case "forward": player.SeekBy(10); break;
                }
            }), url => _main.RunOnUiAsync(() => _main.Player.Play(new PlaybackRequest { Uri = ProxyPushedMedia ? _services.LocalControl.RegisterMedia(url) : url, SourceKey = "push", SourceName = "本机推送", MediaId = url })),allowLan:LanControl,adjust:(command,value)=>_main.RunOnUiAsync(()=>
            {
                var player=_main.Player;
                switch(command)
                {
                    case "volume":player.Volume=(int)Math.Round(value);break;
                    case "rate":player.Rate=value;break;
                    case "seek":if(player.Duration>TimeSpan.Zero)player.Seek(TimeSpan.FromSeconds(Math.Clamp(value,0,player.Duration.TotalSeconds)));break;
                }
            }),configure:url=>_main.RunOnUiAsync(()=>
            {
                _main.OpenSettings();Section=0;DialogUrl=url;ConfigDialogOpen=true;
                Message="收到配置地址，请在桌面确认加载";
            }),status:async()=>
            {
                RemotePlaybackStatus? snapshot=null;
                await _main.RunOnUiAsync(()=>{var player=_main.Player;snapshot=new RemotePlaybackStatus(player.Title,player.State.ToString(),player.Position.TotalSeconds,player.Duration.TotalSeconds,player.Volume,player.Rate);});
                return snapshot!;
            });
            PairingInformation=_services.LocalControl.LanEnabled?string.Join("；",_services.LocalControl.LanAddresses)+" 配对码："+_services.LocalControl.PairingCode:"仅本机模式";
            var address=_services.LocalControl.LanAddresses.FirstOrDefault();
            var oldQr=PairingQrImage;PairingQrImage=address is null?null:VodBox.Desktop.Views.PairingQr.Create(address);oldQr?.Dispose();
            Message = "遥控已启动：" + _services.LocalControl.Address;
        }
        catch (Exception error) { Message = $"遥控启动失败：{error.Message}"; }
    }

    [RelayCommand]
    private void ClearSubtitleCache()
    {
        if(_main.Player.Visible){Message="请关闭播放器后再清理字幕缓存";return;}
        var directory=Path.Combine(_services.DataDir,"subtitles");
        try
        {
            if(Directory.Exists(directory))foreach(var path in Directory.EnumerateFiles(directory))
                if(Guid.TryParseExact(Path.GetFileNameWithoutExtension(path),"N",out _))File.Delete(path);
            Message="下载字幕缓存已清理，手动文件保留";
        }
        catch(Exception error){Message=$"字幕缓存清理失败：{error.Message}";}
    }

    [RelayCommand]
    private async Task ClearMediaCache()
    {
        try{await _services.LocalControl.Proxy.ClearCacheAsync();Message="媒体缓存已清空";}
        catch(Exception error){Message=$"缓存清理失败：{error.Message}";}
    }

    [RelayCommand]
    private async Task ClearMediaInbox()
    {
        if(_main.Player.Visible){Message="请先关闭播放器，再清理上传收件箱";return;}
        try{await _services.LocalControl.ClearInboxAsync();Message="服务上传文件已清理，手动放入的文件保留";}
        catch(Exception error){Message=$"收件箱清理失败：{error.Message}";}
    }

    public void SetWallpaper(string path)
    {
        if(!Path.IsPathFullyQualified(path)||!File.Exists(path)||new FileInfo(path).Length>16*1024*1024)throw new InvalidDataException("壁纸必须是小于16 MiB的本地图片。");
        if(Path.GetExtension(path).ToLowerInvariant() is not (".png" or ".jpg" or ".jpeg" or ".webp"))throw new InvalidDataException("壁纸格式不支持。");
        using(var input=File.OpenRead(path))using(var validation=Avalonia.Media.Imaging.Bitmap.DecodeToWidth(input,32)){}
        _services.Prefs.Set("ui.wallpaper",path);_main.WallpaperPath=path;Message="壁纸已保存";
    }
    [RelayCommand]private void ClearWallpaper(){_services.Prefs.Set("ui.wallpaper","");_main.WallpaperPath="";Message="壁纸已清除";}

    [RelayCommand]
    private void OpenMediaInbox()
    {
        // 遥控端上传的媒体落在收件箱目录：直接用系统「打开文件」对话框从该目录开始选，不再有自绘的文件浏览页。
        var directory=Path.Combine(_services.DataDir,"media-inbox");Directory.CreateDirectory(directory);
        _main.RequestOpenFile(directory);
    }

    [RelayCommand]
    private async Task StopLocalControl()
    {
        await _services.LocalControl.DisposeAsync();
        ClearPairingPresentation();Message = "遥控已关闭";
    }

    public Task<string> ExportBackupAsync() => new VodBox.Infrastructure.LibraryBackupService(_services.Store).ExportAsync();
    public async Task RestoreBackupAsync(string json)
    {
        await new VodBox.Infrastructure.LibraryBackupService(_services.Store).ImportMergeAsync(json);
        VodConfigUrl = _services.CurrentVodConfig ?? "";
        LiveConfigUrl = _services.CurrentLiveConfig ?? "";
        await LoadConfigHistoryAsync();
        await LoadLiveConfigHistory();
        await _main.Live.ReloadProgrammeBackupAsync();
        EpgSubscriptionUrl=_services.Prefs.GetString("live.epg-url");
        RefreshEpgStatus();
        Message = "备份已合并；播放偏好在下次启动生效，当前播放不受影响";
    }

    // ---- 弹窗动作 ----

    /// <summary>打开「更改点播配置」弹窗（带历史配置列表）。</summary>
    [RelayCommand]
    public async Task OpenConfigDialog()
    {
        await LoadConfigHistoryAsync();
        DialogUrl = VodConfigUrl;
        ConfigDialogOpen = true;
    }

    /// <summary>从剪贴板读取配置地址。</summary>
    [RelayCommand]
    public async Task PasteFromClipboard()
    {
        try
        {
            var clipboard = Avalonia.Application.Current?.TryGetFeature(typeof(Avalonia.Input.Platform.IClipboard))
                as Avalonia.Input.Platform.IClipboard;
            if (clipboard is null) return;
            var data = await clipboard.TryGetDataAsync() as Avalonia.Input.IDataTransfer;
            if (data?.TryGetText() is not { Length: > 0 } text) return;
            DialogUrl = text.Trim();

            if (!string.IsNullOrWhiteSpace(text)) DialogUrl = text.Trim();
        }
        catch
        {
            Message = "剪贴板读取失败";
        }
    }

    /// <summary>弹窗确定：加载配置并写历史。</summary>
    [RelayCommand]
    public async Task ConfirmConfigDialog()
    {
        ConfigDialogOpen = false;
        VodConfigUrl = DialogUrl;
        await ApplyVodConfig();

    }

    /// <summary>取消弹窗。</summary>
    [RelayCommand]
    private void CancelConfigDialog() => ConfigDialogOpen = false;

    /// <summary>切换到历史配置。</summary>
    [RelayCommand]
    public async Task SwitchConfig(ConfigHistoryEntry entry)
    {
        ConfigDialogOpen = false;
        VodConfigUrl = entry.Url;
        await ApplyVodConfig();
    }

    /// <summary>删除历史配置。</summary>
    [RelayCommand]
    private async Task DeleteConfig(ConfigHistoryEntry entry)
    {
        if (entry.Current) { Message = "请先切换配置，再删除当前订阅"; return; }
        try { await _services.Store.RemoveAsync(entry.Id); await LoadConfigHistoryAsync(); }
        catch (Exception error) { Message = $"删除订阅失败：{error.Message}"; }
    }
}

/// <summary>历史配置行（tvbox.json（当前）/ 使用中角标）。</summary>
public sealed class ConfigHistoryEntry
{
    public long Id { get; init; }
    public string Name { get; init; } = "";
    public string Url { get; init; } = "";
    public bool Current { get; set; }
}
