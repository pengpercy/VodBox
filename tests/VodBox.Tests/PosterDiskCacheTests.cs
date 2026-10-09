using VodBox.Infrastructure;
using Xunit;

namespace VodBox.Tests;

public sealed class PosterDiskCacheTests
{
    [Fact]
    public async Task DiskCacheSurvivesRecreationExpiresAndEnforcesBudget()
    {
        var directory=Path.Combine(AppContext.BaseDirectory,"poster-disk-"+Guid.NewGuid().ToString("N"));var calls=0;
        Task<byte[]> Fetch(Uri _,CancellationToken ct){ct.ThrowIfCancellationRequested();calls++;return Task.FromResult(new byte[10]);}
        try
        {
            var url=new Uri("https://example.com/poster.jpg");
            await new PosterDiskCache(directory,Fetch,budget:20).GetAsync(url,TestContext.Current.CancellationToken);
            await new PosterDiskCache(directory,Fetch,budget:20).GetAsync(url,TestContext.Current.CancellationToken);Assert.Equal(1,calls);
            File.SetLastWriteTimeUtc(Assert.Single(Directory.GetFiles(directory)),DateTime.UtcNow.AddDays(-8));
            await new PosterDiskCache(directory,Fetch,budget:20).GetAsync(url,TestContext.Current.CancellationToken);Assert.Equal(2,calls);
            var cache=new PosterDiskCache(directory,Fetch,budget:20);
            await cache.GetAsync(new Uri("https://example.com/two"),TestContext.Current.CancellationToken);
            await cache.GetAsync(new Uri("https://example.com/three"),TestContext.Current.CancellationToken);
            Assert.True(Directory.GetFiles(directory).Sum(path=>new FileInfo(path).Length)<=20);
            Assert.DoesNotContain(Directory.GetFiles(directory),path=>path.EndsWith(".tmp"));
        }
        finally{if(Directory.Exists(directory))Directory.Delete(directory,true);}
    }

    [Fact]
    public async Task InvalidatingBadPosterForcesFreshFetchWithoutKeepingStaleBytes()
    {
        var directory=Path.Combine(AppContext.BaseDirectory,"poster-invalidate-"+Guid.NewGuid().ToString("N"));var count=0;
        try
        {
            var cache=new PosterDiskCache(directory,(_,_)=>Task.FromResult(new byte[]{(byte)++count}));var uri=new Uri("https://example.com/a");
            Assert.Equal(new byte[]{1},await cache.GetAsync(uri,TestContext.Current.CancellationToken));
            await cache.InvalidateAsync(uri,TestContext.Current.CancellationToken);
            Assert.Equal(new byte[]{2},await cache.GetAsync(uri,TestContext.Current.CancellationToken));
        }
        finally{if(Directory.Exists(directory))Directory.Delete(directory,true);}
    }

    [Fact]
    public async Task DifferentPosterDownloadsRunConcurrentlyWhileSameUrlIsCoalesced()
    {
        var directory=Path.Combine(AppContext.BaseDirectory,"poster-concurrency-"+Guid.NewGuid().ToString("N"));
        var first=new Uri("https://example.com/first");var second=Enumerable.Range(0,100).Select(index=>new Uri("https://example.com/"+index)).First(uri=>(uint)StringComparer.Ordinal.GetHashCode(uri.AbsoluteUri)%64!=(uint)StringComparer.Ordinal.GetHashCode(first.AbsoluteUri)%64);
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var calls=0;
        var cache=new PosterDiskCache(directory,async(_,ct)=>{if(Interlocked.Increment(ref calls)==2)entered.TrySetResult();await release.Task.WaitAsync(ct);return new byte[]{1};});
        try
        {
            var a=cache.GetAsync(first,TestContext.Current.CancellationToken);var duplicate=cache.GetAsync(first,TestContext.Current.CancellationToken);var b=cache.GetAsync(second,TestContext.Current.CancellationToken);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5),TestContext.Current.CancellationToken);Assert.Equal(2,calls);
            release.SetResult();await Task.WhenAll(a,duplicate,b);Assert.Equal(2,calls);
        }
        finally{release.TrySetResult();if(Directory.Exists(directory))Directory.Delete(directory,true);}
    }

    [Fact]
    public async Task OversizePosterLeavesNoPartialFile()
    {
        var directory=Path.Combine(AppContext.BaseDirectory,"poster-oversize-"+Guid.NewGuid().ToString("N"));
        try
        {
            var cache=new PosterDiskCache(directory,(_,_)=>Task.FromResult(new byte[4*1024*1024+1]));
            await Assert.ThrowsAsync<InvalidDataException>(()=>cache.GetAsync(new Uri("https://example.com/a"),TestContext.Current.CancellationToken));
            Assert.Empty(Directory.GetFiles(directory));
        }
        finally{if(Directory.Exists(directory))Directory.Delete(directory,true);}
    }
}
