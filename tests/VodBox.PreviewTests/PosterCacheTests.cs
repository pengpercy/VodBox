using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using VodBox.Desktop.Views;
using Xunit;

namespace VodBox.PreviewTests;

public sealed class PosterCacheTests
{
    private static Uri Url(string name) => new($"https://posters.invalid/{name}");
    private static TaskCompletionSource<T> Gate<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task Done(Task task) => task.WaitAsync(TimeSpan.FromSeconds(10));
    private static Task<T> Done<T>(Task<T> task) => task.WaitAsync(TimeSpan.FromSeconds(10));
    internal static Bitmap Image(int width = 8, int height = 8) =>
        new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
    internal static void AssertUsable(Bitmap image)
    {
        using var stream = new MemoryStream();
        image.Save(stream, PngBitmapEncoderOptions.Default);
        Assert.True(stream.Length > 0);
    }
    private static void AssertDisposed(Bitmap image) => Assert.ThrowsAny<Exception>(() =>
    {
        using var stream = new MemoryStream();
        image.Save(stream, PngBitmapEncoderOptions.Default);
    });

    [AvaloniaFact]
    public async Task ConcurrentConsumersShareOneLoadAndCancellationOnlyReleasesItsReservation()
    {
        var entered = Gate<CancellationToken>();
        var result = Gate<Bitmap>();
        var calls = 0;
        using var cache = new PosterCache((_, ct) =>
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult(ct);
            return result.Task;
        });
        using var cancelled = new CancellationTokenSource();
        var first = cache.AcquireAsync(Url("same"), cancelled.Token);
        var sharedToken = await Done(entered.Task);
        var others = Enumerable.Range(0, 20).Select(_ => cache.AcquireAsync(Url("same"))).ToArray();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Done(first));
        Assert.False(sharedToken.IsCancellationRequested);
        var bitmap = Image();
        result.SetResult(bitmap);
        var leases = await Done(Task.WhenAll(others));
        try
        {
            Assert.Equal(1, calls);
            Assert.All(leases, lease => Assert.Same(bitmap, lease.Bitmap));
            leases[0].Dispose();
            leases[0].Dispose();
            AssertUsable(leases[1].Bitmap);
        }
        finally { foreach (var lease in leases) lease.Dispose(); }
        using var reused = await Done(cache.AcquireAsync(Url("same")));
        Assert.Same(bitmap, reused.Bitmap);
        Assert.Equal(1, calls);
    }

    [AvaloniaFact]
    public async Task BriefAbsenceRejoinsAnInFlightLoad()
    {
        var entered = Gate<CancellationToken>();
        var result = Gate<Bitmap>();
        var calls = 0;
        using var cache = new PosterCache((_, ct) =>
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult(ct);
            return result.Task;
        }, abandonDelay: TimeSpan.FromMinutes(1));
        using var cancellation = new CancellationTokenSource();
        var first = cache.AcquireAsync(Url("same"), cancellation.Token);
        var token = await Done(entered.Task);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Done(first));
        var next = cache.AcquireAsync(Url("same"));
        result.SetResult(Image());
        using var lease = await Done(next);
        Assert.Equal(1, calls);
        Assert.False(token.IsCancellationRequested);
    }

    [AvaloniaFact]
    public async Task AbandonedLoadIsCancelledAndItsLateBitmapCannotReplaceRetry()
    {
        var entered = Gate<CancellationToken>();
        var cancelled = Gate<bool>();
        var oldResult = Gate<Bitmap>();
        var calls = 0;
        using var cache = new PosterCache((_, ct) =>
        {
            if (Interlocked.Increment(ref calls) != 1) return Task.FromResult(Image());
            ct.Register(() => cancelled.TrySetResult(true));
            entered.SetResult(ct);
            return oldResult.Task; // Deliberately ignore cancellation.
        }, abandonDelay: TimeSpan.Zero, maxConcurrentLoads: 2);
        using var cancellation = new CancellationTokenSource();
        var first = cache.AcquireAsync(Url("same"), cancellation.Token);
        await Done(entered.Task);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Done(first));
        await Done(cancelled.Task);
        using var retry = await Done(cache.AcquireAsync(Url("same")));
        var late = Image();
        oldResult.SetResult(late);
        // Fill the other slot: its admission proves the abandoned loader's finally has run.
        using var other = await Done(cache.AcquireAsync(Url("other")));
        using var same = await Done(cache.AcquireAsync(Url("same")));
        Assert.Same(retry.Bitmap, same.Bitmap);
        AssertUsable(retry.Bitmap);
        // Slot completion alone can race; wait on an observable bitmap disposal, with a bounded yield loop.
        await WaitForDisposal(late);
    }

    private static async Task WaitForDisposal(Bitmap image)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            try { AssertUsable(image); }
            catch (Exception error) when (error is not Xunit.Sdk.XunitException) { return; }
            await Task.Delay(1, timeout.Token);
        }
    }

    [AvaloniaFact]
    public async Task FailureIsSharedThenTheNextAcquireRetries()
    {
        var entered = Gate<bool>();
        var failed = Gate<Bitmap>();
        var calls = 0;
        using var cache = new PosterCache((_, _) =>
        {
            if (Interlocked.Increment(ref calls) != 1) return Task.FromResult(Image());
            entered.SetResult(true);
            return failed.Task;
        });
        var first = cache.AcquireAsync(Url("same"));
        await Done(entered.Task);
        var second = cache.AcquireAsync(Url("same"));
        failed.SetException(new InvalidDataException("bad image"));
        await Assert.ThrowsAsync<InvalidDataException>(() => Done(first));
        await Assert.ThrowsAsync<InvalidDataException>(() => Done(second));
        using var retry = await Done(cache.AcquireAsync(Url("same")));
        Assert.Equal(2, calls);
        AssertUsable(retry.Bitmap);
    }

    [AvaloniaFact]
    public async Task IdleImagesFollowLruAndHitsRefreshRecency()
    {
        var calls = new Dictionary<string, int>();
        using var cache = new PosterCache((uri, _) =>
        {
            lock (calls) calls[uri.AbsolutePath] = calls.GetValueOrDefault(uri.AbsolutePath) + 1;
            return Task.FromResult(Image());
        }, maxImages: 2);
        var a = await Done(cache.AcquireAsync(Url("a")));
        var bitmapA = a.Bitmap;
        a.Dispose();
        var b = await Done(cache.AcquireAsync(Url("b")));
        var bitmapB = b.Bitmap;
        b.Dispose();
        using (var hit = await Done(cache.AcquireAsync(Url("a")))) Assert.Same(bitmapA, hit.Bitmap);
        using (var c = await Done(cache.AcquireAsync(Url("c")))) AssertUsable(c.Bitmap);
        AssertDisposed(bitmapB);
        AssertUsable(bitmapA);
        using var reload = await Done(cache.AcquireAsync(Url("b")));
        Assert.Equal(2, calls["/b"]);
        Assert.Equal(1, calls["/a"]);
    }

    [AvaloniaFact]
    public async Task ByteBudgetProtectsLeasesAndTrimsAsSoonAsTheyBecomeIdle()
    {
        using var cache = new PosterCache((_, _) => Task.FromResult(Image()), maxBytes: 256, maxImages: 10);
        var a = await Done(cache.AcquireAsync(Url("a")));
        var b = await Done(cache.AcquireAsync(Url("b")));
        AssertUsable(a.Bitmap);
        AssertUsable(b.Bitmap); // 512 bytes of active images is allowed over the 256-byte budget.
        a.Dispose();
        AssertDisposed(a.Bitmap);
        AssertUsable(b.Bitmap);
        b.Dispose();
        using var hit = await Done(cache.AcquireAsync(Url("b")));
        Assert.Same(b.Bitmap, hit.Bitmap);
    }

    [AvaloniaFact]
    public async Task ZeroBudgetAndCacheDisposalNeverDisposeAnActiveLease()
    {
        using var cache = new PosterCache((_, _) => Task.FromResult(Image()), maxBytes: 0);
        var lease = await Done(cache.AcquireAsync(Url("a")));
        cache.Dispose();
        AssertUsable(lease.Bitmap);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => cache.AcquireAsync(Url("a")));
        lease.Dispose();
        lease.Dispose();
        AssertDisposed(lease.Bitmap);
    }

    [AvaloniaFact]
    public async Task DistinctLoadsRespectTheConcurrencyGateAndCancelledQueueDoesNotCallLoader()
    {
        var firstEntered = Gate<bool>();
        var release = Gate<Bitmap>();
        var calls = 0;
        using var cache = new PosterCache((_, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                firstEntered.SetResult(true);
                return release.Task;
            }
            return Task.FromResult(Image());
        }, maxConcurrentLoads: 1, abandonDelay: TimeSpan.Zero);
        var first = cache.AcquireAsync(Url("first"));
        await Done(firstEntered.Task);
        using var cancellation = new CancellationTokenSource();
        var queued = cache.AcquireAsync(Url("cancelled"), cancellation.Token);
        var next = cache.AcquireAsync(Url("next"));
        Assert.False(next.IsCompleted);
        Assert.Equal(1, Volatile.Read(ref calls));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Done(queued));
        release.SetResult(Image());
        using var firstLease = await Done(first);
        using var nextLease = await Done(next);
        Assert.Equal(2, calls);
    }
    [AvaloniaFact]
    public async Task CompletedCacheFastPathSharesBitmapAndKeepsIdleMemoryWithinBudget()
    {
        var calls = 0;
        using var cache = new PosterCache((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(Image());
        }, maxBytes: 512, maxImages: 2);
        for (var i = 0; i < 100; i++)
        {
            using (var loaded = await Done(cache.AcquireAsync(Url($"logo-{i}"))))
            using (var reused = cache.TryAcquireCached(Url($"logo-{i}")))
            {
                Assert.NotNull(reused);
                Assert.Same(loaded.Bitmap, reused.Bitmap);
            }
            Assert.InRange(cache.Statistics.Bytes, 0, 512);
            Assert.InRange(cache.Statistics.Images, 0, 2);
            Assert.Equal(0, cache.Statistics.Leases);
        }
        Assert.Equal(100, calls);
        Assert.Null(cache.TryAcquireCached(Url("logo-0")));
        Assert.Null(cache.TryAcquireCached(Url("unknown")));
    }

}
