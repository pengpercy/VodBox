using System.Text;
using VodBox.Core;
using VodBox.Infrastructure;
using Xunit;

namespace VodBox.Tests;

/// <summary>远程配置本地副本：冷启动先用副本出首页，后台刷新只在内容变化时重建源。</summary>
public sealed class SourceRegistryConfigCacheTests : IDisposable
{
    private readonly string _cache = Path.Combine(Path.GetTempPath(), "vodbox-config-cache-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_cache)) Directory.Delete(_cache, recursive: true);
    }

    private static LoadedConfig Remote(string key)
    {
        var json = $$"""{"sites":[{"key":"{{key}}","name":"{{key}}","api":"https://example.com/api","type":1}]}""";
        return new LoadedConfig(ConfigLoader.Parse(json)!, "https://example.com/tv", Encoding.UTF8.GetBytes(json));
    }

    [Fact]
    public async Task NoCachedCopyBeforeFirstSuccessfulLoad()
    {
        using var registry = new SourceRegistry((_, _) => Task.FromResult(Remote("a")), _cache);
        Assert.False(await registry.TryLoadCachedConfigAsync("https://example.com/tv", TestContext.Current.CancellationToken));
        Assert.Empty(registry.Sources);
    }

    [Fact]
    public async Task CachedCopyRestoresSourcesWithoutNetwork()
    {
        using (var online = new SourceRegistry((_, _) => Task.FromResult(Remote("a")), _cache))
            await online.LoadConfigAsync("https://example.com/tv", TestContext.Current.CancellationToken);

        using var offline = new SourceRegistry((_, _) => throw new HttpRequestException("offline"), _cache);
        Assert.True(await offline.TryLoadCachedConfigAsync("https://example.com/tv", TestContext.Current.CancellationToken));
        Assert.Equal("a", Assert.Single(offline.Sources).Key);
        Assert.NotNull(offline.Default());
    }

    [Fact]
    public async Task RefreshSkipsRebuildWhenRemoteIsUnchanged()
    {
        using var registry = new SourceRegistry((_, _) => Task.FromResult(Remote("a")), _cache);
        await registry.LoadConfigAsync("https://example.com/tv", TestContext.Current.CancellationToken);
        var source = registry.Default();
        var rebuilt = 0;
        registry.SourcesChanged += () => rebuilt++;

        Assert.False(await registry.RefreshConfigAsync("https://example.com/tv", TestContext.Current.CancellationToken));
        Assert.Equal(0, rebuilt);
        Assert.Same(source, registry.Default());
    }

    [Fact]
    public async Task RefreshAppliesAndCachesChangedRemote()
    {
        var key = "a";
        using var registry = new SourceRegistry((_, _) => Task.FromResult(Remote(key)), _cache);
        await registry.LoadConfigAsync("https://example.com/tv", TestContext.Current.CancellationToken);

        key = "b";
        Assert.True(await registry.RefreshConfigAsync("https://example.com/tv", TestContext.Current.CancellationToken));
        Assert.Equal("b", Assert.Single(registry.Sources).Key);

        using var next = new SourceRegistry((_, _) => throw new HttpRequestException("offline"), _cache);
        Assert.True(await next.TryLoadCachedConfigAsync("https://example.com/tv", TestContext.Current.CancellationToken));
        Assert.Equal("b", Assert.Single(next.Sources).Key);
    }
}
