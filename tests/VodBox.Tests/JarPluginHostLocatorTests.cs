using VodBox.PluginHost.JarPlugin;
using Xunit;

namespace VodBox.Tests;

[CollectionDefinition("Jar host locator environment", DisableParallelization = true)]
public sealed class JarHostLocatorEnvironmentCollection { }

[Collection("Jar host locator environment")]
public sealed class JarPluginHostLocatorTests
{
    [Theory]
    [InlineData("JavaHost", "bin")]
    [InlineData("JavaHost", "Contents/Home/bin")]
    [InlineData("../Resources/JavaHost", "bin")]
    [InlineData("../Resources/JavaHost", "Contents/Home/bin")]
    public void FindsBundledHostAndJava(string hostRelativePath, string javaRelativePath)
    {
        var root = Path.Combine(Path.GetTempPath(), "vodbox-locator-" + Guid.NewGuid().ToString("N"));
        var baseDirectory = Path.Combine(root, "Contents", "MacOS");
        var hostDirectory = Path.GetFullPath(Path.Combine(baseDirectory, hostRelativePath.Replace('/', Path.DirectorySeparatorChar)));
        var host = Path.Combine(hostDirectory, "host.jar");
        var java = Path.Combine(hostDirectory, "jre", javaRelativePath.Replace('/', Path.DirectorySeparatorChar),
            OperatingSystem.IsWindows() ? "java.exe" : "java");
        var oldHost = Environment.GetEnvironmentVariable(JarPluginHostLocator.HostJarEnvironmentVariable);
        var oldJava = Environment.GetEnvironmentVariable(JarPluginHostLocator.JavaEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(JarPluginHostLocator.HostJarEnvironmentVariable, null);
            Environment.SetEnvironmentVariable(JarPluginHostLocator.JavaEnvironmentVariable, null);
            Directory.CreateDirectory(baseDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(java)!);
            File.WriteAllText(host, "host fixture");
            File.WriteAllText(java, "java fixture");
            Assert.Equal(host, JarPluginHostLocator.FindHostJar(baseDirectory));
            Assert.Equal(java, JarPluginHostLocator.FindJava(baseDirectory));

            // Explicit overrides retain precedence over either packaged layout.
            var configuredHost = Path.Combine(root, "override.jar");
            var configuredJava = Path.Combine(root, "override-java");
            File.WriteAllText(configuredHost, "override host");
            File.WriteAllText(configuredJava, "override java");
            Environment.SetEnvironmentVariable(JarPluginHostLocator.HostJarEnvironmentVariable, configuredHost);
            Environment.SetEnvironmentVariable(JarPluginHostLocator.JavaEnvironmentVariable, configuredJava);
            Assert.Equal(configuredHost, JarPluginHostLocator.FindHostJar(baseDirectory));
            Assert.Equal(configuredJava, JarPluginHostLocator.FindJava(baseDirectory));
        }
        finally
        {
            Environment.SetEnvironmentVariable(JarPluginHostLocator.HostJarEnvironmentVariable, oldHost);
            Environment.SetEnvironmentVariable(JarPluginHostLocator.JavaEnvironmentVariable, oldJava);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
