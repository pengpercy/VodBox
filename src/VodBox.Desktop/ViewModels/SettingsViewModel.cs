using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
}

/// <summary>本地文件：目录浏览 + 直接播放。</summary>
public sealed partial class FilesViewModel : ObservableObject
{
    private readonly AppServices _services;

    public ObservableCollection<FileEntry> Entries { get; } = [];

    [ObservableProperty] private string _currentPath = "";
    [ObservableProperty] private string _status = "";

    public FilesViewModel(AppServices services)
    {
        _services = services;
        Navigate(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
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
