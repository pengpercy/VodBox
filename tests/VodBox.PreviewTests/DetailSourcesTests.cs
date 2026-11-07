using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia;
using Avalonia.Threading;
using Avalonia.VisualTree;
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

    internal static async Task<Avalonia.Media.Imaging.Bitmap?> RenderAsync(Avalonia.Controls.Window window)
    {
        for (var i = 0; i < 20; i++)
        {
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick(1); Dispatcher.UIThread.RunJobs();
            if (Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window) is { } frame && i > 3) return frame;
            await Task.Delay(20);
        }
        return null;
    }

    /// <summary>
    /// 详情页排版：没有「试看」占位；剧集是等宽网格（每行填满当前列数、格子同宽，不再 7+3 折行），列数随宽度变化；
    /// 当前播放源 / 线路 / 选集都有选中态。
    /// </summary>
    [AvaloniaFact]
    public async Task DetailPageLaysEpisodesOutAsEvenGrid()
    {
        static VodResult Long(string source, string name) => new()
        {
            Id = source, Title = "庆余年 第二季", Year = "2024", SourceKey = source, SourceName = name, Remarks = "更新至36集",
            Area = "中国大陆", TypeName = "古装", Director = "孙皓", Actor = "张若昀 / 李沁 / 陈道明 / 吴刚 / 郭麒麟",
            Description = "该剧改编自猫腻同名畅销小说，承接上季，范闲率领使团回归途中，二皇子以费介、范思辙以及滕家遗孤的安危来威胁范闲，逼他向自己俯首称臣。",
            Lines = [new PlaybackLine("m3u8", "量子m3u8", Enumerable.Range(1, 36).Select(i => new Episode($"{source}-{i}", i == 36 ? "第36集完结" : $"第{i:00}集", $"https://{source}.example/{i}.m3u8")).ToArray()),
                     new PlaybackLine("yun", "云播", Enumerable.Range(1, 36).Select(i => new Episode($"{source}-y{i}", $"第{i:00}集", $"https://{source}.example/y{i}.m3u8")).ToArray())],
        };
        using var fixture = new Fixture([Long("a", "量子资源"), Long("b", "非凡影视资源站"), Long("c", "暴风"), Long("d", "红牛资源网")]);
        await fixture.Detail.OpenAsync(MediaItem.DoubanSourceKey, DoubanCard());
        var window = new Avalonia.Controls.Window { Width = 1100, Height = 860, Content = new VodBox.Desktop.Views.DetailView { DataContext = fixture.Detail } };
        window.Show();
        try
        {
            var frame = await RenderAsync(window);
            Assert.NotNull(frame);
            var dir = Path.Combine(Environment.CurrentDirectory, "preview-shots");
            Directory.CreateDirectory(dir);
            frame!.Save(Path.Combine(dir, "Detail.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());

            var texts = window.GetVisualDescendants().OfType<Avalonia.Controls.TextBlock>().Select(text => text.Text ?? "").ToArray();
            Assert.DoesNotContain(texts, text => text.Contains("试看"));

            void AssertEvenGrid()
            {
                var columns = fixture.Detail.EpisodeColumns;
                var cells = window.GetVisualDescendants().OfType<Avalonia.Controls.Button>().Where(button => button.Classes.Contains("episode")).ToArray();
                Assert.Equal(36, cells.Length);
                var widths = cells.Select(cell => Math.Round(cell.Bounds.Width)).Distinct().ToArray();
                Assert.Single(widths);
                var rows = cells.GroupBy(cell => Math.Round(cell.TranslatePoint(default, window)!.Value.Y)).OrderBy(group => group.Key).ToArray();
                Assert.All(rows.SkipLast(1), row => Assert.Equal(columns, row.Count()));
                Assert.Equal(36 - columns * (rows.Length - 1), rows[^1].Count());
            }
            AssertEvenGrid();
            var wide = fixture.Detail.EpisodeColumns;
            window.Width = 760;
            await RenderAsync(window);
            Assert.True(fixture.Detail.EpisodeColumns < wide, $"窄窗口列数应减少：{fixture.Detail.EpisodeColumns} / {wide}");
            AssertEvenGrid();

            var current = window.GetVisualDescendants().OfType<Avalonia.Controls.Button>().Where(button => button.Classes.Contains("current")).ToArray();
            Assert.Contains(current, button => button.Classes.Contains("episode") && (button.Tag as Episode)?.Id == "a-1");
            Assert.Single(current, button => button.Tag is DetailSource);
            fixture.Detail.SelectedEpisode = fixture.Detail.SelectedLine!.Episodes[4];
            Assert.Single(window.GetVisualDescendants().OfType<Avalonia.Controls.Button>(), button => button.Classes.Contains("episode") && button.Classes.Contains("current") && (button.Tag as Episode)?.Id == "a-5");
            var lineTabs = window.GetVisualDescendants().OfType<Avalonia.Controls.ListBox>().Single(box => box.Name == "LineTabs");
            Assert.True(lineTabs.IsEffectivelyVisible);
            lineTabs.SelectedIndex = 1;
            Assert.Equal("yun", fixture.Detail.SelectedLine!.Id);
        }
        finally { window.Close(); }
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
