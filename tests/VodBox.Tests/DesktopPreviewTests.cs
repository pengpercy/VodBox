using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Controls;
using VodBox.Core;
using VodBox.Desktop;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(VodBox.Tests.TestAppBuilder))]

namespace VodBox.Tests;

public sealed class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<VodBox.Desktop.App>()
        .WithInterFont().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

public sealed class DesktopPreviewTests
{
    [AvaloniaTheory]
    [InlineData(900, false, true, false)]
    [InlineData(900, true, false, true)]
    [InlineData(1280, false, true, false)]
    [InlineData(1600, true, false, true)]
    public async Task DesktopLayoutAdaptsBrowsingAndPlaybackWithoutRecreatingVideoContainer(int width, bool playback, bool browseVisible, bool playbackVisible)
    {
        var window = new MainWindow(preview: true) { Width = width };
        var model = (DesignMainViewModel)window.DataContext!;
        try
        {
            window.Show(); model.ShowPlaybackPage = playback; Dispatcher.UIThread.RunJobs();
            Assert.Equal(browseVisible, window.FindControl<Grid>("BrowsePane")!.IsVisible);
            Assert.Equal(playbackVisible, window.FindControl<VodBox.Desktop.Views.PlaybackView>("PlaybackPane")!.IsVisible);
            var container = window.PlaybackView.VideoContainer!;
            model.ShowPlaybackPage = !playback; Dispatcher.UIThread.RunJobs();
            Assert.Same(container, window.PlaybackView.VideoContainer); Assert.Null(model.Engine.ActiveEngine);
        }
        finally { window.Close(); await model.DisposeAsync(); }
    }

    [AvaloniaFact]
    public async Task PreviewBuildsBoundCardsWithoutStartingNativePlayer()
    {
        var window = new MainWindow(preview: true);
        var viewModel = Assert.IsType<DesignMainViewModel>(window.DataContext);
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            Assert.Equal(3, viewModel.Cards.Count); Assert.Null(viewModel.Engine.ActiveEngine);
            Assert.All(viewModel.Cards, card => Assert.False(card.IsActive));
            Assert.Equal(3, viewModel.PlaybackEngineChoices.Count);
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "探索自然");
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "你的媒体，随处播放");
            await viewModel.NavigateCommand.ExecuteAsync("设置");
            window.SettingsWindow!.SettingsView.ShowPlaybackSettings(); Dispatcher.UIThread.RunJobs();
            Assert.Contains(window.SettingsWindow.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "播放内核");
            viewModel.SelectedPlaybackEngine = viewModel.PlaybackEngineChoices[2];
            Assert.Equal(PlaybackEngineMode.LibVlc, viewModel.Engine.Mode); Assert.Null(viewModel.Engine.ActiveEngine);
        }
        finally { window.Close(); await viewModel.DisposeAsync(); }
    }

    [AvaloniaTheory]
    [InlineData(false, "#111317")]
    [InlineData(true, "#F4F6FA")]
    public async Task ThemeResourcesResolveForBothVariants(bool light, string expected)
    {
        Avalonia.Application.Current!.RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark;
        var window = new MainWindow(preview: true);
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            var brush = Assert.IsAssignableFrom<ISolidColorBrush>(window.TransparencyBackgroundFallback);
            Assert.Equal(Color.Parse(expected), brush.Color);
        }
        finally { window.Close(); await ((MainViewModel)window.DataContext!).DisposeAsync(); }
    }

    [AvaloniaFact]
    public async Task DanmakuPreviewDrawsWithoutNativePlayerAndHonorsToggle()
    {
        var window = new MainWindow(preview: true);
        var model = (DesignMainViewModel)window.DataContext!;
        model.ShowPlaybackPage = true;
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            var view = window.PlaybackView.DanmakuPreview!; view.RefreshFrame();
            Assert.True(view.ActiveCount > 0); Assert.Null(model.Engine.ActiveEngine);
            model.DanmakuEnabled = false; Assert.Equal(0, view.ActiveCount);
            model.DanmakuEnabled = true; Assert.True(view.ActiveCount > 0);
            model.DanmakuComments = [new(0, "delay")]; model.Position = 1000; model.DanmakuDelayMs = 2000;
            Assert.Equal(0, view.ActiveCount); model.Position = 2500; Assert.True(view.ActiveCount > 0);
        }
        finally { window.Close(); await model.DisposeAsync(); }
    }

    [AvaloniaFact]
    public async Task PosterGridVirtualizesLargeCatalogAndPreservesSelectionAcrossReflow()
    {
        var window = new MainWindow(preview: true) { Width = 1280 };
        var model = (DesignMainViewModel)window.DataContext!;
        await model.NavigateCommand.ExecuteAsync("点播");
        try
        {
            model.Items.Clear();
            for (int i = 0; i < 1000; i++) model.Items.Add(new(i.ToString(), $"影片 {i}"));
            window.Show(); Dispatcher.UIThread.RunJobs();
            var selected = model.Cards[123]; model.SelectedCard = selected;
            var firstRow = model.CardRows[0];
            model.Items.Add(new("more", "追加影片"));
            Assert.Same(firstRow, model.CardRows[0]);
            model.UpdateCardColumns(1000);
            Assert.Same(selected, model.SelectedCard); Assert.True(selected.IsSelected);
            Assert.Equal(model.Cards, model.CardRows.SelectMany(row => row.Cards));
            Assert.All(model.CardRows, row => Assert.InRange(row.Cards.Count, 1, model.CardColumns));
            Dispatcher.UIThread.RunJobs();
            model.ShowPlaybackPage = false; Dispatcher.UIThread.RunJobs();
            var images = window.LibraryView.FindControl<ListBox>("PosterGrid")!.GetVisualDescendants().OfType<Image>().Count();
            Assert.InRange(images, 1, 100);
            model.SelectedCard = model.Cards[0]; Assert.False(selected.IsSelected);
            model.Items.Clear(); Assert.Empty(model.CardRows); Assert.Null(model.SelectedCard);
        }
        finally { window.Close(); await model.DisposeAsync(); }
    }

    [AvaloniaFact]
    public async Task EpisodeSearchAndReversePreserveLineOrderWithoutOpeningPlayback()
    {
        var window = new MainWindow(preview: true);
        var model = (DesignMainViewModel)window.DataContext!;
        model.ShowPlaybackPage = true;
        try
        {
            var episodes = Enumerable.Range(1, 2000).Select(i => new Episode(i.ToString(), $"第 {i} 集")).ToArray();
            model.SelectedLine = new("large", "高清线路", episodes);
            window.Show(); Dispatcher.UIThread.RunJobs();
            Assert.Equal(2000, model.VisibleEpisodes.Count); Assert.Equal("共 2000 集", model.EpisodeCountText);
            model.EpisodeSearch = " 19 "; model.ReverseEpisodes = true;
            var expected = episodes.Where(episode => episode.Title.Contains("19", StringComparison.OrdinalIgnoreCase)).Reverse();
            Assert.Equal(expected, model.VisibleEpisodes); Assert.Equal(episodes, model.Episodes);
            Assert.Null(model.Engine.ActiveEngine);
            model.EpisodeSearch = "不存在"; Assert.Empty(model.VisibleEpisodes);
            model.EpisodeSearch = ""; Dispatcher.UIThread.RunJobs();
            Assert.Equal("2000", model.VisibleEpisodes[0].Id);
            int buttons = window.PlaybackView.EpisodeBrowser.FindControl<ListBox>("EpisodeList")!.GetVisualDescendants().OfType<Button>().Count();
            Assert.InRange(buttons, 1, 100);
            model.SelectedLine = new("other", "另一线路", [new("new", "新集数")]);
            Dispatcher.UIThread.RunJobs(); Assert.Single(model.VisibleEpisodes); Assert.Equal("new", model.VisibleEpisodes[0].Id);
            model.SelectedLine = null; Dispatcher.UIThread.RunJobs(); Assert.Empty(model.VisibleEpisodes);
            Assert.Null(model.Engine.ActiveEngine);
        }
        finally { window.Close(); await model.DisposeAsync(); }
    }

    [AvaloniaTheory]
    [InlineData("home")]
    [InlineData("source-settings")]
    [InlineData("library")]
    [InlineData("live")]
    [InlineData("history")]
    [InlineData("favorites")]
    [InlineData("settings")]
    [InlineData("playback")]
    [InlineData("controls")]
    [InlineData("summary")]
    [InlineData("episodes")]
    [InlineData("programmes")]
    [InlineData("general-settings")]
    [InlineData("playback-settings")]
    [InlineData("danmaku-settings")]
    public async Task ExtractedViewsRenderIndependentlyWithSharedPreviewState(string page)
    {
        var model = new DesignMainViewModel();
        UserControl view = page switch
        {
            "home" => new VodBox.Desktop.Views.HomeView(),
            "source-settings" => new VodBox.Desktop.Views.SourceConfigurationView(),
            "library" => new VodBox.Desktop.Views.LibraryView(),
            "live" => new VodBox.Desktop.Views.LiveView(),
            "history" => new VodBox.Desktop.Views.HistoryView(),
            "favorites" => new VodBox.Desktop.Views.FavoritesView(),
            "settings" => new VodBox.Desktop.Views.SettingsView(),
            "playback" => new VodBox.Desktop.Views.PlaybackView(),
            "summary" => new VodBox.Desktop.Views.DetailSummaryView(),
            "episodes" => new VodBox.Desktop.Views.EpisodeBrowserView(),
            "programmes" => new VodBox.Desktop.Views.ProgrammeView(),
            "general-settings" => new VodBox.Desktop.Views.SettingsGeneralView(),
            "playback-settings" => new VodBox.Desktop.Views.SettingsPlaybackView(),
            "danmaku-settings" => new VodBox.Desktop.Views.SettingsDanmakuView(),
            _ => new VodBox.Desktop.Views.PlaybackControlsView()
        };
        view.DataContext = model;
        if (view is VodBox.Desktop.Views.PlaybackView playback) playback.ConfigureDesignPreview(model);
        var window = new Window { Content = view, Width = 900, Height = 640 };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            Assert.Same(model, view.DataContext); Assert.Null(model.Engine.ActiveEngine);
            if (view is VodBox.Desktop.Views.LibraryView)
            {
                Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "探索自然");
                var cardButton = view.GetVisualDescendants().OfType<Button>().First(button => button.DataContext is MediaCard);
                cardButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                Assert.Same(cardButton.DataContext, model.SelectedCard);
                window.Width = 1280; Dispatcher.UIThread.RunJobs(); Assert.True(model.CardColumns >= 5);
            }
            if (view is VodBox.Desktop.Views.PlaybackView playbackView) Assert.True(playbackView.DanmakuPreview.ActiveCount > 0);
        }
        finally
        {
            window.Close();
            if (view is VodBox.Desktop.Views.PlaybackView playbackView) playbackView.DanmakuPreview.Dispose();
            await model.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task PlaybackPageUsesWideVideoDetailsLayoutAndRetainsSurfaceAcrossNavigation()
    {
        var window = new MainWindow(preview: true) { Width = 1600 };
        var model = (DesignMainViewModel)window.DataContext!;
        model.ShowPlaybackPage = true;
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            var playback = window.PlaybackView;
            var video = playback.VideoContainer;
            var summary = playback.FindControl<Grid>("DetailRegion")!;
            Assert.Equal(0, Grid.GetRow(summary)); Assert.Equal(1, Grid.GetColumn(summary));
            await model.NavigateCommand.ExecuteAsync("设置"); Dispatcher.UIThread.RunJobs();
            Assert.True(model.ShowPlaybackPage); Assert.True(playback.IsVisible); Assert.True(window.SettingsWindow!.IsVisible);
            window.SettingsWindow.Close();
            await model.NavigateCommand.ExecuteAsync("点播"); Dispatcher.UIThread.RunJobs();
            Assert.False(model.ShowPlaybackPage); Assert.False(playback.IsVisible);
            model.ShowPlaybackPage = true; window.Width = 900; Dispatcher.UIThread.RunJobs();
            Assert.Equal(2, Grid.GetRow(summary)); Assert.Equal(0, Grid.GetColumn(summary));
            Assert.Same(video, playback.VideoContainer); Assert.Equal("影片与播放", model.WorkspaceTitle);
            Assert.Null(model.Engine.ActiveEngine);
        }
        finally { window.Close(); await model.DisposeAsync(); }
    }

    [AvaloniaTheory]
    [InlineData(900)]
    [InlineData(1064)]
    [InlineData(1280)]
    [InlineData(1600)]
    public async Task WindowedControlsFloatWithinVideoInsteadOfReservingWindowSpace(int width)
    {
        var window = new MainWindow(preview: true) { Width = width };
        var model = (DesignMainViewModel)window.DataContext!;
        try
        {
            model.ShowPlaybackPage = true; window.Show(); Dispatcher.UIThread.RunJobs();
            var controls = window.PlaybackView.ControlsView;
            var video = window.PlaybackView.VideoContainer;
            var origin = controls.TranslatePoint(default, video)!.Value;
            Assert.InRange(origin.X, 11, 13);
            Assert.InRange(origin.Y + controls.Bounds.Height, video.Bounds.Height - 13, video.Bounds.Height - 11);
            Assert.InRange(controls.Bounds.Width, video.Bounds.Width - 25, video.Bounds.Width - 23);
            var background = controls.FindControl<Border>("ControlsBackground")!;
            Assert.InRange(Assert.IsAssignableFrom<ISolidColorBrush>(background.Background).Color.A, (byte)1, (byte)254);
            Assert.Equal(new CornerRadius(6), background.CornerRadius);
            Assert.Empty(controls.GetVisualDescendants().OfType<ComboBox>());
            var seek = controls.FindControl<Slider>("SeekSlider")!;
            var thumb = seek.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.Thumb>().Single();
            var dot = thumb.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Ellipse>().Single();
            Assert.Equal(8, dot.Width); Assert.Equal(8, dot.Height);
            Assert.Equal(1, Assert.IsType<ScaleTransform>(dot.RenderTransform).ScaleX);
            window.MouseMove(thumb.TranslatePoint(new Point(thumb.Bounds.Width / 2, thumb.Bounds.Height / 2), window)!.Value);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1.5, Assert.IsType<ScaleTransform>(dot.RenderTransform).ScaleX);
            window.MouseMove(new Point(0, 0)); Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, Assert.IsType<ScaleTransform>(dot.RenderTransform).ScaleX);
            var rateButton = controls.FindControl<Button>("RateButton")!;
            if (rateButton.IsVisible)
            {
                var label = rateButton.GetVisualDescendants().OfType<TextBlock>().Single(text => Equals(text.Text, rateButton.Content));
                Assert.InRange(label.TranslatePoint(new Point(0, label.Bounds.Height / 2), rateButton)!.Value.Y, 15, 17);
            }

            Assert.Single(window.FindControl<Grid>("AppShell")!.RowDefinitions);
            var row = controls.FindControl<Grid>("TransportRow")!;
            var play = controls.FindControl<Button>("PauseButton")!;
            Assert.InRange(play.TranslatePoint(new Point(play.Bounds.Width / 2, play.Bounds.Height / 2), row)!.Value.X, row.Bounds.Width / 2 - 1, row.Bounds.Width / 2 + 1);
            var cluster = controls.FindControl<StackPanel>("PlaybackCluster")!;
            var clusterRight = cluster.TranslatePoint(new Point(cluster.Bounds.Width, 0), row)!.Value.X;
            Assert.All(row.Children.Where(child => child.IsVisible && Grid.GetColumn(child) >= 3), child =>
                Assert.True(child.TranslatePoint(default, row)!.Value.X >= clusterRight + 2, "Auxiliary controls overlap the centered playback group."));
            Assert.All(row.Children.Where(child => child.IsVisible), child => Assert.InRange(child.TranslatePoint(new Point(0, child.Bounds.Height / 2), row)!.Value.Y, row.Bounds.Height / 2 - 1, row.Bounds.Height / 2 + 1));
        }
        finally { window.Close(); await model.DisposeAsync(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FullscreenKeepsVideoSurfaceAndRestoresPreviousPage(bool playback)
    {
        var window = new MainWindow(preview: true);
        var model = (DesignMainViewModel)window.DataContext!;
        await model.NavigateCommand.ExecuteAsync("点播");
        try
        {
            model.ShowPlaybackPage = playback; window.Show(); Dispatcher.UIThread.RunJobs();
            var video = window.PlaybackView.VideoContainer;
            var fullscreenButton = window.GetVisualDescendants().OfType<Button>().First(button => Equals(button.Content, "全屏"));
            fullscreenButton.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Avalonia.Input.Key.F11 });
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(WindowState.FullScreen, window.WindowState);
            Assert.True(model.ShowPlaybackPage);
            Assert.False(window.FindControl<Border>("NavigationPane")!.IsVisible);
            var rateButton = window.PlaybackView.ControlsView.FindControl<Button>("RateButton")!;
            rateButton.Flyout!.ShowAt(rateButton); Dispatcher.UIThread.RunJobs();
            var preset = ((Control)((Flyout)rateButton.Flyout).Content!).GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Tag, "2"));
            preset.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(2, model.Rate); Assert.Equal("2×", rateButton.Content);
            rateButton.Flyout.ShowAt(rateButton); Dispatcher.UIThread.RunJobs();
            var rate = window.PlaybackView.ControlsView.FindControl<Slider>("PlaybackRate")!;
            rate.Value = 1.5; Assert.Equal(1.5, model.Rate);
            Assert.Equal("1.5×", rateButton.Content); rateButton.Flyout.Hide();
            var controls = window.PlaybackView.ControlsView;
            Assert.Equal(0, Grid.GetRow(controls));
            var row = controls.FindControl<Grid>("TransportRow")!;
            var play = controls.FindControl<Button>("PauseButton")!;
            Assert.InRange(play.TranslatePoint(new Point(play.Bounds.Width / 2, play.Bounds.Height / 2), row)!.Value.X, row.Bounds.Width / 2 - 1, row.Bounds.Width / 2 + 1);
            Assert.All(row.Children.Where(child => child.IsVisible), child => Assert.InRange(child.TranslatePoint(new Point(0, child.Bounds.Height / 2), row)!.Value.Y, row.Bounds.Height / 2 - 1, row.Bounds.Height / 2 + 1));
            model.PlayerState = PlaybackState.Playing;
            Assert.True(controls.FindControl<Avalonia.Controls.Shapes.Path>("PauseIcon")!.IsVisible);
            Assert.False(controls.FindControl<Avalonia.Controls.Shapes.Path>("PlayIcon")!.IsVisible);
            model.PlayerState = PlaybackState.Paused;
            Assert.True(controls.FindControl<Avalonia.Controls.Shapes.Path>("PlayIcon")!.IsVisible);
            Assert.Equal(Avalonia.Layout.VerticalAlignment.Bottom, controls.VerticalAlignment);
            Assert.InRange(Assert.IsAssignableFrom<ISolidColorBrush>(controls.FindControl<Border>("ControlsBackground")!.Background).Color.A, (byte)1, (byte)254);
            Assert.Contains(window.PlaybackView.FindControl<Border>("VideoFrame")!, controls.GetVisualAncestors());
            Assert.Single(window.FindControl<Grid>("AppShell")!.RowDefinitions);
            Assert.False(window.PlaybackView.FindControl<Grid>("DetailRegion")!.IsVisible);
            Assert.Same(video, window.PlaybackView.VideoContainer);
            var exitButton = controls.FindControl<Button>("FullscreenButton")!;
            exitButton.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Escape });
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(WindowState.Normal, window.WindowState);
            Assert.Equal(playback, model.ShowPlaybackPage); Assert.Equal(1.5, model.Rate);
            Assert.Equal(0, Grid.GetRow(controls));
            Assert.NotEqual(Colors.Transparent, Assert.IsAssignableFrom<ISolidColorBrush>(controls.FindControl<Border>("ControlsBackground")!.Background).Color);
            Assert.True(window.FindControl<Border>("NavigationPane")!.IsVisible);
            Assert.True(window.PlaybackView.FindControl<Grid>("DetailRegion")!.IsVisible);
            Assert.Null(model.Engine.ActiveEngine);
        }
        finally { window.Close(); await model.DisposeAsync(); }
    }

    [Fact]
    public void PosterVisibilityCancelsOldLoadAndReleasesInactiveCard()
    {
        using var lifetime = new CancellationTokenSource(); using var card = new MediaCard(new("one", "title"));
        Assert.True(card.Activate(lifetime.Token)); var first = card.LoadToken;
        Assert.False(card.Activate(lifetime.Token)); card.Deactivate(); Assert.True(first.IsCancellationRequested);
        Assert.True(card.Activate(lifetime.Token)); Assert.False(card.LoadToken.IsCancellationRequested);
        card.Dispose(); Assert.False(card.IsActive); Assert.False(card.Activate(lifetime.Token));
    }
}
