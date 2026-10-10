using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VodBox.Core;
using VodBox.Desktop.Services;

namespace VodBox.Desktop.ViewModels;

/// <summary>详情：元信息 + 线路 + 选集 + 播放入口。</summary>
public sealed partial class DetailViewModel : ObservableObject
{
    private readonly Func<string, IContentSource?> _getSource;
    private readonly ILibraryStore _store;
    private readonly MainViewModel _main;

    public ObservableCollection<PlaybackLine> Lines { get; } = [];

    /// <summary>剧集行集合（每行 10 集，虚拟化网格数据源；切线路时重建）。</summary>
    public ObservableCollection<EpisodeRow> EpisodeRows { get; } = [];

    [ObservableProperty] private MediaDetail? _detail;
    [ObservableProperty] private PlaybackLine? _selectedLine;
    [ObservableProperty] private Episode? _selectedEpisode;
    [ObservableProperty] private bool _loading;
    /// <summary>详情空态/错误态文案（非空时覆盖内容位置，避免整页白屏）。</summary>
    [ObservableProperty] private string _hint = "";
    [ObservableProperty] private bool _isFavorite;

    /// <summary>加载中且尚无详情 → 整页骨架（首屏不留白）。</summary>
    public bool ShowSkeleton => Loading && Detail is null;
    /// <summary>已拿到详情 → 显示真实内容（线路 + 选集）。</summary>
    public bool ShowContent => Detail is not null;
    /// <summary>加载结束仍无详情且无失败文案 → 占位空态（失败时由 <see cref="Hint"/> 承担）。</summary>
    public bool ShowEmpty => !Loading && Detail is null && Hint.Length == 0;

    partial void OnLoadingChanged(bool value) => NotifyDetailState();
    partial void OnDetailChanged(MediaDetail? value) => NotifyDetailState();
    partial void OnHintChanged(string value) => NotifyDetailState();

    private void NotifyDetailState()
    {
        OnPropertyChanged(nameof(ShowSkeleton));
        OnPropertyChanged(nameof(ShowContent));
        OnPropertyChanged(nameof(ShowEmpty));
    }
    private string _sourceKey = "";
    [ObservableProperty] private string _sourceName = "";
    [ObservableProperty] private bool _episodesReversed;
    public string EpisodeCountLabel => $"共 {SelectedLine?.Episodes.Count ?? 0} 集";
    public string FavoriteLabel => IsFavorite ? "已收藏" : "收藏";
    public string PlayLabel => SelectedEpisode is not { } episode ? "播放"
        : (_resumePositionMs > 0 && MediaDetail.ResumePositionApplies(_resumeLineId, _resumeEpisodeId, SelectedLine?.Id ?? "", episode.Id)
            ? $"续播 {episode.Title}" : $"播放 {episode.Title}");
    partial void OnIsFavoriteChanged(bool value) => OnPropertyChanged(nameof(FavoriteLabel));
    partial void OnSelectedEpisodeChanged(Episode? value) => OnPropertyChanged(nameof(PlayLabel));
    partial void OnEpisodesReversedChanged(bool value) => RebuildEpisodeRows();
    [RelayCommand] private void ToggleEpisodeOrder() => EpisodesReversed = !EpisodesReversed;
    private CancellationTokenSource? _request;
    private long _generation;
    private IReadOnlySet<string> _watched = new HashSet<string>();
    // 续播上下文：上次看的是哪条线路的哪一集、看到哪；只有选集对得上才复用位置
    private string _resumeLineId = "";
    private string _resumeEpisodeId = "";
    private long _resumePositionMs;

    public DetailViewModel(AppServices services, MainViewModel main) : this(services.Registry.Get, services.Store, main)
    {
    }

    internal DetailViewModel(Func<string, IContentSource?> getSource, ILibraryStore store, MainViewModel main)
    {
        _getSource = getSource;
        _store = store;
        _main = main;
    }

    partial void OnSelectedLineChanged(PlaybackLine? value)
    {
        OnPropertyChanged(nameof(EpisodeCountLabel));
        RebuildEpisodeRows();
        SelectedEpisode = value?.Episodes.FirstOrDefault();
        _ = RefreshWatchedAsync();
    }

    /// <summary>当前线路剧集按 10 集/行分块（虚拟化网格）。</summary>
    private void RebuildEpisodeRows()
    {
        EpisodeRows.Clear();
        if (SelectedLine is null) return;
        var episodes = (EpisodesReversed ? SelectedLine.Episodes.Reverse() : SelectedLine.Episodes).ToList();
        for (var i = 0; i < episodes.Count; i += 10)
            EpisodeRows.Add(new EpisodeRow(episodes.Skip(i).Take(10).ToList(), _watched));
    }

    public void RefreshWatchedFor(string sourceKey,string mediaId,string lineId)
    {
        if(_sourceKey==sourceKey&&Detail?.Item.Id==mediaId&&SelectedLine?.Id==lineId)_=RefreshWatchedAsync();
    }

    private async Task RefreshWatchedAsync()
    {
        var generation=_generation;var line=SelectedLine?.Id;var media=Detail?.Item.Id;
        _watched=new HashSet<string>();RebuildEpisodeRows();
        if(line is null||media is null||_sourceKey.Length==0)return;
        try
        {
            var watched=await _store.GetWatchedEpisodesAsync(_sourceKey,media,line);
            await _main.RunOnUiAsync(()=>{if(generation!=_generation||SelectedLine?.Id!=line||Detail?.Item.Id!=media)return;_watched=watched;RebuildEpisodeRows();});
        }
        catch(Exception error){System.Diagnostics.Debug.WriteLine(error.Message);}
    }

    /// <summary>从卡片进入详情。</summary>
    public void Open(string sourceKey, MediaItem item) => _ = OpenAsync(sourceKey, item);

    public Task OpenAsync(string sourceKey, MediaItem item) => LoadAsync(sourceKey, item.Id, null);

    /// <summary>从历史记录续播。</summary>
    public void Resume(HistoryEntry entry) => _ = ResumeAsync(entry);

    public Task ResumeAsync(HistoryEntry entry) => LoadAsync(entry.SourceKey, entry.MediaId, entry);

    /// <summary>离开详情或开始新请求时取消旧请求；代次门控也拦截忽略取消的源/存储。</summary>
    public void CancelPending()
    {
        ++_generation;
        _request?.Cancel();
        _request = null;
        Loading = false;
    }

    private async Task LoadAsync(string sourceKey, string mediaId, HistoryEntry? resume)
    {
        CancellationTokenSource? scope = null;
        IContentSource? source = null;
        long generation = 0;
        await _main.RunOnUiAsync(() =>
        {
            CancelPending();
            generation = _generation;
            Detail = null;
            Lines.Clear();
            SelectedLine = null;
            SelectedEpisode = null;
            IsFavorite = false;
            Hint = "";
            ApplyResume(null);
            _sourceKey = "";
            SourceName = "";
            source = _getSource(sourceKey);
            if (source is null)
            {
                _main.StatusMessage = $"站点 {resume?.SourceName ?? sourceKey} 在当前配置中不可用";
                Hint = $"站点 {resume?.SourceName ?? sourceKey} 在当前配置中不可用";
                return;
            }
            scope = new CancellationTokenSource();
            _request = scope;
            Loading = true;
            _main.Navigate(AppPage.Detail);
        });
        if (scope is null || source is null) return;
        var ct = scope.Token;
        try
        {
            var detail = await source.GetDetailAsync(mediaId, ct);
            ct.ThrowIfCancellationRequested();
            var favorite = await _store.IsFavoriteAsync(sourceKey, mediaId, ct);
            ct.ThrowIfCancellationRequested();
            var history = resume ?? await _store.FindHistoryAsync(sourceKey, mediaId, ct);
            ct.ThrowIfCancellationRequested();
            await _main.RunOnUiAsync(() =>
            {
                if (generation != _generation || ct.IsCancellationRequested) return;
                _sourceKey = sourceKey;
                SourceName = source.Name;
                Detail = detail;
                foreach (var line in detail.Lines) Lines.Add(line);
                SelectedLine = Lines.FirstOrDefault();
                IsFavorite = favorite;
                ApplyResume(history);
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception error)
        {
            await _main.RunOnUiAsync(() =>
            {
                if (generation == _generation && !ct.IsCancellationRequested)
                {
                    _main.StatusMessage = $"详情加载失败：{error.Message}";
                    Hint = $"详情加载失败：{error.Message}";
                }
            });
        }
        finally
        {
            await _main.RunOnUiAsync(() =>
            {
                if (generation != _generation) return;
                _request = null;
                Loading = false;
            });
            scope.Dispose();
        }
    }

    /// <summary>还原续播上下文（线路 + 选集 + 位置），并记下集号供 <see cref="Play"/> 判断位置能否复用。</summary>
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
        if (Loading || Detail is null || SelectedLine is null || SelectedEpisode is null)
        {
            _main.StatusMessage = "没有可播放的选集";
            return;
        }
        var resumeMs = MediaDetail.ResumePositionApplies(_resumeLineId, _resumeEpisodeId, SelectedLine.Id, SelectedEpisode.Id)
            ? _resumePositionMs
            : 0;

        var detail = Detail;
        var line = SelectedLine;
        var sourceKey = _sourceKey;
        var sourceName = SourceName;
        var source=_getSource(sourceKey);
        var resolver = source as IResolvingContentSource;
        var selectedId = SelectedEpisode.Id;
        var entries = line.Episodes.Select(episode => new PlaylistEntry(episode.Id, episode.Title, async ct =>
        {
            PlaybackRequest request;
            if (resolver is not null) request = await resolver.ResolvePlaybackAsync(detail.Item.Id, episode.Id, ct);
            else
            {
                if (episode.Uri is null) throw new InvalidOperationException("没有可播放的地址");
                request = new PlaybackRequest
                {
                    Uri = episode.Uri, Title = detail.Item.Title, SourceKey = sourceKey,
                    ParseEndpoint=source?.ParseEndpoint,
                    Resolution=string.IsNullOrWhiteSpace(source?.ParseEndpoint)?ResolutionKind.Direct:ResolutionKind.Json,
                    SourceName = sourceName, MediaId = detail.Item.Id, LineId = line.Id,
                    EpisodeId = episode.Id, Poster = detail.Item.Poster, Remarks = detail.Item.Remarks,
                };
            }
            return request with { StartPositionMs = 0 };
        })).ToArray();
        var index = Array.FindIndex(entries, entry => entry.Id == selectedId);
        await _main.Player.PlayResolvedAsync(async ct =>
        {
            var request = await entries[index].Resolve(ct);
            return request with { StartPositionMs = resumeMs };
        }, entries, index);
    }

    [RelayCommand]
    private void OpenRemoteControl()=>_main.Settings.OpenRemoteSettings();

    [RelayCommand]
    private void SearchOtherSources()
    {
        if (Detail is null) return;
        _main.Search.Keyword = Detail.Item.Title;
        _main.Navigate(AppPage.Search);
        _main.Search.RunSearchCommand.Execute(null);
    }

    [RelayCommand]
    private async Task ToggleFavorite()
    {
        if (Detail is null || Loading) return;
        var generation = _generation;
        var previous = IsFavorite;
        var target = !previous;
        var entry = new FavoriteEntry
        {
            Kind = FavoriteKind.Vod, SourceKey = _sourceKey, SourceName = SourceName,
            MediaId = Detail.Item.Id, Title = Detail.Item.Title, Poster = Detail.Item.Poster, Remarks = Detail.Item.Remarks,
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

/// <summary>剧集网格行（10 集）。</summary>
public sealed class EpisodeRow
{
    public IReadOnlyList<EpisodePresentation> Items { get; }
    public EpisodeRow(IReadOnlyList<Episode> items,IReadOnlySet<string>? watched=null) => Items=items.Select(episode=>new EpisodePresentation(episode,watched?.Contains(episode.Id)??false)).ToArray();
}

public sealed record EpisodePresentation(Episode Episode,bool IsWatched)
{
    public string Title => Episode.Title;
    public string Label => IsWatched ? "已看 · "+Episode.Title : Episode.Title;
}
