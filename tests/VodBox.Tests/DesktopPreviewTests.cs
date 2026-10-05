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
    [AvaloniaFact]
    public async Task PreviewBuildsBoundCardsWithoutStartingNativePlayer()
    {
        var window = new MainWindow(preview: true);
        var viewModel = Assert.IsType<DesignMainViewModel>(window.DataContext);
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            Assert.Equal(3, viewModel.Cards.Count); Assert.Null(viewModel.Engine.Player);
            Assert.All(viewModel.Cards, card => Assert.False(card.IsActive));
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "探索自然");
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "你的媒体，随处播放");
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
