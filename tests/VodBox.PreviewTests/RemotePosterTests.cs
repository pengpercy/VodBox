using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using VodBox.Desktop.Views;
using Xunit;

namespace VodBox.PreviewTests;

public sealed class RemotePosterTests
{
    private static TaskCompletionSource<T> Gate<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task Done(Task task) => task.WaitAsync(TimeSpan.FromSeconds(10));

    [AvaloniaFact]
    public async Task OldUrlAndItsCleanupCannotReplaceOrCancelTheNewUrl()
    {
        var oldEntered = Gate<bool>();
        var newEntered = Gate<bool>();
        var oldResult = Gate<Bitmap>();
        var newResult = Gate<Bitmap>();
        using var cache = new PosterCache((uri, _) =>
        {
            if (uri.AbsolutePath == "/old") { oldEntered.SetResult(true); return oldResult.Task; }
            newEntered.SetResult(true);
            return newResult.Task;
        });
        var poster = new RemotePoster(cache) { Url = "https://posters.invalid/old" };
        var window = new Window { Content = poster };
        try
        {
            window.Show();
            await Done(oldEntered.Task);
            var oldLoad = poster.LoadingTask;
            poster.Url = "https://posters.invalid/new";
            var newLoad = poster.LoadingTask;
            await Done(newEntered.Task);
            await Done(oldLoad); // Old finally must not clear the new request's scope.
            var oldBitmap = PosterCacheTests.Image();
            oldResult.SetResult(oldBitmap);
            var newBitmap = PosterCacheTests.Image();
            newResult.SetResult(newBitmap);
            await Done(newLoad);
            Assert.Same(newBitmap, poster.Source);
            poster.Url = "file:///tmp/poster.png";
            Assert.Null(poster.Source);
            PosterCacheTests.AssertUsable(newBitmap);
            poster.Url = "https://posters.invalid/old";
            await Done(poster.LoadingTask);
            Assert.Same(oldBitmap, poster.Source);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task DetachReattachWhileLoadingReusesTheTaskAndCannotCommitTheOldAttachment()
    {
        var entered = Gate<bool>();
        var result = Gate<Bitmap>();
        var calls = 0;
        using var cache = new PosterCache((_, _) =>
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult(true);
            return result.Task;
        });
        var poster = new RemotePoster(cache) { Url = "https://posters.invalid/same" };
        var window = new Window { Content = poster };
        try
        {
            window.Show();
            await Done(entered.Task);
            var oldLoad = poster.LoadingTask;
            window.Content = null;
            Assert.Null(poster.Source);
            window.Content = poster;
            var newLoad = poster.LoadingTask;
            Assert.NotSame(oldLoad, newLoad);
            await Done(oldLoad);
            var bitmap = PosterCacheTests.Image();
            result.SetResult(bitmap);
            await Done(newLoad);
            Assert.Equal(1, calls);
            Assert.Same(bitmap, poster.Source);
            window.Content = null;
            Assert.Null(poster.Source);
            PosterCacheTests.AssertUsable(bitmap);
            window.Content = poster;
            await Done(poster.LoadingTask);
            Assert.Same(bitmap, poster.Source);
            Assert.Equal(1, calls);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task DetachingOneOfTwoPostersKeepsTheOtherSharedBitmapAlive()
    {
        var calls = 0;
        using var cache = new PosterCache((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(PosterCacheTests.Image());
        }, maxBytes: 0);
        var first = new RemotePoster(cache) { Url = "https://posters.invalid/same" };
        var second = new RemotePoster(cache) { Url = first.Url };
        var panel = new StackPanel { Children = { first, second } };
        var window = new Window { Content = panel };
        try
        {
            window.Show();
            await Done(Task.WhenAll(first.LoadingTask, second.LoadingTask));
            var bitmap = Assert.IsAssignableFrom<Bitmap>(second.Source);
            Assert.Same(bitmap, first.Source);
            panel.Children.Remove(first);
            Assert.Null(first.Source);
            Assert.Same(bitmap, second.Source);
            PosterCacheTests.AssertUsable(bitmap);
            panel.Children.Add(first);
            await Done(first.LoadingTask);
            Assert.Same(bitmap, first.Source);
            Assert.Equal(1, calls);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task DetachedUrlChangeLoadsOnlyTheLatestUrlOnAttach()
    {
        var oldEntered = Gate<bool>();
        var oldResult = Gate<Bitmap>();
        using var cache = new PosterCache((uri, _) =>
        {
            if (uri.AbsolutePath == "/old") { oldEntered.SetResult(true); return oldResult.Task; }
            Assert.Equal("/latest", uri.AbsolutePath);
            return Task.FromResult(PosterCacheTests.Image());
        });
        var poster = new RemotePoster(cache) { Url = "https://posters.invalid/old" };
        var window = new Window { Content = poster };
        try
        {
            window.Show();
            await Done(oldEntered.Task);
            var oldLoad = poster.LoadingTask;
            window.Content = null;
            poster.Url = "https://posters.invalid/intermediate";
            poster.Url = "https://posters.invalid/latest";
            window.Content = poster;
            await Done(poster.LoadingTask);
            var latest = poster.Source;
            await Done(oldLoad);
            oldResult.SetResult(PosterCacheTests.Image());
            Assert.Same(latest, poster.Source);
        }
        finally { window.Close(); }
    }
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedM3uLogoFallsBackToIndexedUrlAndUnknownNameStaysEmpty(bool timeout)
    {
        var attempted = new List<Uri>();
        using var cache = new PosterCache((uri, _) =>
        {
            attempted.Add(uri);
            return uri.Host == "logos.invalid"
                ? Task.FromException<Bitmap>(timeout ? new TaskCanceledException("timeout") : new IOException("offline"))
                : Task.FromResult(PosterCacheTests.Image());
        });
        var poster = new RemotePoster(cache) { Url = "https://logos.invalid/broken", ChannelName = "CCTV-1 综合高清" };
        var window = new Window { Content = poster };
        try
        {
            window.Show();
            await Done(poster.LoadingTask);
            Assert.True(poster.HasImage);
            Assert.NotNull(poster.Source);
            Assert.Equal("logos.invalid", attempted[0].Host);
            Assert.Equal(VodBox.Desktop.Services.BuiltInChannelLogos.Find(poster.ChannelName), attempted[1]);
            poster.ChannelName = "不存在的频道-XYZ";
            await Done(poster.LoadingTask);
            Assert.False(poster.HasImage);
            Assert.Null(poster.Source);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task SuccessfulM3uLogoTakesPriorityOverBuiltInImage()
    {
        var bitmap = PosterCacheTests.Image();
        using var cache = new PosterCache((_, _) => Task.FromResult(bitmap));
        var poster = new RemotePoster(cache) { Url = "https://logos.invalid/good", ChannelName = "CCTV1" };
        var window = new Window { Content = poster };
        try
        {
            window.Show();
            await Done(poster.LoadingTask);
            Assert.Same(bitmap, poster.Source);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void BuiltInLogoLookupKeepsSportsPlusDistinctAndIndexContainsOnlyHttpsUrls()
    {
        var normal = VodBox.Desktop.Services.BuiltInChannelLogos.Find("CCTV-5高清");
        var plus = VodBox.Desktop.Services.BuiltInChannelLogos.Find("CCTV5+ 体育赛事HD");
        Assert.NotNull(normal); Assert.NotNull(plus); Assert.NotEqual(normal, plus);
        using var stream = Avalonia.Platform.AssetLoader.Open(new Uri("avares://VodBox.Desktop/Assets/ChannelLogos/index.json"));
        using var json = System.Text.Json.JsonDocument.Parse(stream);
        Assert.Equal(3266, json.RootElement.EnumerateObject().Count());
        foreach (var entry in json.RootElement.EnumerateObject())
        {
            var uri = new Uri(entry.Value.GetString()!);
            Assert.Equal("https", uri.Scheme);
        }
    }

    [AvaloniaFact]
    public async Task RapidLogoRecyclingStartsOnlyTheLatestDownload()
    {
        var requested = new List<Uri>();
        using var cache = new PosterCache((uri, _) =>
        {
            requested.Add(uri);
            return Task.FromResult(PosterCacheTests.Image());
        });
        var poster = new RemotePoster(cache) { ChannelName = "CCTV1", Url = "https://logos.invalid/start" };
        var window = new Window { Content = poster };
        try
        {
            window.Show();
            var loads = new List<Task> { poster.LoadingTask };
            for (var i = 0; i < 20; i++)
            {
                poster.Url = $"https://logos.invalid/{i}";
                loads.Add(poster.LoadingTask);
            }
            await Done(Task.WhenAll(loads));
            Assert.Equal("/19", Assert.Single(requested).AbsolutePath);
            Assert.True(poster.HasImage);
            Assert.Null(poster.Transitions);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ScrollingImmediatelyFinishesLogoFadeAndDoesNotReplayItAfterStopping()
    {
        using var cache = new PosterCache((_, _) => Task.FromResult(PosterCacheTests.Image()));
        var poster = new RemotePoster(cache) { ChannelName = "CCTV1", Url = "https://logos.invalid/first" };
        var window = new Window { Content = poster };
        try
        {
            window.Show();
            await Done(poster.LoadingTask);
            Assert.True(poster.IsLogoFading);
            Assert.True(poster.Opacity < 1);
            poster.ChannelIsScrolling = true;
            Assert.False(poster.IsLogoFading);
            Assert.Equal(1, poster.Opacity);
            poster.ChannelIsScrolling = false;
            Assert.False(poster.IsLogoFading);
            Assert.Equal(1, poster.Opacity);
            poster.ChannelIsScrolling = true;
            poster.Url = "https://logos.invalid/while-scrolling";
            await Done(poster.LoadingTask);
            Assert.False(poster.IsLogoFading);
            Assert.False(poster.HasImage); // 新台标延后到停止滚动后才加载。
            Assert.Equal(0, poster.Opacity);
            poster.ChannelIsScrolling = false;
            poster.Url = "https://logos.invalid/after-scrolling";
            await Done(poster.LoadingTask);
            Assert.True(poster.IsLogoFading);
            window.Content = null;
            Assert.False(poster.IsLogoFading);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ScrollUsesCachedLogoImmediatelyAndDefersUncachedLogoUntilIdle()
    {
        var calls = 0;
        using var cache = new PosterCache((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(PosterCacheTests.Image());
        });
        using (var preloaded = await cache.AcquireAsync(new Uri("https://logos.invalid/cached")).WaitAsync(TimeSpan.FromSeconds(10))) { }
        var poster = new RemotePoster(cache)
        {
            ChannelName = "CCTV1", Url = "https://logos.invalid/cached", ChannelIsScrolling = true,
        };
        var window = new Window { Content = poster };
        try
        {
            window.Show();
            Assert.True(poster.HasImage);
            Assert.Equal(1, poster.Opacity);
            Assert.False(poster.IsLogoFading);
            Assert.Equal(1, calls);
            poster.Url = "https://logos.invalid/not-cached";
            await Done(poster.LoadingTask);
            Assert.False(poster.HasImage);
            Assert.Equal(1, calls);
            poster.ChannelIsScrolling = false;
            await Done(poster.LoadingTask);
            Assert.True(poster.HasImage);
            Assert.Equal(2, calls);
            Assert.True(poster.IsLogoFading);
        }
        finally { window.Close(); }
    }

}
