using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VodBox.Core;
using VodBox.Desktop;
using VodBox.Desktop.Views;
using VodBox.Infrastructure;
using Xunit;

namespace VodBox.Tests;

public sealed class CollectionPageTests
{
    [AvaloniaTheory]
    [InlineData("发现")]
    [InlineData("搜索")]
    [InlineData("收藏")]
    [InlineData("历史")]
    public async Task HomeEntriesOpenIndependentPagesWithoutLegacyNavigation(string page)
    {
        var window = new MainWindow(preview: true); var model = (MainViewModel)window.DataContext!;
        try
        {
            window.Show(); await model.NavigateCommand.ExecuteAsync(page); Dispatcher.UIThread.RunJobs();
            Assert.Equal(page == "发现", model.ShowLibrary); Assert.False(model.ShowPlaybackPage);
            Assert.Null(window.FindControl<Border>("NavigationPane"));
            Assert.Null(window.FindControl<Grid>("WorkspaceHeader"));
            Assert.Equal(page == "搜索", model.ShowSearch);
            Assert.Equal(page == "收藏", model.ShowFavorites);
            Assert.Equal(page == "历史", model.ShowHistory);
            await model.NavigateCommand.ExecuteAsync("首页"); Assert.True(model.ShowHome);
        }
        finally { window.Close(); await model.DisposeAsync(); }
    }
    [AvaloniaFact]
    public async Task SelectedHomeVodEntryDoesNotNavigateIntoTheLibrary()
    {
        var window = new MainWindow(preview: true); var model = (MainViewModel)window.DataContext!;
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            var button = window.FindControl<HomeView>("HomePage")!.GetVisualDescendants().OfType<Button>().Single(item => item.Classes.Contains("current"));
            Assert.Equal("首页", button.CommandParameter);
            await model.NavigateCommand.ExecuteAsync((string)button.CommandParameter!);
            Assert.True(model.ShowHome); Assert.False(model.ShowLibrary);
            Assert.Null(window.FindControl<Border>("NavigationPane"));
        }
        finally { window.Close(); await model.DisposeAsync(); }
    }
    [AvaloniaFact]
    public async Task SearchBackReturnsToKeywordsBeforeHomeAndIncognitoDoesNotRecordTerms()
    {
        var model = new MainViewModel(designMode: true);
        try
        {
            await model.NavigateCommand.ExecuteAsync("搜索");
            await model.SubmitSearchCommand.ExecuteAsync("  电影  ");
            Assert.True(model.SearchSubmitted); Assert.Equal("电影", Assert.Single(model.SearchHistory));
            Assert.Contains("配置", model.SearchPageStatus);
            await model.SearchBackCommand.ExecuteAsync(null);
            Assert.True(model.ShowSearch); Assert.False(model.SearchSubmitted);
            model.Incognito = true; await model.SubmitSearchCommand.ExecuteAsync("私密关键词");
            Assert.Equal("电影", Assert.Single(model.SearchHistory));
            await model.SearchBackCommand.ExecuteAsync(null); await model.SearchBackCommand.ExecuteAsync(null);
            Assert.True(model.ShowHome); Assert.False(model.ShowSearch);
        }
        finally { await model.DisposeAsync(); }
    }
    [AvaloniaFact]
    public async Task SearchSourceFilterRetainsCardsAndRecentViewingGroupsEpisodes()
    {
        var model = new MainViewModel(designMode: true);
        try
        {
            var sourceA = new SourceDefinition { Id = "a", Name = "站点 A" };
            var sourceB = new SourceDefinition { Id = "b", Name = "站点 B" };
            model.SearchResults.Add(new(sourceA, new("1", "影片 A", "https://example.com/a.jpg")));
            var first = Assert.Single(model.FilteredSearchCards);
            model.SearchResults.Add(new(sourceB, new("2", "影片 B")));
            Assert.Same(first, model.FilteredSearchCards[0]);
            model.SearchSourceFilter = sourceB;
            Assert.Equal("影片 B", Assert.Single(model.FilteredSearchCards).Title);
            model.SearchSourceFilter = null; Assert.Equal(2, model.FilteredSearchCards.Count);
            var older = new HistoryEntry("c", "a", "1", "e1", "影片 A", "https://example.com/video", 1000, DateTimeOffset.UtcNow.AddDays(-1));
            model.History.Add(older); model.History.Add(older with { EpisodeId = "e2", PositionMs = 60000, UpdatedAt = DateTimeOffset.UtcNow });
            Assert.Equal("e2", Assert.Single(model.HistoryCards).History!.EpisodeId);
            Assert.Contains("00:01:00", model.HistoryCards[0].Caption);
        }
        finally { await model.DisposeAsync(); }
    }
    [AvaloniaFact]
    public async Task SearchGridVirtualizesRowsAndAdaptsToWindowWidth()
    {
        var window = new MainWindow(preview: true) { Width = 1280, Height = 720 };
        var model = (MainViewModel)window.DataContext!;
        try
        {
            await model.NavigateCommand.ExecuteAsync("搜索"); model.SearchSubmitted = true;
            var source = new SourceDefinition { Id = "a", Name = "站点 A" };
            for (int index = 0; index < 1000; index++) model.SearchResults.Add(new(source, new(index.ToString(), $"影片 {index}")));
            window.Show(); Dispatcher.UIThread.RunJobs();
            var grid = window.FindControl<SearchView>("SearchPage")!.GetVisualDescendants().OfType<CollectionGridView>().Single();
            Assert.Equal(2, grid.Rows[0].Columns);
            Assert.InRange(grid.GetVisualDescendants().OfType<CollectionCardView>().Count(), 1, 40);
            window.Width = 800; Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, grid.Rows[0].Columns);
            var list = grid.FindControl<ListBox>("CardRows")!;
            list.ScrollIntoView(grid.Rows[^1]); Dispatcher.UIThread.RunJobs();
            Assert.InRange(grid.GetVisualDescendants().OfType<CollectionCardView>().Count(), 1, 40);
            Assert.Equal(1000, model.FilteredSearchCards.Count);
        }
        finally { window.Close(); await model.DisposeAsync(); }
    }
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CardsKeepThreeToFourArtworkAndFillTheirGridCells(bool search)
    {
        var window = new MainWindow(preview: true) { Width = 1280, Height = 720 };
        var model = (MainViewModel)window.DataContext!;
        try
        {
            await model.NavigateCommand.ExecuteAsync(search ? "搜索" : "收藏");
            if (search) { model.SearchSubmitted = true; model.SearchResults.Add(new(new() { Id = "s", Name = "站点" }, new("1", "影片"))); }
            else model.Favorites.Add(new("c", "s", "1", "影片"));
            window.Show(); Dispatcher.UIThread.RunJobs();
            var card = window.GetVisualDescendants().OfType<CollectionCardView>().First(view => view.IsEffectivelyVisible);
            var artwork = card.FindControl<Border>("Artwork")!;
            Assert.InRange(artwork.Bounds.Width / artwork.Bounds.Height, .749, .751);
            Assert.InRange(artwork.Bounds.Width, search ? 149 : 180, search ? 151 : 220);
            var button = card.FindControl<Button>("CardButton")!;
            Assert.InRange(button.Bounds.Width, card.Bounds.Width - 1, card.Bounds.Width + 1);
            if (search) Assert.True(button.Bounds.Width > 400);
        }
        finally { window.Close(); await model.DisposeAsync(); }
    }
    [Fact]
    public async Task PosterMetadataSurvivesReopenAndBackupMerge()
    {
        string directory = Path.Combine(Path.GetTempPath(), "vodbox-artwork-" + Guid.NewGuid()); Directory.CreateDirectory(directory);
        try
        {
            var store = new LibraryStore(Path.Combine(directory, "library.db"));
            var favorite = new FavoriteEntry("c", "s", "m", "影片", "https://example.com/poster.jpg", "站点名称");
            var history = new HistoryEntry("c", "s", "m", "e", "影片", "https://example.com/video", 60000, DateTimeOffset.UtcNow, Poster: favorite.Poster, SourceName: favorite.SourceName);
            await store.SetFavoriteAsync(favorite, true); await store.SaveHistoryAsync(history);
            var reopened = new LibraryStore(Path.Combine(directory, "library.db"));
            Assert.Equal(favorite, Assert.Single(await reopened.GetFavoritesAsync())); Assert.Equal(history, Assert.Single(await reopened.GetHistoryAsync()));
            var target = new LibraryStore(Path.Combine(directory, "restored.db")); target.MergeSnapshot(reopened.ExportSnapshot());
            Assert.Equal(favorite, Assert.Single(await target.GetFavoritesAsync())); Assert.Equal(history, Assert.Single(await target.GetHistoryAsync()));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }
}
