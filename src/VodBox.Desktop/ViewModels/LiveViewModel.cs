using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VodBox.Core;
using VodBox.Desktop.Services;

namespace VodBox.Desktop.ViewModels;

/// <summary>直播：频道面板（分组/收藏/历史）+ 播放。</summary>
public sealed partial class LiveViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly MainViewModel _main;
    private int _lineIndex;
    private int _failedLines;
    private long _lastFailedIntent=-1;
    public void HandlePlaybackFailure(PlaybackRequest request,long intent)
    {
        if(_lastFailedIntent==intent||request.SourceKey!="live"||!request.IsLive||CurrentChannel is not {} channel||IsLocked(channel))return;
        if(channel.Uris.Count==0||request.MediaId!=channel.Uris[0]||request.Uri!=channel.Uris[_lineIndex])return;
        _lastFailedIntent=intent;
        if(++_failedLines>=channel.Uris.Count)
        {
            VodBox.Core.VodBoxLog.Error("live", $"频道全部线路失败：{channel.Name}（{channel.Uris.Count} 条线路）");
            _main.StatusMessage="该频道全部线路播放失败，请手动重试或切换频道";return;
        }
        VodBox.Core.VodBoxLog.Warn("live", $"线路失败自动换线：{channel.Name} → 线路 {_lineIndex+2}/{channel.Uris.Count}");
        _lineIndex=(_lineIndex+1)%channel.Uris.Count;OnPropertyChanged(nameof(CurrentLineLabel));
        _main.StatusMessage=$"直播线路失败，尝试线路 {_lineIndex+1}";
        PlayCurrentLine(channel);
    }
    private void PlayCurrentLine(LiveChannel channel)=>_main.Player.Play(new PlaybackRequest
    {
        Uri=channel.Uris[_lineIndex],Title=channel.Name,Poster=channel.Logo,SourceKey="live",SourceName="直播",MediaId=channel.Uris[0],IsLive=true,
        Headers=new Dictionary<string,string>(channel.Headers),
    });
    private readonly HashSet<string> _unlockedGroups=new();
    private readonly HashSet<string> _collapsedGroups = new(StringComparer.Ordinal);

    public void ToggleGroup(string name)
    {
        if (Groups.Any(group => group.Name == name && group.Locked && !_unlockedGroups.Contains(name)))
        { RequestGroupUnlock(name); return; }
        if (!_collapsedGroups.Add(name)) _collapsedGroups.Remove(name);
        ApplyFilter();
    }
    private readonly Dictionary<string,(int Count,DateTimeOffset Start)> _unlockAttempts=new();
    [ObservableProperty] private bool _groupUnlockOpen;
    [ObservableProperty] private string _groupPassword="";
    [ObservableProperty] private string _lockedGroupName="";
    public void RequestGroupUnlock(string name)
    {LockedGroupName=name;GroupPassword="";GroupUnlockOpen=true;}
    [RelayCommand] private void CancelGroupUnlock(){GroupUnlockOpen=false;GroupPassword="";}
    [RelayCommand] private void UnlockGroup()
    {
        var group=Groups.FirstOrDefault(group=>group.Name==LockedGroupName&&group.Locked);
        if(group?.PasswordHash is null){_main.StatusMessage="该锁定组没有可用解锁凭据";GroupPassword="";return;}
        var now=DateTimeOffset.UtcNow;
        var attempts=_unlockAttempts.GetValueOrDefault(group.Name);
        if(now-attempts.Start>TimeSpan.FromMinutes(5))attempts=(0,now);
        if(attempts.Count>=5){GroupPassword="";_main.StatusMessage="尝试过多，请5分钟后重试";return;}
        _unlockAttempts[group.Name]=(attempts.Count+1,attempts.Start==default?now:attempts.Start);
        var supplied=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(GroupPassword)));
        GroupPassword="";
        if(!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(supplied),System.Text.Encoding.ASCII.GetBytes(group.PasswordHash)))
        {_main.StatusMessage="分组密码不正确";return;}
        _unlockAttempts.Remove(group.Name);_unlockedGroups.Add(group.Name);GroupUnlockOpen=false;ApplyFilter();
    }
    [RelayCommand]
    private async Task LockGroups()
    {
        _unlockedGroups.Clear();GroupPassword="";GroupUnlockOpen=false;ApplyFilter();
        if(CurrentChannel is {} channel&&IsLocked(channel))
        {
            await _main.Player.CloseCommand.ExecuteAsync(null);channel.IsCurrent=false;CurrentChannel=null;EpgTimeline.Clear();
        }
    }

    private bool IsLocked(LiveChannel channel)=>Groups.Any(group=>group.Locked&&!_unlockedGroups.Contains(group.Name)&&group.Channels.Contains(channel));
    private readonly Func<string, CancellationToken, Task<List<LiveGroup>>> _loadGroups;
    private CancellationTokenSource? _loadRequest;
    private long _loadGeneration;
    private long _epgImportGeneration;
    private CancellationTokenSource? _epgDownload;
    private long _epgDownloadGeneration;
    private readonly SemaphoreSlim _programmeWrites = new(1, 1);
    private bool _programmeCacheLoaded;
    public IReadOnlyList<string> ImportWarnings { get; private set; } = [];
    private VodBox.Infrastructure.ProgrammeStore ProgrammeCache => new(Path.Combine(_services.DataDir, "programmes.xml"));
    private IReadOnlyList<Programme> _programmes = [];

    // ---- 内置节目表 ----
    private readonly VodBox.Desktop.Services.EpgService _epg;
    private VodBox.Infrastructure.EpgProgrammeIndex? _legacyIndex;
    private bool _epgInitialized;
    private static readonly VodBox.Infrastructure.EpgProgrammeIndex EmptyIndex = VodBox.Infrastructure.EpgProgrammeIndex.CreateEmpty("");
    public string CurrentLineLabel => $"线路 {_lineIndex + 1}";
    public string LiveSourceLabel
    {
        get
        {
            var config = _services.CurrentLiveConfig;
            if (string.IsNullOrWhiteSpace(config)) return "未配置直播源";
            // 本地播放列表显示文件名，方便确认当前用的是哪个文件。
            return VodBox.Infrastructure.LocalMedia.TryResolveFile(config, out var path)
                ? $"本地播放列表：{Path.GetFileName(path)}"
                : "已配置直播源";
        }
    }
    public async Task RefreshEpgSubscriptionAsync(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var address) || address.Scheme is not ("http" or "https"))
            throw new InvalidDataException("节目订阅必须是 HTTP(S) 地址。");
        _epgDownload?.Cancel();
        var generation=++_epgDownloadGeneration;
        using var scope=new CancellationTokenSource();_epgDownload=scope;
        try
        {
            using var http = new VodBox.Infrastructure.DefaultHttp();
            var bytes = await http.GetBoundedAsync(url, 16 * 1024 * 1024,scope.Token);
            scope.Token.ThrowIfCancellationRequested();
            if(generation!=_epgDownloadGeneration)return;
            using var input = new MemoryStream(bytes);
            await ImportXmlTvAsync(input, bytes.Length >= 2 && bytes[0] == 0x1f && bytes[1] == 0x8b,scope.Token);
            scope.Token.ThrowIfCancellationRequested();
            if(generation==_epgDownloadGeneration)
            {
                _services.Prefs.Set(EpgService.ManualUrlKey, url);
                // 手动订阅成功才切到手动数据；失败路径在上面直接抛出，旧缓存不受影响。
                NotifyManualSubscription();
            }
        }
        finally{if(ReferenceEquals(_epgDownload,scope))_epgDownload=null;}
    }

    public async Task ImportXmlTvAsync(Stream stream, bool gzip = false,CancellationToken ct=default)
    {
        await using var decompressed = gzip ? new System.IO.Compression.GZipStream(stream, System.IO.Compression.CompressionMode.Decompress, leaveOpen: true) : null;
        var generation = Interlocked.Increment(ref _epgImportGeneration);
        using var reader = new StreamReader(decompressed ?? stream, leaveOpen: true);
        var buffer = new char[8192];
        var text = new System.Text.StringBuilder();
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(),ct)) > 0)
        {
            if (text.Length + count > 16 * 1024 * 1024) throw new InvalidDataException("节目表超过 16 MiB 字符上限。");
            text.Append(buffer, 0, count);
        }
        var programmes = await Task.Run(() => VodBox.Infrastructure.XmlTvParser.Parse(text.ToString()),ct);
        await _programmeWrites.WaitAsync(ct);
        try
        {
            if (generation != Volatile.Read(ref _epgImportGeneration)||ct.IsCancellationRequested) return;
            var xml=VodBox.Infrastructure.ProgrammeStore.Serialize(programmes);
            ct.ThrowIfCancellationRequested();
            _services.Prefs.Set(EpgService.ManualXmlKey,xml);
            try{await ProgrammeCache.SaveAsync(programmes,ct);}
            catch(Exception error)when(error is IOException or UnauthorizedAccessException){System.Diagnostics.Debug.WriteLine($"[epg cache] {error.Message}");}
        }
        finally { _programmeWrites.Release(); }
        await _main.RunOnUiAsync(() =>
        {
            if (generation != Volatile.Read(ref _epgImportGeneration)||ct.IsCancellationRequested) return;
            _programmeCacheLoaded = true;
            SetProgrammes(programmes);
            _main.StatusMessage = $"已导入 {programmes.Count} 条节目，按频道 tvg-id 匹配";
        });
    }

    public ObservableCollection<LiveGroup> Groups { get; } = [];
    public IReadOnlyList<LiveGroup> GroupOptions { get; private set; } = [];
    private LiveGroup? _allChannelsGroup;

    private void RefreshGroupOptions()
    {
        var selected = SelectedGroup;
        var useAll = selected is null || ReferenceEquals(selected, _allChannelsGroup) || !Groups.Contains(selected);
        var channels = Groups.SelectMany(group => group.Channels).ToArray();
        _allChannelsGroup = new LiveGroup("全部频道", channels, false, channels.Length);
        GroupOptions = new[] { _allChannelsGroup }.Concat(Groups).ToArray();
        OnPropertyChanged(nameof(GroupOptions));
        var next = useAll ? _allChannelsGroup : selected;
        if (ReferenceEquals(SelectedGroup, next)) ApplyFilter();
        else SelectedGroup = next;
    }
    public ObservableCollection<LiveChannel> Favorites { get; } = [];
    public ObservableCollection<LiveChannel> RecentChannels { get; } = [];
    [ObservableProperty] private int _channelTab;
    partial void OnChannelTabChanged(int value) => ApplyFilter();

    public async Task RefreshLibraryTabsAsync()
    {
        var favorites = await _services.Store.GetFavoritesAsync(FavoriteKind.Live);
        var history = await _services.Store.GetHistoryAsync();
        await _main.RunOnUiAsync(() =>
        {
            var channels = Groups.SelectMany(group => group.Channels).ToArray();
            Favorites.Clear();
            foreach (var entry in favorites)
                if (channels.FirstOrDefault(liveChannel => liveChannel.Uris.FirstOrDefault() == entry.MediaId) is { } channel) Favorites.Add(channel);
            RecentChannels.Clear();
            foreach (var entry in history.Where(entry => entry.SourceKey == "live"))
                if (channels.FirstOrDefault(liveChannel => liveChannel.Uris.FirstOrDefault() == entry.MediaId) is { } channel && !RecentChannels.Contains(channel)) RecentChannels.Add(channel);
            ApplyFilter();
        });
    }

    [RelayCommand]
    public async Task ToggleChannelFavorite()
    {
        if (CurrentChannel is not { } channel || channel.Uris.Count == 0) return;
        var current = (await _services.Store.GetFavoritesAsync(FavoriteKind.Live)).Any(entry => entry.MediaId == channel.Uris[0]);
        await _services.Store.SetFavoriteAsync(new FavoriteEntry
        {
            Kind = FavoriteKind.Live, SourceKey = "live", SourceName = "直播", MediaId = channel.Uris[0], Title = channel.Name, Poster = channel.Logo,
        }, !current);
        await RefreshLibraryTabsAsync();
    }

    [ObservableProperty] private LiveGroup? _selectedGroup;
    [ObservableProperty] private LiveChannel? _currentChannel;
    [ObservableProperty] private bool _loading;
    public bool ShowChannelSkeleton => Loading && ChannelCards.Count == 0;
    public bool ShowChannelEmpty => !Loading && ChannelCards.Count == 0;
    partial void OnLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowChannelSkeleton));
        OnPropertyChanged(nameof(ShowChannelEmpty));
    }
    [ObservableProperty] private string _filterText = "";
    [ObservableProperty] private bool _isChannelScrolling;

    /// <summary>频道面板行集合：组头（LiveGroupHeader）+ 频道行（LiveChannel）混排，UI 按类型选模板。</summary>
    public ObservableCollection<object> VisibleChannels { get; } = [];

    /// <summary>与左侧分组/收藏/历史及搜索同步，卡片不包含组头或锁定频道。</summary>
    public ObservableCollection<LiveChannel> ChannelCards { get; } = [];

    public sealed record ChannelCardRow(IReadOnlyList<LiveChannel> Channels);
    public ObservableCollection<ChannelCardRow> ChannelCardRows { get; } = [];
    private int _cardColumns = 1;
    public int CardColumns => _cardColumns;

    public void SetCardViewportWidth(double width)
    {
        if (!double.IsFinite(width) || width <= 0) return;
        var columns = Math.Max(1, (int)(width / 220));
        if (columns == _cardColumns) return;
        _cardColumns = columns;
        OnPropertyChanged(nameof(CardColumns));
        RebuildCardRows();
    }

    private void RebuildCardRows()
    {
        ChannelCardRows.Clear();
        for (var index = 0; index < ChannelCards.Count; index += _cardColumns)
            ChannelCardRows.Add(new ChannelCardRow(Enumerable.Range(index, Math.Min(_cardColumns, ChannelCards.Count - index)).Select(i => ChannelCards[i]).ToArray()));
    }


    /// <summary>EPG 时间轴（当前频道的节目卡片，过去可回看、现在高亮）。S4 前为演示数据。</summary>
    public ObservableCollection<LiveEpgCard> EpgTimeline { get; } = [];

    public LiveViewModel(AppServices services, MainViewModel main) : this(services, main, (_, _) => Task.FromResult(new List<LiveGroup>()))
    {
        _loadGroups = async (url, ct) =>
        {
            using var http = new VodBox.Infrastructure.DefaultHttp();
            var loader = new VodBox.Infrastructure.LiveSources(http);
            var groups = await loader.LoadAsync(url, ct);
            ImportWarnings = loader.Warnings;
            return groups;
        };
    }

    internal LiveViewModel(AppServices services, MainViewModel main, Func<string, CancellationToken, Task<List<LiveGroup>>> loadGroups)
    {
        _services = services;
        _main = main;
        _loadGroups = loadGroups;
        _epg = new VodBox.Desktop.Services.EpgService(Path.Combine(services.DataDir, "epg"), services.Prefs);
        _epg.Changed += OnEpgChanged;
        Groups.CollectionChanged += (_, _) => RefreshGroupOptions();
        RefreshGroupOptions();
        ChannelCards.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(ShowChannelSkeleton));
            OnPropertyChanged(nameof(ShowChannelEmpty));
        };

    }

    // ---- 内置节目表：状态与初始化 ----

    /// <summary>工具栏节目表状态入口文案（正常/异常都短）。手动导入的数据也算用户覆盖，不能谎称「内置节目表」。</summary>
    public string EpgStatusLabel
    {
        get
        {
            if (_programmes.Count > 0)
                return _services.Prefs.GetString(EpgService.ManualUrlKey).Length > 0 ? "手动订阅节目表" : "已导入节目表";
            return _epg.StatusLabel;
        }
    }

    /// <summary>数据覆盖日期（与「最近成功获取」分开，未知显示未知）。</summary>
    public string EpgCoverageLabel
    {
        get
        {
            var end = _epg.CoverageEnd;
            return end is { } value ? $"覆盖至 {value.ToLocalTime():MM-dd HH:mm}" : "覆盖日期未知";
        }
    }

    public string EpgLastSuccessLabel
    {
        get
        {
            var last = _epg.LastSuccessAt;
            return last is { } value ? $"最近成功 {value.ToLocalTime():MM-dd HH:mm}" : "尚未成功获取";
        }
    }

    /// <summary>状态入口的悬停详情：状态 / 最近成功获取 / 数据覆盖，三者分开，未知就是未知。</summary>
    public string EpgStatusDetail => $"{EpgStatusLabel}\n{EpgLastSuccessLabel}\n{EpgCoverageLabel}";

    /// <summary>是否处于手动订阅覆盖（此时内置来源让位）。</summary>
    public bool EpgManualOverride => _epg.IsManualOverride;

    private void OnEpgChanged() => _main.RunOnUiAsync(() =>
    {
        OnPropertyChanged(nameof(EpgStatusLabel));
        OnPropertyChanged(nameof(EpgStatusDetail));
        OnPropertyChanged(nameof(EpgLastSuccessLabel));
        OnPropertyChanged(nameof(EpgCoverageLabel));
        OnPropertyChanged(nameof(EpgManualOverride));
        RefreshProgrammeClock(DateTimeOffset.Now);
    }).GetAwaiter().GetResult();

    /// <summary>首次打开直播：立即读缓存，并发起内置刷新（手动订阅覆盖时不发）。</summary>
    public async Task EnsureBuiltInEpgAsync()
    {
        if (_epgInitialized) { await _epg.EnsureFreshAsync(); return; }
        _epgInitialized = true;
        await _epg.LoadCacheAsync();
        RefreshProgrammeClock(DateTimeOffset.Now);
        await _epg.EnsureFreshAsync();
    }

    /// <summary>恢复/退到前台时调用：距离上次成功超过 2 小时才更新。</summary>
    public Task NotifyForegroundAsync() => _epgInitialized ? _epg.EnsureFreshAsync() : EnsureBuiltInEpgAsync();

    [RelayCommand]
    private async Task RetryEpg()
    {
        _main.StatusMessage = "正在重试节目表…";
        var ok = await _epg.RefreshAsync(force: true);
        _main.StatusMessage = ok ? "节目表已更新" : _epg.StatusLabel;
    }

    /// <summary>定位到设置中的节目表区（源与订阅）。</summary>
    [RelayCommand]
    private void OpenEpgSettings()
    {
        _main.Settings.Section = 0;
        _main.OpenSettings();
    }

    /// <summary>手动订阅成功后由设置调用：切到手动数据，内置索引不再参与展示。</summary>
    public void NotifyManualSubscription()
    {
        _epg.NotifyManualOverrideChanged();
        NotifyEpgStatusChanged();
        RefreshProgrammeClock(DateTimeOffset.Now);
    }

    /// <summary>
    /// 「恢复内置节目表」：清除手动订阅覆盖与手动导入数据，回到内置来源。
    /// 只替换节目数据，不触发播放、切台或换线；旧内置缓存仍然可用，不先清空再等下载。
    /// </summary>
    public async Task RestoreBuiltInEpgAsync(CancellationToken ct = default)
    {
        // 先停掉可能正在进行的手动订阅下载，避免迟到结果把手动数据写回来。
        _epgDownload?.Cancel();
        _epgDownloadGeneration++;
        Interlocked.Increment(ref _epgImportGeneration);
        _services.Prefs.Set(EpgService.ManualUrlKey, "");
        _services.Prefs.Set(EpgService.ManualXmlKey, "");
        try { await ProgrammeCache.SaveAsync([], ct); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { System.Diagnostics.Debug.WriteLine($"[epg cache] 清空失败：{error.Message}"); }
        _programmes = [];
        _legacyIndex = EmptyIndex;
        _programmeCacheLoaded = true;
        _epg.NotifyManualOverrideChanged();
        await _main.RunOnUiAsync(() =>
        {
            foreach (var channel in Groups.SelectMany(group => group.Channels)) channel.EpgNow = channel.EpgNext = "";
            ApplyFilter();
            RefreshProgrammeClock(DateTimeOffset.Now);
        });
        // 回到内置后立即确保有数据：没有缓存就下载，有缓存且未过期则不重复请求。
        _epgInitialized = true;
        await _epg.EnsureFreshAsync(ct);
    }

    partial void OnSelectedGroupChanged(LiveGroup? value)
    {
        ChannelTab = 0;
        if (value is { Locked: true } && !_unlockedGroups.Contains(value.Name)) RequestGroupUnlock(value.Name);
        ApplyFilter();
    }
    partial void OnFilterTextChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        try { ApplyChannelFilter(); }
        finally
        {
            ChannelCards.Clear();
            foreach (var channel in VisibleChannels.OfType<LiveChannel>()) ChannelCards.Add(channel);
            RebuildCardRows();
            // 只刷新可见/候选项的必要文案，不重建卡片集合、不改排序筛选与滚动位置。
            if (EpgEngaged) foreach (var channel in ChannelCards) RefreshChannelEpgLabel(channel, DateTimeOffset.Now);
        }
    }

    /// <summary>EPG 是否已介入展示（有手动数据、内置索引，或内置服务已有加载状态）。</summary>
    private bool EpgEngaged => _programmes.Count > 0 || _epg.HasData || _epgInitialized;

    private void ApplyChannelFilter()
    {
        VisibleChannels.Clear();
        if (ChannelTab != 0)
        {
            var source = ChannelTab == 1 ? Favorites : RecentChannels;
            foreach (var channel in source.Where(channel => !IsLocked(channel)&&(string.IsNullOrWhiteSpace(FilterText) || channel.Name.Contains(FilterText, StringComparison.OrdinalIgnoreCase))))
                VisibleChannels.Add(channel);
            return;
        }
        if (ReferenceEquals(SelectedGroup, _allChannelsGroup) && _allChannelsGroup is not null)
        {
            foreach (var group in Groups)
            {
                if (group.Locked && !_unlockedGroups.Contains(group.Name)) continue;
                foreach (var channel in group.Channels)
                    if (string.IsNullOrWhiteSpace(FilterText) || channel.Name.Contains(FilterText, StringComparison.OrdinalIgnoreCase))
                        VisibleChannels.Add(channel);
            }
            return;
        }
        if (SelectedGroup is { } selected)
        {
            if (selected.Locked && !_unlockedGroups.Contains(selected.Name)) return;
            foreach (var channel in selected.Channels.Where(channel => string.IsNullOrWhiteSpace(FilterText) ||
                         channel.Name.Contains(FilterText, StringComparison.OrdinalIgnoreCase)))
                VisibleChannels.Add(channel);
            return;
        }
        // 面板两级结构：组头（名称 + 计数）+ 频道行；密码分组只显示锁定组头不展开。
        // 筛选按频道名过滤；有筛选词时隐藏空组（含密码组）。
        foreach (var group in Groups)
        {
            var locked=group.Locked&&!_unlockedGroups.Contains(group.Name);
            var matches = locked
                ? []
                : group.Channels.Where(c => string.IsNullOrWhiteSpace(FilterText) ||
                       c.Name.Contains(FilterText, StringComparison.OrdinalIgnoreCase)).ToList();
            if (!string.IsNullOrWhiteSpace(FilterText) && matches.Count == 0) continue;
            // 搜索时暂时展开命中的组，不改变用户保存的折叠状态。
            var collapsed = locked || (string.IsNullOrWhiteSpace(FilterText) && _collapsedGroups.Contains(group.Name));
            VisibleChannels.Add(new LiveGroupHeader(group.Name, group.Count, locked, !collapsed));
            if (!collapsed) foreach (var channel in matches) VisibleChannels.Add(channel);
        }
    }

    /// <summary>频道面板组头行：名称、计数与锁定状态，图标由视图呈现。</summary>
    public sealed record LiveGroupHeader(string Name, int Count, bool Locked, bool Expanded = true);

    public async Task RestoreProgrammeCacheAsync()
    {
        if (_programmeCacheLoaded) return;
        var generation = Volatile.Read(ref _epgImportGeneration);
        try
        {
            var xml=_services.Prefs.GetString(EpgService.ManualXmlKey);
            var programmes = xml.Length>0?VodBox.Infrastructure.XmlTvParser.Parse(xml):await ProgrammeCache.LoadAsync();
            if(xml.Length==0&&programmes.Count>0)_services.Prefs.Set(EpgService.ManualXmlKey,VodBox.Infrastructure.ProgrammeStore.Serialize(programmes));
            await _main.RunOnUiAsync(() =>
            {
                if (generation != Volatile.Read(ref _epgImportGeneration)) return;
                _programmeCacheLoaded = true;
                SetProgrammes(programmes);
            });
        }
        catch (Exception error)
        {
            await _main.RunOnUiAsync(() => _main.StatusMessage = $"节目缓存加载失败：{error.Message}");
        }
    }

    public async Task ReloadProgrammeBackupAsync()
    {
        _programmeCacheLoaded=false;
        Interlocked.Increment(ref _epgImportGeneration);
        await RestoreProgrammeCacheAsync();
    }

    public void CancelPending()
    {
        GroupPassword="";GroupUnlockOpen=false;_unlockedGroups.Clear();++_loadGeneration;Interlocked.Increment(ref _epgImportGeneration);++_epgDownloadGeneration;_epgDownload?.Cancel();_loadRequest?.Cancel();_loadRequest=null;Loading=false;_epg.Cancel();
    }

    public async Task LoadAsync()
    {
        await LoadConfigurationAsync(_services.CurrentLiveConfig, false);
        // 首次打开直播立即读缓存并发起内置刷新；与直播源加载互不阻塞、互不影响播放。
        await EnsureBuiltInEpgAsync();
    }

    public Task<bool> ApplyConfigurationAsync(string url) => LoadConfigurationAsync(url, true);

    private async Task<bool> LoadConfigurationAsync(string? url, bool persistOnSuccess)
    {
        // 代次必须在任何 await 之前同步认领：RestoreProgrammeCacheAsync 会做文件读，
        // 两个并发 LoadAsync 若都在 await 之后才 ++_loadGeneration，先发的旧请求可能拿到更大的代次，
        // 于是迟到的旧结果反而通过代次校验覆盖新结果（Linux CI 上负载不同即偶发失败）。
        var generation = ++_loadGeneration;
        _loadRequest?.Cancel();
        _loadRequest = null;
        await RestoreProgrammeCacheAsync();
        if (generation != _loadGeneration) return false;
        if (string.IsNullOrWhiteSpace(url))
        {
            _loadRequest = null;
            Loading = false;
            Groups.Clear();
            VisibleChannels.Clear();
            ChannelCards.Clear();
            ChannelCardRows.Clear();
            CurrentChannel = null;
            EpgTimeline.Clear();
            OnPropertyChanged(nameof(LiveSourceLabel));
            _main.StatusMessage = "未配置直播源，请到设置中添加 m3u 地址";
            return false;
        }
        using var scope = new CancellationTokenSource();
        _loadRequest = scope;
        Loading = true;
        try
        {
            VodBox.Core.VodBoxLog.Info("live", $"开始加载直播源：{url}");
            var groups = await _loadGroups(url, scope.Token);
            VodBox.Core.VodBoxLog.Event("live", "loaded",
                ("url", url), ("groups", groups.Count.ToString()),
                ("channels", groups.Sum(group => group.Channels.Count).ToString()));
            if (groups.Sum(group => group.Channels.Count) == 0) throw new InvalidDataException("直播源没有可播放频道。");
            scope.Token.ThrowIfCancellationRequested();
            await _main.RunOnUiAsync(() =>
            {
                if (generation != _loadGeneration) return;
                if (persistOnSuccess) _services.CurrentLiveConfig = url;
                var keep = CurrentChannel;
                Groups.Clear();_unlockedGroups.Clear();
                foreach (var group in groups) Groups.Add(group);
                SelectedGroup = _allChannelsGroup;
                ApplyFilter();
                var replacement = keep is null ? null : groups.SelectMany(group => group.Channels)
                    .FirstOrDefault(channel => channel.Name == keep.Name && channel.Uris.FirstOrDefault() == keep.Uris.FirstOrDefault());
                if (replacement is not null && !IsLocked(replacement)) SetCurrent(replacement);
                else { CurrentChannel = null; EpgTimeline.Clear(); }
                OnPropertyChanged(nameof(LiveSourceLabel));
            });
            if (generation != _loadGeneration) return false;
            await RefreshLibraryTabsAsync();
            return generation == _loadGeneration;
        }
        catch (OperationCanceledException) when (scope.IsCancellationRequested) { return false; }
        catch (Exception error)
        {
            await _main.RunOnUiAsync(() =>
            {
                if (generation == _loadGeneration) _main.StatusMessage = $"直播源加载失败：{error.Message}";
            });
            return false;
        }
        finally
        {
            await _main.RunOnUiAsync(() =>
            {
                if (generation != _loadGeneration) return;
                _loadRequest = null;
                Loading = false;
            });
        }
    }

    [RelayCommand]
    public void PlayChannel(LiveChannel channel)
    {
        if(IsLocked(channel))
        {
            VodBox.Core.VodBoxLog.Warn("live", $"频道被密码分组锁定，拒绝播放：{channel.Name}");
            _main.StatusMessage="请先解锁频道分组";return;
        }
        VodBox.Core.VodBoxLog.Event("live", "play-channel",
            ("name", channel.Name), ("group", channel.Group), ("number", channel.Number.ToString()),
            ("lines", channel.Uris.Count.ToString()), ("headers", channel.Headers.Count.ToString()),
            ("uri", channel.Uris.FirstOrDefault()));
        SetCurrent(channel);
        _lineIndex = 0;_failedLines=0;_lastFailedIntent=-1;
        OnPropertyChanged(nameof(CurrentLineLabel));
        var uri = channel.Uris.FirstOrDefault();
        if (uri is null)
        {
            VodBox.Core.VodBoxLog.Error("live", $"频道没有播放地址：{channel.Name}");
            return;
        }
        _main.Player.Play(new PlaybackRequest
        {
            Uri = uri,
            Title = channel.Name,
            Poster = channel.Logo,
            // MediaId 用频道地址，否则所有直播频道会挤进历史表同一行（source_key+media_id 是主键）
            SourceKey = "live",
            SourceName = "直播",
            MediaId = channel.Uris.First(),
            IsLive = true,
            Headers = new Dictionary<string, string>(channel.Headers),
        });
    }

    /// <summary>
    /// 按历史记录里的频道地址续播直播。历史表用 source_key="live" 存直播，
    /// 这个键是合成的、不在内容源注册表里，所以首页/历史点击必须走这里而不是详情页。
    /// </summary>
    public bool TryPlayByUri(string mediaId)
    {
        if (string.IsNullOrEmpty(mediaId)) return false;
        var channel = Groups.SelectMany(group => group.Channels)
            .FirstOrDefault(item => item.Uris.FirstOrDefault() == mediaId);
        if (channel is null)
        {
            // 区分「频道确实不在源里」和「直播源还没加载好」：后者给等待提示，别谎称频道不存在。
            if (Loading) _main.StatusMessage = "直播源正在加载，请稍后再试";
            return false;
        }
        if (IsLocked(channel))
        {
            _main.StatusMessage = "请先解锁频道分组";
            return false;
        }
        PlayChannel(channel);
        return true;
    }

    /// <summary>仅更新选中态与 EPG 时间轴，不触播放（设计数据 / 无 URI 频道复用）。</summary>
    /// <summary>关闭播放后清除频道、收藏、历史和卡片的播放标识。</summary>
    public void ClearPlaybackMarker()
    {
        if (CurrentChannel is { } current) current.IsCurrent = false;
        foreach (var channel in Groups.SelectMany(group => group.Channels)
                     .Concat(Favorites).Concat(RecentChannels).Concat(ChannelCards))
            channel.IsCurrent = false;
        CurrentChannel = null;
        EpgTimeline.Clear();
        _lineIndex = 0;
        _failedLines = 0;
        OnPropertyChanged(nameof(CurrentLineLabel));
    }

    partial void OnCurrentChannelChanged(LiveChannel? value)
    {
        OnPropertyChanged(nameof(HasChannelSummary));
        if (value is null) ClearSummary();
    }

    public void SetCurrent(LiveChannel channel)
    {
        foreach (var item in Groups.SelectMany(group => group.Channels)) item.IsCurrent = false;
        if (!ReferenceEquals(CurrentChannel, channel)) _lineIndex = 0;
        channel.IsCurrent = true;
        CurrentChannel = channel;
        OnPropertyChanged(nameof(CurrentLineLabel));
        RefreshEpgTimeline();
    }

    /// <summary>没有真实节目数据就保持空表；演示节目仅由 DesignData 填充。</summary>
    public void SetProgrammes(IReadOnlyList<Programme> programmes)
    {
        _programmes = programmes.ToArray();
        _legacyIndex = BuildLegacyIndex(_programmes);
        NotifyEpgStatusChanged();
        RefreshProgrammeClock(DateTimeOffset.Now);
    }

    /// <summary>节目表来源/状态变化时刷新工具栏状态入口文案。</summary>
    private void NotifyEpgStatusChanged()
    {
        OnPropertyChanged(nameof(EpgStatusLabel));
        OnPropertyChanged(nameof(EpgStatusDetail));
        OnPropertyChanged(nameof(EpgManualOverride));
    }

    private static VodBox.Infrastructure.EpgProgrammeIndex BuildLegacyIndex(IReadOnlyList<Programme> programmes)
    {
        if (programmes.Count == 0) return EmptyIndex;
        var names = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var group in programmes.GroupBy(p => p.ChannelKey, StringComparer.Ordinal))
        {
            var declared = group.Select(p => p.ChannelName).Where(n => n.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
            names[group.Key] = declared.Length > 0 ? declared : [group.Key];
        }
        return VodBox.Infrastructure.EpgProgrammeIndex.Build(new VodBox.Infrastructure.EpgSourceData("", programmes, names));
    }

    public void RefreshProgrammeClock(DateTimeOffset now) => RefreshEpgTimeline(now);

    private void RefreshEpgTimeline(DateTimeOffset? clock = null)
    {
        var now = clock ?? DateTimeOffset.Now;
        EpgTimeline.Clear();
        if (CurrentChannel is not { } channel)
        {
            OnPropertyChanged(nameof(HasChannelSummary));
            ClearSummary();
            // 没有选中频道时也要推进卡片上的「当前节目」，否则时钟走了文案还停在旧节目。
            foreach (var item in ChannelCards) RefreshChannelEpgLabel(item, now);
            return;
        }
        var schedule = ResolveSchedule(channel, now, out var kind);
        channel.EpgNow = EpgNowLabel(channel, schedule, kind, now, out var next);
        channel.EpgNext = next?.Title ?? "";
        if (schedule is not null)
            foreach (var programme in schedule.Programmes.Where(p => p.End > now.AddHours(-6) && p.Start < now.AddHours(24)))
                EpgTimeline.Add(new LiveEpgCard
                {
                    Title = programme.Title, Start = programme.Start, End = programme.End,
                    IsNow = programme.Start <= now && now < programme.End, IsPast = programme.End <= now,
                });
        UpdateSummary(channel, schedule, now);
        if (ProgrammePanelExpanded) RefreshProgrammeRowStates(now);
        foreach (var item in ChannelCards) if (!ReferenceEquals(item, channel)) RefreshChannelEpgLabel(item, now);
    }

    /// <summary>
    /// 为一个频道解析可信节目表：手动导入/旧缓存优先（用户显式覆盖），否则走内置来源。
    /// 内置部分委托 <see cref="VodBox.Infrastructure.EpgScheduleResolver"/>：按来源命名空间匹配，
    /// 仅当主源没有可信匹配、或主源可信匹配但「当前缺节目」时，才按来源级改用补充源，
    /// 且整条时间表只来自一个来源，绝不把两源拼成看似连续的时间表。
    /// </summary>
    private VodBox.Infrastructure.EpgChannelSchedule? ResolveSchedule(LiveChannel channel, DateTimeOffset now,
        out VodBox.Infrastructure.EpgMatchKind kind)
    {
        kind = VodBox.Infrastructure.EpgMatchKind.None;
        if (_programmes.Count > 0)
        {
            var index = _legacyIndex ?? EmptyIndex;
            kind = VodBox.Infrastructure.EpgChannelMatcher.Resolve(index, channel, out var legacyKey);
            if (kind is VodBox.Infrastructure.EpgMatchKind.ByTvgId or VodBox.Infrastructure.EpgMatchKind.ByUniqueName)
                return index.TryGetSchedule(legacyKey.ChannelId, out var schedule) ? schedule : null;
            return null;
        }
        if (_epg.IsManualOverride || !_epg.HasData) return null;
        return VodBox.Infrastructure.EpgScheduleResolver.Resolve(
            _epg.PrimaryIndex, _epg.SupplementIndex, channel, now, out kind);
    }

    /// <summary>卡片一行文案：当前节目，或区分过的短状态（错误不冒充无匹配，过期不冒充无节目）。</summary>
    private string EpgNowLabel(LiveChannel channel, VodBox.Infrastructure.EpgChannelSchedule? schedule,
        VodBox.Infrastructure.EpgMatchKind kind, DateTimeOffset now, out Programme? next)
    {
        next = null;
        if (schedule is null)
        {
            if (kind == VodBox.Infrastructure.EpgMatchKind.Ambiguous) return "频道匹配待确认";
            // 已可信匹配、但该频道没有任何节目记录：是「无节目」，不是「未匹配」。
            if (kind is VodBox.Infrastructure.EpgMatchKind.ByTvgId or VodBox.Infrastructure.EpgMatchKind.ByUniqueName)
                return "暂无节目安排";
            return NoDataStatus();
        }
        if (schedule.Current(now) is { } current)
        {
            next = schedule.Next(now);
            return current.Title;
        }
        next = schedule.Next(now);
        if (schedule.Programmes.Count == 0) return "暂无节目安排";
        if (schedule.IsExpired(now)) return "节目表已过期";
        return next is null ? "暂无节目安排" : "当前暂无节目";
    }

    /// <summary>无该频道数据时的状态：区分加载中 / 加载失败 / 未匹配（手动覆盖时不谎报内置状态）。</summary>
    private string NoDataStatus()
    {
        if (_programmes.Count > 0) return "未匹配节目表";
        if (_epg.IsManualOverride) return "手动订阅未含该频道";
        return _epg.State switch
        {
            VodBox.Desktop.Services.EpgServiceState.Failed when !_epg.HasData => "节目表加载失败",
            VodBox.Desktop.Services.EpgServiceState.Loading or VodBox.Desktop.Services.EpgServiceState.Updating => "正在加载节目表…",
            _ => "未匹配节目表",
        };
    }

    private void RefreshChannelEpgLabel(LiveChannel channel, DateTimeOffset now)
    {
        if (!EpgEngaged) return;
        var schedule = ResolveSchedule(channel, now, out var kind);
        channel.EpgNow = EpgNowLabel(channel, schedule, kind, now, out var next);
        channel.EpgNext = next?.Title ?? "";
    }

    // ---- 底部选中频道摘要 + 展开的单频道节目表 ----

    public bool HasChannelSummary => CurrentChannel is not null;
    [ObservableProperty] private bool _programmePanelExpanded;
    partial void OnProgrammePanelExpandedChanged(bool value) => OnPropertyChanged(nameof(ProgrammeToggleLabel));

    /// <summary>摘要栏右侧的展开 / 收起按钮文案。</summary>
    public string ProgrammeToggleLabel => ProgrammePanelExpanded ? "收起节目表" : "展开节目表";

    [ObservableProperty] private string _summaryChannelName = "";
    [ObservableProperty] private string _summaryNowTitle = "";
    [ObservableProperty] private string _summaryNowRange = "";
    [ObservableProperty] private string _summaryNextTitle = "";
    [ObservableProperty] private string _summaryNextRange = "";
    [ObservableProperty] private string _programmePanelStatus = "";
    [ObservableProperty] private string _programmePanelDateLabel = "";
    [ObservableProperty] private double _programmeListMaxHeight = 260;

    /// <summary>展开后的单频道节目行（虚拟化绑定；时钟推进只更新状态，不重建列表）。</summary>
    public ObservableCollection<LiveProgrammeRow> ProgrammeRows { get; } = [];
    public ObservableCollection<ProgrammeDateOption> ProgrammeDates { get; } = [];
    [ObservableProperty] private ProgrammeDateOption? _selectedProgrammeDate;
    partial void OnSelectedProgrammeDateChanged(ProgrammeDateOption? value) => RebuildProgrammeRows();

    /// <summary>由视图传入可用内容区高度：节目表上限 45%，并优先保留至少一行频道卡片（194 DIP + 摘要）。</summary>
    public void SetProgrammeViewportHeight(double contentHeight)
    {
        if (!double.IsFinite(contentHeight) || contentHeight <= 0) return;
        var fractionCap = contentHeight * 0.45;
        var reserveForCards = contentHeight - 104 - 194; // 摘要 104 + 一行频道卡片 194
        ProgrammeListMaxHeight = Math.Max(140, Math.Min(fractionCap, reserveForCards));
    }

    [RelayCommand]
    private void ToggleProgrammePanel()
    {
        if (CurrentChannel is null) return;
        ProgrammePanelExpanded = !ProgrammePanelExpanded;
        if (ProgrammePanelExpanded) RefreshProgrammeClock(DateTimeOffset.Now);
    }

    /// <summary>关闭摘要/节目表（频道锁定或清空时同步隐藏）。</summary>
    private void ClearSummary()
    {
        ProgrammePanelExpanded = false;
        ProgrammeRows.Clear();
        ProgrammeDates.Clear();
        SelectedProgrammeDate = null;
        SummaryChannelName = SummaryNowTitle = SummaryNowRange = SummaryNextTitle = SummaryNextRange = "";
        ProgrammePanelStatus = ProgrammePanelDateLabel = "";
    }

    private void UpdateSummary(LiveChannel channel, VodBox.Infrastructure.EpgChannelSchedule? schedule, DateTimeOffset now)
    {
        SummaryChannelName = channel.Name;
        OnPropertyChanged(nameof(HasChannelSummary));
        if (schedule is null)
        {
            SummaryNowTitle = EpgNowLabel(channel, null, ResolveKind(channel), now, out _);
            SummaryNowRange = ""; SummaryNextTitle = ""; SummaryNextRange = "";
            // 展开面板时把「未匹配 / 暂无节目 / 加载中」这类状态显示在节目表区，别留一片空白。
            ProgrammePanelStatus = SummaryNowTitle;
        }
        else
        {
            var current = schedule.Current(now);
            var next = schedule.Next(now);
            SummaryNowTitle = current?.Title ?? (schedule.IsExpired(now) ? "节目表已过期" : "当前暂无节目");
            SummaryNowRange = current is null ? "" : LiveProgrammeRow.FormatRange(current.Start, current.End);
            SummaryNextTitle = next is null ? "暂无后续节目" : next.Title;
            SummaryNextRange = next is null ? "" : $"下一档 {next.Start.ToLocalTime():HH:mm}";
        }
        if (ProgrammePanelExpanded) RebuildProgrammeDates(schedule);
    }

    private VodBox.Infrastructure.EpgMatchKind ResolveKind(LiveChannel channel)
    {
        ResolveSchedule(channel, DateTimeOffset.Now, out var kind);
        return kind;
    }

    private void RebuildProgrammeDates(VodBox.Infrastructure.EpgChannelSchedule? schedule)
    {
        // 只提供源里确实有数据的日期，外加「今日」：既默认定位到今天的当前节目，
        // 又绝不臆造未来七天内容（今日无数据时如实显示「该日期没有节目数据」）。
        var dates = new List<DateOnly>();
        if (schedule is not null)
            dates.AddRange(schedule.Programmes.Select(p => DateOnly.FromDateTime(p.Start.ToLocalTime().DateTime)).Distinct());
        var today = DateOnly.FromDateTime(DateTimeOffset.Now.ToLocalTime().DateTime);
        if (!dates.Contains(today)) dates.Add(today);
        dates.Sort();
        ProgrammeDates.Clear();
        foreach (var date in dates) ProgrammeDates.Add(new ProgrammeDateOption(date, DateLabel(date)));
        var preferred = ProgrammeDates.FirstOrDefault(option => option.Date == today) ?? ProgrammeDates.FirstOrDefault();
        if (!ReferenceEquals(SelectedProgrammeDate, preferred)) SelectedProgrammeDate = preferred;
        else RebuildProgrammeRows();
        ProgrammePanelDateLabel = preferred is null ? "" : preferred.Label;
    }

    private void RebuildProgrammeRows()
    {
        ProgrammeRows.Clear();
        var schedule = CurrentChannel is { } channel ? ResolveSchedule(channel, DateTimeOffset.Now, out _) : null;
        if (schedule is null || SelectedProgrammeDate is null)
        {
            ProgrammePanelStatus = schedule is null ? (CurrentChannel is null ? "" : "暂无节目安排") : "该日期没有节目数据";
            return;
        }
        var now = DateTimeOffset.Now;
        var rows = schedule.Programmes.Where(p => DateOnly.FromDateTime(p.Start.ToLocalTime().DateTime) == SelectedProgrammeDate.Date);
        foreach (var programme in rows)
            ProgrammeRows.Add(new LiveProgrammeRow
            {
                Title = programme.Title, Start = programme.Start, End = programme.End,
                IsNow = programme.Start <= now && now < programme.End, IsPast = programme.End <= now,
            });
        ProgrammePanelStatus = ProgrammeRows.Count == 0 ? "该日期没有节目数据" : "";
    }

    private void RefreshProgrammeRowStates(DateTimeOffset now)
    {
        foreach (var row in ProgrammeRows)
        {
            row.IsNow = row.Start <= now && now < row.End;
            row.IsPast = row.End <= now;
        }
    }

    private static string DateLabel(DateOnly date)
    {
        var today = DateOnly.FromDateTime(DateTimeOffset.Now.ToLocalTime().DateTime);
        if (date == today) return $"今天 {date:MM-dd}";
        if (date == today.AddDays(1)) return $"明天 {date:MM-dd}";
        return date.ToString("MM-dd");
    }

    /// <summary>上一台（循环）：按 VisibleChannels 顺序回退。</summary>
    [RelayCommand]
    public void PrevChannel() => StepChannel(-1);

    /// <summary>下一台（循环）。</summary>
    [RelayCommand]
    public void NextChannel() => StepChannel(1);

    private void StepChannel(int delta)
    {
        var channels = VisibleChannels.OfType<LiveChannel>().ToList();
        if (channels.Count == 0) return;
        var index = CurrentChannel is { } current ? channels.IndexOf(current) : -1;
        var next = index < 0 ? (delta < 0 ? channels.Count - 1 : 0) : (index + delta + channels.Count) % channels.Count;
        PlayChannel(channels[next]);
    }

    /// <summary>切换线路：同频道多 URL 轮换（m3u 分号多地址）。</summary>
    [RelayCommand]
    public void SwitchLine()
    {
        if (CurrentChannel is not { } channel || channel.Uris.Count < 2 || IsLocked(channel)) return;
        _failedLines=0;_lastFailedIntent=-1;
        _lineIndex = (_lineIndex + 1) % channel.Uris.Count;
        OnPropertyChanged(nameof(CurrentLineLabel));
        var uri = channel.Uris[_lineIndex];
        _main.Player.Play(new PlaybackRequest
        {
            Uri = uri,
            Title = channel.Name,
            SourceKey = "live",
            SourceName = "直播",
            MediaId = channel.Uris.First(),
            IsLive = true,
            Headers = new Dictionary<string, string>(channel.Headers),
        });
    }

    /// <summary>回看：EPG 过去节目点击后从头播放（catchup seek 由 S4 接 EPG 接口）。</summary>
    [RelayCommand]
    public void Catchup()
    {
        if (EpgTimeline.LastOrDefault(card => card.IsPast) is { } card) PlayCatchup(card);
        else _main.StatusMessage = "暂无可回看的已播出节目";
    }

    public void PlayCatchup(LiveEpgCard card)
    {
        if (CurrentChannel is not { } channel || IsLocked(channel)) return;
        try
        {
            var programme = new Programme(channel.TvgId ?? channel.Name, card.Title, card.Start, card.End);
            var uri = VodBox.Infrastructure.CatchupResolver.Resolve(channel, programme, DateTimeOffset.Now);
            _main.Player.Play(new PlaybackRequest
            {
                Uri = uri, Title = $"{channel.Name} · {card.Title}", SourceKey = "catchup", SourceName = "回看",
                MediaId = channel.Uris.FirstOrDefault() ?? channel.Name, EpisodeId = card.Start.ToUnixTimeSeconds().ToString(),
                Headers = new Dictionary<string, string>(channel.Headers),
            });
        }
        catch (Exception error) { _main.StatusMessage = $"回看不可用：{error.Message}"; }
    }
}

/// <summary>展开的单频道节目表里的一行。只提供播出信息：过去节目不提供播放、seek 或回看入口。</summary>
public sealed partial class LiveProgrammeRow : ObservableObject
{
    public string Title { get; init; } = "";
    public DateTimeOffset Start { get; init; }
    public DateTimeOffset End { get; init; }
    [ObservableProperty] private bool _isNow;
    [ObservableProperty] private bool _isPast;

    partial void OnIsNowChanged(bool value) => OnPropertyChanged(nameof(StatusLabel));
    partial void OnIsPastChanged(bool value) => OnPropertyChanged(nameof(StatusLabel));

    /// <summary>本地时间范围；跨午夜的节目标注日期，避免与同一天的另一档混淆。</summary>
    public string Range => FormatRange(Start, End);

    /// <summary>「正在播出 / 已结束」文字；未开始的节目留空。</summary>
    public string StatusLabel => IsNow ? "正在播出" : IsPast ? "已结束" : "";

    public static string FormatRange(DateTimeOffset start, DateTimeOffset end)
    {
        var localStart = start.ToLocalTime();
        var localEnd = end.ToLocalTime();
        return localStart.Date == localEnd.Date
            ? $"{localStart:HH:mm} — {localEnd:HH:mm}"
            : $"{localStart:MM-dd HH:mm} — {localEnd:MM-dd HH:mm}";
    }
}

/// <summary>节目表日期下拉项。只列出源里确实有数据的日期，不臆造未来七天。</summary>
public sealed record ProgrammeDateOption(DateOnly Date, string Label);

/// <summary>EPG 时间轴卡片（设计稿 p-live ④）。</summary>
public sealed partial class LiveEpgCard : ObservableObject
{
    public string Title { get; init; } = "";
    public DateTimeOffset Start { get; init; }
    public DateTimeOffset End { get; init; }
    [ObservableProperty] private bool _isNow;
    [ObservableProperty] private bool _isPast;

    public string Range => $"{Start:HH:mm} — {End:HH:mm}{(IsPast ? " · 已播出" : IsNow ? " · 直播" : "")}";
}
