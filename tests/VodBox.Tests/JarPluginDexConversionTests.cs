using System.IO.Compression;
using System.Text.Json;
using VodBox.PluginHost.JarPlugin;
using Xunit;

namespace VodBox.Tests;

/// <summary>
/// DEX→JVM 转换子系统的离线契约测试：归档结构判定、内容哈希 + 宿主版本缓存、锁 / 原子提交 /
/// 损坏回退、失败分类。全部不启 JVM、不触网——转换器用假实现注入。
/// </summary>
public sealed class JarPluginDexConversionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vodbox-dex-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (Exception) { /* 清理失败不影响判定。 */ }
    }

    // ---------- 归档结构判定 ----------

    [Fact]
    public void InspectionDetectsMultiDexInLoadOrder()
    {
        var archive = PathOf("multi.apk");
        WriteZip(archive, ("classes.dex", [1]), ("classes10.dex", [2]), ("classes2.dex", [3]), ("res/x.png", [4]));
        var info = JarPluginArchive.Inspect(archive);
        Assert.True(info.Readable);
        Assert.True(info.HasDex);
        Assert.Equal(new[] { "classes.dex", "classes2.dex", "classes10.dex" }, info.DexEntries);
        Assert.Equal(0, info.ClassEntryCount);
    }

    [Fact]
    public void InspectionCountsBytecodeAndIgnoresMissingDex()
    {
        var archive = PathOf("plain.jar");
        WriteZip(archive, ("com/github/catvod/spider/AppRJ.class", [9]), ("META-INF/MANIFEST.MF", [1]));
        var info = JarPluginArchive.Inspect(archive);
        Assert.False(info.HasDex);
        Assert.True(info.HasBytecode);
        Assert.Equal(1, info.ClassEntryCount);
    }

    [Fact]
    public void InspectionOnNonZipIsNotReadableAndNotDex()
    {
        var path = PathOf("broken.jar");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [0, 1, 2, 3, 4]);
        var info = JarPluginArchive.Inspect(path);
        Assert.False(info.Readable);
        Assert.False(info.HasDex);
    }

    // ---------- 转换管道：缓存 / 原子提交 / 损坏回退 ----------

    [Fact]
    public async Task PipelinePassesPlainBytecodeJarThroughWithoutConverting()
    {
        var archive = PathOf("plain.jar");
        WriteZip(archive, ("A.class", [7]));
        var converter = new FakeConverter();
        var pipeline = NewPipeline(converter);
        var result = await pipeline.EnsureLoadableAsync(archive, "host-1", TestContext.Current.CancellationToken);
        Assert.Equal(archive, result);
        Assert.Equal(0, converter.Calls);
    }

    [Fact]
    public async Task PipelineConvertsDexArchiveThenServesFromCache()
    {
        var archive = PathOf("plugin.apk");
        WriteZip(archive, ("classes.dex", [1]), ("classes2.dex", [2]));
        var converter = new FakeConverter();
        var pipeline = NewPipeline(converter);

        var first = await pipeline.EnsureLoadableAsync(archive, "host-1", TestContext.Current.CancellationToken);
        Assert.NotEqual(archive, first);
        Assert.True(JarPluginConversionPipeline.IsLoadableJar(first));
        Assert.Equal(1, converter.Calls);

        var second = await pipeline.EnsureLoadableAsync(archive, "host-1", TestContext.Current.CancellationToken);
        Assert.Equal(first, second);
        Assert.Equal(1, converter.Calls); // 命中缓存，不再转换。
    }

    [Fact]
    public async Task PipelineInvalidatesCacheWhenHostVersionChanges()
    {
        var archive = PathOf("plugin.apk");
        WriteZip(archive, ("classes.dex", [1]));
        var converter = new FakeConverter();
        var pipeline = NewPipeline(converter);

        var first = await pipeline.EnsureLoadableAsync(archive, "host-1", TestContext.Current.CancellationToken);
        var second = await pipeline.EnsureLoadableAsync(archive, "host-2", TestContext.Current.CancellationToken);
        Assert.NotEqual(first, second);
        Assert.Equal(2, converter.Calls);
    }

    [Fact]
    public async Task PipelineInvalidatesCacheWhenContentChanges()
    {
        var archive = PathOf("plugin.apk");
        WriteZip(archive, ("classes.dex", [1]));
        var converter = new FakeConverter();
        var pipeline = NewPipeline(converter);

        var first = await pipeline.EnsureLoadableAsync(archive, "host-1", TestContext.Current.CancellationToken);
        WriteZip(archive, ("classes.dex", [2]), ("classes2.dex", [3])); // 同址换内容。
        var second = await pipeline.EnsureLoadableAsync(archive, "host-1", TestContext.Current.CancellationToken);
        Assert.NotEqual(first, second);
        Assert.Equal(2, converter.Calls);
    }

    [Fact]
    public async Task PipelineRecoversFromCorruptCachedArtifact()
    {
        var archive = PathOf("plugin.apk");
        WriteZip(archive, ("classes.dex", [1]));
        var converter = new FakeConverter();
        var pipeline = NewPipeline(converter);

        var cached = await pipeline.EnsureLoadableAsync(archive, "host-1", TestContext.Current.CancellationToken);
        Assert.Equal(1, converter.Calls);
        File.WriteAllBytes(cached, [0, 1, 2, 3]); // 损坏缓存产物（非 zip）。
        Assert.False(JarPluginConversionPipeline.IsLoadableJar(cached));

        var recovered = await pipeline.EnsureLoadableAsync(archive, "host-1", TestContext.Current.CancellationToken);
        Assert.Equal(cached, recovered);
        Assert.Equal(2, converter.Calls); // 识别损坏 -> 重新转换。
        Assert.True(JarPluginConversionPipeline.IsLoadableJar(recovered));
    }

    [Fact]
    public async Task PipelineLeavesNoTemporaryArtifactsAfterCommit()
    {
        var archive = PathOf("plugin.apk");
        WriteZip(archive, ("classes.dex", [1]));
        var pipeline = NewPipeline(new FakeConverter());
        await pipeline.EnsureLoadableAsync(archive, "host-1", TestContext.Current.CancellationToken);
        var jars = Path.Combine(_root, "cache", "jars");
        Assert.Empty(Directory.GetFiles(jars, "*.tmp"));
        Assert.Single(Directory.GetFiles(jars, "*.jar"));
    }

    [Fact]
    public async Task PipelineFailsAsLockedWhenLockIsHeldElsewhere()
    {
        var archive = PathOf("plugin.apk");
        WriteZip(archive, ("classes.dex", [1]));
        var converter = new FakeConverter();
        var pipeline = new JarPluginConversionPipeline(Path.Combine(_root, "cache"), converter, TimeSpan.FromMilliseconds(200));
        var key = pipeline.CacheKey(archive, "host-1");
        var lockPath = pipeline.LockFilePath(key);
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        using var held = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        var error = await Assert.ThrowsAsync<JarPluginException>(() => pipeline.EnsureLoadableAsync(archive, "host-1", TestContext.Current.CancellationToken));
        Assert.Equal(JarPluginErrors.DexConversionLocked, error.Failure.Code);
        Assert.True(error.Failure.Retryable);
        Assert.Equal(0, converter.Calls); // 拿不到锁就不该开始转换。
    }

    [Fact]
    public async Task PipelineSurfacesConverterFailureAndCommitsNothing()
    {
        var archive = PathOf("plugin.apk");
        WriteZip(archive, ("classes.dex", [1]));
        var converter = new FakeConverter
        {
            Behavior = (_, _, _) => throw new JarPluginException(JarPluginErrors.DexConverterUnavailable, JarPluginFailureKind.Unsupported, "无转换器"),
        };
        var pipeline = NewPipeline(converter);
        var error = await Assert.ThrowsAsync<JarPluginException>(() => pipeline.EnsureLoadableAsync(archive, "host-1", TestContext.Current.CancellationToken));
        Assert.Equal(JarPluginErrors.DexConverterUnavailable, error.Failure.Code);
        var jars = Path.Combine(_root, "cache", "jars");
        Assert.Empty(Directory.GetFiles(jars));
    }

    // ---------- 构件存储装饰器 ----------

    [Fact]
    public async Task ConvertingStoreReturnsLoadableJarForDexArchive()
    {
        var archive = PathOf("plugin.apk");
        WriteZip(archive, ("classes.dex", [1]));
        var pipeline = NewPipeline(new FakeConverter());
        var store = new ConvertingJarPluginArtifactStore(new StubStore(archive), pipeline, "host-1");
        var resolved = await store.AcquireAsync(JarReference.Parse("https://x.invalid/p.apk", null)!, TestContext.Current.CancellationToken);
        Assert.NotEqual(archive, resolved);
        Assert.True(JarPluginConversionPipeline.IsLoadableJar(resolved));
    }

    [Fact]
    public async Task ConvertingStorePassesPlainJarThrough()
    {
        var archive = PathOf("plain.jar");
        WriteZip(archive, ("A.class", [7]));
        var pipeline = NewPipeline(new FakeConverter());
        var store = new ConvertingJarPluginArtifactStore(new StubStore(archive), pipeline, "host-1");
        var resolved = await store.AcquireAsync(JarReference.Parse("https://x.invalid/p.jar", null)!, TestContext.Current.CancellationToken);
        Assert.Equal(archive, resolved);
    }

    // ---------- 进程转换器：无 Java 时明确失败（不真正启动 JVM） ----------

    [Fact]
    public async Task ProcessConverterWithoutJavaFailsAsUnavailable()
    {
        var archive = PathOf("plugin.apk");
        WriteZip(archive, ("classes.dex", [1]));
        var settings = new JarPluginHostSettings
        {
            JavaExecutable = Path.Combine(_root, "no-such-java"),
            HostJarPath = Path.Combine(_root, "no-such-host.jar"),
            WorkRoot = _root,
            DexConversionTimeout = TimeSpan.FromSeconds(5),
        };
        var converter = new JarPluginProcessDexConverter(settings);
        var error = await Assert.ThrowsAsync<JarPluginException>(
            () => converter.ConvertAsync(archive, PathOf("out.jar"), TestContext.Current.CancellationToken));
        Assert.Equal(JarPluginErrors.DexConverterUnavailable, error.Failure.Code);
        Assert.Equal(JarPluginFailureKind.Unsupported, error.Failure.Kind);
    }

    // ---------- 真实转换验收：真 DEX + 真实 dex2jar（有则运行，缺则**显式跳过**） ----------
    //
    // 上面用假转换器的测试只能证明缓存/锁/分类/原子提交；**证明不了 DEX 真能转换**。
    // 下面两个用例把仓库里**真 DEX** fixture（build/dex_fixture.py 生成）喂给真实 dex2jar
    // 转换进程（宿主 --convert），断言产物是可加载 JAR 且含期望的 .class。
    // 需要：JDK ≥17 + 已构建 host.jar + host.jar 同级 dex-tools/（见 docs/DEX-ACCEPTANCE.md）。

    [Fact]
    public async Task RealConverterConvertsSingleDexFixture()
    {
        var setup = DexAcceptance.TryResolve();
        if (setup is null)
        {
            Assert.Skip(DexAcceptance.SkipReason);
            return;
        }
        var fixture = DexAcceptance.Fixture("sample-single.dex");
        Assert.True(File.Exists(fixture), $"缺少 DEX fixture：{fixture}（由 build/dex_fixture.py 生成）");

        var output = Path.Combine(_root, "real-single.jar");
        var result = await setup!.Converter.ConvertAsync(fixture, output, TestContext.Current.CancellationToken);

        Assert.Equal(1, result.DexFileCount);
        Assert.True(result.ClassCount > 0, "转换未产出任何类");
        Assert.True(JarPluginConversionPipeline.IsLoadableJar(output));
        Assert.Contains("dev/vodbox/fixture/Sample.class", ClassEntries(output));
    }

    [Fact]
    public async Task RealConverterConvertsMultiDexFixture()
    {
        var setup = DexAcceptance.TryResolve();
        if (setup is null)
        {
            Assert.Skip(DexAcceptance.SkipReason);
            return;
        }
        var fixture = DexAcceptance.Fixture("sample-multidex.zip");

        var output = Path.Combine(_root, "real-multi.jar");
        var result = await setup!.Converter.ConvertAsync(fixture, output, TestContext.Current.CancellationToken);

        Assert.True(result.DexFileCount >= 2, $"多 DEX 归档只识别到 {result.DexFileCount} 个 DEX");
        Assert.True(JarPluginConversionPipeline.IsLoadableJar(output));
        var entries = ClassEntries(output);
        Assert.Contains("dev/vodbox/fixture/Sample.class", entries);
        Assert.Contains("dev/vodbox/fixture/Second.class", entries);
    }

    // ---------- 协议 / 分类 / 版本 ----------

    [Theory]
    [InlineData(JarPluginErrors.DexConversionFailed, JarPluginFailureKind.Unsupported)]
    [InlineData(JarPluginErrors.DexConverterUnavailable, JarPluginFailureKind.Unsupported)]
    [InlineData(JarPluginErrors.DexCorrupt, JarPluginFailureKind.Unsupported)]
    [InlineData(JarPluginErrors.DexUnsupportedVersion, JarPluginFailureKind.Unsupported)]
    [InlineData(JarPluginErrors.DexConversionLocked, JarPluginFailureKind.Temporary)]
    public void DexErrorCodesMapToKinds(string code, JarPluginFailureKind kind) =>
        Assert.Equal(kind, JarPluginErrors.Classify(code).Kind);

    [Fact]
    public void ConvertRequestRoundTripsThroughSourceGeneratedContext()
    {
        var request = new JarPluginConvertRequest { Input = "/tmp/in.apk", Output = "/tmp/out.jar", Result = "/tmp/convert.json" };
        var text = JarPluginJson.Serialize(request);
        Assert.Contains("\"input\":\"/tmp/in.apk\"", text);
        var restored = JsonSerializer.Deserialize(text, JarPluginJsonContext.Default.JarPluginConvertRequest);
        Assert.Equal("/tmp/out.jar", restored!.Output);
        Assert.Equal("/tmp/convert.json", restored.Result);
    }

    [Fact]
    public void ConvertEnvelopeRoundTripsDexFacts()
    {
        const string text = """{"ok":true,"dexFiles":3,"dexVersion":"039","classCount":812}""";
        var envelope = JsonSerializer.Deserialize(text, JarPluginJsonContext.Default.JarPluginEnvelope);
        Assert.True(envelope!.Ok);
        Assert.Equal(3, envelope.DexFiles);
        Assert.Equal("039", envelope.DexVersion);
        Assert.Equal(812, envelope.ClassCount);
        Assert.Null(JarPluginErrors.Classify(envelope)); // ok 信封不是失败。
    }

    [Fact]
    public void HostVersionIsStableAndUnknownWhenMissing()
    {
        var host = PathOf("host.jar");
        WriteZip(host, ("dev/vodbox/jarhost/Host.class", [1]));
        var first = JarPluginHostVersion.Compute(host);
        Assert.Equal(first, JarPluginHostVersion.Compute(host));
        Assert.Equal(16, first.Length);
        Assert.Equal(JarPluginHostVersion.Unknown, JarPluginHostVersion.Compute(PathOf("absent.jar")));
    }

    [Fact]
    public void ConversionMarkerRoundTrips()
    {
        var marker = new JarPluginConversionMarker
        {
            ContentHash = "abc", HostVersion = "v1", DexFileCount = 2, DexVersion = "037", ClassCount = 10,
        };
        var text = JarPluginJson.Serialize(marker);
        var restored = JsonSerializer.Deserialize(text, JarPluginJsonContext.Default.JarPluginConversionMarker);
        Assert.Equal("abc", restored!.ContentHash);
        Assert.Equal("037", restored.DexVersion);
        Assert.Equal(10, restored.ClassCount);
    }

    // ---------- 辅助 ----------

    private string PathOf(string name)
    {
        Directory.CreateDirectory(_root);
        return Path.Combine(_root, name);
    }

    /// <summary>JAR 里的 .class 条目名，供真实转换断言使用。</summary>
    private static List<string> ClassEntries(string jarPath)
    {
        using var stream = new FileStream(jarPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        return zip.Entries
            .Where(entry => !entry.FullName.EndsWith('/') && entry.FullName.EndsWith(".class", StringComparison.Ordinal))
            .Select(entry => entry.FullName)
            .ToList();
    }

    /// <summary>
    /// 真实转换验收的环境探测：JDK + host.jar + 同级 dex-tools/ 缺任一即返回 null（用例显式跳过）。
    /// 探测顺序与宿主一致：随包目录优先，再 <c>.cache/dex-acceptance</c> 工作区，再环境变量覆盖。
    /// </summary>
    private static class DexAcceptance
    {
        public const string SkipReason =
            "真实 DEX 转换验收需要 JDK + 已构建的 host.jar + host.jar 同级 dex-tools/；" +
            "先执行 `bash build/dex-acceptance.sh`（或 `python3 build/fetch-dex-deps.py stage <JavaHost目录> --host-jar <host.jar>`），" +
            "详见 docs/DEX-ACCEPTANCE.md。";

        public static AcceptanceSetup? TryResolve()
        {
            var java = JarPluginHostLocator.FindJava();
            if (java is null) return null;
            var hostJar = ResolveHostJar();
            if (hostJar is null) return null;
            var settings = new JarPluginHostSettings
            {
                JavaExecutable = java,
                HostJarPath = hostJar,
                WorkRoot = Path.Combine(Path.GetTempPath(), "vodbox-dex-acceptance", Guid.NewGuid().ToString("N")),
                DexConversionTimeout = TimeSpan.FromMinutes(5),
            };
            var dependencies = Path.Combine(Path.GetDirectoryName(hostJar)!, settings.UsesUpstreamRuntime ? "deps" : "dex-tools");
            if (!Directory.Exists(dependencies) || Directory.GetFiles(dependencies, "*.jar").Length == 0) return null;
            return new AcceptanceSetup(new JarPluginProcessDexConverter(settings), hostJar);
        }

        public static string Fixture(string name) =>
            Path.Combine(AppContext.BaseDirectory, "fixtures", "dex", name);

        private static string? ResolveHostJar()
        {
            var explicitPath = Environment.GetEnvironmentVariable("VODBOX_DEX_ACCEPTANCE_HOST_JAR");
            if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath)) return Path.GetFullPath(explicitPath);
            foreach (var candidate in Candidates())
                if (File.Exists(candidate)) return candidate;
            return null;
        }

        private static IEnumerable<string> Candidates()
        {
            yield return Path.Combine(AppContext.BaseDirectory, "JavaHost", "host.jar");
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "build", "dex_fixture.py")))
                {
                    yield return Path.Combine(directory.FullName, ".cache", "dex-acceptance", "JavaHost", "host.jar");
                    yield return Path.Combine(directory.FullName, "src", "VodBox.PluginHost", "JavaHost", "host.jar");
                    yield break;
                }
            }
        }
    }

    private sealed record AcceptanceSetup(IJarPluginDexConverter Converter, string HostJar);

    private JarPluginConversionPipeline NewPipeline(IJarPluginDexConverter converter) =>
        new(Path.Combine(_root, "cache"), converter, TimeSpan.FromSeconds(5));

    private static void WriteZip(string path, params (string Name, byte[] Content)[] entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            var entry = zip.CreateEntry(name);
            using var target = entry.Open();
            target.Write(content, 0, content.Length);
        }
    }

    private sealed class FakeConverter : IJarPluginDexConverter
    {
        public int Calls { get; private set; }
        public Func<string, string, CancellationToken, Task<JarPluginDexConversionResult>>? Behavior { get; set; }

        public Task<JarPluginDexConversionResult> ConvertAsync(string archivePath, string outputJarPath, CancellationToken ct)
        {
            Calls++;
            if (Behavior is not null) return Behavior(archivePath, outputJarPath, ct);
            WriteZip(outputJarPath, ("dev/vodbox/Converted.class", [1, 2, 3]));
            return Task.FromResult(new JarPluginDexConversionResult(2, "039", 1));
        }
    }

    private sealed class StubStore(string path) : IJarPluginArtifactStore
    {
        public Task<string> AcquireAsync(JarReference jar, CancellationToken ct) => Task.FromResult(path);
    }
}
