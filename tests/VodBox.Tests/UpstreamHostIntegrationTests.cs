using VodBox.PluginHost.JarPlugin;
using Xunit;

namespace VodBox.Tests;

public sealed class UpstreamHostIntegrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "vodbox-upstream-" + Guid.NewGuid().ToString("N"));
    private JarPluginHostSettings Settings() => new()
    {
        JavaExecutable = "java", HostJarPath = Path.Combine(_directory, "host.jar"), WorkRoot = _directory,
    };

    public UpstreamHostIntegrationTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void ProvenanceMarkerSelectsUpstreamAdapterAndOnlyItsDependencies()
    {
        File.WriteAllText(Path.Combine(_directory, "upstream-runtime.json"), "{}");
        Directory.CreateDirectory(Path.Combine(_directory, "deps"));
        Directory.CreateDirectory(Path.Combine(_directory, "dex-tools"));
        var settings = Settings();
        Assert.True(settings.UsesUpstreamRuntime);
        Assert.Equal("vodbox.bridge.ProcessAdapter", settings.HostEntryPoint);
        Assert.Contains(Path.Combine(_directory, "deps", "*"), settings.BuildClasspath());
        Assert.DoesNotContain("dex-tools", settings.BuildClasspath());
    }

    [Fact]
    public void UpstreamReceivesOriginalArchiveIncludingNativeAssets()
    {
        File.WriteAllText(Path.Combine(_directory, "upstream-runtime.json"), "{}");
        IJarPluginArtifactStore original = new OriginalStore();
        Assert.Same(original, JarPluginArtifactStores.Create(Settings(), original));
    }

    [Fact]
    public void HostWithoutMarkerKeepsExplicitLegacyEntryPoint()
    {
        Assert.False(Settings().UsesUpstreamRuntime);
        Assert.Equal(JarPluginHostSettings.HostMainClass, Settings().HostEntryPoint);
    }

    private sealed class OriginalStore : IJarPluginArtifactStore
    {
        public Task<string> AcquireAsync(JarReference jar, CancellationToken ct) => throw new NotSupportedException();
    }
    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
