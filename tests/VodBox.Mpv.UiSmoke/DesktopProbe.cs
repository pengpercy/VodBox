using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.VisualTree;
using VodBox.Core;
using VodBox.Desktop;
using VodBox.Playback.LibVlc;
using VodBox.Playback.Mpv;

internal static class DesktopProbe
{
    private static void SetRate(VodBox.Desktop.Views.PlaybackControlsView controls, double rate)
    {
        var button = controls.FindControl<Button>("RateButton")!;
        button.Flyout!.ShowAt(button);
        controls.FindControl<Slider>("PlaybackRate")!.Value = rate;
        button.Flyout.Hide();
    }
    public static Window Create(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var model = new MainViewModel(designMode: true, engineFactory: kind =>
        {
            if (kind == PlaybackEngineKind.Mpv) return new MpvEngine(factory: () => new MpvClient(new Dictionary<string, string>
            { ["vo"] = "libmpv", ["ao"] = "null", ["hwdec"] = "no" }), waitForVideoSurface: true);
            var vlc = new LibVlcEngine(waitForVideoSurface: true); vlc.Initialized += (_, _) => vlc.Player!.Volume = 0; return vlc;
        }) { Volume = 0, AutoNext = false };
        for (int i = 0; i < 1000; i++) model.Items.Add(new(i.ToString(), $"AOT 海报 {i}"));
        model.SetHomeRecommendations(model.Items.Take(12));
        model.SelectedLine = new("probe", "分集验收", Enumerable.Range(1, 2000).Select(i => new Episode(i.ToString(), $"第 {i} 集")).ToArray());
        var window = new MainWindow(model, initialize: false);
        window.Opened += async (_, _) =>
        {
            string stage = "open";
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                stage = "open";
                await model.OpenFileAsync(Program.FixturePath);
                Console.WriteLine($"MainWindow opened media: kind={model.Engine.ActiveKind}, state={model.Engine.Snapshot.State}, status={model.Status}");
                if (Program.FallbackMode)
                {
                    await Until(() => model.Engine.ActiveKind == PlaybackEngineKind.LibVlc && model.Engine.Snapshot.State == PlaybackState.Playing && model.Engine.Snapshot.Position.TotalSeconds > 1);
                    var fallback = (LibVlcEngine)model.Engine.ActiveEngine!;
                    using var media = fallback.Player!.Media!;
                    await Until(() => fallback.Player.VideoTrack >= 0 && media.Statistics.DecodedVideo > 0);
                    var fallbackSurface = window.PlaybackView.VideoSurface!;
                    if (!fallbackSurface.IsVisible || !ReferenceEquals(fallbackSurface.MediaPlayer, fallback.Player)) throw new InvalidOperationException("Fallback VLC surface not attached.");
                    model.DanmakuComments = [new(0, "Fallback danmaku", DanmakuMode.Top)]; await Until(() => window.ActiveDanmakuCount > 0);
                    Console.WriteLine($"MainWindow native fallback: OK | unavailable mpv -> LibVLC, decodedVideo={media.Statistics.DecodedVideo}, native surface/danmaku, settings stay automatic");
                    window.Close(); return;
                }
                await Until(() => window.MpvRenderedFrames >= 10 && model.Engine.Snapshot.Position.TotalSeconds > 1);
                if (model.Engine.ActiveKind != PlaybackEngineKind.Mpv) throw new InvalidOperationException("Main window failed to select/render mpv: " + model.Status);
                model.DanmakuComments = [new(0, "MainWindow danmaku probe", DanmakuMode.Top)];
                await Until(() => window.ActiveDanmakuCount > 0);
                await model.TogglePauseCommand.ExecuteAsync(null); await Until(() => model.Engine.Snapshot.State == PlaybackState.Paused);
                await model.Engine.SeekAsync(TimeSpan.FromSeconds(2.5), timeout.Token);
                await Until(() => Math.Abs(model.Engine.Snapshot.Position.TotalSeconds - 2.5) < .2);
                stage = "switch-vlc";
                model.SelectedPlaybackEngine = model.PlaybackEngineChoices.Single(x => x.Mode == PlaybackEngineMode.LibVlc);
                await Until(() => model.Engine.ActiveKind == PlaybackEngineKind.LibVlc && model.Engine.Snapshot.State == PlaybackState.Paused);
                var vlc = (LibVlcEngine)model.Engine.ActiveEngine!;
                await Until(() => vlc.Player!.VideoTrack >= 0 && Math.Abs(model.Engine.Snapshot.Position.TotalSeconds - 2.5) < .3);
                using (var media = vlc.Player!.Media!) await Until(() => vlc.Player.VoutCount > 0 && media.Statistics.DecodedVideo > 0);
                var surface = window.PlaybackView.VideoSurface!;
                if (!surface.IsVisible || !ReferenceEquals(surface.MediaPlayer, vlc.Player)) throw new InvalidOperationException("VLC surface not attached.");
                await Until(() => vlc.Player!.Volume == 0);
                await Until(() => window.ActiveDanmakuCount > 0);
                await Until(() => desktop.Windows.Any(candidate => candidate.IsVisible && candidate.Content is VodBox.Desktop.Views.PlaybackControlsView));
                var vlcControls = desktop.Windows.Single(candidate => candidate.IsVisible && candidate.Content is VodBox.Desktop.Views.PlaybackControlsView);
                SetRate((VodBox.Desktop.Views.PlaybackControlsView)vlcControls.Content!, 1.25);
                await Until(() => Math.Abs(vlc.Player.Rate - 1.25) < .01);
                stage = "vlc-fullscreen-controls";
                window.ToggleFullscreen();
                await Until(() => window.Bounds.Width > 1500 && !window.PlaybackView.ControlsView.IsVisible);
                var overlay = ((Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)Avalonia.Application.Current!.ApplicationLifetime!).Windows
                    .Single(candidate => candidate.IsVisible && candidate.Content is VodBox.Desktop.Views.PlaybackControlsView);
                SetRate((VodBox.Desktop.Views.PlaybackControlsView)overlay.Content!, 1.25);
                await Until(() => Math.Abs(vlc.Player.Rate - 1.25) < .01);
                await Task.Delay(1200); // Allow the macOS fullscreen Space animation to settle.
                window.ToggleFullscreen();
                await Until(() => window.Bounds.Width < 1500 && overlay.IsVisible);
                await Task.Delay(1200);
                stage = "vlc-navigation";
                await model.NavigateCommand.ExecuteAsync("设置");
                window.SettingsWindow!.SettingsView.ShowPlaybackSettings();
                await Until(() => window.ActiveDanmakuCount == 0);
                window.SettingsWindow!.Close();
                model.ShowPlaybackPage = true;
                await Until(() => window.ActiveDanmakuCount > 0);
                if (!ReferenceEquals(surface.MediaPlayer, vlc.Player)) throw new InvalidOperationException("Navigation recreated VLC drawable.");
                long previous = window.MpvRenderedFrames;
                stage = "switch-mpv";
                model.SelectedPlaybackEngine = model.PlaybackEngineChoices.Single(x => x.Mode == PlaybackEngineMode.Mpv);
                await Until(() => model.Engine.ActiveKind == PlaybackEngineKind.Mpv && model.Engine.Snapshot.State == PlaybackState.Paused && window.MpvRenderedFrames > previous);
                if (surface.IsVisible || surface.MediaPlayer is not null) throw new InvalidOperationException("VLC native view not detached.");
                await Until(() => window.ActiveDanmakuCount > 0);
                window.Width = 900;
                await Until(() => !window.FindControl<Grid>("BrowsePane")!.IsVisible && window.FindControl<VodBox.Desktop.Views.PlaybackView>("PlaybackPane")!.IsVisible);
                var renderSurface = window.PlaybackView.VideoContainer!.Children.OfType<MpvVideoSurface>().Single();
                model.ShowPlaybackPage = false;
                await Until(() => window.FindControl<Grid>("BrowsePane")!.IsVisible && !window.FindControl<VodBox.Desktop.Views.PlaybackView>("PlaybackPane")!.IsVisible);
                model.ShowPlaybackPage = true; window.Width = 1280;
                await Until(() => !window.FindControl<Grid>("BrowsePane")!.IsVisible && window.FindControl<VodBox.Desktop.Views.PlaybackView>("PlaybackPane")!.IsVisible);
                stage = "poster-page";
                await model.NavigateCommand.ExecuteAsync("发现");
                await Until(() => window.LibraryView.IsEffectivelyVisible && window.LibraryView.FindControl<ListBox>("PosterGrid")!.GetVisualDescendants().OfType<Image>().Any());
                int images = window.LibraryView.FindControl<ListBox>("PosterGrid")!.GetVisualDescendants().OfType<Image>().Count();
                if (images is < 1 or > 100 || model.CardRows.SelectMany(row => row.Cards).Count() != 1000) throw new InvalidOperationException($"Poster virtualization failed: images={images}");
                model.ShowPlaybackPage = true;
                stage = "episode-page";
                model.EpisodeSearch = "2000"; model.ReverseEpisodes = true;
                await Until(() => model.VisibleEpisodes.Count == 1);
                if (model.VisibleEpisodes[0].Id != "2000") throw new InvalidOperationException("Episode filtering failed.");
                model.EpisodeSearch = "";
                await Until(() => window.PlaybackView.EpisodeBrowser.FindControl<ListBox>("EpisodeList")!.GetVisualDescendants().OfType<Button>().Any());
                int episodeButtons = window.PlaybackView.EpisodeBrowser.FindControl<ListBox>("EpisodeList")!.GetVisualDescendants().OfType<Button>().Count();
                if (episodeButtons > 100 || model.Episodes[0].Id != "1") throw new InvalidOperationException("Episode virtualization/order failed.");
                if (!ReferenceEquals(renderSurface, window.PlaybackView.VideoContainer!.Children.OfType<MpvVideoSurface>().Single())) throw new InvalidOperationException("Resize recreated the native renderer.");
                stage = "page-navigation";
                foreach (string page in new[] { "首页", "设置", "搜索", "直播", "历史", "收藏", "点播" })
                {
                    await model.NavigateCommand.ExecuteAsync(page);
                    if (page == "设置")
                    {
                        if (window.SettingsWindow?.IsVisible != true || !model.ShowPlaybackPage) throw new InvalidOperationException("Settings window changed the playback page.");
                        var sourceDialog = new VodBox.Desktop.Views.SourceConfigurationWindow(model);
                        sourceDialog.Show(window.SettingsWindow); await Task.Delay(100); sourceDialog.Close();
                        window.SettingsWindow.Close();
                        if (!ReferenceEquals(renderSurface, window.PlaybackView.VideoContainer.Children.OfType<MpvVideoSurface>().Single())) throw new InvalidOperationException("Settings recreated mpv renderer.");
                        continue;
                    }
                    await Until(() => !window.PlaybackView.IsEffectivelyVisible);
                    if (page == "首页" && (!window.FindControl<VodBox.Desktop.Views.HomeView>("HomePage")!.IsEffectivelyVisible || window.FindControl<Border>("NavigationPane") is not null || model.HomeCards.Count != 12)) throw new InvalidOperationException("Home layout or recommendation snapshot failed.");
                    if (page is "搜索" or "历史" or "收藏")
                    {
                        if (window.FindControl<Border>("NavigationPane") is not null) throw new InvalidOperationException("Collection/search page retained legacy navigation.");
                        if (page == "搜索")
                        {
                            model.SearchSubmitted = true;
                            var searchSource = new VodBox.Core.SourceDefinition { Id = "probe-search", Name = "AOT 搜索站点" };
                            for (int index = 0; index < 1000; index++) model.SearchResults.Add(new(searchSource, new(index.ToString(), $"AOT 搜索 {index}")));
                            await Task.Delay(100);
                            var searchPage = window.FindControl<VodBox.Desktop.Views.SearchView>("SearchPage")!;
                            int visibleCards = searchPage.GetVisualDescendants().OfType<VodBox.Desktop.Views.CollectionCardView>().Count();
                            if (visibleCards is < 1 or > 100) throw new InvalidOperationException("Search result virtualization failed.");
                            await model.SearchBackCommand.ExecuteAsync(null);
                            if (!model.ShowSearch || model.SearchSubmitted) throw new InvalidOperationException("Search back navigation failed.");
                        }
                    }
                    model.ShowPlaybackPage = true;
                    await Until(() => window.PlaybackView.IsEffectivelyVisible);
                    if (!ReferenceEquals(renderSurface, window.PlaybackView.VideoContainer.Children.OfType<MpvVideoSurface>().Single())) throw new InvalidOperationException("Page navigation recreated mpv renderer.");
                }
                await model.TogglePauseCommand.ExecuteAsync(null);
                stage = "fullscreen";
                window.ToggleFullscreen();
                await Until(() => window.WindowState == WindowState.FullScreen && window.Bounds.Width > 1400 && window.PlaybackView.VideoContainer.Bounds.Width > 1400 && Math.Abs(window.PlaybackView.VideoContainer.Bounds.Height - window.Bounds.Height) < 2);
                Console.WriteLine($"Fullscreen actual client={window.Bounds}, renderScale={window.RenderScaling}, screen={window.Screens.ScreenFromWindow(window)?.Bounds}");
                var fullscreenControls = window.PlaybackView.ControlsView;
                var background = fullscreenControls.FindControl<Border>("ControlsBackground")!;
                var row = fullscreenControls.FindControl<Grid>("TransportRow")!;
                if (background.Background is not Avalonia.Media.ISolidColorBrush { Color.A: > 0 and < 255 }) throw new InvalidOperationException("Floating controls do not have a translucent background.");
                var controlsOrigin = fullscreenControls.TranslatePoint(default, window.PlaybackView.VideoContainer);
                if (controlsOrigin is null || controlsOrigin.Value.Y >= window.PlaybackView.VideoContainer.Bounds.Height || Math.Abs(controlsOrigin.Value.Y + fullscreenControls.Bounds.Height + 12 - window.PlaybackView.VideoContainer.Bounds.Height) > 2)
                    throw new InvalidOperationException("Controls do not overlay the video bottom.");
                var playButton = fullscreenControls.FindControl<Button>("PauseButton")!;
                var playCenter = playButton.TranslatePoint(new Avalonia.Point(playButton.Bounds.Width / 2, playButton.Bounds.Height / 2), row);
                if (playCenter is null || Math.Abs(playCenter.Value.X - row.Bounds.Width / 2) > 1) throw new InvalidOperationException("Play button is not centered over the video.");
                foreach (var control in row.Children.Where(child => child.IsVisible))
                {
                    var center = control.TranslatePoint(new Avalonia.Point(0, control.Bounds.Height / 2), row);
                    if (center is null || Math.Abs(center.Value.Y - row.Bounds.Height / 2) > 1) throw new InvalidOperationException("Playback controls are not horizontally aligned.");
                }
                await Until(() => window.FindControl<Border>("NavigationPane") is null && window.PlaybackView.VideoContainer.Bounds.Width > 900);
                if (!ReferenceEquals(renderSurface, window.PlaybackView.VideoContainer.Children.OfType<MpvVideoSurface>().Single())) throw new InvalidOperationException("Fullscreen recreated mpv renderer.");
                SetRate(window.PlaybackView.ControlsView, 1.5);
                await Until(() => Math.Abs(((MpvEngine)model.Engine.ActiveEngine!).Client!.GetDouble("speed").GetValueOrDefault() - 1.5) < .01);
                await Task.Delay(1200);
                window.ToggleFullscreen();
                await Until(() => window.PlaybackView.FindControl<Grid>("PlayerPageHeader")!.IsVisible);
                if (model.Rate != 1.5 || Math.Abs(((MpvEngine)model.Engine.ActiveEngine!).Client!.GetDouble("speed").GetValueOrDefault() - 1.5) > .01) throw new InvalidOperationException("Fullscreen rate was not preserved.");
                stage = "resume";
                await Until(() => window.MpvRenderedFrames > previous + 5 && model.Engine.Snapshot.Position.TotalSeconds > 2.7);
                Console.WriteLine($"MainWindow dual engines: OK | mpv/VLC/mpv, frames={window.MpvRenderedFrames}, pause/position, device mute, surface reuse, settings binding, inline/native-overlay danmaku, narrow/wide layout, 1000 posters / 2000 episodes virtualized, filter/reverse, independent pages, VLC overlay navigation, fullscreen surface reuse/rate retained");
                window.Close();
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error); desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                window.Closed += (_, _) => desktop.Shutdown(1); window.Close();
            }
            async Task Until(Func<bool> condition)
            {
                while (!condition())
                {
                    if (model.Engine.Snapshot.State == PlaybackState.Failed) throw new InvalidOperationException(model.Engine.Snapshot.Error);
                    try { await Task.Delay(50, timeout.Token); }
                    catch (OperationCanceledException) when (timeout.IsCancellationRequested)
                    {
                        var container = window.PlaybackView.VideoContainer!;
                        var native = (model.Engine.ActiveEngine as LibVlcEngine)?.Player;
                        throw new TimeoutException($"Desktop stage {stage} timed out: windowState={window.WindowState}, bounds={window.Bounds}, kind={model.Engine.ActiveKind}, state={model.Engine.Snapshot.State}, position={model.Engine.Snapshot.Position.TotalSeconds:F2}, frames={window.MpvRenderedFrames}, vlcVolume={native?.Volume}, vlcDrawable={native?.NsObject}, status={model.Status}, surfaces={string.Join(';', container.Children.Select(x => $"{x.GetType().Name}:{x.Bounds}:{x.IsVisible}"))}");
                    }
                }
            }
        };
        return window;
    }
}
