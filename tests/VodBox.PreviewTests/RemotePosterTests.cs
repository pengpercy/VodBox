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
}
