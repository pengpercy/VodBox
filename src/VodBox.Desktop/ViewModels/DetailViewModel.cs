using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VodBox.Core;
using VodBox.Desktop.Services;
using VodBox.Infrastructure;

namespace VodBox.Desktop.ViewModels;

/// <summary>详情页依赖（可在测试里替换为假实现）。</summary>
public sealed record DetailDependencies(
    Func<string, string, string?, CancellationToken, Task<VodResult>> GetDetail,
    Func<string, string?, string?, CancellationToken, IAsyncEnumerable<VodResult>> FindSources,
    Func<SiteSettings> Settings,
    Func<IReadOnlyList<VodResult>, Action<VodResult, SpeedTestResult>, CancellationToken, Task<IReadOnlyList<(VodResult Item, SpeedTestResult Result, double Score)>>>? Rank = null,
    Func<string, string?, CancellationToken, Task<DoubanSuggestion?>>? Scrape = null)
{
    public static DetailDependencies From(AppServices services) => new(
        services.Catalog.GetDetailAsync,
        services.Catalog.FindSourcesStreamAsync,
        () => services.SourceConfig.Settings,
        (items, progress, ct) => services.SpeedTester.RankAsync(items, TestUrl, progress, ct),
        services.Douban.ScrapeAsync);

    /// <summary>测速用的地址：主线路第二集（LunaTV 同款，避开片头预告），只有一集则用第一集。</summary>
    public static string? TestUrl(VodResult result) => result.Lines.FirstOrDefault() is { } line && line.Episodes.Count > 0
        ? line.Episodes[Math.Min(1, line.Episodes.Count - 1)].Uri : null;
}

/// <summary>
/// 详情 / 播放页（LunaTV play 页）：同一部影片在全部采集源上的结果作为「播放源」，可随时换源；
/// 未指定源时测速优选（分辨率 + 速度 + 延迟），指定了源（续播/从某源打开）则先展示该源再后台补齐其他源。
/// 海报：豆瓣卡片进来用豆瓣海报，采集源缺海报时按片名 + 年份到豆瓣刮削。
/// </summary>
public sealed partial class DetailViewModel : ObservableObject
{
    private readonly DetailDependencies _deps;
    private readonly ILibraryStore _store;
    private readonly MainViewModel _main;
    /// <summary>主窗口的详情页打开时切到详情页；搜索窗口里的详情是独立实例，不动主窗口。</summary>
    private readonly bool _navigateMainWindow;

    /// <summary>播放源（每个采集源上的同名结果）。</summary>
    public ObservableCollection<DetailSource> Sources { get; } = [];
    /// <summary>当前播放源内的线路（vod_play_from 拆分）。</summary>
    public ObservableCollection<PlaybackLine> Lines { get; } = [];

    /// <summary>剧集行集合（每行 10 集，虚拟化网格数据源；切线路时重建）。</summary>
    public ObservableCollection<EpisodeRow> EpisodeRows { get; } = [];

    [ObservableProperty] private MediaDetail? _detail;
    [ObservableProperty] private DetailSource? _currentSource;
    [ObservableProperty] private PlaybackLine? _selectedLine;
    [ObservableProperty] private Episode? _selectedEpisode;
    [ObservableProperty] private bool _loading;
    /// <summary>详情空态/错误态文案（非空时覆盖内容位置，避免整页白屏）。</summary>
    [ObservableProperty] private string _hint = "";
    [ObservableProperty] private bool _isFavorite;
    /// <summary>仍在搜索/测速其他播放源。</summary>
    [ObservableProperty] private bool _sourcesBusy;
    [ObservableProperty] private string _sourcesStatus = "";

    public bool ShowSkeleton => Loading && Detail is null;
    public bool ShowContent => Detail is not null;
    public bool ShowEmpty => !Loading && Detail is null && Hint.Length == 0;
    public bool HasMultipleLines => Lines.Count > 1;

    partial void OnLoadingChanged(bool value) => NotifyDetailState();
    partial void OnDetailChanged(MediaDetail? value) { NotifyDetailState(); OnPropertyChanged(nameof(MetaLine)); }

    /// <summary>标题下的一行元信息：年份 · 地区 · 类型（缺项自动省略）。</summary>
    public string MetaLine => Detail?.Item is not { } item ? ""
        : string.Join(" · ", new[] { item.Year, item.Area, item.TypeName }.Where(part => !string.IsNullOrWhiteSpace(part)).Select(part => part!.Trim()));
    partial void OnHintChanged(string value) => NotifyDetailState();

    private void NotifyDetailState()
    {
        OnPropertyChanged(nameof(ShowSkeleton));
        OnPropertyChanged(nameof(ShowContent));
        OnPropertyChanged(nameof(ShowEmpty));
    }

    [ObservableProperty] private string _sourceName = "";
    [ObservableProperty] private bool _episodesReversed;
    public string EpisodeCountLabel => $"共 {SelectedLine?.Episodes.Count ?? 0} 集";
    public string SourceCountLabel => Sources.Count == 0 ? "" : $"{Sources.Count} 个播放源";
    public string FavoriteLabel => IsFavorite ? "已收藏" : "收藏";
    public string PlayLabel => SelectedEpisode is not { } episode ? "播放"
        : (_resumePositionMs > 0 && MediaDetail.ResumePositionApplies(_resumeLineId, _resumeEpisodeId, SelectedLine?.Id ?? "", episode.Id)
            ? $"续播 {episode.Title}" : $"播放 {episode.Title}");
    partial void OnIsFavoriteChanged(bool value) => OnPropertyChanged(nameof(FavoriteLabel));
    partial void OnSelectedEpisodeChanged(Episode? value) { OnPropertyChanged(nameof(PlayLabel)); MarkCurrentEpisode(); }
    partial void OnEpisodesReversedChanged(bool value) => RebuildEpisodeRows();
    [RelayCommand] private void ToggleEpisodeOrder() => EpisodesReversed = !EpisodesReversed;

    private CancellationTokenSource? _request;
    private long _generation;
    private IReadOnlySet<string> _watched = new HashSet<string>();
    private string _resumeLineId = "";
    private string _resumeEpisodeId = "";
    private long _resumePositionMs;
    /// <summary>用户手动选过源：后台测速结束后不再自动切换。</summary>
    private bool _userPickedSource;
    // 打开时的影片身份（换源搜索用）。
    private string _title = "";
    private string? _year;
    private string? _kind;
    private string? _doubanPoster;
    private string? _doubanId;
    private HistoryEntry? _resumeEntry;

    public DetailViewModel(AppServices services, MainViewModel main) : this(DetailDependencies.From(services), services.Store, main) { }

    internal DetailViewModel(DetailDependencies deps, ILibraryStore store, MainViewModel main, bool navigateMainWindow = true)
    {
        _deps = deps;
        _store = store;
        _main = main;
        _navigateMainWindow = navigateMainWindow;
        Sources.CollectionChanged += (_, _) => OnPropertyChanged(nameof(SourceCountLabel));
        Lines.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasMultipleLines));
    }

    partial void OnSelectedLineChanged(PlaybackLine? value)
    {
        OnPropertyChanged(nameof(EpisodeCountLabel));
        RebuildEpisodeRows();
        SelectedEpisode = value?.Episodes.FirstOrDefault();
        _ = RefreshWatchedAsync();
    }

    /// <summary>剧集格最小宽度与间距（与 DetailView 的格子一致）。</summary>
    public const double EpisodeCellMinWidth = 96, EpisodeCellGap = 8;
    private int _episodeColumns = 8;
    public int EpisodeColumns => _episodeColumns;

    /// <summary>剧集区宽度变化：按最小格宽算列数，列数变了才重排。</summary>
    public void SetEpisodeWidth(double width)
    {
        if (width <= 0) return;
        var columns = Math.Clamp((int)Math.Floor((width + EpisodeCellGap) / (EpisodeCellMinWidth + EpisodeCellGap)), 2, 16);
        if (columns == _episodeColumns) return;
        _episodeColumns = columns;
        OnPropertyChanged(nameof(EpisodeColumns));
        RebuildEpisodeRows();
    }

    private void RebuildEpisodeRows()
    {
        EpisodeRows.Clear();
        if (SelectedLine is null) return;
        var episodes = (EpisodesReversed ? SelectedLine.Episodes.Reverse() : SelectedLine.Episodes).ToList();
        for (var i = 0; i < episodes.Count; i += _episodeColumns)
            EpisodeRows.Add(new EpisodeRow(episodes.Skip(i).Take(_episodeColumns).ToList(), _watched, _episodeColumns, SelectedEpisode?.Id));
    }

    private void MarkCurrentEpisode()
    {
        var id = SelectedEpisode?.Id;
        foreach (var row in EpisodeRows) foreach (var item in row.Items) item.IsCurrent = item.Episode.Id == id;
    }

    private string CurrentKey => CurrentSource?.Result.SourceKey ?? "";
    private string CurrentId => CurrentSource?.Result.Id ?? "";

    public void RefreshWatchedFor(string sourceKey, string mediaId, string lineId)
    {
        if (CurrentKey == sourceKey && CurrentId == mediaId && SelectedLine?.Id == lineId) _ = RefreshWatchedAsync();
    }

    private async Task RefreshWatchedAsync()
    {
        var generation = _generation; var line = SelectedLine?.Id; var media = CurrentId; var key = CurrentKey;
        _watched = new HashSet<string>(); RebuildEpisodeRows();
        if (line is null || media.Length == 0 || key.Length == 0) return;
        try
        {
            var watched = await _store.GetWatchedEpisodesAsync(key, media, line);
            await _main.RunOnUiAsync(() => { if (generation != _generation || SelectedLine?.Id != line || CurrentId != media) return; _watched = watched; RebuildEpisodeRows(); });
        }
        catch (Exception error) { System.Diagnostics.Debug.WriteLine(error.Message); }
    }

    // ---------- 入口 ----------

    /// <summary>从卡片进入：豆瓣卡片按片名聚合搜索全部源；采集源卡片先展示该源，再后台补齐其他源。</summary>
    public void Open(string sourceKey, MediaItem item) => _ = OpenAsync(sourceKey, item);

    public Task OpenAsync(string sourceKey, MediaItem item)
    {
        var douban = sourceKey == MediaItem.DoubanSourceKey;
        return LoadAsync(new OpenRequest(item.Title, item.Year, item.Kind,
            douban ? null : (sourceKey, item.Id), null, null,
            douban && !string.IsNullOrWhiteSpace(item.Poster) ? item.Poster : null, item.DoubanId));
    }

    /// <summary>从聚合搜索卡片进入：整组结果即全部播放源，不再重复搜索。</summary>
    public void OpenGroup(IReadOnlyList<VodResult> group, VodResult? preferred = null) => _ = OpenGroupAsync(group, preferred);

    /// <param name="preferred">用户点的是组内某个具体源：先展示它，测速后也不自动切走。</param>
    public Task OpenGroupAsync(IReadOnlyList<VodResult> group, VodResult? preferred = null)
    {
        if (group.Count == 0) return Task.CompletedTask;
        var ordered = preferred is null ? group : [preferred, .. group.Where(result => !ReferenceEquals(result, preferred))];
        var first = ordered[0];
        return LoadAsync(new OpenRequest(first.Title, first.Year, first.IsMovie ? "movie" : "tv", null, ordered, null, null, SearchAggregation.MajorityDoubanId(group))
            { PinFirst = preferred is not null });
    }

    /// <summary>从历史记录续播。</summary>
    public void Resume(HistoryEntry entry) => _ = ResumeAsync(entry);

    public Task ResumeAsync(HistoryEntry entry) =>
        LoadAsync(new OpenRequest(entry.Title, null, null, (entry.SourceKey, entry.MediaId), null, entry, entry.Poster, null));

    private sealed record OpenRequest(string Title, string? Year, string? Kind, (string Key, string Id)? Preferred,
        IReadOnlyList<VodResult>? Known, HistoryEntry? Resume, string? Poster, string? DoubanId)
    {
        public bool PinFirst { get; init; }
    }

    public void CancelPending()
    {
        ++_generation;
        _request?.Cancel();
        _request = null;
        Loading = false;
        SourcesBusy = false;
    }

    private async Task LoadAsync(OpenRequest open)
    {
        CancellationTokenSource? scope = null;
        long generation = 0;
        await _main.RunOnUiAsync(() =>
        {
            CancelPending();
            generation = _generation;
            Detail = null; CurrentSource = null;
            Sources.Clear(); Lines.Clear();
            SelectedLine = null; SelectedEpisode = null;
            IsFavorite = false; Hint = ""; SourcesStatus = ""; SourceName = "";
            _userPickedSource = open.PinFirst;
            _title = open.Title; _year = open.Year is { Length: 4 } ? open.Year : null; _kind = open.Kind;
            _doubanPoster = open.Poster; _doubanId = open.DoubanId; _resumeEntry = open.Resume;
            ApplyResume(null);
            scope = new CancellationTokenSource();
            _request = scope;
            Loading = true;
            if (_navigateMainWindow) _main.Navigate(AppPage.Detail);
        });
        var ct = scope!.Token;
        try
        {
            if (open.Preferred is { } preferred)
            {
                // 指定源：先拿该源详情立即展示，再后台搜索其他源供换源。
                var result = await _deps.GetDetail(preferred.Key, preferred.Id, open.Title, ct);
                ct.ThrowIfCancellationRequested();
                _year ??= result.Year is { Length: 4 } ? result.Year : null;
                _kind ??= result.IsMovie ? "movie" : "tv";
                DetailSource? added = null;
                await _main.RunOnUiAsync(() => { if (generation == _generation) added = AddSource(result); });
                if (added is not null) await SelectSourceAsync(added, generation, userAction: false, ct);
                await _main.RunOnUiAsync(() => { if (generation == _generation) Loading = false; });
                await CollectOtherSourcesAsync(generation, preferBest: false, ct);
            }
            else if (open.Known is { Count: > 0 } known)
            {
                DetailSource? first = null;
                await _main.RunOnUiAsync(() =>
                {
                    if (generation != _generation) return;
                    foreach (var result in known) AddSource(result);
                    first = Sources[0];
                });
                if (first is not null) await SelectSourceAsync(first, generation, userAction: false, ct);
                await _main.RunOnUiAsync(() => { if (generation == _generation) Loading = false; });
                if (_deps.Settings().PreferBestSource && known.Count > 1) await PreferBestAsync(generation, ct);
            }
            else
            {
                await CollectOtherSourcesAsync(generation, preferBest: _deps.Settings().PreferBestSource, ct);
                await _main.RunOnUiAsync(() =>
                {
                    if (generation != _generation) return;
                    if (Sources.Count == 0) Hint = $"没有在已启用的采集源中找到「{_title}」，可以到设置里添加更多源后再试";
                });
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception error)
        {
            await _main.RunOnUiAsync(() =>
            {
                if (generation != _generation || ct.IsCancellationRequested) return;
                if (Detail is null)
                {
                    Hint = $"详情加载失败：{error.Message}";
                    _main.StatusMessage = Hint;
                }
                else SourcesStatus = $"换源搜索失败：{error.Message}";
            });
        }
        finally
        {
            await _main.RunOnUiAsync(() =>
            {
                if (generation != _generation) return;
                _request = null;
                Loading = false;
                SourcesBusy = false;
            });
            scope.Dispose();
        }
    }

    /// <summary>
    /// 全源搜索同名影片并逐个加入播放源；第一个到达的源立即展示。全部到齐后按需测速优选。
    /// </summary>
    private async Task CollectOtherSourcesAsync(long generation, bool preferBest, CancellationToken ct)
    {
        await _main.RunOnUiAsync(() => { if (generation == _generation) { SourcesBusy = true; SourcesStatus = "正在搜索其他播放源…"; } });
        await foreach (var result in _deps.FindSources(_title, _year, _kind, ct))
        {
            DetailSource? select = null;
            await _main.RunOnUiAsync(() =>
            {
                if (generation != _generation) return;
                if (Sources.Any(source => source.Result.SourceKey == result.SourceKey && source.Result.Id == result.Id)) return;
                var source = AddSource(result);
                SourcesStatus = $"已找到 {Sources.Count} 个播放源，继续搜索…";
                if (CurrentSource is null) { select = source; Loading = false; }
            });
            if (select is not null) await SelectSourceAsync(select, generation, userAction: false, ct);
        }
        ct.ThrowIfCancellationRequested();
        await _main.RunOnUiAsync(() => { if (generation == _generation) SourcesStatus = Sources.Count > 0 ? $"共 {Sources.Count} 个播放源" : ""; });
        if (preferBest && Sources.Count > 1) await PreferBestAsync(generation, ct);
    }

    /// <summary>测速全部播放源（最多 4 路并发），按评分重排；用户没手动选过源且还没开始播放时切到最佳源。</summary>
    private async Task PreferBestAsync(long generation, CancellationToken ct)
    {
        if (_deps.Rank is null) return;
        var snapshot = Sources.Select(source => source.Result).ToArray();
        await _main.RunOnUiAsync(() =>
        {
            if (generation != _generation) return;
            SourcesBusy = true; SourcesStatus = $"正在测速 {snapshot.Length} 个播放源…";
            foreach (var source in Sources) source.SpeedLabel = "测速中…";
        });
        var ranked = await _deps.Rank(snapshot, (result, speed) => _main.RunOnUi(() =>
        {
            if (generation != _generation) return;
            if (Sources.FirstOrDefault(source => ReferenceEquals(source.Result, result)) is { } source) source.Apply(speed);
        }), ct);
        ct.ThrowIfCancellationRequested();
        await _main.RunOnUiAsync(() =>
        {
            if (generation != _generation) return;
            var order = ranked.Select(entry => entry.Item).ToList();
            var sorted = Sources.OrderBy(source => order.IndexOf(source.Result) is var index && index < 0 ? int.MaxValue : index).ToArray();
            Sources.Clear();
            foreach (var source in sorted) Sources.Add(source);
            SourcesStatus = $"共 {Sources.Count} 个播放源 · 已按测速排序";
            var best = ranked.FirstOrDefault(entry => entry.Score >= 0).Item;
            if (best is not null && !_userPickedSource && !ReferenceEquals(CurrentSource?.Result, best)
                && Sources.FirstOrDefault(source => ReferenceEquals(source.Result, best)) is { } bestSource)
                _ = SelectSourceAsync(bestSource, generation, userAction: false, ct);
        });
    }

    private DetailSource AddSource(VodResult result)
    {
        var source = new DetailSource(result);
        Sources.Add(source);
        return source;
    }

    /// <summary>界面换源。</summary>
    [RelayCommand]
    private Task PickSource(DetailSource source) => SelectSourceAsync(source, _generation, userAction: true, _request?.Token ?? CancellationToken.None);

    private async Task SelectSourceAsync(DetailSource source, long generation, bool userAction, CancellationToken ct)
    {
        var applied = false;
        await _main.RunOnUiAsync(() => applied = ApplySource(source, generation, userAction));
        if (!applied) return;
        var result = source.Result;
        try
        {
            var key = result.SourceKey; var id = result.Id;
            var favorite = await _store.IsFavoriteAsync(key, id, ct);
            ct.ThrowIfCancellationRequested();
            var history = _resumeEntry is { } resume && resume.SourceKey == key && resume.MediaId == id ? resume : await _store.FindHistoryAsync(key, id, ct);
            ct.ThrowIfCancellationRequested();
            await _main.RunOnUiAsync(() =>
            {
                if (generation != _generation || !ReferenceEquals(CurrentSource, source) || ct.IsCancellationRequested) return;
                IsFavorite = favorite;
                if (history is not null) ApplyResume(history);
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception error) { VodBoxLog.Warn("detail", $"读取收藏/历史失败：{error.Message}"); }
    }

    /// <summary>在 UI 线程上切换当前播放源（详情、线路、选集）；代次已过期返回 false。</summary>
    private bool ApplySource(DetailSource source, long generation, bool userAction)
    {
        if (generation != _generation) return false;
        if (userAction) _userPickedSource = true;
        var previousLine = SelectedLine?.Name;
        var previousEpisodeIndex = SelectedLine is { } line && SelectedEpisode is { } episode ? IndexOf(line, episode) : -1;
        foreach (var item in Sources) item.IsCurrent = ReferenceEquals(item, source);
        CurrentSource = source;
        var result = source.Result;
        SourceName = result.SourceName;
        var poster = !string.IsNullOrWhiteSpace(_doubanPoster) ? _doubanPoster : result.Poster;
        Detail = new MediaDetail
        {
            Item = SearchAggregation.ToItem(result) with { Poster = poster, DoubanId = _doubanId ?? result.DoubanId },
            Description = result.Description,
            Director = result.Director,
            Actor = result.Actor,
            Lines = result.Lines,
        };
        Lines.Clear();
        foreach (var item in result.Lines) Lines.Add(item);
        SelectedLine = Lines.FirstOrDefault(item => item.Name == previousLine) ?? Lines.FirstOrDefault();
        // 换源时保持当前集序号（第 N 集换到另一个源的第 N 集）。
        if (previousEpisodeIndex >= 0 && SelectedLine is { } selected && previousEpisodeIndex < selected.Episodes.Count)
            SelectedEpisode = selected.Episodes[previousEpisodeIndex];
        if (string.IsNullOrWhiteSpace(poster)) _ = ScrapePosterAsync(generation, result);
        IsFavorite = false;
        return true;
    }

    private static int IndexOf(PlaybackLine line, Episode episode)
    {
        for (var i = 0; i < line.Episodes.Count; i++) if (line.Episodes[i].Id == episode.Id) return i;
        return -1;
    }

    /// <summary>海报刮削：采集源与豆瓣卡片都没有海报时，按片名 + 年份到豆瓣联想取海报。</summary>
    private async Task ScrapePosterAsync(long generation, VodResult result)
    {
        if (_deps.Scrape is null) return;
        try
        {
            var match = await _deps.Scrape(result.Title, result.Year is { Length: 4 } year ? year : _year, CancellationToken.None);
            if (match is null || match.Poster.Length == 0) return;
            await _main.RunOnUiAsync(() =>
            {
                if (generation != _generation || Detail is null || !ReferenceEquals(CurrentSource?.Result, result)) return;
                _doubanPoster = match.Poster;
                _doubanId ??= match.Id;
                Detail = Detail with { Item = Detail.Item with { Poster = match.Poster, DoubanId = _doubanId } };
            });
        }
        catch (Exception error) when (error is not OperationCanceledException) { VodBoxLog.Warn("detail", $"海报刮削失败：{error.Message}"); }
    }

    /// <summary>重新测速全部播放源（界面按钮）。</summary>
    [RelayCommand]
    private async Task RetestSources()
    {
        if (SourcesBusy || Sources.Count == 0 || _request is not null) return;
        using var scope = new CancellationTokenSource();
        _request = scope;
        var generation = _generation;
        try { await PreferBestAsync(generation, scope.Token); }
        catch (OperationCanceledException) { }
        finally
        {
            await _main.RunOnUiAsync(() => { if (generation == _generation) { SourcesBusy = false; if (ReferenceEquals(_request, scope)) _request = null; } });
        }
    }

    private void ApplyResume(HistoryEntry? history)
    {
        _resumeLineId = history?.LineId ?? "";
        _resumeEpisodeId = history?.EpisodeId ?? "";
        _resumePositionMs = history is null ? 0 : ResumePolicy.Position(history.PositionMs, history.DurationMs);
        OnPropertyChanged(nameof(PlayLabel));
        if (history is null || Detail is null) return;
        SelectedLine = Detail.FindLine(history.LineId);
        SelectedEpisode = Detail.FindEpisode(SelectedLine, history.EpisodeId);
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task PlayAsync()
    {
        if (Detail is null || CurrentSource is null || SelectedLine is null || SelectedEpisode is null)
        {
            _main.StatusMessage = "没有可播放的选集";
            return;
        }
        var resumeMs = MediaDetail.ResumePositionApplies(_resumeLineId, _resumeEpisodeId, SelectedLine.Id, SelectedEpisode.Id) ? _resumePositionMs : 0;
        var detail = Detail;
        var line = SelectedLine;
        var result = CurrentSource.Result;
        var blockAds = _deps.Settings().BlockAds;
        var selectedId = SelectedEpisode.Id;
        var entries = line.Episodes.Select(episode => new PlaylistEntry(episode.Id, episode.Title, _ =>
        {
            if (episode.Uri is null) throw new InvalidOperationException("没有可播放的地址");
            var hls = Uri.TryCreate(episode.Uri, UriKind.Absolute, out var address) && address.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase);
            return Task.FromResult(new PlaybackRequest
            {
                Uri = episode.Uri, Title = detail.Item.Title, SourceKey = result.SourceKey, SourceName = result.SourceName,
                MediaId = result.Id, LineId = line.Id, EpisodeId = episode.Id, Poster = detail.Item.Poster,
                Remarks = episode.Title, FilterAds = blockAds && hls,
            });
        })).ToArray();
        var index = Array.FindIndex(entries, entry => entry.Id == selectedId);
        await _main.Player.PlayResolvedAsync(async ct =>
        {
            var request = await entries[index].Resolve(ct);
            return request with { StartPositionMs = resumeMs };
        }, entries, index);
    }


    /// <summary>「全站搜索」：用当前片名发起聚合搜索，交给独立搜索窗口。</summary>
    [RelayCommand]
    private void SearchOtherSources()
    {
        if (Detail is null) return;
        _main.RequestSearch(Detail.Item.Title);
    }

    [RelayCommand]
    private async Task ToggleFavorite()
    {
        if (Detail is null || CurrentSource is null) return;
        var generation = _generation;
        var previous = IsFavorite;
        var target = !previous;
        var result = CurrentSource.Result;
        var entry = new FavoriteEntry
        {
            Kind = FavoriteKind.Vod, SourceKey = result.SourceKey, SourceName = result.SourceName,
            MediaId = result.Id, Title = Detail.Item.Title, Poster = Detail.Item.Poster, Remarks = Detail.Item.Remarks,
        };
        IsFavorite = target;
        try { await _store.SetFavoriteAsync(entry, target); }
        catch (Exception error)
        {
            await _main.RunOnUiAsync(() =>
            {
                if (generation != _generation) return;
                IsFavorite = previous;
                _main.StatusMessage = $"收藏保存失败：{error.Message}";
            });
        }
    }
}

/// <summary>详情页的一个播放源。</summary>
public sealed partial class DetailSource(VodResult result) : ObservableObject
{
    public VodResult Result { get; } = result;
    public string Name => Result.SourceName;
    public string EpisodeLabel => Result.EpisodeCount > 1 ? $"{Result.EpisodeCount} 集" : Result.Remarks ?? "";
    [ObservableProperty] private bool _isCurrent;
    [ObservableProperty] private string _speedLabel = "";
    [ObservableProperty] private bool _failed;

    public void Apply(SpeedTestResult result)
    {
        SpeedLabel = result.Summary;
        Failed = result.Failed;
    }
}

/// <summary>剧集网格的一行：列数随可用宽度变化，最后一行不足时按同样列宽左对齐。</summary>
public sealed class EpisodeRow
{
    public IReadOnlyList<EpisodePresentation> Items { get; }
    public int Columns { get; }
    public EpisodeRow(IReadOnlyList<Episode> items, IReadOnlySet<string>? watched = null, int columns = 10, string? currentId = null)
    {
        Columns = Math.Max(1, columns);
        Items = items.Select(episode => new EpisodePresentation(episode, watched?.Contains(episode.Id) ?? false) { IsCurrent = episode.Id == currentId }).ToArray();
    }
}

public sealed partial class EpisodePresentation(Episode episode, bool isWatched) : ObservableObject
{
    public Episode Episode { get; } = episode;
    public bool IsWatched { get; } = isWatched;
    /// <summary>当前选中（或续播）的这一集。</summary>
    [ObservableProperty] private bool _isCurrent;
    public string Title => Episode.Title;
    public string Label => Episode.Title;
    public string Tip => IsWatched ? Episode.Title + "（已看）" : Episode.Title;
}
