using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using VodBox.Core;
using VodBox.Desktop.Services;
using VodBox.Desktop.ViewModels;
using VodBox.Desktop.Views;
using Xunit;

namespace VodBox.PreviewTests;

/// <summary>真实 VM + 可控异步闸门；源/存储可忽略取消，复现迟到结果与关闭/切片竞争。</summary>
public sealed class ViewModelRaceTests
{
    private static TaskCompletionSource<T> Gate<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task Done(Task task) => task.WaitAsync(TimeSpan.FromSeconds(10));
    private static MediaItem Item(string id) => new() { Id = id, Title = id };
    private static MediaDetail Detail(string id) => new()
    {
        Item = Item(id),
        Lines = [new("line", "线路", [new("ep1", "第一集", "https://example.com/1"), new("ep2", "第二集", "https://example.com/2")])],
    };
    private static HistoryEntry History(string id, string episode = "ep2", long position = 42000) => new()
    {
        SourceKey = "source", SourceName = "站点", MediaId = id, Title = id,
        LineId = "line", EpisodeId = episode, PositionMs = position,
    };
    private static PlaybackRequest Request(string episode) => new()
    {
        Uri = $"https://example.com/{episode}", Title = episode, SourceKey = "source", SourceName = "站点",
        MediaId = "series", LineId = "line", EpisodeId = episode, Poster = "poster", Remarks = "remarks",
    };

    [AvaloniaFact]
    public async Task Detail_OldOpenCannotOverwriteNewResumeOrItsPlaybackContext()
    {
        using var context = new Context();
        var old = Gate<MediaDetail>();
        CancellationToken oldToken = default;
        context.Source.Load = (id, ct) =>
        {
            if (id == "old") { oldToken = ct; return old.Task; }
            return Task.FromResult(Detail(id));
        };
        var first = context.Main.Detail.OpenAsync("source", Item("old"));
        await Done(context.Main.Detail.ResumeAsync(History("new")));
        Assert.True(oldToken.IsCancellationRequested);
        old.SetResult(Detail("old"));
        await Done(first);
        var vm = context.Main.Detail;
        Assert.Equal("new", vm.Detail!.Item.Id);
        Assert.Equal("ep2", vm.SelectedEpisode!.Id);
        Assert.False(vm.Loading);
        await Done(vm.PlayCommand.ExecuteAsync(null));
        Assert.Equal("new", context.Source.ResolvedMedia);
        Assert.Equal("ep2", context.Source.ResolvedEpisode);
        Assert.Equal(42000, Assert.Single(context.Engine.Opened).Request.StartPositionMs);
    }

    [AvaloniaFact]
    public async Task Detail_StaleFavoriteAndHistoryReadsCannotCommit()
    {
        using var context = new Context();
        var favorite = Gate<bool>();
        var history = Gate<HistoryEntry?>();
        CancellationToken favoriteToken = default;
        CancellationToken historyToken = default;
        context.Store.Favorite = (_, id, ct) =>
        {
            if (id == "favorite") { favoriteToken = ct; return favorite.Task; }
            return Task.FromResult(false);
        };
        context.Store.History = (_, id, ct) =>
        {
            if (id == "history") { historyToken = ct; return history.Task; }
            return Task.FromResult<HistoryEntry?>(null);
        };
        var a = context.Main.Detail.OpenAsync("source", Item("favorite"));
        var b = context.Main.Detail.OpenAsync("source", Item("history"));
        var c = context.Main.Detail.OpenAsync("source", Item("current"));
        await Done(c);
        favorite.SetResult(true);
        history.SetResult(History("history"));
        await Done(Task.WhenAll(a, b));
        Assert.True(favoriteToken.IsCancellationRequested);
        Assert.True(historyToken.IsCancellationRequested);
        Assert.Equal("current", context.Main.Detail.Detail!.Item.Id);
        Assert.False(context.Main.Detail.IsFavorite);
        Assert.Equal("ep1", context.Main.Detail.SelectedEpisode!.Id);
    }

    [AvaloniaFact]
    public async Task Detail_StaleFailureCannotClearNewLoadingOrShowError()
    {
        using var context = new Context();
        var old = Gate<MediaDetail>();
        var current = Gate<MediaDetail>();
        context.Source.Load = (id, _) => id == "old" ? old.Task : current.Task;
        var a = context.Main.Detail.ResumeAsync(History("old"));
        var b = context.Main.Detail.OpenAsync("source", Item("new"));
        Assert.Null(context.Main.Detail.Detail);
        Assert.Empty(context.Main.Detail.Lines);
        old.SetException(new InvalidOperationException("stale failure"));
        await Done(a);
        Assert.True(context.Main.Detail.Loading);
        Assert.Equal("", context.Main.StatusMessage);
        current.SetResult(Detail("new"));
        await Done(b);
        Assert.Equal("new", context.Main.Detail.Detail!.Item.Id);
        Assert.Equal("ep1", context.Main.Detail.SelectedEpisode!.Id);
    }

    [AvaloniaFact]
    public async Task Detail_NavigationCancelsAndLateResponseCannotRepopulate()
    {
        using var context = new Context();
        var response = Gate<MediaDetail>();
        CancellationToken token = default;
        context.Source.Load = (_, ct) => { token = ct; return response.Task; };
        var load = context.Main.Detail.OpenAsync("source", Item("old"));
        context.Main.Navigate(AppPage.Home);
        Assert.True(token.IsCancellationRequested);
        response.SetResult(Detail("old"));
        await Done(load);
        Assert.Equal(AppPage.Home, context.Main.Page);
        Assert.Null(context.Main.Detail.Detail);
        Assert.False(context.Main.Detail.Loading);
    }

    [AvaloniaFact]
    public async Task Detail_MissingSourceInvalidatesPendingRequestAndOldSelection()
    {
        using var context = new Context();
        await Done(context.Main.Detail.OpenAsync("source", Item("existing")));
        var response = Gate<MediaDetail>();
        context.Source.Load = (_, _) => response.Task;
        var old = context.Main.Detail.OpenAsync("source", Item("old"));
        await Done(context.Main.Detail.ResumeAsync(History("missing") with { SourceKey = "missing" }));
        response.SetResult(Detail("old"));
        await Done(old);
        Assert.Null(context.Main.Detail.Detail);
        Assert.Null(context.Main.Detail.SelectedEpisode);
        Assert.Empty(context.Main.Detail.EpisodeRows);
        Assert.Contains("不可用", context.Main.StatusMessage);
    }

    [AvaloniaFact]
    public async Task Player_SwitchSavesImmutableOldProgressBeforeOpeningNewEpisode()
    {
        using var context = new Context();
        var vm = context.Main.Player;
        await Done(vm.PlayResolvedAsync(_ => Task.FromResult(Request("ep1"))));
        context.Engine.EmitCurrent(PlaybackState.Playing, 23000, 90000);
        var write = Gate<bool>();
        context.Store.Save = _ => write.Task;
        var next = vm.PlayResolvedAsync(_ => Task.FromResult(Request("ep2")));
        var saved = Assert.Single(context.Store.Saved);
        Assert.Equal("ep1", saved.EpisodeId);
        Assert.Equal(23000, saved.PositionMs);
        Assert.Equal(90000, saved.DurationMs);
        Assert.Equal("poster", saved.Poster);
        Assert.Equal("remarks", saved.Remarks);
        Assert.Equal(1, saved.Rate);
        Assert.Single(context.Engine.Opened);
        vm.Position = TimeSpan.FromSeconds(5);
        vm.Duration = TimeSpan.FromSeconds(10);
        write.SetResult(true);
        await Done(next);
        Assert.Equal(23000, saved.PositionMs);
        Assert.Equal(90000, saved.DurationMs);
        Assert.Equal("ep2", vm.Title);
        Assert.Equal(TimeSpan.Zero, vm.Position);
        Assert.Equal(2, context.Engine.Opened.Count);
    }

    [AvaloniaFact]
    public async Task Player_CloseWhileSavingThenNewPlayCannotHideOrStopNewSession()
    {
        using var context = new Context();
        var vm = context.Main.Player;
        await Done(vm.PlayResolvedAsync(_ => Task.FromResult(Request("ep1"))));
        context.Engine.EmitCurrent(PlaybackState.Playing, 12000, 90000);
        var write = Gate<bool>();
        context.Store.Save = _ => write.Task;
        var close = vm.Close();
        Assert.Equal(1, context.Engine.Stops);
        var next = vm.PlayResolvedAsync(_ => Task.FromResult(Request("ep2")));
        write.SetResult(true);
        await Done(Task.WhenAll(close, next));
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.Visible);
        Assert.Equal("ep2", vm.Title);
        Assert.Equal(PlaybackState.Playing, vm.State);
        Assert.Equal(1, context.Engine.Stops);
        Assert.Equal("ep1", Assert.Single(context.Store.Saved).EpisodeId);
        context.Engine.EmitCurrent(PlaybackState.Playing, 34000, 90000);
        await Done(vm.Close());
        Assert.Equal("ep2", context.Store.Saved[^1].EpisodeId);
        Assert.Equal(34000, context.Store.Saved[^1].PositionMs);
    }

    [AvaloniaFact]
    public async Task Player_SupersededPlayStopsWaitingForHistoryWithoutOpeningStaleRequest()
    {
        using var context = new Context();
        var vm = context.Main.Player;
        await Done(vm.PlayResolvedAsync(_ => Task.FromResult(Request("ep1"))));
        context.Engine.EmitCurrent(PlaybackState.Playing, 12000, 90000);
        var write = Gate<bool>();
        context.Store.Save = _ => write.Task;
        var old = vm.PlayResolvedAsync(_ => Task.FromResult(Request("ep2")));
        var current = vm.PlayResolvedAsync(_ => Task.FromResult(Request("ep3")));
        try
        {
            await Done(old);
            Assert.Single(context.Engine.Opened);
            Assert.False(current.IsCompleted);
        }
        finally { write.TrySetResult(true); }
        await Done(current);
        Assert.Equal(["ep1", "ep3"], context.Engine.Opened.Select(open => open.Request.EpisodeId));
        Assert.Equal("ep1", Assert.Single(context.Store.Saved).EpisodeId);
        Assert.Equal("ep3", vm.Title);
    }

    [AvaloniaFact]
    public async Task Player_CloseCancelsResolutionIgnoringCancellationWithoutResavingOldEpisode()
    {
        using var context = new Context();
        var vm = context.Main.Player;
        await Done(vm.PlayResolvedAsync(_ => Task.FromResult(Request("ep1"))));
        context.Engine.EmitCurrent(PlaybackState.Playing, 12000, 90000);
        var response = Gate<PlaybackRequest>();
        CancellationToken token = default;
        var next = vm.PlayResolvedAsync(ct => { token = ct; return response.Task; });
        await Done(vm.Close());
        Assert.True(token.IsCancellationRequested);
        response.SetResult(Request("ep2"));
        await Done(next);
        Assert.Single(context.Engine.Opened);
        Assert.Single(context.Store.Saved);
        Assert.False(vm.Visible);
        Assert.Equal(PlaybackState.Idle, vm.State);
        Assert.Null(vm.Error);
    }

    [AvaloniaFact]
    public async Task Player_OldResolutionFailureCannotReplaceNewSessionState()
    {
        using var context = new Context();
        var response = Gate<PlaybackRequest>();
        var old = context.Main.Player.PlayResolvedAsync(_ => response.Task);
        await Done(context.Main.Player.PlayResolvedAsync(_ => Task.FromResult(Request("ep2"))));
        response.SetException(new InvalidOperationException("stale failure"));
        await Done(old);
        Assert.Equal("ep2", context.Main.Player.Title);
        Assert.Equal(PlaybackState.Playing, context.Main.Player.State);
        Assert.Null(context.Main.Player.Error);
    }

    [AvaloniaFact]
    public async Task Player_QueuedOldEventAndOldSessionEventCannotPolluteNewProgress()
    {
        using var context = new Context();
        var vm = context.Main.Player;
        await Done(vm.PlayResolvedAsync(_ => Task.FromResult(Request("ep1"))));
        context.Engine.EmitCurrent(PlaybackState.Playing, 23000, 90000);
        var oldSession = context.Engine.Opened[^1].Session;
        // 在 UI 线程暂不泵队列，确保事件在切换后才回填。
        var eventThread = new Thread(() => context.Engine.Emit(oldSession, PlaybackState.Ended, 80000, 90000));
        eventThread.Start();
        Assert.True(eventThread.Join(TimeSpan.FromSeconds(10)));
        await Done(vm.PlayResolvedAsync(_ => Task.FromResult(Request("ep2"))));
        Dispatcher.UIThread.RunJobs();
        context.Engine.Emit(oldSession, PlaybackState.Failed, 70000, 90000);
        Assert.Equal("ep2", vm.Title);
        Assert.Equal(PlaybackState.Playing, vm.State);
        Assert.Equal(TimeSpan.Zero, vm.Position);
        Assert.Equal(23000, Assert.Single(context.Store.Saved).PositionMs);
    }

    [AvaloniaFact]
    public async Task Player_HistoryWritesStayOrderedAndStorageFailureDoesNotBlockPlayback()
    {
        using var context = new Context();
        var vm = context.Main.Player;
        await Done(vm.PlayResolvedAsync(_ => Task.FromResult(Request("ep1"))));
        context.Engine.EmitCurrent(PlaybackState.Playing, 10000, 90000);
        var write = Gate<bool>();
        context.Store.Save = _ => write.Task;
        context.Engine.EmitCurrent(PlaybackState.Ended, 11000, 90000);
        var next = vm.PlayResolvedAsync(_ => Task.FromResult(Request("ep2")));
        Assert.Single(context.Store.Saved);
        write.SetException(new IOException("storage unavailable"));
        await Done(next);
        Assert.Equal(2, context.Store.Saved.Count);
        Assert.Equal(0, context.Store.Saved[^1].PositionMs);
        Assert.Equal("ep2", vm.Title);
        Assert.Null(vm.Error);
    }

    [AvaloniaFact]
    public async Task Player_CloseAndNewPlaySerializeAnInFlightNativeOpen()
    {
        using var context = new Context();
        var release = Gate<bool>();
        var entered = Gate<bool>();
        context.Engine.Open = async request =>
        {
            if (request.EpisodeId != "ep1") return;
            entered.SetResult(true);
            await release.Task; // 原生打开忽略取消。
        };
        var old = context.Main.Player.PlayResolvedAsync(_ => Task.FromResult(Request("ep1")));
        await Done(entered.Task);
        var close = context.Main.Player.Close();
        var next = context.Main.Player.PlayResolvedAsync(_ => Task.FromResult(Request("ep2")));
        release.SetResult(true);
        await Done(Task.WhenAll(old, close, next));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, context.Engine.MaxConcurrentOpens);
        Assert.Equal(0, context.Engine.Stops);
        Assert.True(context.Main.Player.Visible);
        Assert.Equal("ep2", context.Main.Player.Title);
        Assert.Equal(PlaybackState.Playing, context.Main.Player.State);
    }

    [AvaloniaFact]
    public async Task BackgroundEntryPointsAndCompletionsUpdateOnlyOnUiThread()
    {
        using var context = new Context();
        var onUi = new List<bool>();
        context.Main.Detail.PropertyChanged += (_, _) => onUi.Add(Dispatcher.UIThread.CheckAccess());
        context.Main.Detail.Lines.CollectionChanged += (_, _) => onUi.Add(Dispatcher.UIThread.CheckAccess());
        context.Main.Player.PropertyChanged += (_, _) => onUi.Add(Dispatcher.UIThread.CheckAccess());
        await Done(Task.Run(() => context.Main.Detail.OpenAsync("source", Item("new"))));
        await Done(Task.Run(() => context.Main.Player.PlayResolvedAsync(_ => Task.Run(() => Request("ep1")))));
        await Done(Task.Run(() => context.Main.Player.Close()));
        Assert.NotEmpty(onUi);
        Assert.All(onUi, Assert.True);
    }

    [AvaloniaFact]
    public async Task Playlist_ManualNavigationUsesCapturedEntriesAndBoundaries()
    {
        using var context = new Context();
        var vm = context.Main.Player;
        var entries = new[]
        {
            new PlaylistEntry("ep1", "第一集", _ => Task.FromResult(Request("ep1"))),
            new PlaylistEntry("ep2", "第二集", _ => Task.FromResult(Request("ep2"))),
        };
        await Done(vm.PlayResolvedAsync(entries[0].Resolve, entries, 0));
        Assert.False(vm.HasPreviousEpisode);
        Assert.True(vm.HasNextEpisode);
        await Done(vm.PlayPlaylistIndexAsync(1));
        Assert.Equal("ep2", context.Engine.Opened[^1].Request.EpisodeId);
        Assert.True(vm.HasPreviousEpisode);
        Assert.False(vm.HasNextEpisode);
        await Done(vm.PlayPlaylistIndexAsync(2));
        Assert.Equal(2, context.Engine.Opened.Count);
        await Done(vm.Close());
        Assert.Empty(vm.Playlist);
        Assert.Equal(-1, vm.PlaylistIndex);
    }

    [AvaloniaFact]
    public async Task Playlist_EofAdvancesOnceButFailureAndLastEpisodeDoNotAdvance()
    {
        using var context = new Context();
        var vm = context.Main.Player;
        var entries = new[]
        {
            new PlaylistEntry("ep1", "第一集", _ => Task.FromResult(Request("ep1"))),
            new PlaylistEntry("ep2", "第二集", _ => Task.FromResult(Request("ep2"))),
        };
        await Done(vm.PlayResolvedAsync(entries[0].Resolve, entries, 0));
        context.Engine.EmitCurrent(PlaybackState.Failed, 5000, 90000);
        Assert.Single(context.Engine.Opened);
        var firstSession = context.Engine.Opened[0].Session;
        context.Engine.Emit(firstSession, PlaybackState.Ended, 90000, 90000);
        context.Engine.Emit(firstSession, PlaybackState.Ended, 90000, 90000);
        for (var i = 0; i < 30 && context.Engine.Opened.Count < 2; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, context.Engine.Opened.Count);
        Assert.Equal("ep2", context.Engine.Opened[1].Request.EpisodeId);
        context.Engine.EmitCurrent(PlaybackState.Ended, 90000, 90000);
        Assert.Equal(2, context.Engine.Opened.Count);
    }

    [AvaloniaFact]
    public async Task Playlist_DisabledAutoNextSavesCompletionWithoutAdvancing()
    {
        using var context = new Context();
        var vm = context.Main.Player;
        vm.AutoNext = false;
        var entries = new[]
        {
            new PlaylistEntry("ep1", "第一集", _ => Task.FromResult(Request("ep1"))),
            new PlaylistEntry("ep2", "第二集", _ => Task.FromResult(Request("ep2"))),
        };
        await Done(vm.PlayResolvedAsync(entries[0].Resolve, entries, 0));
        context.Engine.EmitCurrent(PlaybackState.Ended, 90000, 90000);
        Assert.Single(context.Engine.Opened);
        Assert.Equal(0, context.Store.Saved[^1].PositionMs);
        Assert.Equal(90000, context.Store.Saved[^1].DurationMs);
    }

    [AvaloniaFact]
    public async Task Player_SeekPreviewSurvivesProgressEventsAndCommitsOnlyOnce()
    {
        using var context = new Context();
        var vm = context.Main.Player;
        await Done(vm.PlayResolvedAsync(_ => Task.FromResult(Request("ep1"))));
        context.Engine.EmitCurrent(PlaybackState.Playing, 10000, 90000);
        Assert.Equal(10, vm.SeekPositionSeconds);
        Assert.Empty(context.Engine.Seeks);
        vm.BeginSeek();
        vm.SeekPositionSeconds = 45;
        context.Engine.EmitCurrent(PlaybackState.Playing, 11000, 90000);
        Assert.Equal(45, vm.SeekPositionSeconds);
        vm.CommitSeek();
        Assert.Equal(TimeSpan.FromSeconds(45), Assert.Single(context.Engine.Seeks));
        vm.CommitSeek();
        Assert.Single(context.Engine.Seeks);
    }

    [AvaloniaFact]
    public async Task Player_SwitchingDiscardsAnOldSeekGesture()
    {
        using var context = new Context();
        var vm = context.Main.Player;
        await Done(vm.PlayResolvedAsync(_ => Task.FromResult(Request("ep1"))));
        context.Engine.EmitCurrent(PlaybackState.Playing, 10000, 90000);
        vm.BeginSeek();
        vm.SeekPositionSeconds = 45;
        await Done(vm.PlayResolvedAsync(_ => Task.FromResult(Request("ep2"))));
        vm.CommitSeek();
        Assert.Empty(context.Engine.Seeks);
    }

    [AvaloniaFact]
    public async Task Player_ControlsHideOnlyDuringUnattendedPlayback()
    {
        using var context = new Context();
        var vm = context.Main.Player;
        var view = new PlayerOverlay { DataContext = context.Main };
        await Done(vm.PlayResolvedAsync(_ => Task.FromResult(Request("ep1"))));
        context.Engine.EmitCurrent(PlaybackState.Playing, 10000, 90000);
        view.UpdateControls(TimeSpan.FromSeconds(4));
        Assert.False(vm.ControlsVisible);
        view.UpdateControls(TimeSpan.FromSeconds(1));
        Assert.True(vm.ControlsVisible);
        vm.BeginSeek();
        view.UpdateControls(TimeSpan.FromSeconds(4));
        Assert.True(vm.ControlsVisible);
        vm.CancelSeek();
        context.Engine.EmitCurrent(PlaybackState.Paused, 10000, 90000);
        view.UpdateControls(TimeSpan.FromSeconds(4));
        Assert.True(vm.ControlsVisible);
        await Done(vm.Close());
        view.UpdateControls(TimeSpan.FromSeconds(4));
        Assert.True(vm.ControlsVisible);
    }

    private static IReadOnlyList<(SourceInfo Site, MediaItem Item)> SearchResults(string id, int count = 1)
    {
        var site = new SourceInfo { Key = "source", Name = "站点", Runtime = SourceRuntime.MacCms };
        return Enumerable.Range(0, count).Select(i => (site, new MediaItem { Id = id + i, Title = id + i })).ToArray();
    }

    [AvaloniaFact]
    public async Task Search_LateResultsCannotReplaceNewQuery()
    {
        using var context = new Context();
        var late = Gate<IReadOnlyList<(SourceInfo Site, MediaItem Item)>>();
        var search = new SearchViewModel((query, _) => query == "旧" ? late.Task : Task.FromResult(SearchResults("新")), context.Main);
        search.Keyword = "旧";
        var old = search.RunSearchAsync();
        search.Keyword = "新";
        await Done(search.RunSearchAsync());
        late.SetResult(SearchResults("旧"));
        await Done(old);
        Assert.Equal("新0", Assert.Single(search.AllResults).Id);
        Assert.Contains("新", search.Summary);
        Assert.False(search.Searching);
        Assert.Equal(["新"], search.SearchHistory);
    }

    [AvaloniaFact]
    public async Task Search_ResultsAfterFortiethItemRetainTheirSourceAndOpen()
    {
        using var context = new Context();
        var loaded = Gate<string>();
        context.Source.Load = (id, _) => { loaded.TrySetResult(id); return Task.FromResult(Detail(id)); };
        var search = new SearchViewModel((_, _) => Task.FromResult(SearchResults("item", 120)), context.Main) { Keyword = "测试" };
        await Done(search.RunSearchAsync());
        var site = Assert.Single(search.SiteResults);
        Assert.Equal(120, site.Items.Count);
        search.SelectedSite = site;
        Assert.Equal(120, search.ResultRows.Sum(row => row.Items.Count));
        search.OpenItemCommand.Execute(search.AllResults[119]);
        Assert.Equal("item119", await loaded.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [AvaloniaFact]
    public async Task Home_LateLoadCannotReplaceNewRecommendationsOrHero()
    {
        using var context = new Context();
        var oldPage = Gate<MediaPage>();
        var calls = 0;
        context.Source.Home = _ => ++calls == 1 ? oldPage.Task : Task.FromResult(new MediaPage([new MediaItem { Id = "new", Title = "新推荐", Poster = "https://example/new.jpg" }], 1, 1));
        var home = new HomeViewModel(() => context.Source, context.Store, context.Main);
        var old = home.LoadAsync();
        await Done(home.LoadAsync());
        oldPage.SetResult(new MediaPage([new MediaItem { Id = "old", Title = "旧推荐" }], 1, 1));
        await Done(old);
        Assert.Equal("new", Assert.Single(home.Recommendations).Id);
        Assert.Equal("新推荐", home.HeroTitle);
        Assert.Equal("https://example/new.jpg", home.HeroPoster);
        Assert.Equal(context.Source.Name, home.SourceName);
        Assert.False(home.Loading);
    }

    [AvaloniaFact]
    public async Task Home_HeroUsesTheSourceThatProvidedRecommendations()
    {
        using var context = new Context();
        var loaded = Gate<string>();
        context.Source.Load = (id, _) => { loaded.TrySetResult(id); return Task.FromResult(Detail(id)); };
        context.Source.Home = _ => Task.FromResult(new MediaPage([new MediaItem { Id = "hero", Title = "首页推荐" }], 1, 1));
        var home = new HomeViewModel(() => context.Source, context.Store, context.Main);
        await Done(home.LoadAsync());
        home.OpenHeroCommand.Execute(null);
        Assert.Equal("hero", await loaded.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private sealed class Context : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), $"vodbox-vm-race-{Guid.NewGuid():N}");
        private readonly AppServices _services;
        public Source Source { get; } = new();
        public Store Store { get; } = new();
        public Engine Engine { get; } = new();
        public MainViewModel Main { get; }
        public Context()
        {
            _services = new AppServices(_directory);
            Main = new MainViewModel(_services, true, key => key == Source.Key ? Source : null, Store);
            Main.Player = new PlayerViewModel(Engine, Store, Main);
            Main.AttachDispatcher(Dispatcher.UIThread);
        }
        public void Dispose()
        {
            Dispatcher.UIThread.RunJobs();
            _services.Dispose();
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class Source : IResolvingContentSource
    {
        public string Key => "source";
        public string Name => "站点";
        public Func<string, CancellationToken, Task<MediaDetail>> Load { get; set; } = (id, _) => Task.FromResult(Detail(id));
        public string? ResolvedMedia { get; private set; }
        public string? ResolvedEpisode { get; private set; }
        public Task<MediaDetail> GetDetailAsync(string mediaId, CancellationToken ct = default) => Load(mediaId, ct);
        public Task<PlaybackRequest> ResolvePlaybackAsync(string mediaId, string episodeId, CancellationToken ct = default)
        {
            ResolvedMedia = mediaId;
            ResolvedEpisode = episodeId;
            return Task.FromResult(Request(episodeId) with { MediaId = mediaId });
        }
        public Task<IReadOnlyList<Category>> GetCategoriesAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Func<CancellationToken, Task<MediaPage>> Home { get; set; } = _ => Task.FromResult(new MediaPage([], 1, 1));
        public Task<MediaPage> GetHomeAsync(CancellationToken ct = default) => Home(ct);
        public Task<MediaPage> GetItemsAsync(string categoryId, int page, IReadOnlyDictionary<string, string>? filters, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<MediaPage> SearchAsync(string query, int page, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class Store : ILibraryStore
    {
        public List<HistoryEntry> Saved { get; } = [];
        public Func<HistoryEntry, Task> Save { get; set; } = _ => Task.CompletedTask;
        public Func<string, string, CancellationToken, Task<bool>> Favorite { get; set; } = (_, _, _) => Task.FromResult(false);
        public Func<string, string, CancellationToken, Task<HistoryEntry?>> History { get; set; } = (_, _, _) => Task.FromResult<HistoryEntry?>(null);
        public Task SaveHistoryAsync(HistoryEntry entry, CancellationToken ct = default) { Saved.Add(entry); return Save(entry); }
        public Task<bool> IsFavoriteAsync(string sourceKey, string mediaId, CancellationToken ct = default) => Favorite(sourceKey, mediaId, ct);
        public Task<HistoryEntry?> FindHistoryAsync(string sourceKey, string mediaId, CancellationToken ct = default) => History(sourceKey, mediaId, ct);
        public Task<IReadOnlyList<HistoryEntry>> GetHistoryAsync(int limit = 200, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<HistoryEntry>>(Saved.ToArray());
        public Task DeleteHistoryAsync(string sourceKey, string mediaId, CancellationToken ct = default) => Task.CompletedTask;
        public Task ClearHistoryAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SetFavoriteAsync(FavoriteEntry entry, bool favorite, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<FavoriteEntry>> GetFavoritesAsync(FavoriteKind kind, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<FavoriteEntry>>([]);
    }

    private sealed class Engine : IPlaybackEngine
    {
        public List<(PlaybackRequest Request, long Session)> Opened { get; } = [];
        public Func<PlaybackRequest, Task> Open { get; set; } = _ => Task.CompletedTask;
        public int Stops { get; private set; }
        public int MaxConcurrentOpens { get; private set; }
        private int _opens;
        public PlaybackSnapshot Snapshot { get; private set; } = new(PlaybackState.Idle, TimeSpan.Zero, TimeSpan.Zero, true);
        public event EventHandler<PlaybackEvent>? StateChanged;
        public async Task OpenAsync(PlaybackRequest request, long sessionId, CancellationToken ct = default)
        {
            Opened.Add((request, sessionId));
            MaxConcurrentOpens = Math.Max(MaxConcurrentOpens, ++_opens);
            try { await Open(request); }
            finally { --_opens; }
            Emit(sessionId, PlaybackState.Playing, 0, 0);
        }
        public void EmitCurrent(PlaybackState state, long position, long duration) => Emit(Opened[^1].Session, state, position, duration);
        public void Emit(long session, PlaybackState state, long position, long duration)
        {
            Snapshot = new(state, TimeSpan.FromMilliseconds(position), TimeSpan.FromMilliseconds(duration), true);
            StateChanged?.Invoke(this, new(session, Snapshot));
        }
        public Task StopAsync(CancellationToken ct = default) { Stops++; return Task.CompletedTask; }
        public Task PlayAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task PauseAsync(CancellationToken ct = default) => Task.CompletedTask;
        public List<TimeSpan> Seeks { get; } = [];
        public Task SeekToAsync(TimeSpan position, CancellationToken ct = default) { Seeks.Add(position); return Task.CompletedTask; }
        public Task SeekByAsync(TimeSpan delta, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetRateAsync(double rate, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetVolumeAsync(int volume, CancellationToken ct = default) => Task.CompletedTask;
        public IReadOnlyList<MediaTrack> GetTracks(TrackKind kind) => [];
        public Task SelectTrackAsync(TrackKind kind, string trackId, CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
