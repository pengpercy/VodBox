using Microsoft.Extensions.Logging;
using Avalonia.Headless.XUnit;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
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

    [AvaloniaFact]
    public async Task Player_RestoresPreferencesBeforeOpeningAndPersistsMuteAndRate()
    {
        using var context = new Context();
        context.Preferences.Set("player.volume", 35);
        context.Preferences.Set("player.rate", 1.5);
        context.Preferences.Set("player.auto-next", false);
        var vm = new PlayerViewModel(context.Engine, context.Store, context.Main, context.Preferences);
        Assert.Equal(35, vm.Volume);
        Assert.Equal(1.5, vm.Rate);
        Assert.False(vm.AutoNext);
        await Done(vm.PlayResolvedAsync(_ => Task.FromResult(Request("ep1"))));
        Assert.Equal(35, context.Engine.AppliedVolume);
        Assert.Equal(1.5, context.Engine.AppliedRate);
        vm.ToggleMuteCommand.Execute(null);
        Assert.True(vm.IsMuted);
        Assert.Equal(0, context.Engine.AppliedVolume);
        vm.ToggleMuteCommand.Execute(null);
        Assert.Equal(35, vm.Volume);
        vm.Rate = 2;
        vm.AutoNext = true;
        var restored = new PlayerViewModel(new Engine(), context.Store, context.Main, context.Preferences);
        Assert.Equal(35, restored.Volume);
        Assert.Equal(2, restored.Rate);
        Assert.True(restored.AutoNext);
    }

    [AvaloniaFact]
    public async Task Player_IncognitoSuppressesSwitchEndAndCloseHistory()
    {
        using var context = new Context();
        context.Preferences.Set("player.incognito", true);
        var vm = new PlayerViewModel(context.Engine, context.Store, context.Main, context.Preferences);
        Assert.True(vm.Incognito);
        await Done(vm.PlayResolvedAsync(_ => Task.FromResult(Request("ep1"))));
        context.Engine.EmitCurrent(PlaybackState.Playing, 5000, 10000);
        await Done(vm.PlayResolvedAsync(_ => Task.FromResult(Request("ep2"))));
        context.Engine.EmitCurrent(PlaybackState.Ended, 10000, 10000);
        await Done(vm.Close());
        Assert.Empty(context.Store.Saved);
        vm.Incognito = false;
        Assert.False(context.Preferences.GetBool("player.incognito"));
        await Done(vm.PlayResolvedAsync(_ => Task.FromResult(Request("ep3"))));
        await Done(vm.Close());
        Assert.Single(context.Store.Saved);
    }

    [AvaloniaFact]
    public async Task Player_RestoresAndResetsAspectPreference()
    {
        using var context = new Context();
        context.Preferences.Set("player.aspect-ratio", 16d / 9);
        var vm = new PlayerViewModel(context.Engine, context.Store, context.Main, context.Preferences);
        await Done(vm.PlayResolvedAsync(_ => Task.FromResult(Request("ep1"))));
        Assert.Equal(16d / 9, context.Engine.AppliedAspect);
        await Done(vm.SetAspectRatioAsync(4d / 3));
        Assert.Equal(4d / 3, context.Preferences.GetDouble("player.aspect-ratio"));
        await Done(vm.SetAspectRatioAsync(null));
        Assert.Null(vm.AspectRatio);
        Assert.Null(context.Engine.AppliedAspect);
        Assert.Equal(0, context.Preferences.GetDouble("player.aspect-ratio"));
        context.Preferences.Set("player.aspect-ratio", "NaN");
        Assert.Null(new PlayerViewModel(new Engine(), context.Store, context.Main, context.Preferences).AspectRatio);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => vm.SetAspectRatioAsync(double.NaN));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => vm.SetAspectRatioAsync(0));
    }

    [AvaloniaFact]
    public void Player_CompactWindowRestoresSizeConstraintsAndTopmost()
    {
        using var context = new Context();
        var view = new PlayerOverlay { DataContext = context.Main };
        var window = new Window { Width = 1280, Height = 800, MinWidth = 960, MinHeight = 600 };
        view.ToggleCompactWindow(window);
        Assert.True(window.Topmost);
        Assert.Equal(640, window.Width);
        Assert.Equal(360, window.Height);
        Assert.True(context.Main.Player.CompactMode);
        view.ToggleCompactWindow(window);
        Assert.False(window.Topmost);
        Assert.Equal(1280, window.Width);
        Assert.Equal(800, window.Height);
        Assert.Equal(960, window.MinWidth);
        Assert.Equal(600, window.MinHeight);
        Assert.False(context.Main.Player.CompactMode);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Player_SubtitlePickerResultCannotApplyAfterSwitchOrClose()
    {
        using var context = new Context();
        var vm = context.Main.Player;
        await Done(vm.PlayResolvedAsync(_ => Task.FromResult(Request("ep1"))));
        var oldIntent = vm.CurrentSessionId;
        await Done(vm.PlayResolvedAsync(_ => Task.FromResult(Request("ep2"))));
        await Done(vm.LoadSubtitleAsync("old.srt", oldIntent));
        Assert.Empty(context.Engine.Subtitles);
        await Done(vm.LoadSubtitleAsync("current.srt", vm.CurrentSessionId));
        Assert.Equal("current.srt", Assert.Single(context.Engine.Subtitles));
        var currentIntent = vm.CurrentSessionId;
        await Done(vm.CloseCommand.ExecuteAsync(null));
        await Done(vm.LoadSubtitleAsync("closed.srt", currentIntent));
        Assert.Single(context.Engine.Subtitles);
    }

    [AvaloniaFact]
    public async Task Player_SubtitleErrorsAreReportedWithoutFailingPlayback()
    {
        using var context = new Context();
        var vm = context.Main.Player;
        await Done(vm.PlayResolvedAsync(_ => Task.FromResult(Request("ep1"))));
        context.Engine.SubtitleError = new IOException("unavailable");
        await Done(vm.LoadSubtitleAsync("test.srt", vm.CurrentSessionId));
        Assert.Contains("unavailable", vm.ToastText);
        Assert.Equal(PlaybackState.Playing, vm.State);
    }

    [AvaloniaFact]
    public async Task Player_NumberSelectionUsesPlaylistAndIgnoresInvalidNumbers()
    {
        using var context = new Context();
        var vm = context.Main.Player;
        var entries = new[]
        {
            new PlaylistEntry("ep1", "第一集", _ => Task.FromResult(Request("ep1"))),
            new PlaylistEntry("ep2", "第二集", _ => Task.FromResult(Request("ep2"))),
        };
        await Done(vm.PlayResolvedAsync(entries[0].Resolve, entries, 0));
        await Done(vm.SelectEpisodeNumberAsync(2));
        Assert.Equal("ep2", context.Engine.Opened[^1].Request.EpisodeId);
        Assert.Equal(1, vm.PlaylistIndex);
        var intent = vm.CurrentSessionId;
        foreach (var number in new[] { -1, 0, 3, 10 }) await Done(vm.SelectEpisodeNumberAsync(number));
        Assert.Equal(intent, vm.CurrentSessionId);
    }

    [AvaloniaFact]
    public async Task Window_NumberKeySwitchesEpisodeButModifiedKeyDoesNot()
    {
        using var context = new Context();
        var vm = context.Main.Player;
        var entries = new[]
        {
            new PlaylistEntry("ep1", "第一集", _ => Task.FromResult(Request("ep1"))),
            new PlaylistEntry("ep2", "第二集", _ => Task.FromResult(Request("ep2"))),
        };
        await Done(vm.PlayResolvedAsync(entries[0].Resolve, entries, 0));
        var shell = new VodBox.Desktop.MainWindow { DataContext = context.Main };
        var window = Assert.IsType<PlayerWindow>(shell.PlaybackWindow);
        try
        {
            var modified = new Avalonia.Input.KeyEventArgs
            {
                RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent,
                Key = Avalonia.Input.Key.D2, KeyModifiers = Avalonia.Input.KeyModifiers.Shift,
            };
            window.RaiseEvent(modified);
            Assert.False(modified.Handled);
            Assert.Equal(0, vm.PlaylistIndex);
            var plain = new Avalonia.Input.KeyEventArgs
            {
                RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Avalonia.Input.Key.NumPad2,
            };
            window.RaiseEvent(plain);
            Assert.True(plain.Handled);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, vm.PlaylistIndex);
        }
        finally { await Done(vm.Close()); shell.Close(); }
    }

    [AvaloniaFact]
    public async Task Player_AutomaticSkipsRunOncePerSessionAndPreserveResume()
    {
        using var context = new Context();
        var vm = context.Main.Player;
        vm.OpeningSkipSeconds = 30;
        vm.EndingSkipSeconds = 20;
        await Done(vm.PlayResolvedAsync(_ => Task.FromResult(Request("ep1"))));
        context.Engine.EmitCurrent(PlaybackState.Playing, 1000, 120000);
        context.Engine.EmitCurrent(PlaybackState.Playing, 2000, 120000);
        Assert.Equal(TimeSpan.FromSeconds(30), Assert.Single(context.Engine.Seeks));
        context.Engine.EmitCurrent(PlaybackState.Playing, 101000, 120000);
        context.Engine.EmitCurrent(PlaybackState.Playing, 102000, 120000);
        Assert.Equal(2, context.Engine.Seeks.Count);
        Assert.Equal(TimeSpan.FromSeconds(120), context.Engine.Seeks[^1]);
        await Done(vm.PlayResolvedAsync(_ => Task.FromResult(Request("ep2") with { StartPositionMs = 60000 })));
        context.Engine.EmitCurrent(PlaybackState.Playing, 1000, 120000);
        Assert.Equal(2, context.Engine.Seeks.Count);
    }

    [AvaloniaFact]
    public async Task Player_AutomaticSkipsIgnoreLivePausedShortMediaAndUserSeek()
    {
        using var context = new Context();
        var vm = context.Main.Player;
        vm.OpeningSkipSeconds = 30;
        vm.EndingSkipSeconds = 20;
        await Done(vm.PlayResolvedAsync(_ => Task.FromResult(Request("live") with { IsLive = true })));
        context.Engine.EmitCurrent(PlaybackState.Playing, 1000, 120000);
        Assert.Empty(context.Engine.Seeks);
        await Done(vm.PlayResolvedAsync(_ => Task.FromResult(Request("ep1"))));
        context.Engine.EmitCurrent(PlaybackState.Paused, 1000, 120000);
        context.Engine.EmitCurrent(PlaybackState.Playing, 1000, 40000);
        vm.BeginSeek();
        context.Engine.EmitCurrent(PlaybackState.Playing, 1000, 120000);
        Assert.Empty(context.Engine.Seeks);
        vm.CancelSeek();
        context.Engine.EmitCurrent(PlaybackState.Playing, 1000, 120000);
        Assert.Single(context.Engine.Seeks);
    }

    [AvaloniaFact]
    public void Player_SkipPreferencesAreBoundedAndRestored()
    {
        using var context = new Context();
        var vm = new PlayerViewModel(context.Engine, context.Store, context.Main, context.Preferences);
        vm.OpeningSkipSeconds = 900;
        vm.EndingSkipSeconds = -5;
        Assert.Equal(300, vm.OpeningSkipSeconds);
        Assert.Equal(0, vm.EndingSkipSeconds);
        var restored = new PlayerViewModel(new Engine(), context.Store, context.Main, context.Preferences);
        Assert.Equal(300, restored.OpeningSkipSeconds);
        Assert.Equal(0, restored.EndingSkipSeconds);
    }

    [AvaloniaFact]
    public void Live_LineSwitchCyclesAndKeepsStableChannelHistoryKey()
    {
        using var context = new Context();
        var channel = new LiveChannel { Name = "频道", Headers = new() { ["Referer"] = "https://source.example/" }, Uris = ["https://example.com/a", "https://example.com/b", "https://example.com/c"] };
        var live = context.Main.Live;
        live.Groups.Add(new LiveGroup("组", [channel], false));
        live.SelectedGroup = live.Groups[0];
        live.PlayChannel(channel);
        Assert.Equal(channel.Uris[0], context.Engine.Opened[^1].Request.Uri);
        for (var i = 1; i <= 4; i++)
        {
            live.SwitchLine();
            Assert.Equal(channel.Uris[i % 3], context.Engine.Opened[^1].Request.Uri);
            Assert.Equal(channel.Uris[0], context.Engine.Opened[^1].Request.MediaId);
            Assert.True(context.Engine.Opened[^1].Request.IsLive);
            Assert.Equal("https://source.example/", context.Engine.Opened[^1].Request.Headers["Referer"]);
        }
        Assert.Empty(live.EpgTimeline);
    }

    [AvaloniaFact]
    public void Live_ChannelNavigationHandlesNoSelectionAndHiddenCurrentFlags()
    {
        using var context = new Context();
        var first = new LiveChannel { Name = "A", Uris = ["https://example.com/a"] };
        var last = new LiveChannel { Name = "B", Uris = ["https://example.com/b"] };
        var live = context.Main.Live;
        live.Groups.Add(new LiveGroup("组", [first, last], false));
        live.SelectedGroup = live.Groups[0];
        live.PrevChannel();
        Assert.Same(last, live.CurrentChannel);
        live.FilterText = "A";
        live.NextChannel();
        Assert.Same(first, live.CurrentChannel);
        Assert.False(last.IsCurrent);
        Assert.Empty(live.EpgTimeline);
        Assert.Equal("00:00 — 00:00 · 已播出", new LiveEpgCard { Start = DateTimeOffset.UnixEpoch, End = DateTimeOffset.UnixEpoch, IsPast = true }.Range);
    }

    [AvaloniaFact]
    public void Live_RealProgrammesMatchTvgIdAndDoNotInventMissingEntries()
    {
        using var context = new Context();
        var live = context.Main.Live;
        var now = DateTimeOffset.Now;
        var channel = new LiveChannel { Name = "显示名", TvgId = "c1" };
        live.SetProgrammes([
            new Programme("c1", "当前节目", now.AddMinutes(-10), now.AddMinutes(10), "不同显示名"),
            new Programme("c1", "下一节目", now.AddMinutes(10), now.AddMinutes(30)),
            new Programme("c2", "别台节目", now.AddMinutes(-10), now.AddMinutes(10)),
        ]);
        live.SetCurrent(channel);
        Assert.Equal("当前节目", channel.EpgNow);
        Assert.Equal("下一节目", channel.EpgNext);
        Assert.Equal(2, live.EpgTimeline.Count);
        Assert.True(live.EpgTimeline[0].IsNow);
        live.SetCurrent(new LiveChannel { Name = "没数据", TvgId = "missing" });
        Assert.Empty(live.EpgTimeline);
    }

    [AvaloniaFact]
    public async Task Live_ImportsXmlTvThroughStreamAndKeepsExistingDataOnFailure()
    {
        using var context = new Context();
        var live = context.Main.Live;
        live.SetCurrent(new LiveChannel { Name = "频道", TvgId = "c1" });
        const string xml = "<tv><programme channel='c1' start='20261009010000 +0800' stop='20261009020000 +0800'><title>节目</title></programme></tv>";
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(xml));
        await Done(live.ImportXmlTvAsync(stream));
        Assert.Contains("1 条节目", context.Main.StatusMessage);
        using var bad = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("<html/>"));
        await Assert.ThrowsAsync<InvalidDataException>(() => live.ImportXmlTvAsync(bad));
        Assert.Contains("1 条节目", context.Main.StatusMessage);
    }

    [AvaloniaFact]
    public async Task Live_StaleLoadCannotOverwriteNewGroupsAndRefreshRetainsChannel()
    {
        using var context = new Context();
        var old = Gate<List<LiveGroup>>();
        var channel = new LiveChannel { Name = "新频道", Uris = ["https://example.com/new"] };
        context.Services.CurrentLiveConfig = "https://example.com/old";
        var live = new LiveViewModel(context.Services, context.Main, (url, _) => url.EndsWith("old")
            ? old.Task : Task.FromResult(new List<LiveGroup> { new("新组", [channel], false) }));
        var first = live.LoadAsync();
        context.Services.CurrentLiveConfig = "https://example.com/new";
        await Done(live.LoadAsync());
        live.SetCurrent(channel);
        old.SetResult([new LiveGroup("旧组", [], false)]);
        await Done(first);
        Assert.Equal("新组", Assert.Single(live.Groups).Name);
        Assert.Same(channel, live.CurrentChannel);
        Assert.False(live.Loading);
        await Done(live.LoadAsync());
        Assert.Same(channel, live.CurrentChannel);
    }

    [AvaloniaFact]
    public async Task Live_RemovingConfigurationCancelsPendingLoadAndClearsOldGroups()
    {
        using var context = new Context();
        var pending = Gate<List<LiveGroup>>();
        CancellationToken token = default;
        var live = new LiveViewModel(context.Services, context.Main, (_, ct) => { token = ct; return pending.Task; });
        context.Services.CurrentLiveConfig = "https://example.com/list";
        var loading = live.LoadAsync();
        context.Services.CurrentLiveConfig = null;
        await Done(live.LoadAsync());
        Assert.True(token.IsCancellationRequested);
        pending.SetResult([new LiveGroup("旧结果", [], false)]);
        await Done(loading);
        Assert.Empty(live.Groups);
        Assert.Empty(live.VisibleChannels);
        Assert.False(live.Loading);
        Assert.Null(live.CurrentChannel);
    }

    [AvaloniaFact]
    public async Task Live_ImportsCompressedXmlTvAndLeavesCallerStreamOpen()
    {
        using var context = new Context();
        const string xml = "<tv><programme channel='c1' start='20261009010000 +0800' stop='20261009020000 +0800'><title>节目</title></programme></tv>";
        using var stream = new MemoryStream();
        using (var compressed = new System.IO.Compression.GZipStream(stream, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
            await compressed.WriteAsync(System.Text.Encoding.UTF8.GetBytes(xml));
        stream.Position = 0;
        await Done(context.Main.Live.ImportXmlTvAsync(stream, gzip: true));
        Assert.Contains("1 条节目", context.Main.StatusMessage);
        Assert.True(stream.CanRead);
    }

    [AvaloniaFact]
    public void Live_ProgrammeClockMovesNowToNextWithoutReloadingData()
    {
        using var context = new Context();
        var live = context.Main.Live;
        var start = new DateTimeOffset(2026, 10, 9, 1, 0, 0, TimeSpan.FromHours(8));
        var channel = new LiveChannel { Name = "频道", TvgId = "c1" };
        live.SetProgrammes([
            new Programme("c1", "第一档", start, start.AddMinutes(30)),
            new Programme("c1", "第二档", start.AddMinutes(30), start.AddHours(1)),
        ]);
        live.SetCurrent(channel);
        live.RefreshProgrammeClock(start.AddMinutes(15));
        Assert.Equal("第一档", channel.EpgNow);
        Assert.Equal("第二档", channel.EpgNext);
        live.RefreshProgrammeClock(start.AddMinutes(30));
        Assert.Equal("第二档", channel.EpgNow);
        Assert.Equal("", channel.EpgNext);
        Assert.True(live.EpgTimeline[0].IsPast);
        Assert.True(live.EpgTimeline[1].IsNow);
    }

    [AvaloniaFact]
    public async Task Live_ImportedProgrammeCacheSurvivesViewModelRecreation()
    {
        using var context = new Context();
        var now = DateTimeOffset.Now;
        var start = now.AddMinutes(-10).ToString("yyyyMMddHHmmss zzz").Replace(":", "");
        var stop = now.AddMinutes(10).ToString("yyyyMMddHHmmss zzz").Replace(":", "");
        var xml = $"<tv><programme channel='c1' start='{start}' stop='{stop}'><title>缓存节目</title></programme></tv>";
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(xml));
        await Done(context.Main.Live.ImportXmlTvAsync(stream));
        var fresh = new LiveViewModel(context.Services, context.Main);
        await Done(fresh.RestoreProgrammeCacheAsync());
        var channel = new LiveChannel { Name = "频道", TvgId = "c1" };
        fresh.SetCurrent(channel);
        Assert.Equal("缓存节目", channel.EpgNow);
    }

    [AvaloniaFact]
    public async Task Settings_HistoryRestoresAndFailedConfigDoesNotCreateSubscription()
    {
        using var context = new Context();
        await context.Services.Store.AddAsync(new ConfigSubscription { Url = "https://example.com/config", Name = "订阅", Kind = ConfigKind.Vod });
        var subscription = Assert.Single(await context.Services.Store.ListAsync(ConfigKind.Vod));
        await context.Services.Store.SetActiveAsync(ConfigKind.Vod, subscription.Id);
        await Done(context.Main.Settings.LoadConfigHistoryAsync());
        Assert.True(Assert.Single(context.Main.Settings.ConfigHistory).Current);
        context.Main.Settings.DialogUrl = "invalid://unavailable";
        await Done(context.Main.Settings.ConfirmConfigDialogCommand.ExecuteAsync(null));
        Assert.Single(await context.Services.Store.ListAsync(ConfigKind.Vod));
        Assert.Contains("失败", context.Main.Settings.Message);
    }

    [AvaloniaFact]
    public async Task Live_FavoriteAndHistoryTabsShowStoredChannelsAndTogglePersists()
    {
        using var context = new Context();
        var live = context.Main.Live;
        var channel = new LiveChannel { Name = "频道", Uris = ["https://example.com/live"] };
        live.Groups.Add(new LiveGroup("组", [channel], false));
        live.SelectedGroup = live.Groups[0];
        live.SetCurrent(channel);
        await Done(live.ToggleChannelFavoriteCommand.ExecuteAsync(null));
        Assert.Single(await context.Services.Store.GetFavoritesAsync(FavoriteKind.Live));
        live.ChannelTab = 1;
        Assert.Same(channel, Assert.Single(live.VisibleChannels));
        await context.Services.Store.SaveHistoryAsync(new HistoryEntry
        {
            SourceKey = "live", SourceName = "直播", MediaId = channel.Uris[0], Title = channel.Name,
        });
        await Done(live.RefreshLibraryTabsAsync());
        live.ChannelTab = 2;
        Assert.Same(channel, Assert.Single(live.VisibleChannels));
        await Done(live.ToggleChannelFavoriteCommand.ExecuteAsync(null));
        Assert.Empty(await context.Services.Store.GetFavoritesAsync(FavoriteKind.Live));
    }

    [AvaloniaFact]
    public async Task Vod_UsesLoadedSourceForDetailAndDropsStaleCategoryResults()
    {
        using var context = new Context();
        var first = Gate<MediaPage>();
        context.Source.Items = (category, _, _) => category == "a" ? first.Task : Task.FromResult(new MediaPage([Item("new")], 1, 3));
        var vod = new VodViewModel(context.Services, context.Main, () => context.Source);
        var loading = vod.LoadAsync();
        vod.SelectedCategory = new Category("b", "新分类");
        Dispatcher.UIThread.RunJobs();
        first.SetResult(new MediaPage([Item("old")], 1, 2));
        await Done(loading);
        Assert.Equal("new", Assert.Single(vod.Items).Id);
        Assert.Equal(1, vod.Page);
        vod.OpenItemCommand.Execute(vod.Items[0]);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("new", context.Main.Detail.Detail!.Item.Id);
    }

    [AvaloniaFact]
    public async Task Search_HistorySurvivesRecreationAndProducesLocalSuggestions()
    {
        using var context = new Context();
        var search = new SearchViewModel((_, _) => Task.FromResult<IReadOnlyList<(SourceInfo, MediaItem)>>([]), context.Main, context.Preferences);
        search.Keyword = "庆余年";
        await Done(search.RunSearchCommand.ExecuteAsync(null));
        var fresh = new SearchViewModel((_, _) => Task.FromResult<IReadOnlyList<(SourceInfo, MediaItem)>>([]), context.Main, context.Preferences);
        Assert.Equal("庆余年", Assert.Single(fresh.SearchHistory));
        fresh.Keyword = "庆";
        Assert.Equal("庆余年", Assert.Single(fresh.Suggestions).Text);
        Assert.True(fresh.ShowSuggestions);
    }

    [AvaloniaFact]
    public void Settings_ThemePreferenceIsPersistedAndRestored()
    {
        using var context = new Context();
        context.Main.Settings.ThemeIndex = 2;
        Assert.Equal(2, context.Preferences.GetInt("ui.theme"));
        Assert.Equal(2, new SettingsViewModel(context.Services, context.Main).ThemeIndex);
    }

    [AvaloniaFact]
    public async Task Live_FailedConfigurationDoesNotReplaceCurrentUrlOrChannels()
    {
        using var context = new Context();
        context.Services.CurrentLiveConfig = "https://example.com/working";
        var channel = new LiveChannel { Name = "当前频道", Uris = ["https://example.com/live"] };
        var live = new LiveViewModel(context.Services, context.Main, (url, _) => url.EndsWith("working")
            ? Task.FromResult(new List<LiveGroup> { new("组", [channel], false) })
            : Task.FromException<List<LiveGroup>>(new IOException("源失败")));
        Assert.True(await live.ApplyConfigurationAsync("https://example.com/working"));
        Assert.False(await live.ApplyConfigurationAsync("https://example.com/broken"));
        Assert.Equal("https://example.com/working", context.Services.CurrentLiveConfig);
        Assert.Same(channel, Assert.Single(Assert.Single(live.Groups).Channels));
        Assert.Contains("源失败", context.Main.StatusMessage);
    }

    [AvaloniaFact]
    public async Task Live_EmptyConfigurationDoesNotPersistSuccess()
    {
        using var context = new Context();
        var live = new LiveViewModel(context.Services, context.Main, (_, _) => Task.FromResult(new List<LiveGroup>()));
        Assert.False(await live.ApplyConfigurationAsync("https://example.com/empty"));
        Assert.Empty(context.Services.CurrentLiveConfig!);
        Assert.Contains("没有可播放频道", context.Main.StatusMessage);
    }

    [AvaloniaFact]
    public async Task Live_EpgSubscriptionRejectsUnsupportedProtocolWithoutReplacingCache()
    {
        using var context = new Context();
        await Assert.ThrowsAsync<InvalidDataException>(() => context.Main.Live.RefreshEpgSubscriptionAsync("file:///secret"));
        Assert.Empty(context.Preferences.GetString("live.epg-url"));
    }

    [AvaloniaFact]
    public async Task Vod_FilterValuesReachSourceAndClearResetsThem()
    {
        using var context = new Context();
        IReadOnlyDictionary<string, string>? received = null;
        context.Source.FilteredItems = (_, _, filters, _) =>
        {
            received = filters;
            return Task.FromResult(new MediaPage([], 1, 1));
        };
        var vod = new VodViewModel(context.Services, context.Main, () => context.Source);
        await Done(vod.LoadAsync());
        vod.FilterYear = "2026"; vod.FilterArea = "大陆";
        await Done(vod.ApplyFiltersCommand.ExecuteAsync(null));
        Assert.Equal("2026", received!["year"]);
        Assert.Equal("大陆", received["area"]);
        await Done(vod.ClearFiltersCommand.ExecuteAsync(null));
        Assert.Equal("", received!["year"]);
        Assert.Equal(1, vod.Page);
    }

    [AvaloniaFact]
    public async Task Player_SubtitleStyleIsBoundedPersistedAndAppliedOnOpen()
    {
        using var context = new Context();
        var player = new PlayerViewModel(context.Engine, context.Store, context.Main, context.Preferences);
        player.SubtitleDelay = 500;
        player.SubtitleFontSize = 1;
        Assert.Equal(120, player.SubtitleDelay);
        Assert.Equal(12, player.SubtitleFontSize);
        var restored = new PlayerViewModel(context.Engine, context.Store, context.Main, context.Preferences);
        await Done(restored.PlayResolvedAsync(_ => Task.FromResult(Request("ep1"))));
        Assert.Equal(120, context.Engine.SubtitleDelay);
        Assert.Equal(12, context.Engine.SubtitleFontSize);
        restored.SubtitleDelay = double.NaN;
        Assert.Equal(0, restored.SubtitleDelay);
    }

    [AvaloniaFact]
    public void Player_PreferredParserPersistsWithoutInterruptingCurrentPlayback()
    {
        using var context = new Context();
        var player = new PlayerViewModel(context.Engine, context.Store, context.Main, context.Preferences);
        var intent = player.CurrentSessionId;
        player.PreferredParser = "https://parse.example/?url=";
        Assert.Equal(intent, player.CurrentSessionId);
        Assert.Equal(player.PreferredParser, new PlayerViewModel(context.Engine, context.Store, context.Main, context.Preferences).PreferredParser);
    }

    [AvaloniaFact]
    public async Task Settings_LiveSubscriptionHistoryRestoresAndProtectsCurrentEntry()
    {
        using var context = new Context();
        await context.Services.Store.AddAsync(new ConfigSubscription { Url = "https://example.com/live", Name = "直播", Kind = ConfigKind.Live });
        var stored = Assert.Single(await context.Services.Store.ListAsync(ConfigKind.Live));
        await context.Services.Store.SetActiveAsync(ConfigKind.Live, stored.Id);
        await Done(context.Main.Settings.LoadLiveConfigHistoryCommand.ExecuteAsync(null));
        var entry = Assert.Single(context.Main.Settings.LiveConfigHistory);
        Assert.True(entry.Current);
        await Done(context.Main.Settings.DeleteLiveConfigCommand.ExecuteAsync(entry));
        Assert.Single(await context.Services.Store.ListAsync(ConfigKind.Live));
        Assert.Contains("先切换", context.Main.Settings.Message);
    }

    [AvaloniaFact]
    public async Task Detail_FavoriteFailureRollsBackOnlyThePageThatStartedIt()
    {
        using var context = new Context();
        await Done(context.Main.Detail.OpenAsync("source", Item("first")));
        context.Store.SetFavorite = (_, _) => Task.FromException(new IOException("磁盘失败"));
        await Done(context.Main.Detail.ToggleFavoriteCommand.ExecuteAsync(null));
        Assert.False(context.Main.Detail.IsFavorite);
        Assert.Contains("磁盘失败", context.Main.StatusMessage);
        var gate = Gate<bool>();
        context.Store.SetFavorite = async (_, _) => { await gate.Task; throw new IOException("旧保存失败"); };
        var saving = context.Main.Detail.ToggleFavoriteCommand.ExecuteAsync(null);
        await Done(context.Main.Detail.OpenAsync("source", Item("second")));
        context.Main.Detail.IsFavorite = true;
        context.Main.StatusMessage = "新页面";
        gate.SetResult(true);
        await Done(saving);
        Assert.True(context.Main.Detail.IsFavorite);
        Assert.Equal("新页面", context.Main.StatusMessage);
    }

    [AvaloniaFact]
    public async Task Detail_CrossSourceSearchUsesActualMediaTitle()
    {
        using var context = new Context();
        await Done(context.Main.Detail.OpenAsync("source", Item("真实片名")));
        context.Main.Detail.SearchOtherSourcesCommand.Execute(null);
        Assert.Equal(AppPage.Search, context.Main.Page);
        Assert.Equal("真实片名", context.Main.Search.Keyword);
    }

    [AvaloniaFact]
    public void Danmaku_TimelineHandlesPauseSeekAndLimitsVisibleComments()
    {
        var comments = Enumerable.Range(0, 50000).Select(index => new DanmakuComment(index / 100d, index.ToString(), 0xFFFFFF, DanmakuMode.Scroll)).ToArray();
        var active = DanmakuLayer.ActiveAt(comments, 100);
        Assert.Equal(60, active.Count);
        Assert.All(active, comment => Assert.InRange(comment.Seconds, 92.001, 100));
        Assert.Equal(active, DanmakuLayer.ActiveAt(comments, 100));
        var sought = DanmakuLayer.ActiveAt(comments, 10);
        Assert.All(sought, comment => Assert.InRange(comment.Seconds, 2.001, 10));
        Assert.Empty(DanmakuLayer.ActiveAt(comments, double.NaN));
    }

    [AvaloniaFact]
    public async Task Player_OnlyRealEndedMarksWatchedAndIncognitoSuppressesIt()
    {
        using var context=new Context();var player=context.Main.Player;
        await Done(player.PlayResolvedAsync(_=>Task.FromResult(Request("ep1"))));
        context.Engine.EmitCurrent(PlaybackState.Playing,89000,90000);
        Assert.Empty(context.Store.Watched);
        context.Engine.EmitCurrent(PlaybackState.Ended,90000,90000);
        Assert.Equal("ep1",Assert.Single(context.Store.Watched));
        context.Engine.EmitCurrent(PlaybackState.Ended,90000,90000);
        Assert.Single(context.Store.Watched);
        player.Incognito=true;
        await Done(player.PlayResolvedAsync(_=>Task.FromResult(Request("ep2"))));
        context.Engine.EmitCurrent(PlaybackState.Ended,90000,90000);
        Assert.Single(context.Store.Watched);
    }

    [AvaloniaFact]
    public void Settings_EightSectionsShowOnlySelectedForm()
    {
        using var context=new Context();
        var view=new SettingsView { DataContext=context.Main };
        var window=new Window { Content=view };
        window.Show();Dispatcher.UIThread.RunJobs();
        try
        {
        var names=new[]{"SourcesSection","PlaybackSection","DanmakuSection","SubtitleSection","InterfaceSection","DataSection","RemoteSection","AboutSection"};
        for(var section=0;section<names.Length;section++)
        {
            context.Main.Settings.Section=section;
            Dispatcher.UIThread.RunJobs();
            for(var index=0;index<names.Length;index++)Assert.Equal(index==section,view.FindControl<StackPanel>(names[index])!.IsVisible);
        }
        Assert.Same(context.Main.Player,context.Main.Settings.PlayerSettings);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Search_DeleteAndClearHistoryPersistAcrossRecreation()
    {
        using var context=new Context();
        var search=new SearchViewModel((_,_)=>Task.FromResult<IReadOnlyList<(SourceInfo,MediaItem)>>([]),context.Main,context.Preferences);
        search.Keyword="one";await Done(search.RunSearchCommand.ExecuteAsync(null));
        search.Keyword="two";await Done(search.RunSearchCommand.ExecuteAsync(null));
        search.DeleteSearchHistoryCommand.Execute("one");
        var restored=new SearchViewModel((_,_)=>Task.FromResult<IReadOnlyList<(SourceInfo,MediaItem)>>([]),context.Main,context.Preferences);
        Assert.Equal("two",Assert.Single(restored.SearchHistory));
        restored.ClearSearchHistoryCommand.Execute(null);
        Assert.Empty(new SearchViewModel((_,_)=>Task.FromResult<IReadOnlyList<(SourceInfo,MediaItem)>>([]),context.Main,context.Preferences).SearchHistory);
    }

    [AvaloniaFact]
    public async Task ShutdownCancelsPendingSourceLoadsAndStopsPlayerBeforeDispose()
    {
        using var context=new Context();
        var old=Gate<MediaDetail>();context.Source.Load=(_,_)=>old.Task;
        var pending=context.Main.Detail.OpenAsync("source",Item("pending"));
        await Done(context.Main.Player.PlayResolvedAsync(_=>Task.FromResult(Request("ep1"))));
        await Done(context.Main.ShutdownAsync());
        old.SetResult(Detail("pending"));await Done(pending);
        Assert.Null(context.Main.Detail.Detail);
        Assert.False(context.Main.Player.Visible);
        Assert.True(context.Engine.Stops>0);
        context.Services.Dispose();context.Services.Dispose();
    }

    [AvaloniaFact]
    public void Settings_MediaInboxOpensUploadedMediaIncludingM4a()
    {
        using var context=new Context();var directory=Path.Combine(context.Services.DataDir,"media-inbox");Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory,"clip.m4a"),[1,2,3]);
        context.Main.Settings.OpenMediaInboxCommand.Execute(null);
        Assert.Equal(AppPage.Files,context.Main.Page);
        Assert.Contains(context.Main.Files.Entries,entry=>entry.Name=="clip.m4a");
    }

    [AvaloniaFact]
    public async Task Vod_WindowWidthAndPosterDensityReflowWithoutReloadingSource()
    {
        using var context=new Context();var calls=0;
        context.Source.Items=(_,_,_)=>{calls++;return Task.FromResult(new MediaPage(Enumerable.Range(0,20).Select(index=>Item(index.ToString())).ToArray(),1,1));};
        var vod=new VodViewModel(context.Services,context.Main,()=>context.Source);await Done(vod.LoadAsync());
        vod.SetGridWidth(400);Assert.Equal(10,vod.Rows.Count);
        context.Preferences.Set("ui.poster-density",2);vod.RefreshPosterDensity();
        Assert.Equal(130,vod.PosterWidth);vod.SetGridWidth(600);Assert.Equal(7,vod.Rows.Count);vod.SetGridWidth(620);Assert.Equal(5,vod.Rows.Count);
        Assert.Equal(1,calls);
    }

    [AvaloniaFact]
    public void Danmaku_DisplayPreferencesPersistAndBoundVisibleCount()
    {
        using var context=new Context();var player=new PlayerViewModel(context.Engine,context.Store,context.Main,context.Preferences);
        player.DanmakuOpacity=2;player.DanmakuLimit=3;
        Assert.Equal(1,player.DanmakuOpacity);Assert.Equal(5,player.DanmakuLimit);
        var restored=new PlayerViewModel(context.Engine,context.Store,context.Main,context.Preferences);
        Assert.Equal(1,restored.DanmakuOpacity);Assert.Equal(5,restored.DanmakuLimit);
        var comments=Enumerable.Range(0,100).Select(index=>new DanmakuComment(1,index.ToString(),0xFFFFFF,DanmakuMode.Scroll)).ToArray();
        Assert.Equal(5,DanmakuLayer.ActiveAt(comments,2,restored.DanmakuLimit).Count);
    }

    [AvaloniaFact]
    public async Task Vod_RepeatedEmptySourceLoadsDoNotRetainDisposedCancellationSource()
    {
        using var context=new Context();var vod=new VodViewModel(context.Services,context.Main,()=>null);
        await Done(vod.LoadAsync());await Done(vod.LoadAsync());vod.CancelPending();
        Assert.False(vod.Loading);Assert.Empty(vod.Items);
    }

    [AvaloniaFact]
    public async Task Search_PartialSourceFailuresAreVisibleInsteadOfPlainZeroResults()
    {
        using var context=new Context();
        var search=new SearchViewModel((_,_)=>Task.FromResult<IReadOnlyList<(SourceInfo,MediaItem)>>([]),context.Main);
        var site=new SourceInfo{Key="bad",Name="失败站",Runtime=SourceRuntime.MacCms};
        search.SetDetailedSearch((_,_)=>Task.FromResult(new VodBox.Infrastructure.AggregateSearchResult([],[(site,"超时")])));
        search.Keyword="片名";await Done(search.RunSearchCommand.ExecuteAsync(null));
        Assert.Contains("失败站",search.Summary);Assert.Contains("超时",search.Summary);Assert.Contains("1 个站点失败",search.Summary);
    }

    [AvaloniaFact]
    public async Task Settings_ChangingLanModeRequiresRestartAndClearsPairingOnStop()
    {
        using var context=new Context();var settings=context.Main.Settings;
        await Done(settings.StartLocalControlCommand.ExecuteAsync(null));
        Assert.False(context.Services.LocalControl.LanEnabled);
        settings.LanControl=true;await Done(settings.StartLocalControlCommand.ExecuteAsync(null));
        Assert.Contains("先关闭",settings.Message);Assert.False(context.Services.LocalControl.LanEnabled);
        await Done(settings.StopLocalControlCommand.ExecuteAsync(null));
        await Done(settings.StartLocalControlCommand.ExecuteAsync(null));
        Assert.True(context.Services.LocalControl.LanEnabled);Assert.Contains("配对码",settings.PairingInformation);
        await Done(settings.StopLocalControlCommand.ExecuteAsync(null));Assert.Equal("",settings.PairingInformation);
    }

    [AvaloniaFact]
    public async Task Live_CancelledProgrammeImportCannotReplaceExistingCache()
    {
        using var context=new Context();
        using var first=new MemoryStream(System.Text.Encoding.UTF8.GetBytes("<tv/>"));
        await Done(context.Main.Live.ImportXmlTvAsync(first));
        var path=Path.Combine(context.Services.DataDir,"programmes.xml");var before=File.ReadAllText(path);
        using var cancelled=new CancellationTokenSource();cancelled.Cancel();
        using var input=new MemoryStream(System.Text.Encoding.UTF8.GetBytes("<tv><channel id='cancelled'/></tv>"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>context.Main.Live.ImportXmlTvAsync(input,ct:cancelled.Token));
        Assert.Equal(before,File.ReadAllText(path));
    }

    [AvaloniaFact]
    public async Task Player_CloseWaitsForQueuedWatchedRecordBeforeServiceCanDispose()
    {
        using var context=new Context();var gate=Gate<bool>();context.Store.SaveWatched=async()=>await gate.Task;
        await Done(context.Main.Player.PlayResolvedAsync(_=>Task.FromResult(Request("ep1"))));
        context.Engine.EmitCurrent(PlaybackState.Ended,90000,90000);
        var closing=context.Main.Player.CloseCommand.ExecuteAsync(null);
        Assert.False(closing.IsCompleted);
        gate.SetResult(true);await Done(closing);Assert.False(context.Main.Player.Visible);
    }

    [AvaloniaFact]
    public void Player_CompactRestoreClampsDisconnectedMonitorPosition()
    {
        var area=new Avalonia.PixelRect(0,0,1920,1080);
        Assert.Equal(new Avalonia.PixelPoint(0,280),PlayerOverlay.ClampRestoredPosition(new Avalonia.PixelPoint(-2000,1500),area,1280,800,1));
        Assert.Equal(new Avalonia.PixelPoint(640,280),PlayerOverlay.ClampRestoredPosition(new Avalonia.PixelPoint(3000,1500),area,1280,800,1));
        Assert.Equal(new Avalonia.PixelPoint(0,0),PlayerOverlay.ClampRestoredPosition(new Avalonia.PixelPoint(3000,1500),area,1280,800,2));
    }

    [AvaloniaFact]
    public void Danmaku_PlacementDropsOverflowInsteadOfOverlappingSameLane()
    {
        var items=Enumerable.Range(0,30).Select(index=>(new DanmakuComment(1,index.ToString(),0xFFFFFF,DanmakuMode.Top),200d)).ToArray();
        var placed=DanmakuLayer.Place(items,2,800,300);
        Assert.True(placed.Count<items.Length);Assert.Equal(placed.Count,placed.Select(item=>item.Y).Distinct().Count());
        Assert.Equal(placed,DanmakuLayer.Place(items,2,800,300));
        Assert.Empty(DanmakuLayer.Place(items,10,800,300));
    }

    [AvaloniaFact]
    public void PairingQrProducesRealPngBitmapWithoutEmbeddingAuthorizationCode()
    {
        using var image=PairingQr.Create("http://192.168.1.100:8080");
        Assert.True(image.PixelSize.Width>100);Assert.Equal(image.PixelSize.Width,image.PixelSize.Height);
        Assert.Throws<ArgumentException>(()=>PairingQr.Create("file:///secret"));
    }

    [AvaloniaFact]
    public async Task Settings_OnlineSubtitleMissingCredentialReportsFailureWithoutPersistingIt()
    {
        using var context=new Context();var settings=context.Main.Settings;
        settings.SubtitleQuery="片名";await Done(settings.SearchOnlineSubtitlesCommand.ExecuteAsync(null));
        Assert.Contains("配置",settings.Message);Assert.Empty(settings.OnlineSubtitles);Assert.False(settings.SubtitleSearching);
        settings.SubtitleCredential="memory-only-test";settings.CancelSubtitleSearch();Assert.Equal("",settings.SubtitleCredential);
        var preferences=await context.Services.Store.ExportPreferencesAsync();Assert.DoesNotContain(preferences.Values,value=>value.Contains("memory-only-test"));
    }

    [AvaloniaFact]
    public async Task Settings_OnlineSubtitleDownloadRequiresPlayingMediaAndSafeFileType()
    {
        using var context=new Context();var settings=context.Main.Settings;
        await Done(settings.LoadOnlineSubtitleCommand.ExecuteAsync(new OnlineSubtitleFile("clip.srt","https://files.example/a.srt")));
        Assert.Contains("先播放",settings.Message);
        await Done(context.Main.Player.PlayResolvedAsync(_=>Task.FromResult(Request("ep1"))));
        await Done(settings.LoadOnlineSubtitleCommand.ExecuteAsync(new OnlineSubtitleFile("archive.zip","https://files.example/archive.zip")));
        Assert.Contains("类型不支持",settings.Message);
        Assert.False(Directory.Exists(Path.Combine(context.Services.DataDir,"subtitles")));
    }

    [AvaloniaFact]
    public async Task Settings_SubtitleCacheCleanupPreservesManualFilesAndRejectsActivePlayback()
    {
        using var context=new Context();var directory=Path.Combine(context.Services.DataDir,"subtitles");Directory.CreateDirectory(directory);
        var owned=Path.Combine(directory,Guid.NewGuid().ToString("N")+".srt");var manual=Path.Combine(directory,"manual.srt");File.WriteAllText(owned,"test");File.WriteAllText(manual,"manual");
        await Done(context.Main.Player.PlayResolvedAsync(_=>Task.FromResult(Request("ep1"))));
        context.Main.Settings.ClearSubtitleCacheCommand.Execute(null);Assert.True(File.Exists(owned));
        await Done(context.Main.Player.CloseCommand.ExecuteAsync(null));context.Main.Settings.ClearSubtitleCacheCommand.Execute(null);
        Assert.False(File.Exists(owned));Assert.True(File.Exists(manual));
    }

    [AvaloniaFact]
    public async Task Player_SubtitleLoadReturnsActualSuccessAndFailure()
    {
        using var context=new Context();var player=context.Main.Player;
        Assert.False(await player.LoadSubtitleAsync("none.srt",player.CurrentSessionId));
        await Done(player.PlayResolvedAsync(_=>Task.FromResult(Request("ep1"))));
        Assert.True(await player.LoadSubtitleAsync("current.srt",player.CurrentSessionId));
        context.Engine.SubtitleError=new IOException("字幕失败");
        Assert.False(await player.LoadSubtitleAsync("failed.srt",player.CurrentSessionId));
    }

    [AvaloniaFact]
    public void ThemeSwitchChangesActualWindowBackdropAndSidebarText()
    {
        using var context=new Context();var window=new VodBox.Desktop.MainWindow{DataContext=context.Main};
        window.Show();
        try
        {
            window.RequestedThemeVariant=Avalonia.Styling.ThemeVariant.Light;Dispatcher.UIThread.RunJobs();window.UpdateLayout();
            var background=Assert.IsType<Avalonia.Media.SolidColorBrush>(window.FindControl<Avalonia.Controls.Shapes.Rectangle>("PageBackdrop")!.Fill);
            Assert.Equal(Avalonia.Media.Color.Parse("#F3F3F3"),background.Color);
            window.RequestedThemeVariant=Avalonia.Styling.ThemeVariant.Dark;Dispatcher.UIThread.RunJobs();window.UpdateLayout();
            background=Assert.IsType<Avalonia.Media.SolidColorBrush>(window.FindControl<Avalonia.Controls.Shapes.Rectangle>("PageBackdrop")!.Fill);
            Assert.Equal(Avalonia.Media.Color.Parse("#202020"),background.Color);
        }
        finally{window.Close();}
    }

    [AvaloniaFact]
    public void SidebarNavigationUsesDarkTextInLightThemeAndWhiteTextInDarkTheme()
    {
        using var context=new Context();var nav=new NavItemView{Label="首页",IsSelected=true};
        var window=new Window{Content=nav,RequestedThemeVariant=Avalonia.Styling.ThemeVariant.Light};window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();Assert.Equal(Avalonia.Media.Color.Parse("#202020"),Assert.IsType<Avalonia.Media.SolidColorBrush>(nav.Foreground).Color);
            window.RequestedThemeVariant=Avalonia.Styling.ThemeVariant.Dark;Dispatcher.UIThread.RunJobs();
            Assert.Equal(Avalonia.Media.Colors.White,Assert.IsType<Avalonia.Media.SolidColorBrush>(nav.Foreground).Color);
        }
        finally{window.Close();}
    }

    [AvaloniaFact]
    public void Settings_WallpaperPreferencePersistsAndClearRemovesIt()
    {
        using var context=new Context();var path=Path.Combine(context.Services.DataDir,"wallpaper.png");
        using(var bitmap=new Avalonia.Media.Imaging.WriteableBitmap(new Avalonia.PixelSize(2,2),new Avalonia.Vector(96,96),Avalonia.Platform.PixelFormat.Bgra8888,Avalonia.Platform.AlphaFormat.Premul))bitmap.Save(path,new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        context.Main.Settings.SetWallpaper(path);Assert.Equal(path,context.Main.WallpaperPath);Assert.Equal(path,context.Preferences.GetString("ui.wallpaper"));
        context.Main.Settings.ClearWallpaperCommand.Execute(null);Assert.Equal("",context.Main.WallpaperPath);Assert.Equal("",context.Preferences.GetString("ui.wallpaper"));
        Assert.Throws<InvalidDataException>(()=>context.Main.Settings.SetWallpaper("https://example.com/picture.png"));
    }

    [AvaloniaFact]
    public async Task WallpaperLoadsOnlyWhenAttachedAndClearsOnPathChangeAndDetach()
    {
        using var context=new Context();var path=Path.Combine(context.Services.DataDir,"wallpaper-layer.png");
        using(var bitmap=new Avalonia.Media.Imaging.WriteableBitmap(new Avalonia.PixelSize(2,2),new Avalonia.Vector(96,96),Avalonia.Platform.PixelFormat.Bgra8888,Avalonia.Platform.AlphaFormat.Premul))bitmap.Save(path,new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        var wallpaper=new LocalWallpaper{Path=path};Assert.Null(wallpaper.Source);
        var window=new Window{Content=wallpaper};window.Show();
        try
        {
            await Done(wallpaper.LoadingTask);Dispatcher.UIThread.RunJobs();Assert.NotNull(wallpaper.Source);
            wallpaper.Path="";await Done(wallpaper.LoadingTask);Assert.Null(wallpaper.Source);
            wallpaper.Path=path;await Done(wallpaper.LoadingTask);Assert.NotNull(wallpaper.Source);
            window.Content=null;Assert.Null(wallpaper.Source);
        }
        finally{window.Close();}
    }

    [AvaloniaFact]
    public async Task Live_PasswordGroupRejectsPlaybackUntilUnlockedAndRelocksOnReload()
    {
        using var context=new Context();var groups=VodBox.Infrastructure.TxtLiveParser.Parse("保护组_test-pass,#genre#\n频道,https://example.com/live");
        var live=new LiveViewModel(context.Services,context.Main,(_,_)=>Task.FromResult(groups));
        Assert.True(await live.ApplyConfigurationAsync("https://example.com/list"));var channel=groups[0].Channels[0];
        live.PlayChannel(channel);Assert.Empty(context.Engine.Opened);
        live.RequestGroupUnlock(groups[0].Name);live.GroupPassword="wrong";live.UnlockGroupCommand.Execute(null);
        Assert.True(live.GroupUnlockOpen);Assert.Equal("",live.GroupPassword);Assert.DoesNotContain(channel,live.VisibleChannels);
        live.GroupPassword="test-pass";live.UnlockGroupCommand.Execute(null);Assert.False(live.GroupUnlockOpen);Assert.Contains(channel,live.VisibleChannels);
        live.PlayChannel(channel);Assert.Single(context.Engine.Opened);
        await live.LoadAsync();live.PlayChannel(channel);Assert.Single(context.Engine.Opened);Assert.DoesNotContain(channel,live.VisibleChannels);
    }

    [AvaloniaFact]
    public async Task Vod_DynamicFilterDefaultsAndSelectionReachActualSourceRequest()
    {
        using var context=new Context();context.Source.Filters=[new FilterGroup("year","年份",[new FilterValue("2026","2026"),new FilterValue("2025","2025")],"2026")];
        IReadOnlyDictionary<string,string>? received=null;
        context.Source.FilteredItems=(_,_,filters,_)=>{received=filters;return Task.FromResult(new MediaPage([],1,1));};
        var vod=new VodViewModel(context.Services,context.Main,()=>context.Source);await Done(vod.LoadAsync());
        Assert.Single(vod.DynamicFilters);Assert.Equal("2026",received!["year"]);
        vod.SelectFilter("year","2025");Dispatcher.UIThread.RunJobs();Assert.Equal("2025",received!["year"]);
        vod.SelectFilter("year","invalid");Assert.Equal("2025",received!["year"]);
        await Done(vod.ClearFiltersCommand.ExecuteAsync(null));Assert.Equal("2026",received!["year"]);
    }

    [AvaloniaFact]
    public async Task Player_ExplicitJsonEndpointResolvesBeforeOpeningEngine()
    {
        using var context=new Context();
        var builder=Microsoft.AspNetCore.Builder.WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        Microsoft.AspNetCore.Hosting.WebHostBuilderKestrelExtensions.UseKestrel(builder.WebHost,options=>options.Listen(System.Net.IPAddress.Loopback,0));
        await using var app=builder.Build();
        Microsoft.AspNetCore.Builder.EndpointRouteBuilderExtensions.MapGet(app,"/parse",async(Microsoft.AspNetCore.Http.HttpContext http)=>
        {
            Assert.Equal("https://video.example/watch?id=1",http.Request.Query["url"].ToString());
            await Microsoft.AspNetCore.Http.HttpResponseWritingExtensions.WriteAsync(http.Response,"{\"url\":\"https://cdn.example/video.mp4\"}");
        });
        await app.StartAsync();
        try
        {
            var request=Request("ep1") with{Uri="https://video.example/watch?id=1",Resolution=ResolutionKind.Json,ParseEndpoint=app.Urls.Single()+"/parse?url={url}"};
            await Done(context.Main.Player.PlayResolvedAsync(_=>Task.FromResult(request)));
            Assert.Equal("https://cdn.example/video.mp4",Assert.Single(context.Engine.Opened).Request.Uri);
            Assert.Equal(ResolutionKind.Direct,context.Engine.Opened[0].Request.Resolution);
            var count=context.Engine.Opened.Count;
            await Done(context.Main.Player.PlayResolvedAsync(_=>Task.FromResult(request with{ParseEndpoint=app.Urls.Single()+"/missing?url="})));
            Assert.Equal(count,context.Engine.Opened.Count);Assert.Equal(PlaybackState.Failed,context.Main.Player.State);
        }
        finally{await app.StopAsync();}
    }

    [AvaloniaFact]
    public async Task DanmakuRealHttpLoadRendersAndSwitchingMediaClearsComments()
    {
        using var context=new Context();var builder=Microsoft.AspNetCore.Builder.WebApplication.CreateSlimBuilder();builder.Logging.ClearProviders();
        Microsoft.AspNetCore.Hosting.WebHostBuilderKestrelExtensions.UseKestrel(builder.WebHost,options=>options.Listen(System.Net.IPAddress.Loopback,0));
        await using var app=builder.Build();
        Microsoft.AspNetCore.Builder.EndpointRouteBuilderExtensions.MapGet(app,"/comments",async(Microsoft.AspNetCore.Http.HttpContext http)=>await Microsoft.AspNetCore.Http.HttpResponseWritingExtensions.WriteAsync(http.Response,"<i><d p='1,5,25,16777215'>真实渲染弹幕</d></i>"));
        await app.StartAsync();
        var layer=new DanmakuLayer{DataContext=context.Main.Player};var window=new Window{Width=640,Height=360,Content=layer};window.Show();
        try
        {
            var player=context.Main.Player;player.DanmakuEnabled=true;
            await Done(player.PlayResolvedAsync(_=>Task.FromResult(Request("ep1")with{DanmakuUri=app.Urls.Single()+"/comments"})));
            await Done(player.DanmakuLoadingTask);Assert.Single(player.Danmaku);
            context.Engine.EmitCurrent(PlaybackState.Playing,2000,90000);layer.InvalidateVisual();window.UpdateLayout();
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);Dispatcher.UIThread.RunJobs();
            byte[] RenderPixels()
            {
                using var bitmap=new Avalonia.Media.Imaging.RenderTargetBitmap(new Avalonia.PixelSize(640,360),new Avalonia.Vector(96,96));
                layer.Measure(new Avalonia.Size(640,360));layer.Arrange(new Avalonia.Rect(0,0,640,360));bitmap.Render(layer);
                var bytes=new byte[640*360*4];var handle=System.Runtime.InteropServices.GCHandle.Alloc(bytes,System.Runtime.InteropServices.GCHandleType.Pinned);
                try{bitmap.CopyPixels(new Avalonia.PixelRect(0,0,640,360),handle.AddrOfPinnedObject(),bytes.Length,640*4);}finally{handle.Free();}
                return bytes;
            }
            var enabled=RenderPixels();player.DanmakuEnabled=false;var disabled=RenderPixels();
            Assert.False(enabled.SequenceEqual(disabled));
            await Done(player.PlayResolvedAsync(_=>Task.FromResult(Request("ep2"))));Assert.Empty(player.Danmaku);
        }
        finally{window.Close();await app.StopAsync();}
    }

    [AvaloniaFact]
    public void RemoteEntryClosesConfigDialogAndNavigatesToActualRemoteForm()
    {
        using var context=new Context();context.Main.Settings.ConfigDialogOpen=true;
        context.Main.Detail.OpenRemoteControlCommand.Execute(null);
        Assert.Equal(AppPage.Settings,context.Main.Page);Assert.Equal(6,context.Main.Settings.Section);Assert.False(context.Main.Settings.ConfigDialogOpen);
    }

    [AvaloniaFact]
    public async Task RemoteAdjustmentDispatchesToPlayerWithExplicitUiPump()
    {
        using var context=new Context();
        await Done(context.Main.Player.PlayResolvedAsync(_=>Task.FromResult(Request("ep1"))));
        context.Engine.EmitCurrent(PlaybackState.Playing,1000,90000);
        await Done(context.Main.Settings.StartLocalControlCommand.ExecuteAsync(null));
        try
        {
            var url=context.Services.LocalControl.Address!;
            foreach(var command in new[]{"volume&value=35","rate&value=1.5","seek&value=30"})
            {
                var request=Task.Run(async()=>
                {
                    using var client=new HttpClient{BaseAddress=new Uri(url),Timeout=TimeSpan.FromSeconds(5)};
                    client.DefaultRequestHeaders.Add("X-VodBox-Control","1");
                    using var response=await client.PostAsync("/adjust?command="+command,null);
                    return response.StatusCode;
                });
                var clock=System.Diagnostics.Stopwatch.StartNew();
                while(!request.IsCompleted&&clock.Elapsed<TimeSpan.FromSeconds(7))
                {Dispatcher.UIThread.RunJobs();Thread.Sleep(10);}
                Assert.True(request.IsCompleted,"HTTP callback did not finish while the UI dispatcher was pumped");
                Assert.Equal(System.Net.HttpStatusCode.NoContent,await request);
            }
            Assert.Equal(35,context.Main.Player.Volume);
            Assert.Equal(1.5,context.Main.Player.Rate);
            Assert.Contains(TimeSpan.FromSeconds(30),context.Engine.Seeks);
        }
        finally { await Done(context.Main.Settings.StopLocalControlCommand.ExecuteAsync(null)); }
    }

    [AvaloniaFact]
    public async Task RemoteConfigProposalOpensConfirmationWithoutLoadingOrPersistingIt()
    {
        using var context=new Context();context.Services.CurrentVodConfig="https://existing.example/config";
        await Done(context.Main.Settings.StartLocalControlCommand.ExecuteAsync(null));
        try
        {
            var address=context.Services.LocalControl.Address!;
            var request=Task.Run(async()=>
            {
                using var client=new HttpClient{BaseAddress=new Uri(address),Timeout=TimeSpan.FromSeconds(5)};
                client.DefaultRequestHeaders.Add("X-VodBox-Control","1");
                using var response=await client.PostAsync("/config?url=https%3A%2F%2Fproposed.example%2Fconfig",null);
                return response.StatusCode;
            });
            var clock=System.Diagnostics.Stopwatch.StartNew();
            while(!request.IsCompleted&&clock.Elapsed<TimeSpan.FromSeconds(7)){Dispatcher.UIThread.RunJobs();Thread.Sleep(10);}
            Assert.True(request.IsCompleted);Assert.Equal(System.Net.HttpStatusCode.NoContent,await request);
            Assert.True(context.Main.Settings.ConfigDialogOpen);Assert.Equal("https://proposed.example/config",context.Main.Settings.DialogUrl);
            Assert.Equal("https://existing.example/config",context.Services.CurrentVodConfig);
            Assert.Empty(await context.Services.Store.ListAsync(ConfigKind.Vod));Assert.Empty(context.Services.Registry.Sources);
        }
        finally{await Done(context.Main.Settings.StopLocalControlCommand.ExecuteAsync(null));}
    }

    [AvaloniaFact]
    public async Task LogicalBackupDoesNotContainInMemorySubtitleCredentialOrPairingCode()
    {
        using var context=new Context();var settings=context.Main.Settings;
        settings.SubtitleCredential="memory-only-subtitle-test";settings.LanControl=true;
        await Done(settings.StartLocalControlCommand.ExecuteAsync(null));
        try
        {
            var backup=await settings.ExportBackupAsync();
            Assert.DoesNotContain("memory-only-subtitle-test",backup);Assert.DoesNotContain("pairingCode",backup);
            var code=context.Services.LocalControl.PairingCode;Assert.NotNull(code);Assert.DoesNotContain(code!,backup);
        }
        finally{await Done(settings.StopLocalControlCommand.ExecuteAsync(null));}
    }

    [AvaloniaFact]
    public async Task RestoringProgrammeBackupRefreshesCurrentChannelWithoutImportFile()
    {
        using var context=new Context();var now=DateTimeOffset.Now;
        context.Main.Live.SetCurrent(new LiveChannel{Name="频道",TvgId="c1"});
        var xml=VodBox.Infrastructure.ProgrammeStore.Serialize([new Programme("c1","备份节目",now.AddMinutes(-10),now.AddMinutes(10))]);
        var backup=new LibraryBackup{Preferences=new(){["live.epg-xml"]=xml}};
        await Done(context.Main.Settings.RestoreBackupAsync(System.Text.Json.JsonSerializer.Serialize(backup,Json.TypeInfo<LibraryBackup>())));
        Assert.Equal("备份节目",context.Main.Live.CurrentChannel!.EpgNow);
    }

    [AvaloniaFact]
    public async Task ProgrammeDatabaseRemainsAuthoritativeWhenDerivedCacheWriteFails()
    {
        using var context=new Context();var cachePath=Path.Combine(context.Services.DataDir,"programmes.xml");Directory.CreateDirectory(cachePath);
        var now=DateTimeOffset.Now;var xml=VodBox.Infrastructure.ProgrammeStore.Serialize([new Programme("c1","数据库节目",now.AddMinutes(-5),now.AddMinutes(5))]);
        using var input=new MemoryStream(System.Text.Encoding.UTF8.GetBytes(xml));
        await Done(context.Main.Live.ImportXmlTvAsync(input));
        Assert.Contains("数据库节目",context.Preferences.GetString("live.epg-xml"));
        var fresh=new LiveViewModel(context.Services,context.Main);await Done(fresh.RestoreProgrammeCacheAsync());
        var channel=new LiveChannel{Name="频道",TvgId="c1"};fresh.SetCurrent(channel);Assert.Equal("数据库节目",channel.EpgNow);
    }

    [AvaloniaFact]
    public void PlayerOverlayVideoHostExistsEvenWithoutGeneratedNamedField()
    {
        using var context=new Context();var view=new PlayerOverlay{DataContext=context.Main};
        Assert.NotNull(view.FindControl<Panel>("VideoHost"));
        Assert.NotNull(view.FindControl<DanmakuLayer>("DanmakuCanvas"));
    }

    [AvaloniaFact]
    public async Task Live_GroupUnlockRateLimitAndRelockStopProtectedPlayback()
    {
        using var context=new Context();var group=Assert.Single(VodBox.Infrastructure.TxtLiveParser.Parse("组_test,#genre#\n频道,https://example.com/live"));
        var live=context.Main.Live;live.Groups.Add(group);live.SelectedGroup=group;live.RequestGroupUnlock(group.Name);
        for(var attempt=0;attempt<5;attempt++){live.GroupPassword="wrong";live.UnlockGroupCommand.Execute(null);}
        live.GroupPassword="test";live.UnlockGroupCommand.Execute(null);Assert.Contains("尝试过多",context.Main.StatusMessage);Assert.DoesNotContain(group.Channels[0],live.VisibleChannels);
        var unlocked=new LiveViewModel(context.Services,context.Main);unlocked.Groups.Add(group);unlocked.SelectedGroup=group;
        unlocked.RequestGroupUnlock(group.Name);unlocked.GroupPassword="test";unlocked.UnlockGroupCommand.Execute(null);unlocked.PlayChannel(group.Channels[0]);
        await Done(unlocked.LockGroupsCommand.ExecuteAsync(null));Assert.False(context.Main.Player.Visible);Assert.Null(unlocked.CurrentChannel);Assert.DoesNotContain(group.Channels[0],unlocked.VisibleChannels);
    }

    [AvaloniaTheory]
    [InlineData(1.7777777777777777,1600,900,false)]
    [InlineData(0.5625,1600,900,false)]
    [InlineData(2.4,1600,900,false)]
    [InlineData(0.05,800,600,false)]
    [InlineData(20,800,600,false)]
    [InlineData(1.7777777777777777,320,200,false)]
    [InlineData(0.5625,800,600,true)]
    public void PlayerWindowSizeKeepsVideoRatioAndRespectsScreenAndDimensionLimits(double ratio,double width,double height,bool compact)
    {
        var fit=PlayerWindowSizing.Fit(ratio,width,height,compact);
        Assert.Equal(ratio,fit.Size.Width/fit.Size.Height,10);
        Assert.InRange(fit.Size.Width,fit.Minimum.Width,fit.Maximum.Width);
        Assert.InRange(fit.Size.Height,fit.Minimum.Height,fit.Maximum.Height);
        Assert.True(fit.Maximum.Width<=width&&fit.Maximum.Height<=height);
        Assert.True(fit.Size.Width>0&&fit.Size.Height>0);
        Assert.True(fit.Maximum.Width<=(compact?960:1920)&&fit.Maximum.Height<=(compact?720:1080));
    }

    [AvaloniaFact]
    public void PlayerWindowSizeRejectsInvalidMetadataAndScreenBounds()
    {
        foreach(var ratio in new[]{double.NaN,double.PositiveInfinity,0,-1})Assert.Throws<ArgumentOutOfRangeException>(()=>PlayerWindowSizing.Fit(ratio,800,600));
        Assert.Throws<ArgumentOutOfRangeException>(()=>PlayerWindowSizing.Fit(16d/9,0,600));
    }

    [AvaloniaFact]
    public async Task PlayerWindowFitsPortraitVideoOnceAndRestoresOriginalShellConstraints()
    {
        using var context=new Context();
        var view=new PlayerOverlay{DataContext=context.Main};
        var window=new Window{Content=view,Width=1280,Height=800,MinWidth=960,MinHeight=600,MaxWidth=2000,MaxHeight=1200};window.Show();
        try
        {
            await Done(context.Main.Player.PlayResolvedAsync(_=>Task.FromResult(Request("ep1"))));
            context.Main.Player.VideoAspectRatio=9d/16;Dispatcher.UIThread.RunJobs();
            Assert.Equal(9d/16,window.Width/window.Height,10);
            Assert.True(window.Width<960);Assert.True(window.MinWidth<=window.Width);
            var fittedWidth=window.Width;var fittedHeight=window.Height;
            view.ToggleCompactWindow(window);Assert.Equal(9d/16,window.Width/window.Height,10);Assert.True(window.Topmost);
            view.RestoreWindow();Assert.Equal(fittedWidth,window.Width);Assert.Equal(fittedHeight,window.Height);Assert.False(window.Topmost);
            await Done(context.Main.Player.CloseCommand.ExecuteAsync(null));Dispatcher.UIThread.RunJobs();
            Assert.Equal(1280,window.Width);Assert.Equal(800,window.Height);Assert.Equal(960,window.MinWidth);Assert.Equal(600,window.MinHeight);
            Assert.Equal(2000,window.MaxWidth);Assert.Equal(1200,window.MaxHeight);
        }
        finally{window.Close();}
    }

    [AvaloniaFact]
    public void ClosingNativeWindowWithActiveCompactModeDoesNotAccessDisposedScreenOwner()
    {
        using var context=new Context();var overlay=new PlayerOverlay{DataContext=context.Main};
        var window=new Window{Content=overlay,Width=1280,Height=800};window.Show();
        overlay.ToggleCompactWindow(window);window.Close();overlay.RestoreWindow();
        Assert.False(context.Main.Player.CompactMode);
    }

    [AvaloniaFact]
    public async Task Live_AutomaticFallbackTriesEachLineOnceAndIgnoresStaleFailure()
    {
        using var context=new Context();var live=context.Main.Live;
        var channel=new LiveChannel{Name="频道",Uris=["https://example.com/one","https://example.com/two","https://example.com/three"]};
        live.Groups.Add(new LiveGroup("组",[channel],false));live.SelectedGroup=live.Groups[0];live.PlayChannel(channel);
        var first=context.Engine.Opened[0].Session;
        context.Engine.EmitCurrent(PlaybackState.Failed,0,0);Dispatcher.UIThread.RunJobs();
        await Task.Delay(20);Dispatcher.UIThread.RunJobs();Assert.Equal(channel.Uris[1],context.Engine.Opened[^1].Request.Uri);
        var count=context.Engine.Opened.Count;context.Engine.Emit(first,PlaybackState.Failed,0,0);Dispatcher.UIThread.RunJobs();Assert.Equal(count,context.Engine.Opened.Count);
        context.Engine.EmitCurrent(PlaybackState.Failed,0,0);await Task.Delay(20);Dispatcher.UIThread.RunJobs();Assert.Equal(channel.Uris[2],context.Engine.Opened[^1].Request.Uri);
        context.Engine.EmitCurrent(PlaybackState.Failed,0,0);Dispatcher.UIThread.RunJobs();Assert.Equal(3,context.Engine.Opened.Count);Assert.Contains("全部线路",context.Main.StatusMessage);
        await Done(context.Main.Player.CloseCommand.ExecuteAsync(null));
        context.Engine.EmitCurrent(PlaybackState.Failed,0,0);Dispatcher.UIThread.RunJobs();Assert.Equal(3,context.Engine.Opened.Count);
    }

    [AvaloniaFact]
    public async Task IndependentPlayerKeepsDetailUsableReusesWindowAndClosesWithoutShell()
    {
        using var context = new Context();
        var shell = new VodBox.Desktop.MainWindow { DataContext = context.Main };
        shell.Show();
        try
        {
            context.Main.Navigate(AppPage.Detail);
            var size = (shell.Width, shell.Height);
            await Done(context.Main.Player.PlayResolvedAsync(_ => Task.FromResult(Request("ep1"))));
            var player = Assert.IsType<PlayerWindow>(shell.PlaybackWindow);
            Assert.True(player.IsVisible);
            Assert.Null(player.Owner);
            Assert.Equal(AppPage.Detail, context.Main.Page);
            Assert.DoesNotContain(shell.GetVisualDescendants(), v => v is PlayerOverlay);
            await Done(context.Main.Player.PlayResolvedAsync(_ => Task.FromResult(Request("ep2"))));
            Assert.Same(player, shell.PlaybackWindow);
            Assert.Equal(size, (shell.Width, shell.Height));
            Assert.Equal("ep2", context.Main.Player.Title);
            await Done(context.Main.Player.Close());
            Assert.Null(shell.PlaybackWindow);
            Assert.True(shell.IsVisible);
            Assert.Equal(AppPage.Detail, context.Main.Page);
            await Done(context.Main.Player.PlayResolvedAsync(_ => Task.FromResult(Request("ep1"))));
            Assert.NotSame(player, shell.PlaybackWindow);
            await Done(context.Main.Player.Close());
        }
        finally { shell.Close(); }
    }

    [AvaloniaFact]
    public async Task PlayerNativeCloseStopsPlaybackAndRetainsMainWindow()
    {
        using var context = new Context();
        var shell = new VodBox.Desktop.MainWindow { DataContext = context.Main };
        shell.Show();
        try
        {
            await Done(context.Main.Player.PlayResolvedAsync(_ => Task.FromResult(Request("ep1"))));
            var player = Assert.IsType<PlayerWindow>(shell.PlaybackWindow);
            player.Close();
            await Done(context.Main.Player.CloseCommand.ExecutionTask ?? Task.CompletedTask);
            Dispatcher.UIThread.RunJobs();
            Assert.False(context.Main.Player.Visible);
            Assert.Null(shell.PlaybackWindow);
            Assert.True(shell.IsVisible);
        }
        finally { shell.Close(); }
    }

    [AvaloniaFact]
    public async Task PlayerPanelIsExactlyEightyPercentAndRatePopupDoesNotCycle()
    {
        using var context = new Context();
        await Done(context.Main.Player.PlayResolvedAsync(_ => Task.FromResult(Request("ep1"))));
        var window = new PlayerWindow { DataContext = context.Main, Width = 1000, Height = 600 };
        window.Show();
        try
        {
            window.UpdateLayout();
            var overlay=window.Overlay;
            var panel=overlay.FindControl<Border>("BottomControls")!;
            Assert.Equal(overlay.Bounds.Width * .8, panel.Bounds.Width, 5);
            Assert.InRange(panel.Bounds.Height, 50, 74);
            var button=overlay.FindControl<Button>("RateButton")!;
            button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(1, context.Main.Player.Rate);
            var menu=Assert.IsType<ContextMenu>(button.ContextMenu);
            Assert.True(menu.IsOpen);
            var choice=menu.Items.OfType<MenuItem>().Single(item=>item.Header?.ToString()=="1.5×");
            choice.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Equal(1.5,context.Main.Player.Rate);
            menu.Close();
            var pin=overlay.FindControl<Button>("TopmostButton")!;
            pin.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.True(window.Topmost);Assert.True(context.Main.Player.AlwaysOnTop);
            pin.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.False(window.Topmost);Assert.False(context.Main.Player.AlwaysOnTop);
        }
        finally { await Done(context.Main.Player.Close());window.CloseAfterPlayback(); }
    }

    private sealed class Context : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), $"vodbox-vm-race-{Guid.NewGuid():N}");
        private readonly AppServices _services;
        public AppServices Services => _services;
        public Source Source { get; } = new();
        public Store Store { get; } = new();
        public Engine Engine { get; } = new();
        public IPreferences Preferences => _services.Prefs;
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
        public IReadOnlyList<FilterGroup> Filters {get;set;}=[];
        public Task<IReadOnlyList<FilterGroup>> GetFiltersAsync(string categoryId,CancellationToken ct=default)=>Task.FromResult(Filters);
        public Task<IReadOnlyList<Category>> GetCategoriesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Category>>([new Category("a", "分类")]);
        public Func<CancellationToken, Task<MediaPage>> Home { get; set; } = _ => Task.FromResult(new MediaPage([], 1, 1));
        public Task<MediaPage> GetHomeAsync(CancellationToken ct = default) => Home(ct);
        public Func<string, int, CancellationToken, Task<MediaPage>> Items { get; set; } = (_, _, _) => Task.FromResult(new MediaPage([], 1, 1));
        public Func<string, int, IReadOnlyDictionary<string, string>?, CancellationToken, Task<MediaPage>>? FilteredItems { get; set; }
        public Task<MediaPage> GetItemsAsync(string categoryId, int page, IReadOnlyDictionary<string, string>? filters, CancellationToken ct = default) => FilteredItems?.Invoke(categoryId, page, filters, ct) ?? Items(categoryId, page, ct);
        public Task<MediaPage> SearchAsync(string query, int page, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class Store : ILibraryStore
    {
        public List<HistoryEntry> Saved { get; } = [];
        public List<string> Watched { get; } = [];
        public Func<Task> SaveWatched { get; set; }=()=>Task.CompletedTask;
        public Task MarkEpisodeWatchedAsync(string sourceKey,string mediaId,string lineId,string episodeId,CancellationToken ct=default) { Watched.Add(episodeId);return SaveWatched(); }
        public Func<HistoryEntry, Task> Save { get; set; } = _ => Task.CompletedTask;
        public Func<string, string, CancellationToken, Task<bool>> Favorite { get; set; } = (_, _, _) => Task.FromResult(false);
        public Func<string, string, CancellationToken, Task<HistoryEntry?>> History { get; set; } = (_, _, _) => Task.FromResult<HistoryEntry?>(null);
        public Task SaveHistoryAsync(HistoryEntry entry, CancellationToken ct = default) { Saved.Add(entry); return Save(entry); }
        public Task<bool> IsFavoriteAsync(string sourceKey, string mediaId, CancellationToken ct = default) => Favorite(sourceKey, mediaId, ct);
        public Task<HistoryEntry?> FindHistoryAsync(string sourceKey, string mediaId, CancellationToken ct = default) => History(sourceKey, mediaId, ct);
        public Task<IReadOnlyList<HistoryEntry>> GetHistoryAsync(int limit = 200, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<HistoryEntry>>(Saved.ToArray());
        public Task DeleteHistoryAsync(string sourceKey, string mediaId, CancellationToken ct = default) => Task.CompletedTask;
        public Task ClearHistoryAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Func<FavoriteEntry, bool, Task> SetFavorite { get; set; } = (_, _) => Task.CompletedTask;
        public Task SetFavoriteAsync(FavoriteEntry entry, bool favorite, CancellationToken ct = default) => SetFavorite(entry, favorite);
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
        public double AppliedRate { get; private set; } = 1;
        public Task SetRateAsync(double rate, CancellationToken ct = default) { AppliedRate = rate; return Task.CompletedTask; }
        public double? AppliedAspect { get; private set; }
        public Task SetAspectRatioAsync(double? ratio, CancellationToken ct = default) { AppliedAspect = ratio; return Task.CompletedTask; }
        public int AppliedVolume { get; private set; } = 80;
        public Task SetVolumeAsync(int volume, CancellationToken ct = default) { AppliedVolume = volume; return Task.CompletedTask; }
        public double SubtitleDelay { get; private set; }
        public int SubtitleFontSize { get; private set; }
        public Task SetSubtitleStyleAsync(double delaySeconds, int fontSize, CancellationToken ct = default)
        { SubtitleDelay = delaySeconds; SubtitleFontSize = fontSize; return Task.CompletedTask; }
        public List<string> Subtitles { get; } = [];
        public Exception? SubtitleError { get; set; }
        public Task LoadSubtitleAsync(string path, long sessionId, CancellationToken ct = default)
        {
            if (SubtitleError is not null) return Task.FromException(SubtitleError);
            Subtitles.Add(path);
            return Task.CompletedTask;
        }
        public IReadOnlyList<MediaTrack> GetTracks(TrackKind kind) => [];
        public Task SelectTrackAsync(TrackKind kind, string trackId, CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
