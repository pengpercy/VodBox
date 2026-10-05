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
    [InlineData(1280, false, true, true)]
    [InlineData(1600, true, true, true)]
    public async Task DesktopLayoutAdaptsBrowsingAndPlaybackWithoutRecreatingVideoContainer(int width, bool playback, bool browseVisible, bool playbackVisible)
    {
        var window = new MainWindow(preview: true) { Width = width };
        var model = (DesignMainViewModel)window.DataContext!;
        try
        {
            window.Show(); model.ShowPlaybackPage = playback; Dispatcher.UIThread.RunJobs();
            Assert.Equal(browseVisible, window.FindControl<Grid>("BrowsePane")!.IsVisible);
            Assert.Equal(playbackVisible, window.FindControl<Grid>("PlaybackPane")!.IsVisible);
            var container = window.FindControl<Grid>("VideoContainer")!;
            model.ShowPlaybackPage = !playback; Dispatcher.UIThread.RunJobs();
            Assert.Same(container, window.FindControl<Grid>("VideoContainer")); Assert.Null(model.Engine.ActiveEngine);
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
            viewModel.ShowLibrary = false; viewModel.ShowSettings = true; Dispatcher.UIThread.RunJobs();
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "播放内核");
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
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            var view = window.FindControl<DanmakuView>("DanmakuPreview")!; view.RefreshFrame();
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
            var images = window.FindControl<ListBox>("PosterGrid")!.GetVisualDescendants().OfType<Image>().Count();
            Assert.InRange(images, 1, 100);
            model.SelectedCard = model.Cards[0]; Assert.False(selected.IsSelected);
            model.Items.Clear(); Assert.Empty(model.CardRows); Assert.Null(model.SelectedCard);
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
