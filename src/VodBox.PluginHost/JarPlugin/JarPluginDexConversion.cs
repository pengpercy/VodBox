using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace VodBox.PluginHost.JarPlugin;

/// <summary>一次成功转换的事实（DEX 文件数、版本、产物里的 .class 数）。</summary>
public sealed record JarPluginDexConversionResult(int DexFileCount, string DexVersion, int ClassCount);

/// <summary>
/// DEX→JVM 转换器抽象：把含 {@code classes*.dex} 的归档转成可加载的 JVM JAR。
/// 真实实现是外部进程（见 <see cref="JarPluginProcessDexConverter"/>）；测试用假实现注入成功/失败/超时。
/// </summary>
public interface IJarPluginDexConverter
{
    /// <summary>把 <paramref name="archivePath"/> 转换为 <paramref name="outputJarPath"/>；失败抛 <see cref="JarPluginException"/>。</summary>
    Task<JarPluginDexConversionResult> ConvertAsync(string archivePath, string outputJarPath, CancellationToken ct);
}

/// <summary>
/// 进程外转换器：{@code java -cp host.jar dev.vodbox.jarhost.Host --convert <request.json>}。
/// 与 serve 同宿主、同发现规则（dex2jar 在宿主侧运行时发现）；此处只负责起进程、传路径、读结果信封并分类。
/// </summary>
public sealed class JarPluginProcessDexConverter(JarPluginHostSettings settings) : IJarPluginDexConverter
{
    private readonly JarPluginHostSettings _settings = settings;

    public async Task<JarPluginDexConversionResult> ConvertAsync(string archivePath, string outputJarPath, CancellationToken ct)
    {
        var working = Path.Combine(Path.GetTempPath(), "vodbox-dexconvert", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(working);
        try
        {
            var requestPath = Path.Combine(working, "request.json");
            var resultPath = Path.Combine(working, "convert.json");
            var request = new JarPluginConvertRequest { Input = archivePath, Output = outputJarPath, Result = resultPath };
            await File.WriteAllTextAsync(requestPath, JarPluginJson.Serialize(request), Encoding.UTF8, ct).ConfigureAwait(false);

            var startInfo = new ProcessStartInfo
            {
                FileName = _settings.JavaExecutable,
                WorkingDirectory = working,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var argument in _settings.BuildJvmArguments()) startInfo.ArgumentList.Add(argument);
            foreach (var argument in _settings.JvmArguments) startInfo.ArgumentList.Add(argument);
            startInfo.ArgumentList.Add("-cp");
            startInfo.ArgumentList.Add(_settings.BuildClasspath());
            startInfo.ArgumentList.Add(_settings.HostEntryPoint);
            startInfo.ArgumentList.Add(JarPluginProtocol.ConvertFlag);
            startInfo.ArgumentList.Add(requestPath);

            Process process;
            try
            {
                process = Process.Start(startInfo) ?? throw new JarPluginException(JarPluginErrors.DexConverterUnavailable, JarPluginFailureKind.Unsupported, "无法启动 DEX 转换进程。");
            }
            catch (Exception error) when (error is not JarPluginException)
            {
                throw new JarPluginException(JarPluginErrors.DexConverterUnavailable, JarPluginFailureKind.Unsupported,
                    $"无法启动 DEX 转换进程（检查 Java 运行时与宿主 JAR）：{error.Message}", error);
            }
            var diagnostics = "";
            using (process)
            {
                _ = DrainAsync(process.StandardOutput);
                var errors = DrainAsync(process.StandardError);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(_settings.DexConversionTimeout);
                try
                {
                    await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
                    diagnostics = await errors.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    TryKill(process);
                    throw new JarPluginException(JarPluginErrors.DexConversionFailed, JarPluginFailureKind.Temporary,
                        $"DEX 转换超时（{_settings.DexConversionTimeout.TotalSeconds:0}s）。");
                }
                catch
                {
                    TryKill(process);
                    throw;
                }
            }
            if (!File.Exists(resultPath))
                throw new JarPluginException(JarPluginErrors.DexConversionFailed, JarPluginFailureKind.Unsupported,
                    $"DEX 转换进程未产出结果：{diagnostics}");
            JarPluginEnvelope? envelope;
            try
            {
                envelope = JsonSerializer.Deserialize(await File.ReadAllTextAsync(resultPath, ct).ConfigureAwait(false), JarPluginJsonContext.Default.JarPluginEnvelope);
            }
            catch (JsonException error)
            {
                throw new JarPluginException(JarPluginErrors.DexConversionFailed, JarPluginFailureKind.Unsupported, $"DEX 转换结果不是合法 JSON：{error.Message}", error);
            }
            if (JarPluginErrors.Classify(envelope) is { } failure) throw new JarPluginException(failure);
            if (envelope?.Ok != true)
                throw new JarPluginException(JarPluginErrors.DexConversionFailed, JarPluginFailureKind.Unsupported, "DEX 转换未报告成功。");
            if (!JarPluginConversionPipeline.IsLoadableJar(outputJarPath))
                throw new JarPluginException(JarPluginErrors.DexConversionFailed, JarPluginFailureKind.Unsupported, "DEX 转换产物不含任何 .class。");
            return new JarPluginDexConversionResult(envelope.DexFiles ?? 0, envelope.DexVersion ?? "", envelope.ClassCount ?? 0);
        }
        finally
        {
            TryDeleteDirectory(working);
        }
    }

    private static async Task<string> DrainAsync(StreamReader reader)
    {
        var tail = new System.Text.StringBuilder();
        try
        {
            var buffer = new char[4096];
            int count;
            while ((count = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                tail.Append(buffer, 0, count);
                if (tail.Length > 8192) tail.Remove(0, tail.Length - 8192);
            }
        }
        catch (Exception) { /* 进程结束属正常。 */ }
        return tail.ToString();
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception) { /* 进程状态竞态。 */ }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception) { /* 临时目录清理失败不影响正确性。 */ }
    }
}

/// <summary>
/// DEX 转换产物的磁盘缓存与并发控制：键 = <b>输入内容哈希</b> + <b>宿主版本</b>。
///
/// <list type="bullet">
///   <item><b>内容哈希</b>：同一地址换内容会重新转换，避免张冠李戴；</item>
///   <item><b>宿主版本</b>：宿主升级后旧产物作废；</item>
///   <item><b>锁 + 原子提交</b>：同一键的并发转换由跨进程文件锁串行化；产物先写临时文件、校验通过后再改名，
///         并写一份提交标记；缺标记或产物不可读一律视为未命中；</item>
///   <item><b>损坏回退</b>：缓存项损坏时删除并重新转换，绝不把坏产物交给类加载器。</item>
/// </list>
/// </summary>
public sealed class JarPluginConversionPipeline
{
    private readonly string _cacheRoot;
    private readonly IJarPluginDexConverter _converter;
    private readonly TimeSpan _lockTimeout;

    public JarPluginConversionPipeline(string cacheRoot, IJarPluginDexConverter converter, TimeSpan? lockTimeout = null)
    {
        _cacheRoot = cacheRoot;
        _converter = converter;
        _lockTimeout = lockTimeout ?? TimeSpan.FromSeconds(60);
    }

    /// <summary>归档是否需要转换（含 DEX）；普通字节码 JAR 返回 false。</summary>
    public static bool NeedsConversion(string archivePath) => JarPluginArchive.Inspect(archivePath).HasDex;

    /// <summary>返回归档对应的可加载 JAR：普通 JAR 原样返回；DEX 归档转换后（带缓存）返回。</summary>
    public async Task<string> EnsureLoadableAsync(string archivePath, string hostVersion, CancellationToken ct = default)
    {
        if (!JarPluginArchive.Inspect(archivePath).HasDex) return archivePath;
        var key = CacheKey(archivePath, hostVersion);
        var jars = Path.Combine(_cacheRoot, "jars");
        var markers = Path.Combine(_cacheRoot, "markers");
        var locks = Path.Combine(_cacheRoot, "locks");
        Directory.CreateDirectory(jars);
        Directory.CreateDirectory(markers);
        Directory.CreateDirectory(locks);
        var target = Path.Combine(jars, key + ".jar");
        var marker = Path.Combine(markers, key + ".json");
        var contentHash = JarPluginContentHash.OfFile(archivePath);
        if (IsHit(target, marker, contentHash)) return target;
        using var handle = await AcquireLockAsync(Path.Combine(locks, key + ".lock"), ct).ConfigureAwait(false);
        if (IsHit(target, marker, contentHash)) return target;
        // 损坏回退：清掉不可信产物后重转。
        TryDelete(target);
        TryDelete(marker);
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var result = await _converter.ConvertAsync(archivePath, temporary, ct).ConfigureAwait(false);
            if (!IsLoadableJar(temporary))
                throw new JarPluginException(JarPluginErrors.DexConversionFailed, JarPluginFailureKind.Unsupported, "DEX 转换产物不可加载，已丢弃。");
            File.Move(temporary, target, overwrite: true);
            await WriteMarkerAsync(marker, contentHash, hostVersion, result, ct).ConfigureAwait(false);
        }
        finally
        {
            TryDelete(temporary);
        }
        return target;
    }

    /// <summary>缓存键（内容哈希-宿主版本）；供诊断与测试构造同一路径。</summary>
    public string CacheKey(string archivePath, string hostVersion) =>
        ComposeKey(JarPluginContentHash.OfFile(archivePath), hostVersion);

    private static string ComposeKey(string contentHash, string hostVersion) =>
        contentHash + "-" + Sanitize(string.IsNullOrWhiteSpace(hostVersion) ? JarPluginHostVersion.Unknown : hostVersion);

    /// <summary>某缓存键对应的锁文件路径（跨进程互斥）。</summary>
    public string LockFilePath(string key) => Path.Combine(_cacheRoot, "locks", key + ".lock");

    /// <summary>缓存命中判定：产物是可加载 JAR，且提交标记里的内容哈希与当前一致。</summary>
    private static bool IsHit(string jar, string marker, string contentHash)
    {
        if (!File.Exists(jar) || !File.Exists(marker)) return false;
        if (!IsLoadableJar(jar)) return false;
        try
        {
            var text = File.ReadAllText(marker);
            var parsed = JsonSerializer.Deserialize(text, JarPluginJsonContext.Default.JarPluginConversionMarker);
            return parsed is not null && string.Equals(parsed.ContentHash, contentHash, StringComparison.Ordinal);
        }
        catch (Exception error) when (error is IOException or JsonException)
        {
            return false;
        }
    }

    private async Task<FileStream> AcquireLockAsync(string path, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + _lockTimeout;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(50, ct).ConfigureAwait(false);
            }
            catch (IOException error)
            {
                throw new JarPluginException(JarPluginErrors.DexConversionLocked, JarPluginFailureKind.Temporary,
                    "等待 DEX 转换锁超时（可能有其它会话正在转换同一插件）。", error);
            }
        }
    }

    private static async Task WriteMarkerAsync(string marker, string contentHash, string hostVersion, JarPluginDexConversionResult result, CancellationToken ct)
    {
        var payload = JarPluginJson.Serialize(new JarPluginConversionMarker
        {
            ContentHash = contentHash,
            HostVersion = hostVersion,
            DexFileCount = result.DexFileCount,
            DexVersion = result.DexVersion,
            ClassCount = result.ClassCount,
        });
        var temporary = marker + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllTextAsync(temporary, payload, Encoding.UTF8, ct).ConfigureAwait(false);
        File.Move(temporary, marker, overwrite: true);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { /* 清理失败不影响正确性。 */ }
    }

    private static string Sanitize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
            builder.Append(char.IsLetterOrDigit(character) || character is '-' or '_' or '.' ? character : '_');
        return builder.ToString();
    }

    /// <summary>路径是含 .class 的可读 zip。</summary>
    public static bool IsLoadableJar(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            return zip.Entries.Any(entry => !entry.FullName.EndsWith('/') && entry.FullName.EndsWith(".class", StringComparison.Ordinal));
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>
/// 装饰器：先把归档落地（下载/本地），若含 DEX 则经 <see cref="JarPluginConversionPipeline"/> 转换并缓存，
/// 最终返回**可加载**的 JAR 路径给宿主。普通字节码 JAR 原样透传。
/// </summary>
public sealed class ConvertingJarPluginArtifactStore : IJarPluginArtifactStore
{
    private readonly IJarPluginArtifactStore _inner;
    private readonly JarPluginConversionPipeline _pipeline;
    private readonly string _hostVersion;

    public ConvertingJarPluginArtifactStore(IJarPluginArtifactStore inner, JarPluginConversionPipeline pipeline, string hostVersion)
    {
        _inner = inner;
        _pipeline = pipeline;
        _hostVersion = hostVersion;
    }

    public async Task<string> AcquireAsync(JarReference jar, CancellationToken ct)
    {
        var archive = await _inner.AcquireAsync(jar, ct).ConfigureAwait(false);
        try
        {
            return await _pipeline.EnsureLoadableAsync(archive, _hostVersion, ct).ConfigureAwait(false);
        }
        catch (JarPluginException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            throw new JarPluginException(JarPluginErrors.DexConversionFailed, JarPluginFailureKind.Unsupported, $"插件 DEX 转换失败：{error.Message}", error);
        }
    }
}

/// <summary>按宿主设置构造默认构件存储：HTTP 落地 → 原生 guard 前置检查（默认启用，见
/// <see cref="JarPluginNativeGuard"/>）→ DEX 转换缓存（宿主版本取自宿主 JAR 内容哈希）。
/// guard 必须检查**原始归档**：DEX→JVM 转换产物只含 .class，不含 `assets/`/`lib/arm64-v8a`。</summary>
public static class JarPluginArtifactStores
{
    public static IJarPluginArtifactStore Create(JarPluginHostSettings settings, IJarPluginArtifactStore? inner = null)
    {
        inner ??= new HttpJarPluginArtifactStore(Path.Combine(settings.WorkRoot, "plugins"));
        // The upstream runtime owns lazy DEX conversion and native validation of the original archive.
        if (settings.UsesUpstreamRuntime) return inner;
        IJarPluginArtifactStore store = inner;
        if (settings.EnableNativeGuardPreflight)
        {
            var guard = new JarPluginNativeGuard(
                JarPluginNativeGuardBackends.Discover(settings.HostJarPath),
                settings.NativeGuardLimits);
            store = new NativeGuardJarPluginArtifactStore(store, guard, Path.Combine(settings.WorkRoot, "native"));
        }
        var pipeline = new JarPluginConversionPipeline(
            Path.Combine(settings.WorkRoot, "dex"),
            new JarPluginProcessDexConverter(settings),
            settings.DexConversionLockTimeout);
        return new ConvertingJarPluginArtifactStore(store, pipeline, JarPluginHostVersion.Compute(settings.HostJarPath));
    }
}
