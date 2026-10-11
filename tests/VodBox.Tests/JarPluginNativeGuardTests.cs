using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using VodBox.PluginHost.JarPlugin;
using Xunit;

namespace VodBox.Tests;

/// <summary>
/// P2-10「ARM64 native guard 安全模拟」的离线契约测试：归档扫描/抽取、ARM64 小端 ELF 校验、
/// 大小与 md5 边界、可插拔后端与「默认明确不可用」、错误分类接线、进程资源上限参数，
/// 以及「Android .so 绝不 dlopen」的源码级断言。全部不启 JVM、不触网、不执行任何原生代码。
/// </summary>
public sealed class JarPluginNativeGuardTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vodbox-native-guard-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (Exception) { /* 清理失败不影响判定。 */ }
    }

    // ---------- 工具 ----------

    private string PathOf(string name)
    {
        Directory.CreateDirectory(_root);
        return Path.Combine(_root, name);
    }

    /// <summary>构造一个合法的小端 ARM64 ELF（仅头 20 字节有意义，其余补零）。</summary>
    private static byte[] Arm64Elf(int size = 64)
    {
        var bytes = new byte[Math.Max(size, 20)];
        bytes[0] = 0x7F; bytes[1] = (byte)'E'; bytes[2] = (byte)'L'; bytes[3] = (byte)'F';
        bytes[4] = JarPluginArm64Elf.Class64;
        bytes[5] = JarPluginArm64Elf.DataLittleEndian;
        bytes[18] = (byte)JarPluginArm64Elf.MachineAArch64;
        bytes[19] = 0;
        return bytes;
    }

    private static void WriteZip(string path, params (string Name, byte[] Bytes)[] entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var (name, bytes) in entries)
        {
            var entry = zip.CreateEntry(name);
            using var output = entry.Open();
            output.Write(bytes, 0, bytes.Length);
        }
    }

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    private static byte[] Md5File(byte[] library) => Utf8("arm64-v8a=" + Convert.ToHexString(MD5.HashData(library)).ToLowerInvariant() + "\n");

    // ---------- ARM64 小端 ELF 校验 ----------

    [Fact]
    public void ValidArm64LittleEndianElfIsAccepted()
    {
        Assert.True(JarPluginArm64Elf.TryValidate(Arm64Elf(), out var reason));
        Assert.Null(reason);
    }

    [Theory]
    [InlineData(0, 0x00)] // 破坏 magic 第 1 字节
    [InlineData(4, 1)]    // EI_CLASS = 32 位
    [InlineData(5, 2)]    // EI_DATA = 大端
    [InlineData(18, 0x3E)] // e_machine = x86-64
    [InlineData(19, 1)]    // e_machine 高字节非零
    public void WrongElfHeaderFieldIsRejected(int offset, byte value)
    {
        var bytes = Arm64Elf();
        bytes[offset] = (byte)value;
        Assert.False(JarPluginArm64Elf.TryValidate(bytes, out var reason));
        Assert.False(string.IsNullOrEmpty(reason));
    }

    [Fact]
    public void ShortInputIsRejected()
    {
        Assert.False(JarPluginArm64Elf.TryValidate(new byte[19], out var reason));
        Assert.Contains("过短", reason);
    }

    // ---------- 归档扫描 ----------

    [Fact]
    public void ScanDetectsGuardConventionAndAndroidAbiEntries()
    {
        var archive = PathOf("scan.jar");
        WriteZip(archive,
            ("assets/fishguard-v8.so", Arm64Elf()),
            ("assets/other_v8.so", Arm64Elf()),
            ("assets/plain.so", Arm64Elf()),          // 非 guard 约定：忽略
            ("assets/sub/nested-v8.so", Arm64Elf()),  // 子目录：忽略（约定是单层）
            ("lib/arm64-v8a/libfoo.so", Arm64Elf()),
            ("lib/armeabi-v7a/libfoo.so", Arm64Elf()), // 非 arm64 目录：忽略
            ("classes.dex", [1, 2, 3]));

        var scan = JarPluginNativeLibraryScanner.Scan(archive);
        Assert.True(scan.Readable);
        Assert.True(scan.RequiresGuardEmulation);
        var names = scan.Libraries.Select(library => library.EntryName).ToList();
        Assert.Contains("assets/fishguard-v8.so", names);
        Assert.Contains("assets/other_v8.so", names);
        Assert.Contains("lib/arm64-v8a/libfoo.so", names);
        Assert.DoesNotContain("assets/plain.so", names);
        Assert.DoesNotContain("assets/sub/nested-v8.so", names);
        Assert.DoesNotContain("lib/armeabi-v7a/libfoo.so", names);
        Assert.Equal(JarPluginNativeLibraryKind.Guard, scan.Libraries.Single(library => library.FileName == "fishguard-v8.so").Kind);
        Assert.Equal(JarPluginNativeLibraryKind.AndroidAbi, scan.Libraries.Single(library => library.FileName == "libfoo.so").Kind);
    }

    [Fact]
    public void ScanOnNonZipIsNotReadable()
    {
        var path = PathOf("broken.jar");
        File.WriteAllBytes(path, [0, 1, 2, 3]);
        var scan = JarPluginNativeLibraryScanner.Scan(path);
        Assert.False(scan.Readable);
        Assert.False(scan.HasLibraries);
    }

    [Theory]
    [InlineData("ftyguard_v8.so", true)]
    [InlineData("ftyguard-v8.so", true)]
    [InlineData("fishguard-v8.so", false)]
    public void FtyGuardNamesAreRecognised(string fileName, bool expected) =>
        Assert.Equal(expected, JarPluginNativeLibraryScanner.IsFtyGuard(fileName));

    [Fact]
    public void ResolvePrefersAndroidAbiThenAssetVariants()
    {
        var archive = PathOf("resolve.jar");
        WriteZip(archive,
            ("lib/arm64-v8a/libguard.so", Arm64Elf()),
            ("assets/libguard.so", Arm64Elf()),
            ("assets/guard-v8.so", Arm64Elf()));
        Assert.Equal("lib/arm64-v8a/libguard.so", JarPluginNativeLibraryScanner.Resolve(archive, "guard"));

        var assetOnly = PathOf("resolve2.jar");
        WriteZip(assetOnly, ("assets/guard_v8.so", Arm64Elf()));
        Assert.Equal("assets/guard_v8.so", JarPluginNativeLibraryScanner.Resolve(assetOnly, "guard-v8"));

        Assert.Null(JarPluginNativeLibraryScanner.Resolve(assetOnly, "missing"));
    }

    [Fact]
    public void ResolveRejectsPathUnsafeNames()
    {
        var archive = PathOf("resolve3.jar");
        WriteZip(archive, ("assets/guard-v8.so", Arm64Elf()));
        var error = Assert.Throws<JarPluginException>(() => JarPluginNativeLibraryScanner.Resolve(archive, "../evil"));
        Assert.Equal(JarPluginErrors.UnsupportedNativeLibrary, error.Failure.Code);
    }

    // ---------- 抽取 + 校验边界 ----------

    [Fact]
    public void ExtractWritesValidatedLibraryToStaging()
    {
        var archive = PathOf("extract.jar");
        var library = Arm64Elf(128);
        WriteZip(archive, ("assets/fishguard-v8.so", library));
        var staging = Path.Combine(_root, "staging");

        var extracted = JarPluginNativeLibraryScanner.Extract(JarPluginNativeLibraryScanner.Scan(archive), archive, staging);

        var entry = Assert.Single(extracted);
        Assert.Equal("fishguard-v8.so", entry.FileName);
        var written = Path.Combine(staging, "fishguard-v8.so");
        Assert.True(File.Exists(written));
        Assert.Equal(library, File.ReadAllBytes(written));
    }

    [Fact]
    public void ExtractRejectsOversizedLibraryAndWritesNothing()
    {
        var archive = PathOf("big.jar");
        WriteZip(archive, ("assets/fishguard-v8.so", Arm64Elf(200)));
        var staging = Path.Combine(_root, "big-staging");

        // 用很小的上限触发边界（等价于默认 16 MiB 的判定路径，但不必真的造 16 MiB 文件）。
        var error = Assert.Throws<JarPluginException>(() =>
            JarPluginNativeLibraryScanner.Extract(JarPluginNativeLibraryScanner.Scan(archive), archive, staging, maxLibraryBytes: 64));
        Assert.Equal(JarPluginErrors.UnsupportedNativeLibrary, error.Failure.Code);
        Assert.Contains("MiB", error.Failure.Message);
        Assert.False(Directory.Exists(staging) && Directory.EnumerateFileSystemEntries(staging).Any());
    }

    [Fact]
    public void ExtractRejectsNonArm64Library()
    {
        var archive = PathOf("bad-elf.jar");
        var notElf = new byte[64];
        notElf[0] = 0x4D; notElf[1] = 0x5A; // MZ，而非 ELF
        WriteZip(archive, ("assets/fishguard-v8.so", notElf));
        var error = Assert.Throws<JarPluginException>(() =>
            JarPluginNativeLibraryScanner.Extract(JarPluginNativeLibraryScanner.Scan(archive), archive, Path.Combine(_root, "bad-staging")));
        Assert.Equal(JarPluginErrors.UnsupportedNativeLibrary, error.Failure.Code);
        Assert.Contains("fishguard-v8.so", error.Failure.Message);
    }

    [Fact]
    public void ExtractAcceptsMatchingDeclaredMd5()
    {
        var archive = PathOf("md5-ok.jar");
        var library = Arm64Elf(96);
        WriteZip(archive,
            ("assets/fishguard-v8.so", library),
            ("assets/fishguard.md5", Md5File(library)));
        var scan = JarPluginNativeLibraryScanner.Scan(archive);
        var entry = Assert.Single(scan.Libraries);
        Assert.True(entry.HasDeclaredMd5);

        var extracted = JarPluginNativeLibraryScanner.Extract(scan, archive, Path.Combine(_root, "md5-staging"));
        Assert.Single(extracted);
    }

    [Fact]
    public void ExtractRejectsMismatchedDeclaredMd5()
    {
        var archive = PathOf("md5-bad.jar");
        WriteZip(archive,
            ("assets/fishguard-v8.so", Arm64Elf(96)),
            ("assets/fishguard.md5", Utf8("arm64-v8a=00000000000000000000000000000000\n")));
        var error = Assert.Throws<JarPluginException>(() =>
            JarPluginNativeLibraryScanner.Extract(JarPluginNativeLibraryScanner.Scan(archive), archive, Path.Combine(_root, "md5-bad-staging")));
        Assert.Equal(JarPluginErrors.UnsupportedNativeLibrary, error.Failure.Code);
        Assert.Contains("md5", error.Failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LibrarySizeCeilingIsSixteenMebibytes() =>
        Assert.Equal(16L * 1024 * 1024, JarPluginNativeLibraryScanner.MaxLibraryBytes);

    // ---------- 可插拔后端与「默认绝不伪报成功」 ----------

    [Fact]
    public async Task DefaultBackendIsExplicitlyUnavailable()
    {
        var backend = JarPluginNativeGuardBackends.Discover();
        Assert.False(backend.IsAvailable);
        var outcome = await backend.EmulateAsync(
            new JarPluginNativeGuardRequest("a.jar", "staging", [GuardEntry()], new JarPluginNativeGuardLimits()),
            TestContext.Current.CancellationToken);
        Assert.False(outcome.Emulated); // 绝不伪报已模拟
        Assert.Equal("fishguard-v8.so", outcome.UnsupportedLibrary);
        Assert.False(string.IsNullOrEmpty(outcome.Detail));
    }

    [Fact]
    public void NativeBackendCapabilityMatchesTheBundledBackendAssetCoverage()
    {
        // The upstream pom pins unidbg-unicorn2:0.9.8, whose jar ships no natives/windows_arm64.
        // The capability surface must reflect exactly that, and the diagnostic must name the
        // backend so a failure is actionable rather than a bare "host unavailable".
        Assert.False(NativeBackendCapability.IsSupported(Architecture.Arm64, OSPlatform.Windows));
        Assert.True(NativeBackendCapability.IsSupported(Architecture.X64, OSPlatform.Windows));
        Assert.True(NativeBackendCapability.IsSupported(Architecture.Arm64, OSPlatform.Linux));
        Assert.True(NativeBackendCapability.IsSupported(Architecture.X64, OSPlatform.OSX));
        Assert.True(NativeBackendCapability.IsSupported(Architecture.Arm64, OSPlatform.OSX));
        var reason = NativeBackendCapability.UnsupportedReason("VODBOX_UNIDBG_CLASSPATH=/x");
        Assert.Contains("unidbg-unicorn2:0.9.8", reason);
        Assert.Contains("unsupported_native_library", reason);
        Assert.Contains("windows_arm64", reason);
    }

    [Fact]
    public async Task ConfiguredUnidbgOnAnUnsupportedPlatformStillRefusesToDefer()
    {
        // A configured unidbg classpath is not evidence the backend has a native library on this
        // platform. On win-arm64 the guard must stay Unsupported, never Deferred (which would
        // hand the archive to a host that cannot load it and report success).
        var previous = Environment.GetEnvironmentVariable(JarPluginNativeGuardBackends.UnidbgClasspathEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(JarPluginNativeGuardBackends.UnidbgClasspathEnvironmentVariable, "/opt/unidbg");
            var archive = PathOf("configured.jar");
            WriteZip(archive, ("assets/fishguard-v8.so", Arm64Elf()));
            var guard = new JarPluginNativeGuard(JarPluginNativeGuardBackends.Discover());
            var decision = await guard.PrepareAsync(archive, Path.Combine(_root, "configured-staging"), TestContext.Current.CancellationToken);
            // On a supported host the same configuration defers; on win-arm64 it must not.
            var expected = NativeBackendCapability.IsSupported()
                ? JarPluginNativeGuardStatus.Deferred
                : JarPluginNativeGuardStatus.Unsupported;
            Assert.Equal(expected, decision.Status);
            if (expected == JarPluginNativeGuardStatus.Unsupported)
                Assert.Equal(JarPluginErrors.UnsupportedNativeLibrary, JarPluginNativeGuard.ToException(decision).Failure.Code);
        }
        finally
        {
            Environment.SetEnvironmentVariable(JarPluginNativeGuardBackends.UnidbgClasspathEnvironmentVariable, previous);
        }
    }

    [Fact]
    public async Task GuardWithDefaultBackendIsUnsupportedAndNamesTheLibrary()
    {
        var archive = PathOf("guard.jar");
        WriteZip(archive, ("assets/fishguard-v8.so", Arm64Elf()));
        var guard = new JarPluginNativeGuard(JarPluginNativeGuardBackends.Discover());

        var decision = await guard.PrepareAsync(archive, Path.Combine(_root, "guard-staging"), TestContext.Current.CancellationToken);

        Assert.Equal(JarPluginNativeGuardStatus.Unsupported, decision.Status);
        Assert.True(decision.RequiresGuardEmulation);
        Assert.Equal("fishguard-v8.so", decision.UnsupportedLibrary);
        var error = JarPluginNativeGuard.ToException(decision);
        Assert.Equal(JarPluginErrors.UnsupportedNativeLibrary, error.Failure.Code);
        Assert.Equal(JarPluginFailureKind.Unsupported, error.Failure.Kind);
        Assert.Contains("fishguard-v8.so", error.Failure.Message);
    }

    [Fact]
    public async Task GuardWithoutLibrariesIsNotRequired()
    {
        var archive = PathOf("plain.jar");
        WriteZip(archive, ("com/github/catvod/spider/AppRJ.class", [9]));
        var guard = new JarPluginNativeGuard(JarPluginNativeGuardBackends.Discover());
        var decision = await guard.PrepareAsync(archive, Path.Combine(_root, "plain-staging"), TestContext.Current.CancellationToken);
        Assert.Equal(JarPluginNativeGuardStatus.NotRequired, decision.Status);
        Assert.Empty(decision.Libraries);
    }

    [Fact]
    public async Task AbiOnlyLibrariesAreExtractedButDoNotDisableTheSource()
    {
        var archive = PathOf("abi.jar");
        WriteZip(archive, ("lib/arm64-v8a/libfoo.so", Arm64Elf()));
        var staging = Path.Combine(_root, "abi-staging");
        var guard = new JarPluginNativeGuard(JarPluginNativeGuardBackends.Discover());

        var decision = await guard.PrepareAsync(archive, staging, TestContext.Current.CancellationToken);

        Assert.Equal(JarPluginNativeGuardStatus.NotRequired, decision.Status);
        Assert.False(decision.RequiresGuardEmulation);
        Assert.Single(decision.Libraries);
        Assert.True(File.Exists(Path.Combine(staging, "libfoo.so")));
    }

    [Fact]
    public async Task PluggableBackendThatReallyEmulatesIsHonoured()
    {
        var archive = PathOf("emulated.jar");
        WriteZip(archive, ("assets/fishguard-v8.so", Arm64Elf()));
        var guard = new JarPluginNativeGuard(new FakeEmulatingBackend());
        var decision = await guard.PrepareAsync(archive, Path.Combine(_root, "emulated-staging"), TestContext.Current.CancellationToken);
        Assert.Equal(JarPluginNativeGuardStatus.Emulated, decision.Status);
    }

    [Fact]
    public async Task PluggableBackendThatRefusesIsReportedAsUnsupported()
    {
        var archive = PathOf("refused.jar");
        WriteZip(archive, ("assets/fishguard-v8.so", Arm64Elf()));
        var guard = new JarPluginNativeGuard(new FakeRefusingBackend());
        var decision = await guard.PrepareAsync(archive, Path.Combine(_root, "refused-staging"), TestContext.Current.CancellationToken);
        Assert.Equal(JarPluginNativeGuardStatus.Unsupported, decision.Status);
        Assert.Equal("fishguard-v8.so", decision.UnsupportedLibrary);
    }

    // ---------- 构件存储接线：普通 JAR 透传，guard 归档明确禁用 ----------

    [Fact]
    public async Task ArtifactStorePassesPlainJarThrough()
    {
        var archive = PathOf("store-plain.jar");
        WriteZip(archive, ("A.class", [7]));
        var store = new NativeGuardJarPluginArtifactStore(
            new FixedArtifacts(archive),
            new JarPluginNativeGuard(JarPluginNativeGuardBackends.Discover()),
            Path.Combine(_root, "store-native"));
        Assert.Equal(archive, await store.AcquireAsync(Reference(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ArtifactStoreRejectsGuardArchiveWithNativeLibraryClassification()
    {
        var archive = PathOf("store-guard.jar");
        WriteZip(archive, ("assets/fishguard-v8.so", Arm64Elf()));
        var store = new NativeGuardJarPluginArtifactStore(
            new FixedArtifacts(archive),
            new JarPluginNativeGuard(JarPluginNativeGuardBackends.Discover()),
            Path.Combine(_root, "store-native2"));
        var error = await Assert.ThrowsAsync<JarPluginException>(() => store.AcquireAsync(Reference(), TestContext.Current.CancellationToken));
        Assert.Equal(JarPluginErrors.UnsupportedNativeLibrary, error.Failure.Code);
        Assert.Equal(JarPluginFailureKind.Unsupported, error.Failure.Kind);
        Assert.Contains("fishguard-v8.so", error.Failure.Message);
    }

    [Fact]
    public async Task DefaultStoreCompositionChecksGuardsOnTheRawArchiveBeforeDexConversion()
    {
        // APK 形态：既有 classes.dex（需转换），又有原生 guard。若 guard 排在转换之后就会漏检
        // （转换产物只含 .class），所以这里必须在前置阶段就明确失败，且不去碰转换器。
        var archive = PathOf("store-apk.apk");
        WriteZip(archive, ("classes.dex", [1, 2, 3]), ("assets/fishguard-v8.so", Arm64Elf()));
        var settings = new JarPluginHostSettings
        {
            JavaExecutable = "java",
            HostJarPath = Path.Combine(_root, "host.jar"),
            WorkRoot = Path.Combine(_root, "work"),
        };
        var store = JarPluginArtifactStores.Create(settings, inner: new FixedArtifacts(archive));
        var error = await Assert.ThrowsAsync<JarPluginException>(() => store.AcquireAsync(Reference(), TestContext.Current.CancellationToken));
        Assert.Equal(JarPluginErrors.UnsupportedNativeLibrary, error.Failure.Code);
        Assert.Contains("fishguard-v8.so", error.Failure.Message);
    }

    [Fact]
    public async Task DisablingGuardPreflightSkipsTheNativeGuardCheck()
    {
        var archive = PathOf("store-noguard.jar");
        WriteZip(archive, ("A.class", [7]));
        var settings = new JarPluginHostSettings
        {
            JavaExecutable = "java",
            HostJarPath = Path.Combine(_root, "host.jar"),
            WorkRoot = Path.Combine(_root, "work2"),
            EnableNativeGuardPreflight = false,
        };
        var store = JarPluginArtifactStores.Create(settings, inner: new FixedArtifacts(archive));
        Assert.Equal(archive, await store.AcquireAsync(Reference(), TestContext.Current.CancellationToken));
    }

    // ---------- 进程资源上限 ----------

    [Fact]
    public void HostHeapCeilingBecomesAJvmArgument()
    {
        var settings = new JarPluginHostSettings
        {
            JavaExecutable = "java",
            HostJarPath = "host.jar",
            WorkRoot = _root,
            MaxHostHeapMegabytes = 512,
            ExtraJvmArguments = ["--add-opens=java.base/java.lang=ALL-UNNAMED"],
        };
        var arguments = settings.BuildJvmArguments();
        Assert.Contains("--add-opens=java.base/java.lang=ALL-UNNAMED", arguments);
        Assert.Contains("-Xmx512m", arguments);
    }

    [Fact]
    public void DefaultSettingsAddNoHeapCeilingAndEnableGuardPreflight()
    {
        var settings = new JarPluginHostSettings { JavaExecutable = "java", HostJarPath = "host.jar", WorkRoot = _root };
        Assert.Empty(settings.BuildJvmArguments());
        Assert.True(settings.EnableNativeGuardPreflight);
        Assert.Equal(JarPluginNativeLibraryScanner.MaxLibraryBytes, settings.NativeGuardLimits.MaxLibraryBytes);
    }

    // ---------- 许可与「绝不 dlopen」的源码级断言 ----------

    [Fact]
    public void NoGplEmulationDependencyIsReferenced()
    {
        var repository = FindRepositoryRoot();
        Assert.NotNull(repository);
        var manifests = new List<string> { Path.Combine(repository!, "Directory.Packages.props") };
        manifests.AddRange(Directory.GetFiles(Path.Combine(repository!, "src"), "*.csproj", SearchOption.AllDirectories));
        foreach (var manifest in manifests)
        {
            Assert.True(File.Exists(manifest), $"缺少清单：{manifest}");
            var text = File.ReadAllText(manifest);
            Assert.DoesNotContain("<PackageVersion Include=\"Unicorn", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("<PackageReference Include=\"Unicorn", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("<PackageVersion Include=\"Keystone", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("<PackageReference Include=\"Keystone", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void JavaHostNeverDlopensArchivedLibraries()
    {
        var javaRoot = FindJavaHostSource();
        Assert.NotNull(javaRoot);
        var offending = new List<string>();
        foreach (var file in Directory.GetFiles(javaRoot!, "*.java", SearchOption.AllDirectories))
        {
            var text = Regex.Replace(File.ReadAllText(file), @"/\*[\s\S]*?\*/|//[^
]*", "");
            if (Regex.IsMatch(text, @"System\s*\.\s*load(Library)?\s*\(") ||
                Regex.IsMatch(text, @"Runtime\s*\.\s*getRuntime\s*\(\s*\)\s*\.\s*load") ||
                Regex.IsMatch(text, @"\bdlopen\s*\("))
            {
                offending.Add(Path.GetFileName(file));
            }
        }
        Assert.Empty(offending);
    }

    [Fact]
    public void JavaGuardModuleExposesTheAlignedContract()
    {
        var javaRoot = FindJavaHostSource();
        Assert.NotNull(javaRoot);
        var guardFile = Path.Combine(javaRoot!, "dev", "vodbox", "jarhost", "guard", "NativeGuards.java");
        Assert.True(File.Exists(guardFile), $"缺少 Java guard 模块：{guardFile}");
        var text = File.ReadAllText(guardFile);
        Assert.Contains("class UnsupportedLibrary", text);
        Assert.Contains("prepareGuards", text);
        Assert.Contains("validateArm64", text);
        Assert.Contains("resolve", text);
        Assert.Contains("assets/[^/]+[-_]v8", text);
        Assert.Contains("lib/arm64-v8a/", text);
        Assert.Contains("\"unsupported_native_library\"", File.ReadAllText(Path.Combine(javaRoot!, "dev", "vodbox", "jarhost", "Host.java")));
    }

    // ---------- Host 的 FTY 真实链路与原生改写自测（源码级契约） ----------

    [Fact]
    public void JavaHostDrivesFtyBootstrapInitLoaderAndGetSpider()
    {
        var javaRoot = FindJavaHostSource();
        Assert.NotNull(javaRoot);
        var host = File.ReadAllText(Path.Combine(javaRoot!, "dev", "vodbox", "jarhost", "Host.java"));
        // FTY（饭太硬）真实链路（据 bootstrap 字节码定位）：bootstrap Init 初始化（Init.init → loader）
        // → Init.getSpider（loader → 解密后的 spider）。而不是直接 new 壳类。
        Assert.Contains("\"com.github.catvod.spider.Init\"", host);
        Assert.Contains("ftySpider", host);
        Assert.Contains("getSpider", host);
        // 任一步失败都可诊断（带步骤名抛出、不静默吞掉）。
        Assert.Contains("FtyBootstrapException", host);
        // open 失败时释放已建立的模拟后端与插件类加载器。
        Assert.Contains("closeQuietly", host);
        // 桥接门面必须在 guard 初始化（JNI_OnLoad）之前就绪，否则 guest 在 JNI_OnLoad 期回调宿主会被判「未接线」。
        var install = host.IndexOf("installNativeSession(guardBackend", StringComparison.Ordinal);
        var load = host.IndexOf("loadGuards(guardBackend", StringComparison.Ordinal);
        Assert.True(install >= 0 && load > install, "FTY 桥接门面必须在 guard 初始化之前安装");
    }

    [Fact]
    public void JavaHostDoesNotRebootstrapAnAlreadyLoadedGuard()
    {
        var javaRoot = FindJavaHostSource();
        Assert.NotNull(javaRoot);
        var backend = File.ReadAllText(Path.Combine(javaRoot!, "dev", "vodbox", "jarhost", "guard", "DynarmicGuardBackend.java"));
        // 重复 bootstrap / 重复装载防护：首次才 bootstrap，之后复用同一模拟器；已装载库按名/路径去重。
        Assert.Contains("if (vm == null)", backend);
        Assert.Contains("loadedLeaves", backend);
    }

    [Fact]
    public void JavaHostShipsNativeAsmRewriteSelfTestWiredIntoTestScript()
    {
        var javaRoot = FindJavaHostSource();
        Assert.NotNull(javaRoot);
        var javaHost = Directory.GetParent(javaRoot!)!.FullName;
        var selfTest = Path.Combine(javaHost, "test", "dev", "vodbox", "jarhost", "guard", "NativeBytecodeRewriteSelfTest.java");
        Assert.True(File.Exists(selfTest), $"缺少原生 ASM 改写自测：{selfTest}");
        var text = File.ReadAllText(selfTest);
        // 用记录型后端验证改写后的类**真正运行**；并显式声明不是真实 ARM64 测试。
        Assert.Contains("NativeBytecodeRewriter", text);
        Assert.Contains("NativeCallSession", text);
        Assert.Contains("记录型", text);
        Assert.Contains("不是", text);
        // 夹具覆盖静态/实例 native 方法。
        var fixture = File.ReadAllText(Path.Combine(javaHost, "test", "dev", "vodbox", "jarhost", "guard", "NativeRewriteFixture.java"));
        Assert.Contains("public static native", fixture);
        Assert.Contains("public native", fixture);
        // 两个自测都必须接进 test.sh，否则不会在离线验收里跑。
        var testScript = File.ReadAllText(Path.Combine(javaHost, "test.sh"));
        Assert.Contains("NativeBytecodeRewriteSelfTest", testScript);
        Assert.Contains("HostFtyBootstrapSelfTest", testScript);
        // FTY 顺序自测的假 Init 与假 spider 必须存在（同名锚点，供 Host#ftySpider 命中）。
        Assert.True(File.Exists(Path.Combine(javaHost, "test", "com", "github", "catvod", "spider", "Init.java")));
        Assert.True(File.Exists(Path.Combine(javaHost, "test", "com", "github", "catvod", "spider", "FtyOrderSpider.java")));
    }

    // ---------- 假实现与定位 ----------

    private static JarPluginNativeLibraryEntry GuardEntry() =>
        new("assets/fishguard-v8.so", "fishguard-v8.so", JarPluginNativeLibraryKind.Guard, 64, null);

    private static JarReference Reference() => new("https://sub.example.invalid/spider.jar", "https://sub.example.invalid/spider.jar", null);

    private sealed class FixedArtifacts(string path) : IJarPluginArtifactStore
    {
        public Task<string> AcquireAsync(JarReference jar, CancellationToken ct) => Task.FromResult(path);
    }

    private sealed class FakeEmulatingBackend : IJarPluginNativeGuardBackend
    {
        public string Name => "fake-emulating";
        public bool IsAvailable => true;
        public Task<JarPluginNativeGuardOutcome> EmulateAsync(JarPluginNativeGuardRequest request, CancellationToken ct) =>
            Task.FromResult(JarPluginNativeGuardOutcome.Success("fake"));
    }

    private sealed class FakeRefusingBackend : IJarPluginNativeGuardBackend
    {
        public string Name => "fake-refusing";
        public bool IsAvailable => true;
        public Task<JarPluginNativeGuardOutcome> EmulateAsync(JarPluginNativeGuardRequest request, CancellationToken ct) =>
            Task.FromResult(JarPluginNativeGuardOutcome.Unsupported(request.Libraries[0].FileName, "fake 拒绝"));
    }

    private static string? FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Directory.Packages.props"))) return directory.FullName;
            directory = directory.Parent;
        }
        return null;
    }

    private static string? FindJavaHostSource()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "VodBox.PluginHost", "JavaHost", "src");
            if (Directory.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        return null;
    }
}
