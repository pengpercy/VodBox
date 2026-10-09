using VodBox.Core;
using VodBox.Infrastructure;
using Xunit;

namespace VodBox.Tests;

public sealed class SourceRegistryRaceTests
{
    [Fact]
    public async Task UnsupportedAndDuplicateSourcesHaveVisibleImportWarnings()
    {
        var config=new TvBoxConfig{Sites=[
            new TvBoxSite{Key="unsupported",Name="未知Java",Api="csp_NoSuchPlugin",Type=3},
            new TvBoxSite{Key="same",Name="首项",Api="https://example.com/a",Type=1},
            new TvBoxSite{Key="same",Name="重复项",Api="https://example.com/b",Type=1},
        ]};
        using var registry=new SourceRegistry((_,_)=>Task.FromResult(config));
        await registry.LoadConfigAsync("test",TestContext.Current.CancellationToken);
        Assert.Equal("首项",Assert.Single(registry.Sources).Name);
        Assert.Contains(registry.ImportWarnings,warning=>warning.Contains("未知Java"));
        Assert.Contains(registry.ImportWarnings,warning=>warning.Contains("重复"));
    }

    [Fact]
    public async Task OlderConfigurationCannotOverwriteNewerSelection()
    {
        var old=new TaskCompletionSource<TvBoxConfig>(TaskCreationOptions.RunContinuationsAsynchronously);
        static TvBoxConfig Config(string key)=>new(){Sites=[new TvBoxSite {Key=key,Name=key,Api="https://example.com/api",Type=1}]};
        using var registry=new SourceRegistry((url,_)=>url=="old"?old.Task:Task.FromResult(Config("new")));
        var first=registry.LoadConfigAsync("old",TestContext.Current.CancellationToken);
        await registry.LoadConfigAsync("new",TestContext.Current.CancellationToken);
        old.SetResult(Config("old"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>first);
        Assert.Equal("new",Assert.Single(registry.Sources).Key);
    }
}
