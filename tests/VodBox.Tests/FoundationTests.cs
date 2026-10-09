using System.Text;
using VodBox.Infrastructure;
using Xunit;

namespace VodBox.Tests;

/// <summary>S0.5 共享地基回归：文本编码回退、有界读取、原子写、平台路径。</summary>
public sealed class TextEncodingTests
{
    [Fact]
    public void Decode_Utf8WithBom_StripsBom()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("中文")).ToArray();
        Assert.Equal("中文", TextEncoding.Decode(bytes));
    }

    [Fact]
    public void Decode_Utf16LeBom()
    {
        var bytes = new byte[] { 0xFF, 0xFE }.Concat(Encoding.Unicode.GetBytes("测试")).ToArray();
        Assert.Equal("测试", TextEncoding.Decode(bytes));
    }

    [Fact]
    public void Decode_Utf16BeBom()
    {
        var bytes = new byte[] { 0xFE, 0xFF }.Concat(Encoding.BigEndianUnicode.GetBytes("测试")).ToArray();
        Assert.Equal("测试", TextEncoding.Decode(bytes));
    }

    [Fact]
    public void Decode_PlainUtf8_NoBom() =>
        Assert.Equal("苹果CMS", TextEncoding.Decode(Encoding.UTF8.GetBytes("苹果CMS")));

    /// <summary>
    /// 关键回归：GB18030 回退必须在所有平台生效。
    /// 旧 DefaultHttp.Decode 仅在 Windows 回退，macOS/Linux 上中文站点乱码。
    /// </summary>
    [Fact]
    public void Decode_Gb18030_WorksOnEveryPlatform()
    {
        TextEncoding.EnsureRegistered();
        var gb = Encoding.GetEncoding("GB18030");
        var bytes = gb.GetBytes("影视资源采集");
        // 确认这串字节不是合法 UTF-8（否则测不到回退分支）
        var strict = new UTF8Encoding(false, true);
        Assert.Throws<DecoderFallbackException>(() => strict.GetString(bytes));
        Assert.Equal("影视资源采集", TextEncoding.Decode(bytes));
    }

    /// <summary>
    /// DefaultHttp.Decode 必须与 TextEncoding.Decode 行为一致（委托而非重复实现）。
    /// 注意：GB18030 由 CodePagesEncodingProvider 提供，未注册时 GetEncoding 会抛
    /// ArgumentException——这正是本用例要覆盖的前提（TextEncoding 静态构造负责注册）。
    /// </summary>
    [Fact]
    public void DefaultHttpDecode_DelegatesToTextEncoding()
    {
        TextEncoding.EnsureRegistered();
        var gb = Encoding.GetEncoding("GB18030").GetBytes("直播源");
        Assert.Equal(TextEncoding.Decode(gb), DefaultHttp.Decode(gb));
    }

    /// <summary>
    /// GB18030 不可用是未注册 CodePagesEncodingProvider 的症状，不是平台差异。
    /// 锁定这一点：若将来有人移除 System.Text.Encoding.CodePages 包引用，此用例会失败。
    /// </summary>
    [Fact]
    public void CodePagesProvider_IsAvailableAfterRegistration()
    {
        TextEncoding.EnsureRegistered();
        Assert.Equal(54936, Encoding.GetEncoding("GB18030").CodePage);
    }
}

public sealed class BoundedContentTests
{
    [Fact]
    public async Task ReadAsync_UnderLimit_ReturnsAll()
    {
        var data = new byte[1024];
        Random.Shared.NextBytes(data);
        var result = await BoundedContent.ReadAsync(new MemoryStream(data), 4096, token: TestContext.Current.CancellationToken);
        Assert.Equal(data, result);
    }

    [Fact]
    public async Task ReadAsync_OverLimit_Throws()
    {
        var data = new byte[10 * 1024 * 1024];
        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => BoundedContent.ReadAsync(new MemoryStream(data), 1024 * 1024, token: TestContext.Current.CancellationToken));
        Assert.Contains("MiB", error.Message);
    }

    [Fact]
    public async Task ReadAsync_RespectsCancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => BoundedContent.ReadAsync(new MemoryStream(new byte[64]), 1024, cts.Token));
    }
}

public sealed class AtomicFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vodbox-atomic-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task WriteAsync_CreatesParentDirectories()
    {
        var path = Path.Combine(_dir, "nested", "deep", "file.json");
        await AtomicFile.WriteAsync(path, "{\"ok\":true}", token: TestContext.Current.CancellationToken);
        Assert.Equal("{\"ok\":true}", await File.ReadAllTextAsync(path, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WriteAsync_OverwritesExisting()
    {
        var path = Path.Combine(_dir, "file.txt");
        await AtomicFile.WriteAsync(path, "first", token: TestContext.Current.CancellationToken);
        await AtomicFile.WriteAsync(path, "second", token: TestContext.Current.CancellationToken);
        Assert.Equal("second", await File.ReadAllTextAsync(path, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WriteAsync_LeavesNoTempFileBehind()
    {
        var path = Path.Combine(_dir, "clean.txt");
        await AtomicFile.WriteAsync(path, "data", token: TestContext.Current.CancellationToken);
        Assert.Equal(["clean.txt"], Directory.GetFiles(_dir).Select(file => Path.GetFileName(file)!));
    }

    [Fact]
    public async Task WriteBytesAsync_RoundTrips()
    {
        var path = Path.Combine(_dir, "blob.bin");
        byte[] data = [0xFF, 0xD8, 0xFF, 0x00, 0x01];
        await AtomicFile.WriteBytesAsync(path, data, token: TestContext.Current.CancellationToken);
        Assert.Equal(data, await File.ReadAllBytesAsync(path, cancellationToken: TestContext.Current.CancellationToken));
    }

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }
}

public sealed class AppPathsTests
{
    [Fact]
    public void DataDirectory_IsAbsoluteAndVodBoxScoped()
    {
        var path = AppPaths.DataDirectory;
        Assert.True(Path.IsPathRooted(path), "数据目录必须是绝对路径");
        Assert.EndsWith("VodBox", path);
    }

    [Fact]
    public void CacheAndConfiguration_AreAbsolute()
    {
        Assert.True(Path.IsPathRooted(AppPaths.CacheDirectory));
        Assert.True(Path.IsPathRooted(AppPaths.ConfigurationDirectory));
    }

    /// <summary>macOS 必须落在 ~/Library 而非 ~/.vodbox（旧硬编码行为）。</summary>
    [Fact]
    public void DataDirectory_FollowsPlatformConvention()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var data = AppPaths.DataDirectory;
        Assert.StartsWith(home, data);
        if (OperatingSystem.IsMacOS())
            Assert.Contains(Path.Combine("Library", "Application Support"), data);
        else if (OperatingSystem.IsLinux())
            Assert.DoesNotContain("Library", data);
    }
}
