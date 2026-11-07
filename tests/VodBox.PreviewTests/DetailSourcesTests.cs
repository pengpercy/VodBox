using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using VodBox.Core;
using VodBox.Desktop.Services;
using VodBox.Desktop.ViewModels;
using VodBox.Infrastructure;
using Xunit;

namespace VodBox.PreviewTests;

/// <summary>详情页的多播放源（LunaTV 换源/优选/刮削）行为。</summary>
public sealed class DetailSourcesTests
{
    private static VodResult Result(string source, string id, int episodes = 3, string poster = "") => new()
    {
        Id = id, Title = "繁花", Year = "2023", Poster = poster, SourceKey = source, SourceName = source + "资源",
        Lines = [new PlaybackLine("m3u8", "m3u8", Enumerable.Range(1, episodes).Select(i => new Episode($"{source}-{i}", $"第{i}集", $"https://{source}.example/{i}.m3u8")).ToArray())],
    };

    private static async IAsyncEnumerable<VodResult> Stream(IEnumerable<VodResult> results, [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var result in results) { await Task.Yield(); ct.ThrowIfCancellationRequested(); yield return result; }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), $"vodbox-detail-sources-{Guid.NewGuid():N}");
        private readonly AppServices _services;
        public MainViewModel Main { get; }
        public DetailViewModel Detail { get; }
        public List<(string Title, string? Year, string? Kind)> Searches { get; } = [];

        public Fixture(IReadOnlyList<VodResult> found, bool preferBest = false, Func<VodResult, double>? score = null, DoubanSuggestion? scraped = null)
        {
            _services = new AppServices(_directory);
            var deps = new DetailDependencies(
                (key, id, _, _) => Task.FromResult(found.First(result => result.SourceKey == key && result.Id == id)),
                (title, year, kind, ct) => { Searches.Add((title, year, kind)); return Stream(found, ct); },
                () => new SiteSettings { PreferBestSource = preferBest, BlockAds = true },
                score is null ? null : (items, progress, _) =>
                {
                    foreach (var item in items) progress(item, new SpeedTestResult("1080p", score(item), 50));
                    return Task.FromResult<IReadOnlyList<(VodResult, SpeedTestResult, double)>>(
                        items.Select(item => (item, new SpeedTestResult("1080p", score(item), 50), score(item))).OrderByDescending(entry => entry.Item3).ToArray());
                },
                (_, _, _) => Task.FromResult(scraped));
            Main = new MainViewModel(_services, true, deps, _services.Store);
            Main.AttachDispatcher(Dispatcher.UIThread);
            Detail = Main.Detail;
        }

        public void Dispose()
        {
            Dispatcher.UIThread.RunJobs();
            _services.Dispose();
            try { Directory.Delete(_directory, true); } catch (IOException) { }
        }
    }

    private static MediaItem DoubanCard() => new() { Id = "34874646", Title = "繁花", Year = "2023", Kind = "tv", SourceKey = MediaItem.DoubanSourceKey, Poster = "https://img.example/douban.jpg", DoubanId = "34874646" };

    [AvaloniaFact]
    public async Task DoubanCardSearchesAllSourcesAndShowsFirstArrival()
    {
        using var fixture = new Fixture([Result("a", "1"), Result("b", "2")]);
        await fixture.Detail.OpenAsync(MediaItem.DoubanSourceKey, DoubanCard());
        Assert.Equal(("繁花", "2023", "tv"), Assert.Single(fixture.Searches));
        Assert.Equal(["a", "b"], fixture.Detail.Sources.Select(source => source.Result.SourceKey));
        Assert.Equal("a", fixture.Detail.CurrentSource!.Result.SourceKey);
        // 豆瓣进来的用豆瓣海报。
        Assert.Equal("https://img.example/douban.jpg", fixture.Detail.Detail!.Item.Poster);
        Assert.Equal("a-1", fixture.Detail.SelectedEpisode!.Id);
    }

    [AvaloniaFact]
    public async Task PreferBestSwitchesToHighestScoreUnlessUserPicked()
    {
        using var fixture = new Fixture([Result("slow", "1"), Result("fast", "2")], preferBest: true, score: result => result.SourceKey == "fast" ? 90 : 10);
        await fixture.Detail.OpenAsync(MediaItem.DoubanSourceKey, DoubanCard());
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("fast", fixture.Detail.CurrentSource!.Result.SourceKey);
        Assert.Equal("fast", fixture.Detail.Sources[0].Result.SourceKey);
        Assert.All(fixture.Detail.Sources, source => Assert.Contains("1080p", source.SpeedLabel));
    }

    [AvaloniaFact]
    public async Task OpeningAGroupWithPreferredSourcePinsIt()
    {
        var group = new[] { Result("fast", "2"), Result("chosen", "1") };
        using var fixture = new Fixture(group, preferBest: true, score: result => result.SourceKey == "fast" ? 90 : 10);
        await fixture.Detail.OpenGroupAsync(group, group[1]);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("chosen", fixture.Detail.CurrentSource!.Result.SourceKey);
        Assert.Empty(fixture.Searches); // 整组已知，不重复搜索
    }

    [AvaloniaFact]
    public async Task SwitchingSourceKeepsEpisodeIndex()
    {
        using var fixture = new Fixture([Result("a", "1"), Result("b", "2")]);
        await fixture.Detail.OpenAsync(MediaItem.DoubanSourceKey, DoubanCard());
        fixture.Detail.SelectedEpisode = fixture.Detail.SelectedLine!.Episodes[2];
        await fixture.Detail.PickSourceCommand.ExecuteAsync(fixture.Detail.Sources[1]);
        Assert.Equal("b", fixture.Detail.CurrentSource!.Result.SourceKey);
        Assert.Equal("b-3", fixture.Detail.SelectedEpisode!.Id);
        Assert.True(fixture.Detail.Sources[1].IsCurrent);
        Assert.False(fixture.Detail.Sources[0].IsCurrent);
    }

    [AvaloniaFact]
    public async Task SourceCardShowsThatSourceFirstThenAddsOthers()
    {
        using var fixture = new Fixture([Result("a", "1"), Result("b", "2")]);
        await fixture.Detail.OpenAsync("b", new MediaItem { Id = "2", Title = "繁花", Year = "2023" });
        Assert.Equal("b", fixture.Detail.CurrentSource!.Result.SourceKey);
        Assert.Equal(["b", "a"], fixture.Detail.Sources.Select(source => source.Result.SourceKey));
        Assert.Equal(("繁花", "2023", "tv"), Assert.Single(fixture.Searches));
    }

    [AvaloniaFact]
    public async Task MissingPosterIsScrapedFromDouban()
    {
        using var fixture = new Fixture([Result("a", "1")], scraped: new DoubanSuggestion("34874646", "繁花", "繁花", "https://img.example/scraped.jpg", "2023", "tv", "30"));
        await fixture.Detail.OpenAsync("a", new MediaItem { Id = "1", Title = "繁花" });
        for (var i = 0; i < 20 && fixture.Detail.Detail!.Item.Poster != "https://img.example/scraped.jpg"; i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
        Assert.Equal("https://img.example/scraped.jpg", fixture.Detail.Detail!.Item.Poster);
        Assert.Equal("34874646", fixture.Detail.Detail.Item.DoubanId);
    }

    [AvaloniaFact]
    public async Task NoSourcesShowsHint()
    {
        using var fixture = new Fixture([]);
        await fixture.Detail.OpenAsync(MediaItem.DoubanSourceKey, DoubanCard());
        Assert.Null(fixture.Detail.Detail);
        Assert.Contains("没有在已启用的采集源中找到", fixture.Detail.Hint);
    }
}
