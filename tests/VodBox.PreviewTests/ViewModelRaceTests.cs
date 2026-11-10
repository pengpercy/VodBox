using Avalonia;
using Microsoft.Extensions.Logging;
using Avalonia.Headless.XUnit;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VodBox.Core;
using VodBox.Infrastructure;
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

    private static async Task Until(Func<bool> condition, int milliseconds = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not met");
            await Task.Delay(20);
        }
    }
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








    /// <summary>
    /// 搜索窗口按站点流式回填：先完成的站点立即上屏（JAR 站点首次搜索可达数十秒，不能拖住其它站点），
    /// 进度在汇总里可见；全部结束后汇总失败站点。
    /// </summary>




    /// <summary>点播页卡片：来源只在聚合搜索（跨站点）时显示；浏览单个站点时隐藏，避免每张卡片重复同一站名。</summary>










    /// <summary>
    /// 搜索窗口里打开影片：详情在搜索窗口内展示，主窗口不跳到详情页（主窗口自己的详情也不受影响）；
    /// 「返回搜索结果」、Esc、发起新搜索都回到结果网格。
    /// </summary>


    [AvaloniaFact]
    public async Task Home_LateLoadCannotReplaceNewRecommendationsOrHero()
    {
        using var context = new Context();
        var oldPage = Gate<MediaPage>();
        var calls = 0;
        context.Source.Home = _ => ++calls == 1 ? oldPage.Task : Task.FromResult(new MediaPage([new MediaItem { Id = "new", Title = "新推荐", Poster = "https://example/new.jpg" }], 1, 1));
        var home = new HomeViewModel(HomeLoader(context), context.Store, context.Main);
        var old = home.LoadAsync();
        await Done(home.LoadAsync());
        oldPage.SetResult(new MediaPage([new MediaItem { Id = "old", Title = "旧推荐" }], 1, 1));
        await Done(old);
        Assert.Equal("new", Assert.Single(home.Recommendations).Id);
        Assert.Equal("新推荐", home.HeroTitle);
        Assert.Equal("https://example/new.jpg", home.HeroPoster);
        Assert.Equal("豆瓣", home.SourceName);
        Assert.False(home.Loading);
    }

    [AvaloniaFact]
    public async Task Home_RecentEmptyFinishesWhileRecommendationsAreStillLoading()
    {
        using var context = new Context();
        var recommendations = Gate<MediaPage>();
        context.Source.Home = _ => recommendations.Task;
        var home = new HomeViewModel(HomeLoader(context), context.Store, context.Main);
        var load = home.LoadAsync();
        Dispatcher.UIThread.RunJobs();
        Assert.True(home.Loading);
        Assert.False(home.RecentLoading);
        Assert.False(home.ShowRecentSkeleton);
        Assert.True(home.ShowRecentEmpty);
        recommendations.SetResult(new MediaPage([], 1, 1));
        await Done(load);
        Assert.False(home.Loading);
        Assert.NotEmpty(home.RecommendationsHint);
    }

    [AvaloniaFact]
    public async Task Home_RecentFailureDoesNotPreventRecommendationsFromLoading()
    {
        using var context = new Context();
        context.Store.ReadHistory = (_, _) => Task.FromException<IReadOnlyList<HistoryEntry>>(new IOException("history unavailable"));
        context.Source.Home = _ => Task.FromResult(new MediaPage([new MediaItem { Id = "ok", Title = "推荐" }], 1, 1));
        var home = new HomeViewModel(HomeLoader(context), context.Store, context.Main);
        await Done(home.LoadAsync());
        Assert.Equal("ok", Assert.Single(home.Recommendations).Id);
        Assert.NotEmpty(home.RecentHint);
        Assert.False(home.ShowRecentEmpty);
        Assert.False(home.RecentLoading);
        Assert.False(home.Loading);
    }

    [AvaloniaFact]
    public async Task Home_RecommendationsFinishWhileRecentHistoryIsStillLoading()
    {
        using var context = new Context();
        var history = Gate<IReadOnlyList<HistoryEntry>>();
        context.Store.ReadHistory = (_, _) => history.Task;
        context.Source.Home = _ => Task.FromResult(new MediaPage([new MediaItem { Id = "ok", Title = "推荐" }], 1, 1));
        var home = new HomeViewModel(HomeLoader(context), context.Store, context.Main);
        var load = home.LoadAsync();
        Dispatcher.UIThread.RunJobs();
        Assert.False(home.Loading);
        Assert.True(home.ShowRecentSkeleton);
        Assert.Single(home.Recommendations);
        history.SetResult([]);
        await Done(load);
        Assert.False(home.RecentLoading);
    }

    [AvaloniaFact]
    public async Task Home_HeroUsesTheSourceThatProvidedRecommendations()
    {
        using var context = new Context();
        var loaded = Gate<string>();
        context.Source.Load = (id, _) => { loaded.TrySetResult(id); return Task.FromResult(Detail(id)); };
        context.Source.Home = _ => Task.FromResult(new MediaPage([new MediaItem { Id = "hero", Title = "首页推荐" }], 1, 1));
        var home = new HomeViewModel(HomeLoader(context), context.Store, context.Main);
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
        var started = Gate<bool>();
        var epg = Gate<byte[]>();
        var channel = new LiveChannel { Name = "新频道", Uris = ["https://example.com/new"] };
        context.Services.CurrentLiveConfig = "https://example.com/old";
        var live = new LiveViewModel(context.Services, context.Main, (url, _) =>
        {
            if (!url.EndsWith("old")) return Task.FromResult(new List<LiveGroup> { new("新组", [channel], false) });
            started.TrySetResult(true);
            return old.Task;
        }, (_, _) => epg.Task);
        var first = live.LoadAsync();
        await Done(started.Task);
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
        Assert.False(epg.Task.IsCompleted);
        epg.SetResult([]);
        await Done(live.EnsureBuiltInEpgAsync());
    }

    [AvaloniaFact]
    public async Task Live_RemovingConfigurationCancelsPendingLoadAndClearsOldGroups()
    {
        using var context = new Context();
        var pending = Gate<List<LiveGroup>>();
        var started = Gate<bool>();
        var epg = Gate<byte[]>();
        CancellationToken token = default;
        var live = new LiveViewModel(context.Services, context.Main, (_, ct) =>
        {
            token = ct;
            started.TrySetResult(true);
            return pending.Task;
        }, (_, _) => epg.Task);
        context.Services.CurrentLiveConfig = "https://example.com/list";
        var loading = live.LoadAsync();
        await Done(started.Task);
        context.Services.CurrentLiveConfig = null;
        await Done(live.LoadAsync());
        Assert.True(token.IsCancellationRequested);
        pending.SetResult([new LiveGroup("旧结果", [], false)]);
        await Done(loading);
        Assert.Empty(live.Groups);
        Assert.Empty(live.VisibleChannels);
        Assert.False(live.Loading);
        Assert.Null(live.CurrentChannel);
        Assert.False(epg.Task.IsCompleted);
        epg.SetResult([]);
        await Done(live.EnsureBuiltInEpgAsync());
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
    public async Task Vod_PagingKeepsCurrentCardsAndRollsBackOnFailure()
    {
        using var context = new Context();
        var next = Gate<MediaPage>();
        context.Source.Items = (_, page, _) => page == 1
            ? Task.FromResult(new MediaPage([new MediaItem { Id = "first", Title = "第一页" }], 1, 2))
            : next.Task;
        var vod = new VodViewModel(context.Services, context.Main, () => context.Source);
        await Done(vod.LoadAsync());
        var paging = vod.NextPage();
        Assert.True(vod.Paging);
        Assert.False(vod.Loading);
        Assert.Equal("first", Assert.Single(vod.Items).Id);
        next.SetException(new IOException("next page failed"));
        await Done(paging);
        Assert.Equal(1, vod.Page);
        Assert.False(vod.Paging);
        Assert.False(vod.Loading);
        Assert.Equal("first", Assert.Single(vod.Items).Id);
    }

    [AvaloniaFact]
    public async Task Vod_EmptyCategoriesFinishWithInlineHint()
    {
        using var context = new Context();
        context.Source.Categories = _ => Task.FromResult<IReadOnlyList<Category>>([]);
        var vod = new VodViewModel(context.Services, context.Main, () => context.Source);
        await Done(vod.LoadAsync());
        Assert.False(vod.Loading);
        Assert.False(vod.Paging);
        Assert.NotEmpty(vod.ItemsHint);
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
    public async Task Detail_CrossSourceSearchRequestsWindowWithActualMediaTitle()
    {
        using var context = new Context();
        await Done(context.Main.Detail.OpenAsync("source", Item("真实片名")));
        string? requested = null;
        context.Main.SearchRequested += keyword => requested = keyword;
        context.Main.Detail.SearchOtherSourcesCommand.Execute(null);
        // 用真实片名发起全站搜索；结果窗口由主窗口负责打开，VM 不再切主窗口内的搜索页。
        Assert.Equal("真实片名", requested);
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
        // 分区号 6（原推送与遥控）已移除，关于仍是 7。
        var sections=new (int Section,string Name)[]{(0,"SourcesSection"),(1,"PlaybackSection"),(2,"DanmakuSection"),(3,"SubtitleSection"),(4,"InterfaceSection"),(5,"DataSection"),(7,"AboutSection")};
        foreach(var (section,_) in sections)
        {
            context.Main.Settings.Section=section;
            Dispatcher.UIThread.RunJobs();
            foreach(var (other,name) in sections)Assert.Equal(other==section,view.FindControl<StackPanel>(name)!.IsVisible);
        }
        Assert.Null(view.FindControl<StackPanel>("RemoteSection"));
        Assert.Same(context.Main.Player,context.Main.Settings.PlayerSettings);
        }
        finally { window.Close(); }
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


    /// <summary>「打开文件」与拖放共用同一条打开路径：m3u 作为直播源打开，媒体文件直接播放，其他文件给出提示。</summary>
    [AvaloniaFact]
    public async Task OpenLocalFiles_PlaysMediaOpensPlaylistsAndRejectsOthers()
    {
        using var context=new Context();var directory=Path.Combine(context.Services.DataDir,"open-test");Directory.CreateDirectory(directory);
        var media=Path.Combine(directory,"clip.m4a");File.WriteAllBytes(media,[1,2,3]);
        var other=Path.Combine(directory,"notes.txt");File.WriteAllText(other,"x");
        var requests=0;context.Main.OpenFileRequested+=_=>requests++;
        context.Main.OpenFileCommand.Execute(null);
        Assert.Equal(1,requests);

        Assert.False(await context.Main.OpenLocalFilesAsync([other]));
        Assert.Contains("m3u",context.Main.StatusMessage);

        Assert.True(await context.Main.OpenLocalFilesAsync([media]));
        await Until(()=>context.Engine.Opened.Count>0);
        Assert.Equal(new Uri(media).AbsoluteUri,context.Engine.Opened[^1].Request.Uri);
        Assert.Equal("local",context.Engine.Opened[^1].Request.SourceKey);
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
            var background=Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(window.FindControl<Avalonia.Controls.Shapes.Rectangle>("PageBackdrop")!.Fill);
            Assert.Equal(Avalonia.Media.Color.Parse("#F3F3F3"),background.Color);
            window.RequestedThemeVariant=Avalonia.Styling.ThemeVariant.Dark;Dispatcher.UIThread.RunJobs();window.UpdateLayout();
            background=Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(window.FindControl<Avalonia.Controls.Shapes.Rectangle>("PageBackdrop")!.Fill);
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
            Dispatcher.UIThread.RunJobs();Assert.Equal(Avalonia.Media.Color.Parse("#202020"),Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(nav.Foreground).Color);
            window.RequestedThemeVariant=Avalonia.Styling.ThemeVariant.Dark;Dispatcher.UIThread.RunJobs();
            Assert.Equal(Avalonia.Media.Colors.White,Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(nav.Foreground).Color);
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

    private static VodResult FallbackSource(string key, int episodes = 3, params string[] lines) => new()
    {
        Id = key, Title = "繁花", Year = "2023", SourceKey = key, SourceName = key + "资源",
        Lines = (lines.Length == 0 ? ["m3u8"] : lines).Select(line => new PlaybackLine(line, line,
            Enumerable.Range(1, episodes).Select(i => new Episode($"{key}-{line}-{i}", $"第{i}集", $"https://{key}.example/{line}/{i}.m3u8")).ToArray())).ToArray(),
    };

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
        Assert.True(condition());
    }

    [AvaloniaFact]
    public async Task VodPlaybackFailureSwitchesToNextSourceKeepingEpisodeAndPosition()
    {
        using var context = new Context();
        context.Engine.Open = request => request.Uri.Contains("b.example") ? Task.CompletedTask : throw new InvalidOperationException("连接失败");
        await Done(context.Main.Detail.OpenGroupAsync([FallbackSource("a", 3, "m3u8", "backup"), FallbackSource("b", episodes: 1), FallbackSource("c")]));
        context.Main.Detail.SelectedEpisode = context.Main.Detail.SelectedLine!.Episodes[1];
        await Done(context.Main.Detail.PlayCommand.ExecuteAsync(null));
        // a 主线路失败 → 先换 a 的备用线路 → b 没有第 2 集跳过 → c 失败 → 全部失败。
        await WaitFor(() => context.Main.Player.Error?.Contains("已尝试全部播放源") == true);
        Assert.Equal(["https://a.example/m3u8/2.m3u8", "https://a.example/backup/2.m3u8", "https://c.example/m3u8/2.m3u8"],
            context.Engine.Opened.Select(item => item.Request.Uri));
        Assert.Equal("c", context.Main.Detail.CurrentSource!.Result.SourceKey);
        Assert.Equal("第2集", context.Main.Detail.SelectedEpisode!.Title);

        // 播放中途失败：换到下一个源同一集，并从失败处续播。
        context.Engine.Opened.Clear();
        context.Engine.Open = _ => Task.CompletedTask;
        await Done(context.Main.Detail.PickSourceCommand.ExecuteAsync(context.Main.Detail.Sources[0]));
        context.Main.Detail.SelectedEpisode = context.Main.Detail.SelectedLine!.Episodes[0];
        await Done(context.Main.Detail.PlayCommand.ExecuteAsync(null));
        context.Engine.EmitCurrent(PlaybackState.Playing, 42_000, 600_000);
        Dispatcher.UIThread.RunJobs();
        context.Engine.EmitCurrent(PlaybackState.Failed, 42_000, 600_000);
        await WaitFor(() => context.Engine.Opened.Count == 2 && context.Main.Player.State == PlaybackState.Playing);
        Assert.Equal("https://a.example/backup/1.m3u8", context.Engine.Opened[1].Request.Uri);
        Assert.Equal(42_000, context.Engine.Opened[1].Request.StartPositionMs);
        Assert.Equal("a", context.Main.Detail.CurrentSource!.Result.SourceKey);
        Assert.Equal("backup", context.Main.Detail.SelectedLine!.Name);
    }

    [AvaloniaFact]
    public async Task AudioOnlyDisablesVideoTrackAndDrivesTheAudioPage()
    {
        using var context = new Context();
        var overlay = new PlayerOverlay { DataContext = context.Main };
        var window = new Window { Width = 1280, Height = 720, Content = overlay };
        window.Show();
        try
        {
            var player = context.Main.Player;
            await Done(player.PlayResolvedAsync(_ => Task.FromResult(Request("ep1") with { Poster = "https://img.example/p.jpg" })));
            Assert.Equal((TrackKind.Video, "auto"), context.Engine.Selected[^1]);
            var page = overlay.FindControl<AudioModeView>("AudioMode")!;
            Assert.False(page.IsVisible);

            player.ToggleAudioOnlyCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            await WaitFor(() => context.Engine.Selected[^1] == (TrackKind.Video, "no"));
            Assert.True(page.IsVisible && page.IsActive);
            Assert.False(overlay.FindControl<Grid>("StatusLayer")!.IsVisible);
            Assert.Equal("https://img.example/p.jpg", player.Poster);

            // 播放中：唱片逐渐加速转动；暂停后惯性减速到停。
            for (var i = 0; i < 120; i++) page.Step(1 / 60d);
            Assert.InRange(page.SpinSpeed, 50, 61);
            Assert.Equal("正在收听", player.AudioStatusText);
            context.Engine.EmitCurrent(PlaybackState.Paused, 1000, 60_000);
            Dispatcher.UIThread.RunJobs();
            for (var i = 0; i < 60; i++) page.Step(1 / 60d);
            Assert.InRange(page.SpinSpeed, 1, 40);
            Assert.True(page.Step(10));
            Assert.Equal("已暂停", player.AudioStatusText);

            // 新会话沿用听视频；关闭后恢复画面。
            await Done(player.PlayResolvedAsync(_ => Task.FromResult(Request("ep2"))));
            Assert.Contains((TrackKind.Video, "no"), context.Engine.Selected.TakeLast(2));
            player.AudioOnly = false;
            Dispatcher.UIThread.RunJobs();
            await WaitFor(() => context.Engine.Selected[^1] == (TrackKind.Video, "auto"));
            Assert.False(page.IsActive);
            Assert.True(overlay.FindControl<Grid>("StatusLayer")!.IsVisible);

            // 不记住：关闭播放器后下次打开默认看视频。
            player.AudioOnly = true;
            await Done(player.CloseCommand.ExecuteAsync(null));
            Assert.False(player.AudioOnly);
            await Done(player.PlayResolvedAsync(_ => Task.FromResult(Request("ep3"))));
            Assert.Equal((TrackKind.Video, "auto"), context.Engine.Selected[^1]);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task RightClickMenuOffersCommonActionsAndTogglesStats()
    {
        using var context = new Context();
        var overlay = new PlayerOverlay { DataContext = context.Main };
        var window = new Window { Width = 1280, Height = 720, Content = overlay };
        window.Show();
        try
        {
            context.Main.Player.Play(new PlaybackRequest { Uri = "https://example.com/a.m3u8", Title = "片", SourceKey = "s", MediaId = "m", SourceName = "站" });
            await WaitFor(() => context.Main.Player.State == PlaybackState.Playing);
            var menu = overlay.BuildVideoMenu();
            Assert.Contains("glass", menu.Classes);
            var items = menu.Items.OfType<MenuItem>().ToDictionary(item => (string)item.Header!);
            foreach (var header in new[] { "暂停", "倍速", "画面比例", "播放设置", "听视频", "弹幕", "窗口置顶", "画中画", "全屏", "截图", "复制视频地址", "统计信息" })
                Assert.True(items.ContainsKey(header), header);
            Assert.NotEmpty(((IEnumerable<object>)items["播放设置"].ItemsSource!));
            Assert.False(items["统计信息"].IsChecked);

            items["统计信息"].RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
            Assert.True(context.Main.Player.StatsVisible);
            Assert.True(overlay.BuildVideoMenu().Items.OfType<MenuItem>().Single(item => (string)item.Header! == "统计信息").IsChecked);

            items["截图"].RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
            await WaitFor(() => context.Engine.Screenshots.Count == 1);
            Assert.StartsWith(PlayerViewModel.ScreenshotDirectory(), context.Engine.Screenshots[0]);
            Assert.EndsWith(".png", context.Engine.Screenshots[0]);

            // 听视频时不提供画面比例，截图置灰。
            context.Main.Player.AudioOnly = true;
            var audio = overlay.BuildVideoMenu().Items.OfType<MenuItem>().ToDictionary(item => (string)item.Header!);
            Assert.False(audio.ContainsKey("画面比例"));
            Assert.False(audio["截图"].IsEnabled);
            Assert.True(audio["听视频"].IsChecked);
        }
        finally { await Done(context.Main.Player.Close()); window.Close(); }
        Assert.False(context.Main.Player.StatsVisible);
    }

    [AvaloniaFact]
    public async Task StatsPanelSamplesEngineAndStaysInsideTheVideoWhenDragged()
    {
        using var context = new Context();
        context.Engine.Stats = new PlaybackStats
        {
            VideoWidth = 1920, VideoHeight = 1080, FramesPerSecond = 30, VideoCodec = "h264", HardwareDecoder = "videotoolbox",
            AudioCodec = "aac", AudioSampleRate = 48000, AudioChannels = 2, NetworkSpeed = 10_219_000, BufferSeconds = 16.57,
            DroppedFrames = 9, FrameNumber = 1216, Container = "hls",
        };
        var overlay = new PlayerOverlay { DataContext = context.Main };
        var window = new Window { Width = 1280, Height = 720, Content = overlay };
        window.Show();
        try
        {
            context.Main.Player.Play(new PlaybackRequest { Uri = "https://example.com/a.m3u8", Title = "片", SourceKey = "s", MediaId = "m", SourceName = "站" });
            await WaitFor(() => context.Main.Player.State == PlaybackState.Playing);
            var panel = overlay.FindControl<StatsPanel>("Stats")!;
            Assert.False(panel.IsVisible);
            context.Main.Player.StatsVisible = true;
            Assert.True(panel.IsVisible);
            await panel.SampleAsync();
            window.UpdateLayout();
            string Text(string name) => panel.FindControl<TextBlock>(name)!.Text!;
            Assert.Equal("片 / 站", Text("MediaText"));
            Assert.Equal("https://example.com/a.m3u8", Text("UriText"));
            Assert.Equal("1920×1080@30", Text("ResolutionText"));
            Assert.Equal("h264（硬解 videotoolbox） / aac（48 kHz 2 声道）", Text("CodecText"));
            Assert.Equal("10.2 Mbps", Text("SpeedText"));
            Assert.Equal("16.57 s", Text("BufferText"));
            Assert.Contains("丢弃 9 帧，共 1216 帧", Text("ViewportText"));
            Assert.InRange(panel.FindControl<StatsGraph>("SpeedGraph")!.Count, 1, 2);

            panel.MoveTo(-500, 99999);
            window.UpdateLayout();
            Assert.Equal(0, panel.Margin.Left);
            Assert.Equal(overlay.Bounds.Height - panel.Bounds.Height, panel.Margin.Top, 1);
            panel.MoveTo(99999, -10);
            Assert.Equal(overlay.Bounds.Width - panel.Bounds.Width, panel.Margin.Left, 1);
            Assert.Equal(0, panel.Margin.Top);

            context.Main.Player.StatsVisible = false;
            Assert.False(panel.IsVisible);
            Assert.Equal(0, panel.FindControl<StatsGraph>("SpeedGraph")!.Count);
        }
        finally { await Done(context.Main.Player.Close()); window.Close(); }
    }

    [Fact]
    public void ScreenshotNamesAreSafeFileNames()
    {
        var name = PlayerViewModel.ScreenshotFileName("第1集: 开端/重启?", TimeSpan.FromSeconds(3725), new DateTime(2026, 11, 11, 0, 39, 16));
        Assert.Equal("第1集_ 开端_重启_ 1-02-05 20261111-003916.png", name);
        Assert.Equal("VodBox 00-05 20261111-003916.png", PlayerViewModel.ScreenshotFileName("  ", TimeSpan.FromSeconds(5), new DateTime(2026, 11, 11, 0, 39, 16)));
        Assert.Equal("850 Kbps", StatsFormatter.Rate(850_000));
    }

    [AvaloniaFact]
    public void FullscreenShowsClockAndHidesTopmostButton()
    {
        using var context = new Context();
        var overlay = new PlayerOverlay { DataContext = context.Main };
        var window = new Window { Width = 1280, Height = 720, Content = overlay };
        window.Show();
        try
        {
            var clock = overlay.FindControl<TextBlock>("FullscreenClock")!;
            var topmost = overlay.FindControl<Button>("TopmostButton")!;
            var top = overlay.FindControl<Grid>("TopControls")!;
            var normalMargin = top.Margin;
            Assert.False(clock.IsVisible);
            Assert.True(topmost.IsVisible);
            window.WindowState = WindowState.FullScreen;
            Dispatcher.UIThread.RunJobs();
            Assert.True(clock.IsVisible);
            Assert.Matches(@"^\d{2}:\d{2}$", clock.Text);
            Assert.False(topmost.IsVisible);
            Assert.Equal(new Thickness(16, normalMargin.Top, normalMargin.Right, normalMargin.Bottom), top.Margin);
            Assert.True(context.Main.Player.IsFullscreen);
            window.WindowState = WindowState.Normal;
            Dispatcher.UIThread.RunJobs();
            Assert.False(clock.IsVisible);
            Assert.True(topmost.IsVisible);
            Assert.Equal(normalMargin, top.Margin);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void AudioPageLaysOutSideBySideOnWideWindowsAndStackedOnNarrowOnes()
    {
        var page = new AudioModeView();
        page.ApplyLayout(new Size(1280, 720));
        Assert.Equal(Avalonia.Layout.Orientation.Horizontal, page.FindControl<StackPanel>("Layout")!.Orientation);
        page.ApplyLayout(new Size(480, 800));
        Assert.Equal(Avalonia.Layout.Orientation.Vertical, page.FindControl<StackPanel>("Layout")!.Orientation);
        Assert.InRange(page.FindControl<Viewbox>("DeckBox")!.Width, 120 * 360 / 280d, 420 * 360 / 280d);
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
    public async Task SwitchingLiveChannelReusesSinglePlayerWindowAndRaisesIt()
    {
        using var context = new Context();
        var shell = new VodBox.Desktop.MainWindow { DataContext = context.Main };
        shell.Show();
        try
        {
            var first = new LiveChannel
            {
                Name = "CCTV-1综合", Group = "组", Number = 1, Badge = "1",
                Uris = ["http://host/a.m3u8?key=k&playlive=1"],
            };
            var second = new LiveChannel
            {
                Name = "湖南卫视", Group = "组", Number = 2, Badge = "湖南",
                Uris = ["http://host/b.m3u8?key=k&playlive=1"],
            };
            context.Main.Live.Groups.Add(new LiveGroup("组", [first, second], false));
            context.Main.Live.SelectedGroup = context.Main.Live.Groups[0];

            // 选频道走界面同一条路径（PlayChannel 内部即 Player.Play）。
            context.Main.Live.PlayChannel(first);
            await Until(() => context.Engine.Opened.Count == 1);
            Dispatcher.UIThread.RunJobs();
            var player = Assert.IsType<PlayerWindow>(shell.PlaybackWindow);
            Assert.True(player.IsVisible);
            Assert.Equal("CCTV-1综合", context.Main.Player.Title);

            // 换台：必须复用同一个窗口（否则每次点频道都弹新窗），并更新标题与选中态。
            context.Main.Navigate(AppPage.Live);
            context.Main.Live.PlayChannel(second);
            await Until(() => context.Engine.Opened.Count == 2);
            Dispatcher.UIThread.RunJobs();
            Assert.Same(player, shell.PlaybackWindow);
            Assert.Equal("湖南卫视", context.Main.Player.Title);
            Assert.Same(second, context.Main.Live.CurrentChannel);
            Assert.False(first.IsCurrent);

            await Done(context.Main.Player.Close());
            Dispatcher.UIThread.RunJobs();
            Assert.Null(shell.PlaybackWindow);
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
    public async Task LiveChannelPlaybackDrawerListsChannelsAndSwitchesChannel()
    {
        using var context = new Context();
        var channels = Enumerable.Range(1, 300).Select(i => new LiveChannel
        { Name = $"频道{i}", Number = i, Group = "测试", Uris = [$"https://example.com/live/{i}"] }).ToArray();
        context.Main.Live.Groups.Add(new LiveGroup("测试", channels, false, channels.Length));
        context.Main.Live.SelectedGroup = context.Main.Live.Groups[0];
        context.Main.Live.PlayChannel(channels[149]);
        await Until(() => context.Main.Player.IsLiveChannel);
        var window = new PlayerWindow { DataContext = context.Main, Width = 900, Height = 520 };
        window.Show();
        try
        {
            var overlay = window.Overlay;
            var list = overlay.FindControl<ListBox>("DrawerChannels")!;
            overlay.OpenPlaylistDrawer();
            for (var i = 0; i < 4; i++) { window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
            Assert.True(list.IsVisible);
            Assert.False(overlay.FindControl<ScrollViewer>("EpisodeScroller")!.IsVisible);
            Assert.Equal("频道列表", overlay.FindControl<TextBlock>("PlaylistTitle")!.Text);
            Assert.Equal("共 300 个频道", overlay.FindControl<TextBlock>("PlaylistCount")!.Text);
            Button Row(LiveChannel channel) => list.GetVisualDescendants().OfType<Button>()
                .Single(button => ReferenceEquals(button.Tag, channel));
            // 打开即滚到正在播放的频道，且该行高亮；长列表只实现可见行。
            Assert.Contains("current", Row(channels[149]).Classes);
            IEnumerable<LiveChannel> Rows() => list.GetVisualDescendants().OfType<Button>()
                .Select(button => button.Tag).OfType<LiveChannel>();
            Assert.InRange(Rows().Count(), 1, 60);
            var next = Rows().First(channel => !channel.IsCurrent);
            Row(next).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            await Until(() => context.Main.Player.Title == next.Name);
            Assert.Same(next, context.Main.Live.CurrentChannel);
            Assert.Equal(next.Uris[0], context.Engine.Opened[^1].Request.Uri);

            // 单独打开的网络流（同为直播，但不是频道列表发起）仍显示剧集视图。
            context.Main.Player.Play(new PlaybackRequest { Uri = "https://example.com/stream.m3u8", Title = "网络流", SourceKey = "smoke", MediaId = "s", IsLive = true });
            await Until(() => context.Main.Player.Title == "网络流");
            overlay.OpenPlaylistDrawer();
            window.UpdateLayout();
            Assert.False(list.IsVisible);
            Assert.True(overlay.FindControl<ScrollViewer>("EpisodeScroller")!.IsVisible);
            Assert.Equal("播放列表", overlay.FindControl<TextBlock>("PlaylistTitle")!.Text);
        }
        finally { await Done(context.Main.Player.Close()); window.CloseAfterPlayback(); }
    }





    [AvaloniaFact]
    public void PlayerWindowSizingHonoursControlRequirementsAndTinyScreens()
    {
        var normal = PlayerWindowSizing.Fit(16d / 9, 1920, 1080);
        Assert.True(normal.Minimum.Width >= PlayerLayout.MinimumWindowWidth(true));
        Assert.True(normal.Size.Width >= normal.Minimum.Width);
        Assert.True(normal.Minimum.Width <= normal.Maximum.Width);

        var compact = PlayerWindowSizing.Fit(16d / 9, 1920, 1080, compact: true);
        Assert.True(compact.Minimum.Width >= PlayerLayout.MinimumWindowWidth(true));
        Assert.True(compact.Minimum.Width < normal.Minimum.Width);

        // 屏幕比控件需求还小时按屏幕收敛，绝不报出比屏幕更宽的最小值。
        var tiny = PlayerWindowSizing.Fit(16d / 9, 320, 200);
        Assert.True(tiny.Minimum.Width <= 320);
        Assert.InRange(tiny.Size.Width, tiny.Minimum.Width, tiny.Maximum.Width);
        Assert.True(PlayerLayout.MinimumPanelWidth(true) < PlayerLayout.MinimumPanelWidth(false));
    }

    [AvaloniaFact]
    public async Task ClosingPlaybackClearsLiveChannelAndCardMarkers()
    {
        using var context = new Context();
        var channel = new LiveChannel { Name = "关闭测试", Uris = ["https://example.com/live"] };
        context.Main.Live.Groups.Add(new LiveGroup("测试", [channel], false, 1));
        context.Main.Live.FilterText = "关闭测试";
        context.Main.Live.PlayChannel(channel);
        Assert.True(channel.IsCurrent);
        Assert.Same(channel, context.Main.Live.CurrentChannel);
        await context.Main.Player.CloseCommand.ExecuteAsync(null);
        Assert.False(channel.IsCurrent);
        Assert.Null(context.Main.Live.CurrentChannel);
        Assert.All(context.Main.Live.ChannelCards, card => Assert.False(card.IsCurrent));
        Assert.False(context.Main.Player.Visible);
    }

    [AvaloniaFact]
    public void LiveGroupsCollapseIndependentlyAndSearchTemporarilyExpands()
    {
        using var context = new Context();
        var live = context.Main.Live;
        var first = new LiveChannel { Name = "一组频道", Uris = ["https://example.com/1"] };
        var second = new LiveChannel { Name = "二组频道", Uris = ["https://example.com/2"] };
        live.Groups.Add(new LiveGroup("一组", [first], false, 1));
        live.Groups.Add(new LiveGroup("二组", [second], false, 1));
        live.SelectedGroup = null; // 仅验证旧的折叠分组模式；默认模式已改为全部频道。
        live.ToggleGroup("一组");
        Assert.False(live.VisibleChannels.OfType<LiveViewModel.LiveGroupHeader>().First().Expanded);
        Assert.Same(second, Assert.Single(live.ChannelCards));
        live.ToggleGroup("二组");
        Assert.Empty(live.ChannelCards);
        Assert.Equal(2, live.VisibleChannels.Count);
        live.FilterText = "一组频道";
        Assert.Same(first, Assert.Single(live.ChannelCards));
        live.FilterText = "";
        Assert.Empty(live.ChannelCards);
        live.ToggleGroup("一组");
        Assert.Same(first, Assert.Single(live.ChannelCards));
    }

    [AvaloniaFact]
    public void LiveGroupDropdownFiltersCardsAndSearchStaysInSelectedGroup()
    {
        using var context = new Context();
        var first = new LiveChannel { Name = "新闻", Uris = ["https://example.com/1"] };
        var second = new LiveChannel { Name = "新闻二台", Uris = ["https://example.com/2"] };
        var third = new LiveChannel { Name = "体育", Uris = ["https://example.com/3"] };
        var live = context.Main.Live;
        live.Groups.Add(new LiveGroup("央视", [first], false));
        live.Groups.Add(new LiveGroup("卫视", [second, third], false));
        live.SelectedGroup = live.Groups[0];
        var view = new LiveView { DataContext = context.Main };
        var window = new Window { Content = view, Width = 1280, Height = 800 };
        window.Show();
        try
        {
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Assert.Null(view.FindControl<Button>("TabGroups"));
            var groups = view.FindControl<ComboBox>("GroupSelector")!;
            var search = view.FindControl<TextBox>("ChannelSearch")!;
            Assert.Same(first, Assert.Single(live.ChannelCards));
            groups.SelectedItem = live.Groups[1];
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(2, live.ChannelCards.Count);
            search.Text = "新闻";
            Dispatcher.UIThread.RunJobs();
            Assert.Same(second, Assert.Single(live.ChannelCards));
            var groupPoint = groups.TranslatePoint(default, view)!.Value;
            var searchPoint = search.TranslatePoint(default, view)!.Value;
            Assert.Equal(groupPoint.Y + groups.Bounds.Height / 2, searchPoint.Y + search.Bounds.Height / 2, 1);
            Assert.True(searchPoint.X >= groupPoint.X + groups.Bounds.Width);
            Assert.Equal(210, search.Bounds.Width);
            Assert.Equal(34, groups.Bounds.Height);
            Assert.NotNull(groups.SelectionBoxItemTemplate);
            Assert.NotNull(groups.ItemTemplate);
            groups.IsDropDownOpen = true;
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var selectedItem = Assert.IsType<ComboBoxItem>(groups.ContainerFromIndex(2));
            var labels = selectedItem.GetVisualDescendants().OfType<TextBlock>().ToArray();
            var groupName = labels.Single(text => text.Text == "卫视");
            var groupCount = labels.Single(text => text.Text == "2 个频道");
            Assert.True(groupCount.TranslatePoint(default, selectedItem)!.Value.Y > groupName.TranslatePoint(default, selectedItem)!.Value.Y);
            var check = selectedItem.GetVisualDescendants().OfType<PathIcon>().Single(icon => icon.Width == 18);
            Assert.True(check.IsVisible);
            var presenter = selectedItem.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ContentPresenter>()
                .Single(control => control.Name == "PART_ContentPresenter");
            var selectedBrush = Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(presenter.Background);
            Assert.InRange(selectedBrush.Color.A, (byte)0, (byte)32);
            groups.IsDropDownOpen = false;
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void LiveAllChannelsIsDefaultAndCountsEveryGroupWithoutExposingLockedChannels()
    {
        using var context = new Context();
        var first = new LiveChannel { Name = "新闻一", Uris = ["https://example.com/1"] };
        var second = new LiveChannel { Name = "新闻二", Uris = ["https://example.com/2"] };
        var locked = new LiveChannel { Name = "加密", Uris = ["https://example.com/3"] };
        var live = context.Main.Live;
        Assert.Equal("全部频道", live.SelectedGroup!.Name);
        Assert.Equal(0, live.SelectedGroup.Count);
        live.Groups.Add(new LiveGroup("央视", [first], false));
        live.Groups.Add(new LiveGroup("卫视", [second], false));
        live.Groups.Add(new LiveGroup("加密组", [locked], true));
        Assert.Equal("全部频道", live.SelectedGroup.Name);
        Assert.Equal(3, live.SelectedGroup.Count);
        Assert.Equal(4, live.GroupOptions.Count);
        Assert.Equal(new[] { first, second }, live.ChannelCards);
        live.FilterText = "新闻二";
        Assert.Same(second, Assert.Single(live.ChannelCards));
        live.SelectedGroup = live.Groups[0];
        Assert.Empty(live.ChannelCards);
        live.SelectedGroup = live.GroupOptions[0];
        Assert.Same(second, Assert.Single(live.ChannelCards));
        live.Groups.Clear();
        Assert.Equal("全部频道", live.SelectedGroup!.Name);
        Assert.Equal(0, live.SelectedGroup.Count);
        Assert.Empty(live.ChannelCards);
    }

    [AvaloniaFact]
    public void LiveToolbarHasBoundedDynamicGroupWidthAndEdgeAlignedList()
    {
        using var context = new Context();
        var channels = Enumerable.Range(0, 100).Select(i => new LiveChannel { Name = $"测试-{i}", Uris = [$"https://example.com/{i}"] }).ToArray();
        var live = context.Main.Live;
        foreach (var name in new[] { "央视", "河北廊坊组播 河北联通", new string('长', 80) })
            live.Groups.Add(new LiveGroup(name, channels, false));
        live.SelectedGroup = live.Groups[0];
        var view = new LiveView { DataContext = context.Main };
        var window = new Window { Content = view, Width = 1000, Height = 700 };
        window.Show();
        try
        {
            void Layout() { window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
            Layout();
            var selector = view.FindControl<ComboBox>("GroupSelector")!;
            var shortWidth = selector.Bounds.Width;
            selector.SelectedItem = live.Groups[1]; Layout();
            var middleWidth = selector.Bounds.Width;
            selector.SelectedItem = live.Groups[2]; Layout();
            var longWidth = selector.Bounds.Width;
            // 只断言「有上下限」这一约束本身，不断言宽度随文字长度递增：
            // CI 容器常缺 CJK 字体，中文串的测量宽度会退化，那类断言是字体相关的假失败。
            Assert.InRange(shortWidth, 150, 280);
            Assert.InRange(middleWidth, 150, 280);
            Assert.InRange(longWidth, 150, 280);
            // 长名必须被截断而不是把分组框撑破上限。
            Assert.True(longWidth <= 280);
            var label = selector.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Text == live.SelectedGroup!.Name);
            Assert.Equal(Avalonia.Media.TextTrimming.CharacterEllipsis, label.TextTrimming);
            Assert.True(label.Bounds.Width < selector.Bounds.Width);
            var list = view.FindControl<ListBox>("ChannelCardGrid")!;
            var right = list.TranslatePoint(new Avalonia.Point(list.Bounds.Width, 0), view)!.Value.X;
            Assert.Equal(view.Bounds.Width, right, 1);
            var bar = list.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ScrollBar>()
                .Single(control => control.Orientation == Avalonia.Layout.Orientation.Vertical);
            Assert.Equal(view.Bounds.Width, bar.TranslatePoint(new Avalonia.Point(bar.Bounds.Width, 0), view)!.Value.X, 1);
            var heading = view.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Text == "频道列表");
            var count = view.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Text == "共 100 个频道");
            Assert.True(count.TranslatePoint(default, view)!.Value.Y > heading.TranslatePoint(default, view)!.Value.Y);
            Assert.DoesNotContain(view.GetVisualDescendants().OfType<Button>(), button => button.Content is string text && text is "上一台" or "下一台" or "导入节目表" or "回看");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void SidebarGlobalSearchRemainsVisibleAcrossLiveAndHome()
    {
        using var context = new Context();
        var window = new VodBox.Desktop.MainWindow { DataContext = context.Main };
        window.Show();
        try
        {
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var search = window.FindControl<TextBox>("GlobalSearch")!;
            var home = window.GetVisualDescendants().OfType<HomeView>().Single();
            var originalY = home.TranslatePoint(default, window)!.Value.Y;
            // 首页沉浸式：从窗口顶边开始（内容在标题栏下方穿过）。
            Assert.Equal(0, originalY, 1);
            Assert.True(search.IsVisible);
            context.Main.Navigate(AppPage.Live);
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Assert.True(search.IsVisible);
            var live = window.GetVisualDescendants().OfType<LiveView>().Single();
            // 直播页不沉浸：macOS 只上移 24（交通灯在左），其他平台留出整行标题栏。
            Assert.Equal(MainViewModel.TitleBarHeight + (OperatingSystem.IsMacOS() ? -24 : 0), live.TranslatePoint(default, window)!.Value.Y, 1);
            var groups = live.FindControl<ComboBox>("GroupSelector")!;
            Assert.Equal(34, groups.Bounds.Height);
            context.Main.Navigate(AppPage.Home);
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Assert.True(search.IsVisible);
            Assert.Equal(originalY, home.TranslatePoint(default, window)!.Value.Y);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void NonMacLiveToolbarStaysBelowWindowButtonsAtMinimumAndDefaultWidths()
    {
        using var context = new Context();
        var window = new VodBox.Desktop.MainWindow { DataContext = context.Main };
        window.Show();
        try
        {
            context.Main.Navigate(AppPage.Live);
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var live = window.GetVisualDescendants().OfType<LiveView>().Single();
            var page = Assert.IsType<Panel>(live.Parent);
            var safeMargin = MainViewModel.GetPageContentMargin(AppPage.Live, isMacOS: false);
            Assert.Equal(new Avalonia.Thickness(0), safeMargin);
            page.Margin = safeMargin; // 在 macOS 测试环境中也测量 Windows/Linux 的实际页面布局。
            foreach (var width in new[] { 960d, 1280d })
            {
                window.Width = width;
                window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                var titleBar = window.FindControl<Grid>("TitleBar")!;
                var titleBarBottom = titleBar.TranslatePoint(new Avalonia.Point(0, titleBar.Bounds.Height), window)!.Value.Y;
                var toolbar = live.FindControl<Grid>("LiveToolbar")!;
                Assert.True(toolbar.TranslatePoint(default, window)!.Value.Y >= titleBarBottom);
                var search = live.FindControl<TextBox>("ChannelSearch")!;
                Assert.True(search.TranslatePoint(default, window)!.Value.Y >= titleBarBottom);
                Assert.Equal(34, search.Bounds.Height);
            }
            Assert.Equal(new Avalonia.Thickness(0, -24, 0, 0), MainViewModel.GetPageContentMargin(AppPage.Live, isMacOS: true));
            Assert.Equal(new Avalonia.Thickness(0, -MainViewModel.TitleBarHeight, 0, 0), MainViewModel.GetPageContentMargin(AppPage.Home, isMacOS: true));
            Assert.Equal(new Avalonia.Thickness(0, -MainViewModel.TitleBarHeight, 0, 0), MainViewModel.GetPageContentMargin(AppPage.Home, isMacOS: false));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task LiveListScrollMarksMotionThenClearsItAfterIdle()
    {
        using var context = new Context();
        var live = context.Main.Live;
        var channels = Enumerable.Range(0, 100).Select(i => new LiveChannel { Name = $"测试频道{i}-XYZ", Uris = [$"https://example.com/{i}"] }).ToArray();
        live.Groups.Add(new LiveGroup("测试", channels, false));
        live.SelectedGroup = live.Groups[0];
        var view = new LiveView { DataContext = context.Main };
        var window = new Window { Content = view, Width = 1000, Height = 700 };
        window.Show();
        try
        {
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var list = view.FindControl<ListBox>("ChannelCardGrid")!;
            var scroller = list.GetVisualDescendants().OfType<ScrollViewer>().Single();
            scroller.Offset = new Avalonia.Vector(0, 300);
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Assert.True(live.IsChannelScrolling);
            await Until(() => !live.IsChannelScrolling);
            Assert.False(live.IsChannelScrolling);
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// 打开直播页时卡片绑定不得抛异常：Avalonia 会吞掉绑定里的强转异常，但调试器会在第一次抛出时中断，
    /// 表现为「点直播进不去页面」。卡片必须先拿到自己的 LiveChannel 再入树。
    /// </summary>
    [AvaloniaFact]
    public void OpeningLivePageThrowsNoBindingCastExceptions()
    {
        using var context = new Context();
        var channels = Enumerable.Range(1, 138).Select(i => new LiveChannel
        { Name = $"频道{i}", Number = i, Group = "组", Logo = i % 2 == 0 ? $"https://example.invalid/{i}.png" : "", Uris = [$"https://example.com/{i}"] }).ToArray();
        context.Main.Live.Groups.Add(new LiveGroup("组", channels, false, channels.Length));
        var window = new VodBox.Desktop.MainWindow { DataContext = context.Main, Width = 1280, Height = 800 };
        window.Show();
        var seen = new List<string>();
        void Handler(object? s, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e)
        {
            if (e.Exception is InvalidCastException or NullReferenceException) seen.Add(e.Exception.GetType().Name + ": " + e.Exception.Message);
        }
        try
        {
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            AppDomain.CurrentDomain.FirstChanceException += Handler;
            context.Main.GoLiveCommand.Execute(null);
            for (var i = 0; i < 4; i++) { window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
        }
        finally { AppDomain.CurrentDomain.FirstChanceException -= Handler; window.Close(); }
        Assert.True(context.Main.Live.ChannelCards.Count == 138, "频道卡片未加载");
        Assert.Empty(seen);
    }

    [AvaloniaFact]
    public void ChannelListWheelScrollsSmoothlyToTheSameDistance()
    {
        using var context = new Context();
        var channels = Enumerable.Range(1, 400).Select(i => new LiveChannel
        { Name = $"频道{i}", Number = i, Uris = [$"https://example.com/{i}"] }).ToArray();
        context.Main.Live.Groups.Add(new LiveGroup("滚轮", channels, false, channels.Length));
        context.Main.Live.FilterText = "频道";
        context.Main.Navigate(AppPage.Live);
        var window = new VodBox.Desktop.MainWindow { DataContext = context.Main, Width = 1280, Height = 800 };
        window.Show();
        try
        {
            void Layout()
            {
                for (var i = 0; i < 4; i++)
                { window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
            }
            void Frames(int count)
            {
                for (var i = 0; i < count; i++)
                { Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
            }
            Layout();
            var grid = window.GetVisualDescendants().OfType<LiveView>().Single().FindControl<ListBox>("ChannelCardGrid")!;
            var scroller = grid.GetVisualDescendants().OfType<ScrollViewer>().Single();
            var point = grid.TranslatePoint(new Avalonia.Point(grid.Bounds.Width / 2, grid.Bounds.Height / 2), window)!.Value;
            // 不再瞬间跳一整格：一格滚轮要经过多帧中间位置才到达。
            var positions = new List<double>();
            scroller.ScrollChanged += (_, _) => positions.Add(scroller.Offset.Y);
            Avalonia.Headless.HeadlessWindowExtensions.MouseWheel(window, point, new Avalonia.Vector(0, -1));
            Frames(60);
            Assert.True(positions.Count(y => y > 0.5 && y < VodBox.Desktop.Views.SmoothWheelScroll.WheelStep - 0.5) >= 3,
                "滚轮应逐帧推进：" + string.Join(",", positions.Select(y => y.ToString("F1"))));
            Assert.Equal(VodBox.Desktop.Views.SmoothWheelScroll.WheelStep, scroller.Offset.Y, 1);
            // 连续快速滚动：目标累加，最终距离 = 格数 × 每格距离。
            for (var i = 0; i < 3; i++) Avalonia.Headless.HeadlessWindowExtensions.MouseWheel(window, point, new Avalonia.Vector(0, -1));
            Frames(60);
            Assert.Equal(4 * VodBox.Desktop.Views.SmoothWheelScroll.WheelStep, scroller.Offset.Y, 1);
            // 反向：从当前位置起算，不先把剩余的下滚走完。
            Avalonia.Headless.HeadlessWindowExtensions.MouseWheel(window, point, new Avalonia.Vector(0, 1));
            Frames(60);
            Assert.Equal(3 * VodBox.Desktop.Views.SmoothWheelScroll.WheelStep, scroller.Offset.Y, 1);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ClickingChannelCardPlaysThatChannelIncludingRecycledRows()
    {
        using var context = new Context();
        var channels = Enumerable.Range(1, 400).Select(i => new LiveChannel
        { Name = $"频道{i}", Number = i, Uris = [$"https://example.com/{i}"] }).ToArray();
        context.Main.Live.Groups.Add(new LiveGroup("点击测试", channels, false, channels.Length));
        context.Main.Live.FilterText = "频道";
        context.Main.Navigate(AppPage.Live);
        var window = new VodBox.Desktop.MainWindow { DataContext = context.Main, Width = 1280, Height = 800 };
        window.Show();
        try
        {
            void Layout()
            {
                for (var i = 0; i < 4; i++)
                { window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
            }
            Layout();
            var grid = window.GetVisualDescendants().OfType<LiveView>().Single().FindControl<ListBox>("ChannelCardGrid")!;
            Button Card(int number) => grid.GetVisualDescendants().OfType<Button>()
                .First(b => b.Name == "ChannelCard" && b.IsEffectivelyVisible && b.Tag is LiveChannel c && c.Number == number);
            // 走真实指针命中测试，而不是直接 RaiseEvent：卡片必须能被鼠标点中。
            void Press(Button card)
            {
                var center = card.TranslatePoint(new Avalonia.Point(card.Bounds.Width / 2, 40), window)!.Value;
                Avalonia.Headless.HeadlessWindowExtensions.MouseDown(window, center, Avalonia.Input.MouseButton.Left);
                Avalonia.Headless.HeadlessWindowExtensions.MouseUp(window, center, Avalonia.Input.MouseButton.Left);
                Dispatcher.UIThread.RunJobs();
            }
            Press(Card(1));
            Assert.Equal(1, context.Main.Live.CurrentChannel?.Number);
            grid.GetVisualDescendants().OfType<ScrollViewer>().Single().Offset = new Avalonia.Vector(0, 19400);
            Layout();
            var recycled = grid.GetVisualDescendants().OfType<Button>()
                .First(b => b.Name == "ChannelCard" && b.IsEffectivelyVisible && b.Tag is LiveChannel c && c.Number > 100);
            var number = ((LiveChannel)recycled.Tag!).Number;
            Press(recycled);
            Assert.Equal(number, context.Main.Live.CurrentChannel?.Number);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void LargeLiveGridRealizesOnlyViewportRowsAndRecyclesOnScroll()
    {
        using var context = new Context();
        var channels = Enumerable.Range(1, 2000).Select(i => new LiveChannel
        { Name = $"频道{i}", Number = i, Uris = [$"https://example.com/{i}"] }).ToArray();
        context.Main.Live.Groups.Add(new LiveGroup("性能测试", channels, false, channels.Length));
        context.Main.Live.FilterText = "频道";
        context.Main.Navigate(AppPage.Live);
        var window = new VodBox.Desktop.MainWindow { DataContext = context.Main, Width = 1280, Height = 800 };
        window.Show();
        try
        {
            void Layout()
            {
                for (var i = 0; i < 4; i++)
                { window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
            }
            Layout();
            var view = window.GetVisualDescendants().OfType<LiveView>().Single();
            var grid = view.FindControl<ListBox>("ChannelCardGrid")!;
            Assert.Equal(2000, context.Main.Live.ChannelCards.Count);
            Assert.True(context.Main.Live.CardColumns > 1);
            Assert.Contains(grid.GetVisualDescendants(), visual => visual is VirtualizingStackPanel);
            int Cards() => grid.GetVisualDescendants().OfType<Button>().Count(b => b.Name == "ChannelCard");
            Assert.InRange(Cards(), 1, 100);
            var scroll = grid.GetVisualDescendants().OfType<ScrollViewer>().Single();
            scroll.Offset = new Avalonia.Vector(0, 19400);
            Layout();
            Assert.InRange(Cards(), 1, 100);
            Assert.Contains(grid.GetVisualDescendants().OfType<Button>(), b => b.Name == "ChannelCard" && b.Tag is LiveChannel c && c.Number > 100);
            context.Main.Live.FilterText = "频道2000";
            Layout();
            Assert.Single(context.Main.Live.ChannelCards);
            Assert.Single(context.Main.Live.ChannelCardRows);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void LiveCardGridTracksFilteringAndExcludesGroupHeaders()
    {
        using var context = new Context();
        var channels = new[]
        {
            new LiveChannel { Name = "CCTV-1", Number = 1, Uris = ["https://example.com/1"] },
            new LiveChannel { Name = "CCTV-2", Number = 2, Uris = ["https://example.com/2"] },
        };
        context.Main.Live.Groups.Add(new LiveGroup("测试", channels, false, 2));
        context.Main.Live.FilterText = "CCTV";
        Assert.Equal(channels, context.Main.Live.ChannelCards);
        context.Main.Live.FilterText = "CCTV-2";
        Assert.Same(channels[1], Assert.Single(context.Main.Live.ChannelCards));
        context.Main.Live.FilterText = "不存在";
        Assert.Empty(context.Main.Live.ChannelCards);
    }

    [AvaloniaTheory]
    [InlineData(null)]
    [InlineData("新闻联播 19:00")]
    public void ChannelCardShowsNameAndEpgInSelectedGroup(string? epg)
    {
        using var context = new Context();
        var channel = new LiveChannel { Name = "未收录测试频道-XYZ", EpgNow = epg ?? "", Uris = ["https://example.com/live"] };
        context.Main.Live.Groups.Add(new LiveGroup("央视", [channel], false));
        context.Main.Live.SelectedGroup = context.Main.Live.Groups[0];
        var view = new LiveView { DataContext = context.Main };
        var window = new Window { Content = view, Width = 1180, Height = 760 };
        window.Show();
        try
        {
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var logo = view.GetVisualDescendants().OfType<RemotePoster>().Single(p => p.Name == "CardLogo");
            Assert.False(logo.HasImage);
            Assert.Null(logo.Source);
            Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == channel.Name);
            Assert.Equal(Avalonia.Media.Stretch.Uniform, logo.Stretch);
            Assert.True(view.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "CardPlaceholder").IsVisible);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void UnknownChannelWithoutLogoKeepsCardPlaceholder()
    {
        using var context = new Context();
        var channel = new LiveChannel
        {
            Name = "未收录台标的测试频道-XYZ", Group = "组", Number = 1, Badge = "测试", BadgeColor = "#1F6FB2",
            Uris = ["http://host/a.m3u8"],   // 无 Logo
        };
        context.Main.Live.Groups.Add(new LiveGroup("组", [channel], false));
        context.Main.Live.SelectedGroup = context.Main.Live.Groups[0];
        context.Main.Navigate(AppPage.Live);
        var window = new VodBox.Desktop.MainWindow { DataContext = context.Main, Width = 1180, Height = 760 };
        window.Show();
        try
        {
            for (var i = 0; i < 10; i++)
            {
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
                Dispatcher.UIThread.RunJobs();
            }
            var view = window.GetVisualDescendants().OfType<LiveView>().Single();
            // 没有台标时保留文字占位（而不是空一块）。
            Assert.True(view.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "CardPlaceholder").IsVisible);
            Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "TV");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task RealPlaylistLoadsIntoLiveViewWithLogos()
    {
        using var context = new Context();
        var path = "/Users/percy/Downloads/channels_101.72.126.26_9901.m3u";
        if (!File.Exists(path)) return;
        // 端到端：本地 m3u → 直播加载器 → 频道列表 → 渲染，检查台标与分组。
        var loaded = await context.Main.Live.ApplyConfigurationAsync(path);
        Assert.True(loaded);
        Assert.Equal(52, context.Main.Live.Groups.Sum(g => g.Channels.Count));
        Assert.Single(context.Main.Live.Groups);
        Assert.Contains("channels_101", context.Main.Live.LiveSourceLabel);
        Assert.All(context.Main.Live.Groups[0].Channels, c => Assert.False(string.IsNullOrWhiteSpace(c.Badge)));
        Assert.Equal(48, context.Main.Live.Groups[0].Channels.Count(c => !string.IsNullOrWhiteSpace(c.Logo)));

        context.Main.Navigate(AppPage.Live);
        var window = new VodBox.Desktop.MainWindow { DataContext = context.Main, Width = 1180, Height = 760 };
        window.Show();
        try
        {
            // 数据层是可靠断言：列表数据与台标解析不依赖渲染时机。
            Assert.Equal(52, context.Main.Live.VisibleChannels.Count); // 下拉框选中分组，只展示频道
            Assert.Equal("河北保定酒店 河北联通", context.Main.Live.Groups[0].Name);

            // 先强制渲染再取控件：ListBox 虚拟化，行要在布局完成后才存在。
            var frame = await Force(context, window);
            Assert.NotNull(frame);
            var view = window.GetVisualDescendants().OfType<LiveView>().Single();
            var posters = view.GetVisualDescendants().OfType<RemotePoster>().Where(p => !string.IsNullOrWhiteSpace(p.Url)).ToArray();
            Assert.NotEmpty(posters);
            Assert.All(posters, poster =>
            {
                Assert.StartsWith("https://", poster.Url);
                Assert.Contains("taksssss/tv/icon/", poster.Url);
            });
            // 台标是异步加载的：等所有可见行的 RemotePoster 完成加载后再断言图片真的出来了。
            await Task.WhenAll(posters.Select(p => p.LoadingTask.WaitAsync(TimeSpan.FromSeconds(20))));
            Assert.All(posters, p => Assert.NotNull(p.Source));
            // 无台标的频道仍显示文字台标占位（不是空白行）。
            Assert.Equal(4, context.Main.Live.Groups[0].Channels.Count(c => string.IsNullOrWhiteSpace(c.Logo)));
            Assert.All(context.Main.Live.Groups[0].Channels.Where(c => c.Logo is null),
                c => Assert.False(string.IsNullOrWhiteSpace(c.Badge)));
        }
        finally { window.Close(); }
    }

    private static async Task<Avalonia.Media.Imaging.Bitmap?> Force(Context context, VodBox.Desktop.MainWindow window)
    {
        for (var i = 0; i < 10; i++)
        {
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
            Dispatcher.UIThread.RunJobs();
            var f = Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window);
            if (f is not null) return f;
            await Task.Delay(50);
        }
        return null;
    }

    private static (StackPanel Left, StackPanel Right, StackPanel Transport) ControlGroups(PlayerOverlay overlay)
    {
        var bottom = overlay.FindControl<Border>("BottomControls")!;
        var rows = Assert.IsType<StackPanel>(bottom.Child);
        var topRow = Assert.IsType<Panel>(rows.Children[0]);
        var grid = Assert.IsType<Grid>(topRow.Children[0]);
        return (Assert.IsType<StackPanel>(grid.Children[0]), Assert.IsType<StackPanel>(grid.Children[1]),
                Assert.IsType<StackPanel>(topRow.Children[1]));
    }



    [AvaloniaFact]
    public async Task History_SkeletonSitsBelowTheGroupHeadingInsteadOfPushingItDown()
    {
        using var context = new Context();
        context.Main.History.Loading = true;
        var view = new HistoryView { DataContext = context.Main };
        var window = new Window { Width = 1280, Height = 800, Content = view };
        try
        {
            window.Show();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            var heading = view.GetVisualDescendants().OfType<TextBlock>().First(text => text.Text == "历史 · 最近观看");
            var skeleton = view.GetVisualDescendants().OfType<Border>()
                .Where(border => border.Classes.Contains("skeleton") && border.IsEffectivelyVisible).ToArray();
            Assert.NotEmpty(skeleton);

            // 骨架必须排在分组标题之下：否则加载时标题会被顶走，列表看起来"错位"。
            var headingY = heading.TranslatePoint(default, view)!.Value.Y;
            var highestSkeletonY = skeleton.Min(border => border.TranslatePoint(default, view)!.Value.Y);
            Assert.True(highestSkeletonY > headingY,
                $"骨架({highestSkeletonY}) 必须低于分组标题({headingY})");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task HomeCarouselRotatesDistinctPostersAndOpensTheSelectedRecommendation()
    {
        using var context = new Context();
        var items = new[]
        {
            new MediaItem { Id = "one", Title = "第一张", Poster = "https://example.com/one.jpg" },
            new MediaItem { Id = "two", Title = "第二张", Poster = "https://example.com/two.jpg" },
            new MediaItem { Id = "duplicate", Title = "重复海报", Poster = "https://example.com/one.jpg" },
        };
        context.Source.Home = _ => Task.FromResult(new MediaPage(items, 1, 1));
        var home = new HomeViewModel(HomeLoader(context), context.Store, context.Main);
        await Done(home.LoadAsync());
        Assert.Equal(2, home.HeroSlides.Count);
        Assert.True(home.CanRotateHero);
        Assert.Equal("第一张", home.HeroTitle);
        home.NextHeroCommand.Execute(null);
        Assert.Equal("第二张", home.HeroTitle);
        Assert.Equal(items[1].Poster, home.HeroPoster);
        Assert.Equal(1, home.HeroIndex);
        Assert.Equal("今日推荐 · 2/2", home.HeroPositionLabel);
        Assert.Single(home.HeroSlides, slide => slide.IsSelected);
        home.NextHeroCommand.Execute(null);
        Assert.Equal(0, home.HeroIndex);
        home.PreviousHeroCommand.Execute(null);
        Assert.Equal(1, home.HeroIndex);
        var requested = "";
        context.Source.Load = (id, _) => { requested = id; return Task.FromResult(Detail(id)); };
        home.OpenHeroCommand.Execute(null);
        await Until(() => requested == "two");
        home.Recommendations.Clear();
        Assert.False(home.CanRotateHero);
        Assert.Empty(home.HeroSlides);
        Assert.Null(home.HeroPoster);
    }

    [AvaloniaFact]
    public void HomeCarouselIndicatorsSelectSlidesAndTimerStopsWhenHiddenOrDetached()
    {
        using var context = new Context();
        var home = context.Main.Home;
        home.Recommendations.Add(new MediaItem { Id = "one", Title = "第一张", Poster = "https://example.com/one.jpg" });
        home.Recommendations.Add(new MediaItem { Id = "two", Title = "第二张", Poster = "https://example.com/two.jpg" });
        var view = new HomeView { DataContext = context.Main };
        var window = new Window { Content = view, Width = 1280, Height = 800 };
        window.Show();
        try
        {
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.True(view.IsHeroRotationRunning);
            foreach (var name in new[] { "PreviousHero", "NextHero" })
            {
                var button = view.FindControl<Button>(name)!;
                var icon = Assert.IsType<PathIcon>(button.Content);
                var center = icon.TranslatePoint(new Avalonia.Point(icon.Bounds.Width / 2, icon.Bounds.Height / 2), button)!.Value;
                Assert.Equal(button.Bounds.Width / 2, center.X, 6);
                Assert.Equal(button.Bounds.Height / 2, center.Y, 6);
            }
            var indicators = view.FindControl<ItemsControl>("HeroIndicators")!.GetVisualDescendants().OfType<Button>().ToArray();
            Assert.Equal(2, indicators.Length);
            Assert.Equal(22, indicators[0].Bounds.Width);
            Assert.Equal(8, indicators[1].Bounds.Width);
            indicators[1].RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.Equal(1, home.HeroIndex);
            Assert.Equal(22, indicators[1].Bounds.Width);
            view.RotateHero();
            Assert.Equal(0, home.HeroIndex);
            view.IsVisible = false;
            Assert.False(view.IsHeroRotationRunning);
            view.RotateHero();
            Assert.Equal(0, home.HeroIndex);
            view.IsVisible = true;
            Assert.True(view.IsHeroRotationRunning);
        }
        finally { window.Close(); }
        Assert.False(view.IsHeroRotationRunning);
    }

    [AvaloniaFact]
    public void SidebarSearchSharesShapeWithLiveSearchAndHasNoSearchMenuOrPage()
    {
        using var context = new Context();
        var window = new VodBox.Desktop.MainWindow { DataContext = context.Main };
        window.Show();
        try
        {
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            // 侧栏顶部搜索框：与直播页频道搜索框同一形状，左侧图标不拦截输入。
            var global = window.FindControl<TextBox>("GlobalSearch")!;
            var globalIcon = window.FindControl<PathIcon>("GlobalSearchIcon")!;
            Assert.Equal(34, global.Bounds.Height);
            Assert.Equal(new Avalonia.CornerRadius(6), global.CornerRadius);
            Assert.Equal(new Avalonia.Thickness(30, 0, 10, 0), global.Padding);
            Assert.Equal(14, globalIcon.Bounds.Width);
            Assert.False(globalIcon.IsHitTestVisible);
            var homeView = window.GetVisualDescendants().OfType<HomeView>().Single();
            var hero = homeView.FindControl<Border>("HeroCard")!;
            // 沉浸式横幅：铺满内容区、从窗口顶边开始（无圆角）；切换箭头在标题栏下方，不被拖拽区吃掉点击。
            Assert.Equal(default, hero.CornerRadius);
            Assert.Equal(0, hero.TranslatePoint(default, window)!.Value.Y, 1);
            Assert.Equal(homeView.Bounds.Width, hero.Bounds.Width, 1);
            // 箭头只在多张推荐时显示；横幅顶边即窗口顶边，所以它们所在面板的上边距必须越过标题栏。
            var arrows = Assert.IsType<StackPanel>(homeView.FindControl<Button>("PreviousHero")!.Parent);
            Assert.True(arrows.Margin.Top >= MainViewModel.TitleBarHeight);
            // 侧栏不再有「搜索」菜单项；主窗口内也不再有搜索页（结果改由独立窗口承载）。
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<NavItemView>(), item => item.Label == "搜索");
            Assert.Empty(window.GetVisualDescendants().OfType<SearchView>());
            context.Main.Navigate(AppPage.Live);
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var liveSearch = window.GetVisualDescendants().OfType<LiveView>().Single().FindControl<TextBox>("ChannelSearch")!;
            Assert.Equal(global.CornerRadius, liveSearch.CornerRadius);
            Assert.Equal(global.Padding, liveSearch.Padding);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task HistoryRecentMatchesHomeEvenWhenEveryEntryIsFromBeforeToday()
    {
        using var context = new Context();
        var entries = Enumerable.Range(0, 35).Select(i => new HistoryEntry
        {
            SourceKey = i is 0 or 20 ? "live" : "source", SourceName = "站点", MediaId = $"old-{i}",
            Title = i is 0 or 20 ? $"CCTV-{i}综合" : $"历史-{i}",
            UpdatedAt = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.Now.AddDays(-2).AddMinutes(-i).ToUnixTimeMilliseconds()),
        }).ToArray();
        foreach (var entry in entries) await Done(context.Services.Store.SaveHistoryAsync(entry));
        await Done(context.Main.Home.LoadAsync());
        await Done(context.Main.History.LoadAsync());
        var history = context.Main.History;
        Assert.Equal(context.Main.Home.Recent, history.Recent);
        Assert.Equal(20, history.Recent.Count);
        Assert.Equal(entries.Skip(20), history.Earlier);
        Assert.Empty(history.Recent.Intersect(history.Earlier));
        Assert.True(history.ShowRecentArea);

        var view = new HistoryView { DataContext = context.Main };
        var window = new Window { Content = view, Width = 1280, Height = 800 };
        window.Show();
        try
        {
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var scroller = view.FindControl<ScrollViewer>("RecentScroller")!;
            Assert.Equal(520, scroller.Bounds.Height);
            var outer = view.FindControl<ScrollViewer>("HistoryScroller")!;
            var content = view.FindControl<StackPanel>("HistoryContent")!;
            Assert.Equal(0, outer.Padding.Right);
            Assert.Equal(0, content.Margin.Right);
            var outerBar = outer.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ScrollBar>()
                .Single(bar => bar.Orientation == Avalonia.Layout.Orientation.Vertical && ReferenceEquals(bar.GetVisualAncestors().OfType<ScrollViewer>().First(), outer));
            Assert.Equal(view.Bounds.Width, outerBar.TranslatePoint(new Avalonia.Point(outerBar.Bounds.Width, 0), view)!.Value.X, 1);
            var recentBar = scroller.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ScrollBar>()
                .Single(bar => bar.Orientation == Avalonia.Layout.Orientation.Vertical);
            var contentRight = content.TranslatePoint(new Avalonia.Point(content.Bounds.Width, 0), view)!.Value.X;
            var recentRight = recentBar.TranslatePoint(new Avalonia.Point(recentBar.Bounds.Width, 0), view)!.Value.X;
            Assert.Equal(contentRight, recentRight, 1);
            Assert.Equal(0, Math.Max(0, outerBar.TranslatePoint(default, view)!.Value.X - recentRight), 1);
            var earlier = view.FindControl<ItemsControl>("EarlierList")!;
            var posters = earlier.GetVisualDescendants().OfType<RemotePoster>().ToArray();
            Assert.Equal(15, posters.Length);
            foreach (var poster in posters)
            {
                Assert.Equal(Avalonia.Media.Stretch.Uniform, poster.Stretch);
                var entry = Assert.IsType<HistoryEntry>(poster.DataContext);
                Assert.Equal(entry.SourceKey == "live" ? entry.Title : null, poster.ChannelName);
                var frame = Assert.IsType<Border>(poster.Parent!.Parent);
                Assert.Equal(96, frame.Bounds.Width);
                Assert.Equal(144, frame.Bounds.Height);
                Assert.Equal(2d / 3, frame.Bounds.Width / frame.Bounds.Height, 6);
            }
            var recentPosters = scroller.GetVisualDescendants().OfType<RemotePoster>().ToArray();
            Assert.Equal(20, recentPosters.Length);
            foreach (var poster in recentPosters)
            {
                var entry = Assert.IsType<HistoryEntry>(poster.DataContext);
                Assert.Equal(entry.SourceKey == "live" ? entry.Title : null, poster.ChannelName);
                Assert.Equal(entry.SourceKey == "live" ? Avalonia.Media.Stretch.Uniform : Avalonia.Media.Stretch.UniformToFill, poster.Stretch);
            }
            var resumeButtons = earlier.GetVisualDescendants().OfType<Button>().Where(button => button.Name == "EarlierResumeButton").ToArray();
            Assert.Equal(15, resumeButtons.Length);
            foreach (var button in resumeButtons)
            {
                Assert.Equal(26, button.Bounds.Height);
                Assert.InRange(button.Bounds.Width, 30, 64);
                Assert.InRange(Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(button.Background).Color.A, (byte)0, (byte)32);
            }
            await Done(history.ClearAllCommand.ExecuteAsync(null));
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.Empty(history.Recent);
            Assert.Empty(history.Earlier);
            Assert.False(history.ShowRecentArea);
            Assert.True(history.ShowEmpty);
            Assert.False(scroller.IsEffectivelyVisible);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Home_RecentIsCappedAtTwentyForTheSingleRow()
    {
        using var context = new Context();
        var requested = -1;
        context.Store.ReadHistory = (limit, _) =>
        {
            requested = limit;
            return Task.FromResult<IReadOnlyList<HistoryEntry>>(
                Enumerable.Range(0, 50).Select(i => new HistoryEntry
                {
                    SourceKey = "source", SourceName = "站点", MediaId = $"m{i}", Title = $"片{i}",
                }).ToArray());
        };
        var home = new HomeViewModel(HomeLoader(context), context.Store, context.Main);
        await Done(home.LoadAsync());
        // 首页最近观看永远只有一行、最多前 20 条：既要求上限，也要求 Home 不吞下全部 50 条。
        Assert.Equal(20, requested);
        Assert.Equal(20, home.Recent.Count);
    }

    [AvaloniaFact]
    public async Task ResumeEntry_RoutesLiveHistoryToLivePlaybackInsteadOfDetail()
    {
        using var context = new Context();
        var live = context.Main.Live;
        // 直播源就绪：一个真实频道，历史里以 source_key="live" 记录它的地址。
        var channel = new LiveChannel { Name = "CCTV-1", Number = 1, Group = "央视", Logo = "https://example.com/cctv1.png", Uris = ["http://example.com/live1"] };
        live.Groups.Add(new LiveGroup("央视", [channel], Locked: false));
        await Done(live.RefreshLibraryTabsAsync());

        context.Main.ResumeEntry(new HistoryEntry
        {
            SourceKey = "live", SourceName = "直播", MediaId = "http://example.com/live1", Title = "CCTV-1",
        });

        // 关键：不能再当成点播站点去查详情页，否则会报「在当前配置中不可用」。
        Assert.Equal("", context.Main.Detail.Hint);
        Assert.DoesNotContain("在当前配置中不可用", context.Main.StatusMessage);
        await Until(() => context.Engine.Opened.Count == 1);
        var opened = Assert.Single(context.Engine.Opened);
        Assert.Equal("http://example.com/live1", opened.Request.Uri);
        Assert.Equal("https://example.com/cctv1.png", opened.Request.Poster);
        Assert.True(opened.Request.IsLive);
        Assert.Equal("live", opened.Request.SourceKey);
    }

    [AvaloniaFact]
    public async Task ResumeEntry_LiveChannelMissingFromSourceExplainsWhyInsteadOfSiteUnavailable()
    {
        using var context = new Context();
        context.Main.ResumeEntry(new HistoryEntry
        {
            SourceKey = "live", SourceName = "直播", MediaId = "http://example.com/gone", Title = "已下架频道",
        });
        // 频道不在当前直播源里时，文案必须指向频道本身，而不是误导为「站点不可用」。
        Assert.Contains("已下架频道", context.Main.StatusMessage);
        Assert.Contains("不在当前直播源", context.Main.StatusMessage);
        Assert.DoesNotContain("在当前配置中不可用", context.Main.StatusMessage);
        Assert.Equal(AppPage.Live, context.Main.Page);
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
            var deps = new DetailDependencies(
                async (key, id, _, ct) => key == Source.Key ? ToResult(await Source.Load(id, ct)) : throw new InvalidOperationException($"站点 {key} 在当前配置中不可用"),
                (_, _, _, _) => AsyncEnumerable.Empty<VodResult>(),
                () => new SiteSettings { PreferBestSource = false, BlockAds = false });
            Main = new MainViewModel(_services, true, deps, Store);
            // 首页分区走本地假源：真实首页会请求豆瓣，网络慢时测试会超时。
            Main.Home = new HomeViewModel(HomeLoader(Source), _services.Store, Main);
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

    /// <summary>首页分区装载：只有「热门电影」取测试源首页，其余分区为空（横幅与推荐都来自电影分区）。</summary>
    private static Func<string, CancellationToken, Task<IReadOnlyList<MediaItem>>> HomeLoader(Context context) => HomeLoader(context.Source);
    private static Func<string, CancellationToken, Task<IReadOnlyList<MediaItem>>> HomeLoader(IContentSource? source) => async (category, ct) =>
        category == "movie" && source is not null ? (await source.GetHomeAsync(ct)).Items.Select(item => item with { SourceKey = item.SourceKey ?? source.Key }).ToArray() : [];

    private static VodResult ToResult(MediaDetail detail) => new()
    {
        Id = detail.Item.Id, Title = detail.Item.Title, Poster = detail.Item.Poster ?? "", SourceKey = "source", SourceName = "站点",
        Lines = detail.Lines, Description = detail.Description, Remarks = detail.Item.Remarks, Director = detail.Director, Actor = detail.Actor,
        Year = detail.Item.Year ?? "unknown",
    };

    private sealed class Source : IContentSource
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
        public Func<CancellationToken, Task<IReadOnlyList<Category>>> Categories { get; set; } = _ => Task.FromResult<IReadOnlyList<Category>>([new Category("a", "分类")]);
        public Task<IReadOnlyList<Category>> GetCategoriesAsync(CancellationToken ct = default) => Categories(ct);
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
        public Func<int, CancellationToken, Task<IReadOnlyList<HistoryEntry>>>? ReadHistory { get; set; }
        public Task<IReadOnlyList<HistoryEntry>> GetHistoryAsync(int limit = 200, CancellationToken ct = default) => ReadHistory?.Invoke(limit, ct) ?? Task.FromResult<IReadOnlyList<HistoryEntry>>(Saved.ToArray());
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
        public PlaybackStats? Stats { get; set; }
        public Task<PlaybackStats?> GetStatsAsync(CancellationToken ct = default) => Task.FromResult(Stats);
        public List<string> Screenshots { get; } = [];
        public Task SaveScreenshotAsync(string path, CancellationToken ct = default) { Screenshots.Add(path); return Task.CompletedTask; }
        public Task LoadSubtitleAsync(string path, long sessionId, CancellationToken ct = default)
        {
            if (SubtitleError is not null) return Task.FromException(SubtitleError);
            Subtitles.Add(path);
            return Task.CompletedTask;
        }
        public List<MediaTrack> Tracks { get; } = [];
        public IReadOnlyList<MediaTrack> GetTracks(TrackKind kind) => Tracks.Where(track => track.Kind == kind).ToArray();
        public List<(TrackKind Kind, string Id)> Selected { get; } = [];
        public Task SelectTrackAsync(TrackKind kind, string trackId, CancellationToken ct = default) { Selected.Add((kind, trackId)); return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
