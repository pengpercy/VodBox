using System.Net;
using VodBox.PluginHost.JarPlugin;
using Xunit;

namespace VodBox.Tests;

/// <summary>JAR 宿主在桌面端真正启用后暴露的两类问题：并发下载同一插件、开发机上过旧的系统 Java。</summary>
public sealed class JarPluginArtifactAndJavaTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vodbox-jar-artifact-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class SlowJarHandler : HttpMessageHandler
    {
        public int Requests;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Requests);
            await Task.Delay(200, ct);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[64 * 1024]) };
        }
    }

    /// <summary>同一配置的站点共用一个爬虫 JAR：并发搜索同时取它时只下载一次，全部成功，不留临时文件。</summary>
    [Fact]
    public async Task ConcurrentAcquiresOfOneJarShareASingleDownload()
    {
        var handler = new SlowJarHandler();
        var stores = Enumerable.Range(0, 3).Select(_ => new HttpJarPluginArtifactStore(_root, new HttpClient(handler, disposeHandler: false))).ToArray();
        var jar = new JarReference("https://example.invalid/spider.jar", "https://example.invalid/spider.jar", null);
        var paths = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => stores[i % 3].AcquireAsync(jar, TestContext.Current.CancellationToken)));
        Assert.Single(paths.Distinct());
        Assert.Equal(1, handler.Requests);
        Assert.Equal(64 * 1024, new FileInfo(paths[0]).Length);
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
        foreach (var store in stores) store.Dispose();
    }

    private string FakeJdk(string name, string version)
    {
        var home = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(home, "bin"));
        File.WriteAllText(Path.Combine(home, "release"), $"IMPLEMENTOR=\"test\"\nJAVA_VERSION=\"{version}\"\n");
        var java = Path.Combine(home, "bin", OperatingSystem.IsWindows() ? "java.exe" : "java");
        File.WriteAllText(java, "");
        return java;
    }

    [Theory]
    [InlineData("1.8.0_472", 8)]
    [InlineData("17.0.15", 17)]
    [InlineData("21.0.12.1", 21)]
    [InlineData("24", 24)]
    public void JavaVersionReadsTheJdkReleaseFile(string release, int major)
    {
        Assert.Equal(major, JarPluginHostLocator.JavaVersion(FakeJdk("jdk-" + release, release)));
    }

    [Fact]
    public void JavaVersionIsUnknownWithoutReleaseFile()
    {
        var bin = Path.Combine(_root, "bare", "bin");
        Directory.CreateDirectory(bin);
        var java = Path.Combine(bin, "java");
        File.WriteAllText(java, "");
        Assert.Null(JarPluginHostLocator.JavaVersion(java));
    }

    /// <summary>开发机常见：JAVA_HOME/PATH 上是 1.8（不认宿主的 --add-opens，宿主立即退出），另有新 JDK 可用。</summary>
    [Fact]
    public void FindJavaSkipsAJdkOlderThanTheHostTarget()
    {
        var old = FakeJdk("jdk8", "1.8.0_472");
        var modern = FakeJdk("jdk17", "17.0.15");
        var saved = (Java: Environment.GetEnvironmentVariable(JarPluginHostLocator.JavaEnvironmentVariable),
            Home: Environment.GetEnvironmentVariable("JAVA_HOME"), Path: Environment.GetEnvironmentVariable("PATH"));
        try
        {
            Environment.SetEnvironmentVariable(JarPluginHostLocator.JavaEnvironmentVariable, null);
            Environment.SetEnvironmentVariable("JAVA_HOME", Path.GetDirectoryName(Path.GetDirectoryName(old)));
            Environment.SetEnvironmentVariable("PATH", Path.GetDirectoryName(old) + Path.PathSeparator + Path.GetDirectoryName(modern));
            var found = JarPluginHostLocator.FindJava(Path.Combine(_root, "no-bundle"));
            Assert.NotNull(found);
            Assert.NotEqual(old, found);
            Assert.True(JarPluginHostLocator.JavaVersion(found!) >= JarPluginHostLocator.MinimumJavaVersion);
        }
        finally
        {
            Environment.SetEnvironmentVariable(JarPluginHostLocator.JavaEnvironmentVariable, saved.Java);
            Environment.SetEnvironmentVariable("JAVA_HOME", saved.Home);
            Environment.SetEnvironmentVariable("PATH", saved.Path);
        }
    }
}
