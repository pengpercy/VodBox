using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Input;
using VodBox.Core;
using VodBox.Desktop.Services;

namespace VodBox.Desktop.ViewModels;

/// <summary>设置：配置订阅（点播/直播 URL）+ 站点列表 + 关于。</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly MainViewModel _main;

    public ObservableCollection<SourceInfo> Sites { get; } = [];

    [ObservableProperty] private string _vodConfigUrl = "";
    [ObservableProperty] private string _liveConfigUrl = "";
    [ObservableProperty] private bool _applying;
    [ObservableProperty] private string _message = "";

    // ---- 设置分组导航（设计稿 p-setting 左栏） ----
    /// <summary>当前设置分组：0=源与订阅 1=播放 2=数据 3=关于。</summary>
    [ObservableProperty] private int _section = 0;

    // ---- 更改点播配置弹窗（设计稿 ②） ----
    [ObservableProperty] private bool _configDialogOpen;
    [ObservableProperty] private string _dialogUrl = "";
    /// <summary>历史配置（当前 + 可切换/删除），S5 持久化到 Config 表。</summary>
    public ObservableCollection<ConfigHistoryEntry> ConfigHistory { get; } = [];

    public SettingsViewModel(AppServices services, MainViewModel main)
    {
        _services = services;
        _main = main;
        VodConfigUrl = services.CurrentVodConfig ?? "";
        LiveConfigUrl = services.CurrentLiveConfig ?? "";
    }

    [RelayCommand]
    public async Task ApplyVodConfig()
    {
        if (string.IsNullOrWhiteSpace(VodConfigUrl))
        {
            Message = "请输入 TVBox 配置地址";
            return;
        }
        Applying = true;
        Message = "正在加载配置…";
        try
        {
            await _services.Registry.LoadConfigAsync(VodConfigUrl);
            _services.CurrentVodConfig = VodConfigUrl;
            Sites.Clear();
            foreach (var site in _services.Registry.Sources) Sites.Add(site);
            _main.UpdateSourceName();
            Message = $"加载成功：{_services.Registry.Sources.Count} 个站点（脚本源已过滤）";
            await _main.Home.LoadAsync();
        }
        catch (Exception error)
        {
            Message = $"配置加载失败：{error.Message}";
        }
        finally
        {
            Applying = false;
        }
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
            _services.CurrentLiveConfig = LiveConfigUrl;
            await _main.Live.LoadAsync();
            Message = $"直播源加载成功：{_main.Live.Groups.Count} 个分组";
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
    private void ClearHistory()
    {
        _ = _services.Store.ClearHistoryAsync();
        Message = "观看历史已清空";
    }

    // ---- 弹窗动作 ----

    /// <summary>打开「更改点播配置」弹窗（带历史配置列表）。</summary>
    [RelayCommand]
    public void OpenConfigDialog()
    {
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
        if (!ConfigHistory.Any(c => c.Url == DialogUrl))
            ConfigHistory.Insert(0, new ConfigHistoryEntry { Url = DialogUrl, Name = DialogUrl, Current = true });
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
    private void DeleteConfig(ConfigHistoryEntry entry) => ConfigHistory.Remove(entry);
}

/// <summary>历史配置行（tvbox.json（当前）/ 使用中角标）。</summary>
public sealed class ConfigHistoryEntry
{
    public string Name { get; init; } = "";
    public string Url { get; init; } = "";
    public bool Current { get; set; }
}

/// <summary>本地文件：目录浏览 + 直接播放。</summary>
public sealed partial class FilesViewModel : ObservableObject
{
    private readonly AppServices _services;

    public ObservableCollection<FileEntry> Entries { get; } = [];

    [ObservableProperty] private string _currentPath = "";
    [ObservableProperty] private string _status = "";

    /// <summary>designTime=true：不枚举真实文件系统，停在空根目录（预览器加载快且无副作用）。</summary>
    public FilesViewModel(AppServices services, bool designTime = false)
    {
        _services = services;
        if (!designTime)
            Navigate(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        else
            Navigate("");
    }

    public void Navigate(string path)
    {
        try
        {
            CurrentPath = path;
            Entries.Clear();
            var directory = new DirectoryInfo(path);
            if (directory.Parent is not null)
                Entries.Add(new FileEntry { Name = "..", FullPath = directory.Parent.FullName, IsDirectory = true });
            foreach (var dir in directory.GetDirectories().OrderBy(d => d.Name))
                Entries.Add(new FileEntry { Name = dir.Name, FullPath = dir.FullName, IsDirectory = true });
            foreach (var file in directory.GetFiles().OrderBy(f => f.Name))
            {
                var ext = file.Extension.ToLowerInvariant();
                if (ext is ".mp4" or ".mkv" or ".avi" or ".mov" or ".flv" or ".ts" or ".m3u8" or ".webm" or ".mp3" or ".flac" or ".wav")
                    Entries.Add(new FileEntry { Name = file.Name, FullPath = file.FullName, IsDirectory = false });
            }
            Status = $"{Entries.Count} 项";
        }
        catch (Exception error)
        {
            Status = error.Message;
        }
    }

    [RelayCommand]
    public void Open(FileEntry entry)
    {
        if (entry.IsDirectory)
        {
            Navigate(entry.FullPath);
            return;
        }
        // 文件播放经全局命令（由 MainWindow 转给 PlayerViewModel）
        PlayRequested?.Invoke(entry);
    }

    public event Action<FileEntry>? PlayRequested;
}

public sealed class FileEntry
{
    public string Name { get; init; } = "";
    public string FullPath { get; init; } = "";
    public bool IsDirectory { get; init; }
}
