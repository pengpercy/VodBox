using System.Collections.Concurrent;

namespace VodBox.PluginHost.JarPlugin;

/// <summary>一次 guard 检查的结论。</summary>
public enum JarPluginNativeGuardStatus
{
    /// <summary>归档里没有 <c>assets/*[-_]v8.so</c> 约定的原生 guard，无需模拟，按普通流程继续。</summary>
    NotRequired,
    /// <summary>后端真的在隔离环境里跑过了（当前默认后端永远不会返回这个）。</summary>
    Emulated,
    /// <summary>
    /// guard 的真实初始化交给**执行插件的 Java 宿主进程内部**（Dynarmic 后端，与应用同进程同生命周期）。
    /// 放行归档，但**不等于**已模拟、更不等于可播放——成功与否由宿主的真实初始化与后续调用决定。
    /// </summary>
    Deferred,
    /// <summary>需要原生 guard 且当前无法模拟——必须明确禁用，绝不伪装可跑。</summary>
    Unsupported,
}

/// <summary>
/// guard 决策。携带被抽取/校验过的库清单与（不可用时的）具体库名与原因，
/// 便于映射到 <c>unsupported_native_library</c> 并写进响应信封的 <c>library</c> 字段。
/// </summary>
public sealed record JarPluginNativeGuardDecision(
    JarPluginNativeGuardStatus Status,
    IReadOnlyList<JarPluginNativeLibraryEntry> Libraries,
    string? UnsupportedLibrary = null,
    string? Detail = null)
{
    /// <summary>是否需要原生 guard 模拟（存在 <c>assets/*[-_]v8.so</c>）。</summary>
    public bool RequiresGuardEmulation => Libraries.Any(library => library.Kind == JarPluginNativeLibraryKind.Guard);
}

/// <summary>
/// 原生 guard 的安全前置检查：扫描归档 → 安全抽取并校验（大小 / ARM64 小端 ELF / 可选 md5）→
/// 交给可插拔后端模拟。**全程不 dlopen**：抽取产物只落到隔离目录，永远不会被本进程或 Java 宿主加载。
///
/// 普通 guard 与 FTY（饭太硬）分支：两者都只是「需要模拟」，<see cref="JarPluginNativeLibraryEntry.IsFtyGuard"/>
/// 区分出上游 FTY 特有的库名以供诊断。结论有三种：「没有后端」→
/// <see cref="JarPluginNativeGuardStatus.Unsupported"/>；「有真实后端且跑过」→
/// <see cref="JarPluginNativeGuardStatus.Emulated"/>；「配置了宿主侧 Dynarmic，交给执行插件的
/// Java 宿主进程内部真实初始化」→ <see cref="JarPluginNativeGuardStatus.Deferred"/>——对齐接口，但都不伪造成功。
/// </summary>
public sealed class JarPluginNativeGuard
{
    private readonly IJarPluginNativeGuardBackend _backend;
    private readonly JarPluginNativeGuardLimits _limits;

    public JarPluginNativeGuard(IJarPluginNativeGuardBackend backend, JarPluginNativeGuardLimits? limits = null)
    {
        _backend = backend;
        _limits = limits ?? new JarPluginNativeGuardLimits();
    }

    /// <summary>当前后端（诊断用）。</summary>
    public IJarPluginNativeGuardBackend Backend => _backend;

    /// <summary>
    /// 对归档执行 guard 检查。<paramref name="stagingDirectory"/> 用于隔离存放抽取出的库。
    /// 库不合法（超限 / 非 ARM64 小端 ELF / md5 不符）时抛
    /// <see cref="JarPluginException"/>（<c>unsupported_native_library</c>）。
    /// </summary>
    public async Task<JarPluginNativeGuardDecision> PrepareAsync(string archivePath, string stagingDirectory, CancellationToken ct = default)
    {
        var scan = JarPluginNativeLibraryScanner.Scan(archivePath);
        if (!scan.Readable || !scan.HasLibraries) return new JarPluginNativeGuardDecision(JarPluginNativeGuardStatus.NotRequired, []);
        // 抽取即校验：任何不合法库都在这里以 unsupported_native_library 明确失败。
        var extracted = JarPluginNativeLibraryScanner.Extract(scan, archivePath, stagingDirectory, _limits.MaxLibraryBytes, _limits.MaxChecksumBytes);
        var guards = extracted.Where(library => library.Kind == JarPluginNativeLibraryKind.Guard).ToList();
        if (guards.Count == 0)
        {
            // 只带了 lib/arm64-v8a 的普通 ABI 库：插件未必会用到，不据此禁用；真正用到时宿主会以运行时链接错误分类。
            return new JarPluginNativeGuardDecision(JarPluginNativeGuardStatus.NotRequired, extracted);
        }
        if (!_backend.IsAvailable)
        {
            var first = guards[0];
            var reason = _backend is UnavailableNativeGuardBackend unavailable ? unavailable.Reason : "没有可用的原生模拟后端";
            return new JarPluginNativeGuardDecision(JarPluginNativeGuardStatus.Unsupported, extracted, first.FileName, reason);
        }
        if (_backend.DelegatesToHost)
        {
            // 宿主侧 Dynarmic：放行归档，让 Java 宿主进程内真实初始化（loadLibrary + JNI_OnLoad）。
            // 明确**不**报 Emulated：预检跑没跑 guest 代码，不能当作播放成功。
            var detail = _backend is InHostNativeGuardBackend host ? host.Detail : $"委托给宿主进程内的 {_backend.Name}";
            return new JarPluginNativeGuardDecision(JarPluginNativeGuardStatus.Deferred, extracted, Detail: detail);
        }
        var request = new JarPluginNativeGuardRequest(archivePath, stagingDirectory, extracted, _limits);
        var outcome = await _backend.EmulateAsync(request, ct).ConfigureAwait(false);
        return outcome.Emulated
            ? new JarPluginNativeGuardDecision(JarPluginNativeGuardStatus.Emulated, extracted, Detail: outcome.Detail)
            : new JarPluginNativeGuardDecision(JarPluginNativeGuardStatus.Unsupported, extracted, outcome.UnsupportedLibrary, outcome.Detail);
    }

    /// <summary>把「需要原生 guard 且无法模拟」的决策翻译成本层统一的失败分类。</summary>
    public static JarPluginException ToException(JarPluginNativeGuardDecision decision)
    {
        var library = decision.UnsupportedLibrary;
        var detail = decision.Detail ?? "需要在隔离环境模拟 ARM64 原生库，但当前没有可用的宽松许可后端";
        return new JarPluginException(JarPluginErrors.Classify(JarPluginErrors.UnsupportedNativeLibrary, detail, library: library));
    }
}

/// <summary>
/// 构件存储装饰器：在把可加载 JAR 交给宿主之前，先做一次原生 guard 检查（站点隔离、按内容记忆化）。
/// 普通 JAR / 无 guard 的归档完全透传，行为不变；含 <c>assets/*[-_]v8.so</c> 且无可用后端时，
/// 以 <c>unsupported_native_library</c> 明确失败——不启动宿主、不假装可跑。
/// </summary>
public sealed class NativeGuardJarPluginArtifactStore : IJarPluginArtifactStore
{
    private readonly IJarPluginArtifactStore _inner;
    private readonly JarPluginNativeGuard _guard;
    private readonly string _stagingRoot;
    private readonly ConcurrentDictionary<string, JarPluginNativeGuardDecision> _decisions = new(StringComparer.Ordinal);

    public NativeGuardJarPluginArtifactStore(IJarPluginArtifactStore inner, JarPluginNativeGuard guard, string stagingRoot)
    {
        _inner = inner;
        _guard = guard;
        _stagingRoot = stagingRoot;
    }

    public async Task<string> AcquireAsync(JarReference jar, CancellationToken ct)
    {
        var archive = await _inner.AcquireAsync(jar, ct).ConfigureAwait(false);
        var decision = await PrepareOnceAsync(archive, ct).ConfigureAwait(false);
        if (decision.Status == JarPluginNativeGuardStatus.Unsupported) throw JarPluginNativeGuard.ToException(decision);
        return archive;
    }

    /// <summary>同一归档内容只检查/抽取一次（按内容寻址的记忆化，避免每个请求都重扫归档）。</summary>
    private async Task<JarPluginNativeGuardDecision> PrepareOnceAsync(string archivePath, CancellationToken ct)
    {
        var fingerprint = Fingerprint(archivePath);
        if (fingerprint is null) return new JarPluginNativeGuardDecision(JarPluginNativeGuardStatus.NotRequired, []);
        if (_decisions.TryGetValue(fingerprint, out var cached)) return cached;
        var staging = Path.Combine(_stagingRoot, ShortHash(fingerprint));
        var decision = await _guard.PrepareAsync(archivePath, staging, ct).ConfigureAwait(false);
        // 只缓存确定结论；失败（异常）不缓存，避免一次坏归档永久禁用同名归档。
        if (decision.Status != JarPluginNativeGuardStatus.NotRequired || decision.Libraries.Count > 0)
            _decisions[fingerprint] = decision;
        return decision;
    }

    private static string? Fingerprint(string archivePath)
    {
        try
        {
            var info = new FileInfo(archivePath);
            if (!info.Exists) return null;
            return $"{info.FullName}\u0000{info.Length}\u0000{info.LastWriteTimeUtc.Ticks}";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string ShortHash(string value)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes)[..24];
    }
}
