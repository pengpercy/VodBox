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
        Uri=channel.Uris[_lineIndex],Title=channel.Name,SourceKey="live",SourceName="直播",MediaId=channel.Uris[0],IsLive=true,
        Headers=new Dictionary<string,string>(channel.Headers),
    });
    private readonly HashSet<string> _unlockedGroups=new();
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
            if(generation==_epgDownloadGeneration)_services.Prefs.Set("live.epg-url", url);
        }
        finally{if(ReferenceEquals(_epgDownload,scope))_epgDownload=null;}
    }

    public async Task ImportXmlTvAsync(Stream stream, bool gzip = false,CancellationToken ct=default)
    {
        using var decompressed = gzip ? new System.IO.Compression.GZipStream(stream, System.IO.Compression.CompressionMode.Decompress, leaveOpen: true) : null;
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
            _services.Prefs.Set("live.epg-xml",xml);
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
                if (channels.FirstOrDefault(channel => channel.Uris.FirstOrDefault() == entry.MediaId) is { } channel) Favorites.Add(channel);
            RecentChannels.Clear();
            foreach (var entry in history.Where(entry => entry.SourceKey == "live"))
                if (channels.FirstOrDefault(channel => channel.Uris.FirstOrDefault() == entry.MediaId) is { } channel && !RecentChannels.Contains(channel)) RecentChannels.Add(channel);
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
    [ObservableProperty] private string _filterText = "";

    /// <summary>频道面板行集合：组头（LiveGroupHeader）+ 频道行（LiveChannel）混排，UI 按类型选模板。</summary>
    public ObservableCollection<object> VisibleChannels { get; } = [];

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
    }

    partial void OnSelectedGroupChanged(LiveGroup? value) => ApplyFilter();
    partial void OnFilterTextChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        VisibleChannels.Clear();
        if (ChannelTab != 0)
        {
            var source = ChannelTab == 1 ? Favorites : RecentChannels;
            foreach (var channel in source.Where(channel => !IsLocked(channel)&&(string.IsNullOrWhiteSpace(FilterText) || channel.Name.Contains(FilterText, StringComparison.OrdinalIgnoreCase))))
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
            VisibleChannels.Add(new LiveGroupHeader(group.Name, group.Count, locked));
            foreach (var channel in matches) VisibleChannels.Add(channel);
        }
    }

    /// <summary>频道面板组头行：名称、计数与锁定状态，图标由视图呈现。</summary>
    public sealed record LiveGroupHeader(string Name, int Count, bool Locked);

    public async Task RestoreProgrammeCacheAsync()
    {
        if (_programmeCacheLoaded) return;
        var generation = Volatile.Read(ref _epgImportGeneration);
        try
        {
            var xml=_services.Prefs.GetString("live.epg-xml");
            var programmes = xml.Length>0?VodBox.Infrastructure.XmlTvParser.Parse(xml):await ProgrammeCache.LoadAsync();
            if(xml.Length==0&&programmes.Count>0)_services.Prefs.Set("live.epg-xml",VodBox.Infrastructure.ProgrammeStore.Serialize(programmes));
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
        GroupPassword="";GroupUnlockOpen=false;_unlockedGroups.Clear();++_loadGeneration;Interlocked.Increment(ref _epgImportGeneration);++_epgDownloadGeneration;_epgDownload?.Cancel();_loadRequest?.Cancel();_loadRequest=null;Loading=false;
    }

    public Task LoadAsync() => LoadConfigurationAsync(_services.CurrentLiveConfig, false);

    public Task<bool> ApplyConfigurationAsync(string url) => LoadConfigurationAsync(url, true);

    private async Task<bool> LoadConfigurationAsync(string? url, bool persistOnSuccess)
    {
        await RestoreProgrammeCacheAsync();
        _loadRequest?.Cancel();
        var generation = ++_loadGeneration;
        if (string.IsNullOrWhiteSpace(url))
        {
            _loadRequest = null;
            Loading = false;
            Groups.Clear();
            VisibleChannels.Clear();
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
                SelectedGroup = Groups.FirstOrDefault();
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
            // MediaId 用频道地址，否则所有直播频道会挤进历史表同一行（source_key+media_id 是主键）
            SourceKey = "live",
            SourceName = "直播",
            MediaId = channel.Uris.First(),
            IsLive = true,
            Headers = new Dictionary<string, string>(channel.Headers),
        });
    }

    /// <summary>仅更新选中态与 EPG 时间轴，不触播放（设计数据 / 无 URI 频道复用）。</summary>
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
        RefreshProgrammeClock(DateTimeOffset.Now);
    }

    public void RefreshProgrammeClock(DateTimeOffset now) => RefreshEpgTimeline(now);

    private void RefreshEpgTimeline(DateTimeOffset? clock = null)
    {
        EpgTimeline.Clear();
        if (CurrentChannel is not { } channel) return;
        var now = clock ?? DateTimeOffset.Now;
        var matches = _programmes.Where(programme => !string.IsNullOrWhiteSpace(channel.TvgId)
            ? programme.ChannelKey == channel.TvgId
            : programme.ChannelName == channel.Name).OrderBy(programme => programme.Start).ToArray();
        channel.EpgNow = matches.FirstOrDefault(programme => programme.Start <= now && now < programme.End)?.Title ?? "";
        channel.EpgNext = matches.FirstOrDefault(programme => programme.Start > now)?.Title ?? "";
        foreach (var programme in matches.Where(programme => programme.End > now.AddHours(-6) && programme.Start < now.AddHours(24)))
            EpgTimeline.Add(new LiveEpgCard
            {
                Title = programme.Title, Start = programme.Start, End = programme.End,
                IsNow = programme.Start <= now && now < programme.End, IsPast = programme.End <= now,
            });
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
