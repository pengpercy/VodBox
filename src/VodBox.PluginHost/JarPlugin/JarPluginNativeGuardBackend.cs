using System.Runtime.InteropServices;

namespace VodBox.PluginHost.JarPlugin;

/// <summary>
/// 原生 guard 模拟的资源上限。默认全部取保守值：这些数字是**安全边界**，
/// 不是性能调优项——超过边界一律视为不可模拟（<c>unsupported_native_library</c>），而不是硬跑。
/// </summary>
public sealed record JarPluginNativeGuardLimits
{
    /// <summary>单个库的大小上限（默认 16 MiB，与归档抽取一致）。</summary>
    public long MaxLibraryBytes { get; init; } = JarPluginNativeLibraryScanner.MaxLibraryBytes;
    /// <summary>md5 声明文件上限。</summary>
    public int MaxChecksumBytes { get; init; } = JarPluginNativeLibraryScanner.MaxChecksumBytes;
    /// <summary>单次模拟的墙钟上限；后端须在该时间内结束或放弃。</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>后端模拟进程/沙箱的内存上限（字节）；null 表示由后端自行约束。</summary>
    public long? MaxMemoryBytes { get; init; }
}

/// <summary>交给后端的模拟请求：归档、隔离目录与已抽取的库清单。归档路径与库**只读**使用。</summary>
public sealed record JarPluginNativeGuardRequest(
    string ArchivePath,
    string StagingDirectory,
    IReadOnlyList<JarPluginNativeLibraryEntry> Libraries,
    JarPluginNativeGuardLimits Limits);

/// <summary>
/// 一次模拟的结果。<see cref="Emulated"/> 为 true 才代表**真的执行过**并给出结果；
/// 否则必须给出不可用的库名与原因——绝不把「没做」说成「已模拟」。
/// </summary>
public sealed record JarPluginNativeGuardOutcome(bool Emulated, string? UnsupportedLibrary, string? Detail)
{
    /// <summary>成功模拟某个库。</summary>
    public static JarPluginNativeGuardOutcome Success(string? detail = null) => new(true, null, detail);

    /// <summary>明确不可模拟（无后端、库不支持、超限…）。</summary>
    public static JarPluginNativeGuardOutcome Unsupported(string library, string detail) => new(false, library, detail);
}

/// <summary>
/// ARM64 原生 guard 模拟后端。**可插拔**：默认实现明确拒绝，只有显式提供宽松许可的后端才会执行。
///
/// 后端契约：
/// <list type="bullet">
///   <item>必须在**隔离环境**中执行 guest 代码，<b>绝不</b>把归档内的 <c>.so</c> dlopen 进宿主进程；</item>
///   <item>必须遵守 <see cref="JarPluginNativeGuardRequest.Limits"/>（时间/内存/大小）；</item>
///   <item>无法执行时必须返回 <see cref="JarPluginNativeGuardOutcome.Unsupported"/>，不得返回成功。</item>
/// </list>
/// </summary>
public interface IJarPluginNativeGuardBackend
{
    /// <summary>后端标识（用于诊断与错误信息）。</summary>
    string Name { get; }

    /// <summary>是否具备执行能力；false 时调用方必须直接判定为不可用，无需调用 <see cref="EmulateAsync"/>。</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// true 表示本后端**不自证模拟**：它把 guard 交给执行插件的 **Java 宿主进程内部**用真实
    /// ARM64 模拟后端（Dynarmic）初始化。调用方因此必须判定为
    /// <see cref="JarPluginNativeGuardStatus.Deferred"/>，<b>绝不能</b>当成
    /// <see cref="JarPluginNativeGuardStatus.Emulated"/>——「已模拟」只由真正跑过 guest 代码的一方给出。
    /// </summary>
    bool DelegatesToHost => false;

    /// <summary>在隔离环境里模拟给定库。不允许 dlopen 归档内的库，也不允许伪报成功。</summary>
    Task<JarPluginNativeGuardOutcome> EmulateAsync(JarPluginNativeGuardRequest request, CancellationToken ct);
}

/// <summary>
/// 默认后端：**明确不可用**。这是本仓库在有可执行的宽松许可后端之前唯一正确的默认——
/// 与其把没有模拟的插件说成可跑，不如诚实禁用。
/// </summary>
public sealed class UnavailableNativeGuardBackend(string reason) : IJarPluginNativeGuardBackend
{
    /// <summary>不可用的原因（供诊断：环境变量未设置 / 目录为空 / 仅发现 GPL 后端而被拒绝）。</summary>
    public string Reason { get; } = reason;

    public string Name => "unavailable";

    public bool IsAvailable => false;

    public Task<JarPluginNativeGuardOutcome> EmulateAsync(JarPluginNativeGuardRequest request, CancellationToken ct)
    {
        var library = request.Libraries.Count > 0 ? request.Libraries[0].FileName : "(未知)";
        return Task.FromResult(JarPluginNativeGuardOutcome.Unsupported(library, Reason));
    }
}

/// <summary>
/// 宿主侧后端：操作方已在运行时配置了 unidbg + Dynarmic 依赖，guard 的**真实初始化**发生在
/// 执行插件的 **Java 宿主进程内部**（<c>DynarmicGuardBackend</c>：<c>loadLibrary</c> + <c>JNI_OnLoad</c>），
/// 与应用插件同进程、同生命周期。
///
/// <para>本预检**不自证模拟**：它只是放行归档，让宿主去真跑。因此决策是
/// <see cref="JarPluginNativeGuardStatus.Deferred"/>，不是 <see cref="JarPluginNativeGuardStatus.Emulated"/>——
/// 「已模拟」只能由宿主真正执行 guest 代码后给出，绝不能用预检结果冒充播放成功。</para>
/// </summary>
public sealed class InHostNativeGuardBackend(string detail) : IJarPluginNativeGuardBackend
{
    /// <summary>放行原因（供诊断）。</summary>
    public string Detail { get; } = detail;

    public string Name => "host-dynarmic";

    /// <summary>具备放行能力（真正的执行在宿主进程内，不在本层）。</summary>
    public bool IsAvailable => true;

    public bool DelegatesToHost => true;

    public Task<JarPluginNativeGuardOutcome> EmulateAsync(JarPluginNativeGuardRequest request, CancellationToken ct)
    {
        // 本层不做模拟：决策路径会在调用它之前判定为 Deferred（见 JarPluginNativeGuard.PrepareAsync）。
        var library = request.Libraries.Count > 0 ? request.Libraries[0].FileName : "(未知)";
        return Task.FromResult(JarPluginNativeGuardOutcome.Unsupported(library, Detail));
    }
}

/// <summary>
/// 后端发现。与 DEX 转换器一样是**运行时发现**，本仓库不内置也不拷贝任何模拟引擎。
///
/// 发现规则（按顺序）：
/// <list type="number">
///   <item>环境变量 <c>VODBOX_NATIVE_GUARD_BACKEND</c>（外部显式后端位置，本仓库不含实现）；</item>
///   <item>环境变量 <c>VODBOX_UNIDBG_CLASSPATH</c> / <c>VODBOX_UNIDBG_HOME</c>：
///         操作方配置了 unidbg + Dynarmic 依赖 → 交给**执行插件的 Java 宿主进程内部**真实初始化
///         （<see cref="InHostNativeGuardBackend"/>，判定为 Deferred，绝不冒充 Emulated）；</item>
///   <item>宿主 JAR 同级 <c>native-guard/</c>（随包后端，本仓库不含实现）。</item>
/// </list>
/// 唯一成熟的 ARM64 指令模拟栈（Unicorn / Keystone）是 GPL——本仓库**不随包捆绑、不修改其许可证**；
/// 需要它的操作方经上述环境变量在运行时显式引入。找不到任何后端时，含原生 guard 的插件一律
/// <c>unsupported_native_library</c>——这是**预期且诚实**的结果，而不是「已支持」。
/// </summary>
public static class JarPluginNativeGuardBackends
{
    /// <summary>显式指定后端位置（目录或 jar）；由操作方自行保证其许可为宽松许可。</summary>
    public const string BackendEnvironmentVariable = "VODBOX_NATIVE_GUARD_BACKEND";
    /// <summary>随包后端目录名（相对宿主 JAR 所在目录）。</summary>
    public const string BundledDirectoryName = "native-guard";
    /// <summary>unidbg 依赖 classpath 环境变量（与 Java 宿主 UnidbgToolchain 一致）。</summary>
    public const string UnidbgClasspathEnvironmentVariable = "VODBOX_UNIDBG_CLASSPATH";
    /// <summary>unidbg 解压目录环境变量（与 Java 宿主 UnidbgToolchain 一致）。</summary>
    public const string UnidbgHomeEnvironmentVariable = "VODBOX_UNIDBG_HOME";

    /// <summary>不可用原因：没有可执行的宽松许可后端。</summary>
    public const string NoLicensedBackendReason =
        "未发现可执行的宽松许可 ARM64 模拟后端（Unicorn/Keystone 为 GPL，本仓库默认不引入）。";

    /// <summary>返回可用后端；没有时返回明确不可用的默认后端（绝不返回会伪报成功的实现）。</summary>
    public static IJarPluginNativeGuardBackend Discover(string? hostJarPath = null)
    {
        var configured = Environment.GetEnvironmentVariable(BackendEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            // 有显式配置也只承认存在且非空的目录/jar；真正的后端实现由操作方以宽松许可提供。
            if (Directory.Exists(configured) || File.Exists(configured)) return new UnavailableNativeGuardBackend(
                NoLicensedBackendReason + $"（已配置 {BackendEnvironmentVariable}，但本仓库不内置模拟引擎实现。）");
            return new UnavailableNativeGuardBackend(
                $"配置的 {BackendEnvironmentVariable} 指向的路径不存在：{configured}");
        }
        var unidbg = UnidbgDependencyDirectory();
        if (unidbg is not null)
        {
            // 配置了 unidbg 依赖只是「有后端」；后端在**当前平台**是否有原生库是另一回事。
            // 随包上游组件固定 unidbg-unicorn2:0.9.8，其 natives/ 缺 windows_arm64/**，
            // 因此在 win-arm64 上把归档委托给宿主只会换来一个运行期链接错误。
            // 这里按平台直接判定为不可用，给出准确的 unsupported_native_library 边界。
            if (!NativeBackendCapability.IsSupported())
                return new UnavailableNativeGuardBackend(NativeBackendCapability.UnsupportedReason(unidbg));
            return new InHostNativeGuardBackend(
                $"已配置 unidbg 依赖（{unidbg}）：guard 交给执行插件的 Java 宿主进程内部用 Dynarmic 真实初始化"
                + "（loadLibrary + JNI_OnLoad），本预检不自证模拟——成功与否以宿主结果为准。");
        }
        var bundled = BundledDirectory(hostJarPath);
        if (bundled is not null && Directory.Exists(bundled) && Directory.EnumerateFileSystemEntries(bundled).Any())
            return new UnavailableNativeGuardBackend(
                NoLicensedBackendReason + $"（{BundledDirectoryName}/ 目录存在，但本仓库不内置模拟引擎实现。）");
        return new UnavailableNativeGuardBackend(NoLicensedBackendReason);
    }

    /// <summary>若操作方配置了 unidbg 依赖目录，返回其描述；否则 null。</summary>
    public static string? UnidbgDependencyDirectory()
    {
        foreach (var name in new[] { UnidbgClasspathEnvironmentVariable, UnidbgHomeEnvironmentVariable })
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value)) return $"{name}={value}";
        }
        return null;
    }

    /// <summary>随包后端目录（宿主 JAR 同级 <c>native-guard/</c>）；无法判定时返回 null。</summary>
    public static string? BundledDirectory(string? hostJarPath)
    {
        if (string.IsNullOrWhiteSpace(hostJarPath)) return null;
        var directory = Path.GetDirectoryName(Path.GetFullPath(hostJarPath));
        return string.IsNullOrEmpty(directory) ? null : Path.Combine(directory, BundledDirectoryName);
    }
}

/// <summary>
/// 随包上游 ARM64 模拟后端（<c>com.github.zhkl0228:unidbg-unicorn2</c>）在**当前进程所在平台**是否
/// 具备原生库。这是「有后端」与「后端在此平台能跑」之间的分界：随包组件在 win-arm64 上没有
/// <c>natives/windows_arm64/**</c>，因此即使操作方配置了 unidbg classpath，也绝不能在 win-arm64 上
/// 把含原生 guard 的插件当成可跑。
///
/// 覆盖结论与 <c>build/dex-deps.json</c> 的 <c>nativeCoverage.backendRids</c> 一致，并由
/// <c>build/native_coverage.py verify</c> 从实际 deps/*.jar 重新推导校验；此处的常量只负责
/// **运行期**把同一结论落到当前平台，二者由回归测试保持一致。
/// </summary>
public static class NativeBackendCapability
{
    /// <summary>上游 pom 固定的模拟后端坐标版本。</summary>
    public const string BackendArtifact = "com.github.zhkl0228:unidbg-unicorn2:0.9.8";

    /// <summary>有原生库的平台（与 build/dex-deps.json 的 nativeCoverage.backendRids.supported 对应）。</summary>
    public static bool IsSupported(Architecture? architecture = null, OSPlatform? os = null)
    {
        var isWindows = os?.Equals(OSPlatform.Windows) ?? OperatingSystem.IsWindows();
        var isMac = os?.Equals(OSPlatform.OSX) ?? OperatingSystem.IsMacOS();
        var isLinux = os?.Equals(OSPlatform.Linux) ?? OperatingSystem.IsLinux();
        var arch = architecture ?? RuntimeInformation.ProcessArchitecture;
        if (isWindows) return arch != Architecture.Arm64;
        if (isMac) return arch is Architecture.X64 or Architecture.Arm64;
        if (isLinux) return arch is Architecture.X64 or Architecture.Arm64;
        return false;
    }

    /// <summary>不可用时的诊断（含具体平台与库坐标）。</summary>
    public static string UnsupportedReason(string? configured = null)
    {
        // Describe the platform from OperatingSystem/RuntimeInformation so the message matches the
        // predicate above; no RuntimeIdentifier (a trim-incompatible API), so it stays AOT-clean.
        var system = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        var platform = $"{system}/{Describe(RuntimeInformation.ProcessArchitecture)}";
        var prefix = string.IsNullOrWhiteSpace(configured) ? "" : $"（已配置 {configured}）";
        return $"unsupported_native_library：随包上游 ARM64 模拟后端 {BackendArtifact} 在 {platform} 上没有原生库"
             + "（缺 natives/windows_arm64/**）。含原生 guard 的插件在当前平台无法模拟，明确禁用而非伪装可跑；"
             + "随包 JRE 只保证纯 Java 插件可运行，不代表原生后端可用。"
             + prefix;
    }

    private static string Describe(Architecture architecture) => architecture switch
    {
        Architecture.X64 => "x64",
        Architecture.Arm64 => "arm64",
        _ => architecture.ToString().ToLowerInvariant(),
    };
}
