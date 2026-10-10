using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VodBox.Desktop.Services;
using Xunit;

namespace VodBox.Tests;

public class AppUpdaterTests
{
    private static JsonElement Release(string version, string name, string digest, string? url = null) =>
        JsonDocument.Parse($$"""{"draft":false,"prerelease":false,"tag_name":"{{version}}","html_url":"https://github.com/pengpercy/VodBox/releases/tag/{{version}}","assets":[{"name":"{{name.Replace("\\", "\\\\")}}","digest":"{{digest}}","browser_download_url":"{{url ?? $"https://github.com/pengpercy/VodBox/releases/download/{version}/{name}"}}"}]}""").RootElement.Clone();

    [Fact]
    public void Select_RejectsDowngradeMaliciousNamesAndUntrustedDigest()
    {
        var hash = "sha256:" + new string('a', 64);
        Assert.Null(AppUpdater.Select(Release("v1.2.3", "VodBox_1.2.3.win-x64.zip", hash), "1.2.3", "win-x64"));
        Assert.Null(AppUpdater.Select(Release("v1.2.2", "VodBox_1.2.2.win-x64.zip", hash), "1.2.3", "win-x64"));
        Assert.Null(AppUpdater.Select(Release("v1.2.3", "../VodBox_1.2.3.win-x64.zip", hash), "1.0.0", "win-x64"));
        Assert.Null(AppUpdater.Select(Release("v1.2.3", "VodBox_1.2.3.win-arm64.zip", hash), "1.0.0", "win-x64"));
        Assert.Null(AppUpdater.Select(Release("v1.2.3", "VodBox_1.2.3.win-x64.zip", "sha256:bad"), "1.0.0", "win-x64"));
        Assert.Null(AppUpdater.Select(Release("v1.2.3", "VodBox_1.2.3.win-x64.zip", hash, "https://evil.example/asset.zip"), "1.0.0", "win-x64"));
        Assert.NotNull(AppUpdater.Select(Release("v1.2.3", "VodBox_1.2.3.win-x64.zip", hash), "1.0.0", "win-x64"));
        var linux = AppUpdater.Select(Release("v1.2.3", "vodbox_1.2.3_linux-x64.deb", hash), "1.0.0", "linux-x64", "deb");
        Assert.Equal("vodbox_1.2.3_linux-x64.deb", linux?.Name);
        Assert.Equal("", AppUpdater.Select(Release("v1.2.3", "vodbox_1.2.3_linux-x64.deb", hash), "1.0.0", "linux-arm64", "deb")?.Name);
        Assert.Equal("https://github.com/pengpercy/VodBox/releases/tag/v1.2.3",
            AppUpdater.Select(Release("v1.2.3", "vodbox_1.2.3_linux-x64.deb", hash), "1.0.0", "linux-x64")?.Url.AbsoluteUri);
    }

    [Theory]
    [InlineData("https://github.com/pengpercy/VodBox/releases/download/v1.2.4/VodBox_1.2.3.win-x64.zip")]
    [InlineData("https://github.com/pengpercy/VodBox/releases/download/v1.2.3/VodBox_1.2.3.win-arm64.zip")]
    [InlineData("https://github.com/pengpercy/VodBox/releases/download/v1.2.3/VodBox_1.2.3.win-x64.zip/extra")]
    [InlineData("https://user@github.com/pengpercy/VodBox/releases/download/v1.2.3/VodBox_1.2.3.win-x64.zip")]
    [InlineData("https://github.com/pengpercy/VodBox/releases/download/v1.2.3/VodBox_1.2.3.win-x64.zip#fragment")]
    [InlineData("https://@github.com/pengpercy/VodBox/releases/download/v1.2.3/VodBox_1.2.3.win-x64.zip")]
    [InlineData("https://github.com/pengpercy/VodBox/releases/download/v1.2.3/VodBox_1.2.3.win-x64.zip#")]
    [InlineData("https://github.com/pengpercy/VodBox/releases/download/v1.2.3/VodBox_1.2.3.win-x64.zip?asset=other")]
    public void Select_RejectsWrongAssetUrl(string url)
    {
        Assert.Null(AppUpdater.Select(Release("v1.2.3", "VodBox_1.2.3.win-x64.zip", "sha256:" + new string('a', 64), url), "1.0.0", "win-x64"));
    }

    [Fact]
    public void Select_LinuxPackagesAndFallback()
    {
        var hash = "sha256:" + new string('a', 64);
        Assert.Equal("rpm", AppUpdater.LinuxPackage("ID=fedora\nID_LIKE=\"rhel fedora\"\n"));
        Assert.Equal("deb", AppUpdater.LinuxPackage("ID=pop\nID_LIKE=\"ubuntu debian\"\n"));
        Assert.Null(AppUpdater.LinuxPackage("ID=arch\n"));
        Assert.Equal("vodbox_1.2.3_linux-arm64.rpm", AppUpdater.Select(
            Release("v1.2.3", "vodbox_1.2.3_linux-arm64.rpm", hash), "1.0.0", "linux-arm64", "rpm")?.Name);
        Assert.Equal("", AppUpdater.Select(Release("v1.2.3", "vodbox_1.2.3_linux-arm64.rpm", hash), "1.0.0", "linux-arm64", "deb")?.Name);
    }

    [Fact]
    public async Task Download_CacheIsHashedAndCancellationLeavesNoPartial()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vodbox-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "update.zip");
            await File.WriteAllTextAsync(path, "cached", TestContext.Current.CancellationToken);
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("cached"))).ToLowerInvariant();
            var offer = new UpdateOffer("1.2.3", "unused", new Uri("https://example.invalid/synthetic.zip"), hash, "");
            Assert.Equal(path, await AppUpdater.DownloadAsync(offer, dir, TestContext.Current.CancellationToken));
            Assert.True(await AppUpdater.MatchesAsync(path, hash, TestContext.Current.CancellationToken));
            Assert.False(await AppUpdater.MatchesAsync(path, new string('0', 64), TestContext.Current.CancellationToken));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AppUpdater.DownloadAsync(offer with { Sha256 = new string('0', 64) }, dir, new CancellationToken(true)));
            Assert.False(File.Exists(Path.Combine(dir, "download.partial")));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("/tmp/escape")]
    [InlineData("C:\\escape")]
    [InlineData("folder/../../escape")]
    [InlineData("folder/file.txt:stream")]
    [InlineData("VodBox.Desktop.exe:payload")]
    [InlineData("folder:stream/VodBox.Desktop.exe")]
    public void ExtractSafe_RejectsTraversal(string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), "vodbox-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var archive = Path.Combine(dir, "package.zip");
            using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(zip.CreateEntry(name).Open())) writer.Write("attack");
            Assert.Throws<InvalidDataException>(() => AppUpdater.ExtractSafe(archive, Path.Combine(dir, "stage"), false));
            Assert.False(File.Exists(Path.Combine(dir, "escape")));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ExtractSafe_RejectsDuplicateAndEscapingSymlinks()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vodbox-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var archive = Path.Combine(dir, "duplicate.zip");
            using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
            {
                zip.CreateEntry("VodBox.Desktop.exe");
                zip.CreateEntry("VodBox.Desktop.exe");
            }
            Assert.Throws<InvalidDataException>(() => AppUpdater.ExtractSafe(archive, Path.Combine(dir, "duplicate"), false));
            archive = Path.Combine(dir, "link.zip");
            using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
            {
                zip.CreateEntry("VodBox.app/Contents/MacOS/VodBox.Desktop");
                var link = zip.CreateEntry("VodBox.app/Contents/MacOS/evil");
                link.ExternalAttributes = 0xA1FF << 16;
                using var writer = new StreamWriter(link.Open());
                writer.Write("../../../../outside");
            }
            Assert.Throws<InvalidDataException>(() => AppUpdater.ExtractSafe(archive, Path.Combine(dir, "link"), true));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ExtractSafe_RejectsMissingExecutable()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vodbox-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var archive = Path.Combine(dir, "package.zip");
            using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create)) zip.CreateEntry("other.exe");
            Assert.Throws<InvalidDataException>(() => AppUpdater.ExtractSafe(archive, Path.Combine(dir, "stage"), false));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DetachedScript_ReplacesOrRollsBackOnlySyntheticBundle(bool verifySucceeds)
    {
        if (!OperatingSystem.IsMacOS()) return;
        var root = Path.Combine(Path.GetTempPath(), "vodbox-updater-script-" + Guid.NewGuid().ToString("N"));
        var target = Path.Combine(root, "VodBox.app");
        var stage = Path.Combine(root, "stage");
        var incoming = Path.Combine(stage, "VodBox.app");
        var backup = Path.Combine(root, "backup");
        var cache = Path.Combine(root, "cache");
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(incoming);
        Directory.CreateDirectory(cache);
        try
        {
            File.WriteAllText(Path.Combine(target, "marker"), "old");
            File.WriteAllText(Path.Combine(incoming, "marker"), "new");
            var script = AppUpdater.BuildMacScript(target, incoming, stage, backup, cache, 999999999)
                .Replace("/usr/bin/codesign --verify --deep --strict", verifySucceeds ? "/usr/bin/true" : "/usr/bin/false")
                .Replace("/usr/bin/open", "/usr/bin/true");
            var scriptFile = Path.Combine(root, "synthetic.sh");
            await File.WriteAllTextAsync(scriptFile, script, TestContext.Current.CancellationToken);
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("/bin/sh")
            { ArgumentList = { scriptFile }, UseShellExecute = false })!;
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.Equal(0, process.ExitCode);
            Assert.Equal(verifySucceeds ? "new" : "old", File.ReadAllText(Path.Combine(target, "marker")));
            Assert.False(Directory.Exists(backup));
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public void ExtractSafe_UsesOnlyIsolatedSyntheticTree()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vodbox-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var archive = Path.Combine(dir, "package.zip");
            using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(zip.CreateEntry("VodBox.Desktop.exe").Open())) writer.Write("synthetic executable");
            var stage = Path.Combine(dir, "stage");
            AppUpdater.ExtractSafe(archive, stage, false);
            Assert.Equal("synthetic executable", File.ReadAllText(Path.Combine(stage, "VodBox.Desktop.exe")));
            Assert.Throws<DirectoryNotFoundException>(() => AppUpdater.ValidateInstallDirectory(Path.Combine(dir, "missing", "VodBox.app")));
        }
        finally { Directory.Delete(dir, true); }
    }
}
