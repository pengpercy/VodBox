namespace VodBox.PluginHost.JarPlugin;

/// <summary>外部 JAR 宿主的运行参数（超时、并发、进程定位）。</summary>
public sealed record JarPluginHostSettings
{
    /// <summary>Java 可执行文件（绝对路径或 PATH 内命令）。</summary>
    public required string JavaExecutable { get; init; }
    /// <summary>宿主 JAR（含 <c>dev.vodbox.jarhost.Host</c>）的绝对路径。</summary>
    public required string HostJarPath { get; init; }
    /// <summary>会话工作根目录（每次会话在其下建独立子目录）。</summary>
    public required string WorkRoot { get; init; }
    /// <summary>单次请求超时（不含首次启动）。</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(60);
    /// <summary>首次启动（加载插件）超时，通常远大于请求超时。</summary>
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(180);
    /// <summary>空闲会话回收时间。</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(5);
    /// <summary>单次 DEX→JVM 转换超时（大 APK 可能较慢，独立于请求/启动超时）。</summary>
    public TimeSpan DexConversionTimeout { get; init; } = TimeSpan.FromMinutes(5);
    /// <summary>等待 DEX 转换文件锁的超时（并发转换串行化）。</summary>
    public TimeSpan DexConversionLockTimeout { get; init; } = TimeSpan.FromSeconds(60);
    /// <summary>同时保留的宿主会话上限（站点隔离，超限按最久未用回收）。</summary>
    public int MaxSessions { get; init; } = 3;
    /// <summary>额外的 JVM 参数（如 <c>--add-opens</c>），默认空。</summary>
    public IReadOnlyList<string> ExtraJvmArguments { get; init; } = [];
    /// <summary>宿主进程的 JVM 堆上限（MB）；null 表示不显式限制（沿用 JVM 默认）。受控的进程资源上限。</summary>
    public int? MaxHostHeapMegabytes { get; init; }
    /// <summary>
    /// 是否在交给宿主前先做原生 guard 前置检查（默认启用）。启用时，含 <c>assets/*[-_]v8.so</c>
    /// 约定的 ARM64 guard 库、且没有可用宽松许可模拟后端的插件，会以 <c>unsupported_native_library</c>
    /// 明确失败（不启动宿主、不伪装可跑）；关闭则退回「运行时链接错误再分类」。
    /// </summary>
    public bool EnableNativeGuardPreflight { get; init; } = true;
    /// <summary>原生 guard 的资源上限（大小/超时/内存）。</summary>
    public JarPluginNativeGuardLimits NativeGuardLimits { get; init; } = new();

    /// <summary>宿主主类（本仓库自带的最小 Java 宿主）。</summary>
    public const string HostMainClass = "dev.vodbox.jarhost.Host";

    /// <summary>组装启动 JVM 的参数：额外参数 + 可选的堆上限（在 <c>-cp</c> 之前的 JVM 选项位置）。</summary>
    public bool UsesUpstreamRuntime => File.Exists(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(HostJarPath))!, "upstream-runtime.json"));
    public string HostEntryPoint => UsesUpstreamRuntime ? "vodbox.bridge.ProcessAdapter" : HostMainClass;
    public IReadOnlyList<string> JvmArguments => UsesUpstreamRuntime
        ? ["--add-opens=java.base/java.lang=ALL-UNNAMED", "--add-opens=java.base/sun.net.www.protocol.jar=ALL-UNNAMED", "--enable-native-access=ALL-UNNAMED", "-Dorg.slf4j.simpleLogger.defaultLogLevel=error"]
        : [];

    public string BuildClasspath()
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(HostJarPath))!;
        var entries = new List<string> { HostJarPath };
        foreach (var name in UsesUpstreamRuntime ? new[] { "deps" } : new[] { "dex-tools", "runtime", "sqlite-jdbc" })
        {
            var folder = Path.Combine(directory, name);
            if (Directory.Exists(folder)) entries.Add(Path.Combine(folder, "*"));
        }
        return string.Join(Path.PathSeparator, entries);
    }

    public IReadOnlyList<string> BuildJvmArguments()
    {
        var arguments = new List<string>(ExtraJvmArguments);
        if (MaxHostHeapMegabytes is > 0) arguments.Add($"-Xmx{MaxHostHeapMegabytes.Value}m");
        return arguments;
    }
}

/// <summary>
/// 定位 Java 运行时与宿主 JAR。找不到任一项即视为宿主不可用——此时 JAR 源必须保持禁用，
/// 不能创建源、不能假装可播。
/// </summary>
public static class JarPluginHostLocator
{
    /// <summary>环境变量：显式指定 Java 可执行文件（覆盖自动探测）。</summary>
    public const string JavaEnvironmentVariable = "VODBOX_JAR_JAVA";
    /// <summary>环境变量：显式指定宿主 JAR 路径（覆盖随包目录）。</summary>
    public const string HostJarEnvironmentVariable = "VODBOX_JAR_HOST";

    /// <summary>随包宿主 JAR 的相对位置（发布时由构建脚本放置）。</summary>
    public const string BundledHostRelativePath = "JavaHost/host.jar";
    /// <summary>随包 JRE 的相对目录（发布时由构建脚本放置；见 docs/JAR-PLUGIN-DEPS.md §6）。</summary>
    public const string BundledJavaRelativeDirectory = "JavaHost/jre";

    public static bool TryResolve(out JarPluginHostSettings? settings, string? workRoot = null)
    {
        var java = FindJava();
        var hostJar = FindHostJar();
        if (java is null || hostJar is null)
        {
            settings = null;
            return false;
        }
        settings = new JarPluginHostSettings
        {
            JavaExecutable = java,
            HostJarPath = hostJar,
            WorkRoot = workRoot ?? DefaultWorkRoot(),
        };
        return true;
    }

    public static string DefaultWorkRoot() =>
        Path.Combine(Path.GetTempPath(), "vodbox-jarhost");

    /// <summary>宿主字节码目标版本（上游 pom 的 maven.compiler.release=17，类文件 major 61）。</summary>
    public const int MinimumJavaVersion = 17;

    /// <summary>
    /// 按 环境变量 → 随包 JRE → JAVA_HOME → macOS java_home → PATH 的顺序找 Java；找不到返回 null。
    /// 系统 Java 可能过旧（开发机 PATH 上常是 1.8，会拒绝宿主的 --add-opens 参数而立即退出）：
    /// 能从 JDK 的 release 文件读出版本且低于 <see cref="MinimumJavaVersion"/> 的候选跳过；读不出版本的仍按原顺序兜底。
    /// </summary>
    public static string? FindJava(string? baseDirectory = null)
    {
        var configured = Environment.GetEnvironmentVariable(JavaEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
        var executable = OperatingSystem.IsWindows() ? "java.exe" : "java";
        foreach (var directory in BundledHostDirectories(baseDirectory ?? AppContext.BaseDirectory))
        {
            var bundledJava = Path.Combine(directory, "jre", "bin", executable);
            if (File.Exists(bundledJava)) return bundledJava;
            var macBundledJava = Path.Combine(directory, "jre", "Contents", "Home", "bin", executable);
            if (File.Exists(macBundledJava)) return macBundledJava;
        }
        var candidates = new List<string>();
        var home = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(home)) candidates.Add(Path.Combine(home, "bin", executable));
        if (OperatingSystem.IsMacOS() && MacJavaHome() is { } macHome) candidates.Add(Path.Combine(macHome, "bin", executable));
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            candidates.Add(Path.Combine(directory.Trim(), executable));
        var existing = candidates.Where(File.Exists).ToArray();
        return existing.FirstOrDefault(candidate => JavaVersion(candidate) >= MinimumJavaVersion)
            ?? existing.FirstOrDefault(candidate => JavaVersion(candidate) is null);
    }

    /// <summary>从 java 可执行文件所在 JDK 的 release 文件读主版本（1.8 → 8）；读不出返回 null。</summary>
    internal static int? JavaVersion(string javaExecutable)
    {
        try
        {
            var resolved = new FileInfo(javaExecutable).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? javaExecutable;
            var release = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(resolved)))!, "release");
            if (!File.Exists(release)) return null;
            foreach (var line in File.ReadLines(release))
            {
                if (!line.StartsWith("JAVA_VERSION=", StringComparison.Ordinal)) continue;
                var parts = line["JAVA_VERSION=".Length..].Trim('"').Split('.', '_', '-', '+');
                if (!int.TryParse(parts[0], out var major)) return null;
                return major == 1 && parts.Length > 1 && int.TryParse(parts[1], out var legacy) ? legacy : major;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return null;
    }

    /// <summary>macOS 上满足最低版本的已安装 JDK（/usr/libexec/java_home -v 17+）；没有则 null。</summary>
    private static string? MacJavaHome()
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("/usr/libexec/java_home", ["-v", $"{MinimumJavaVersion}+"])
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEnd().Trim();
            if (!process.WaitForExit(5000)) { process.Kill(); return null; }
            return process.ExitCode == 0 && Directory.Exists(output) ? output : null;
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { return null; }
    }

    /// <summary>宿主 JAR：优先环境变量，其次随包目录；找不到返回 null。</summary>
    public static string? FindHostJar(string? baseDirectory = null)
    {
        var configured = Environment.GetEnvironmentVariable(HostJarEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return Path.GetFullPath(configured);
        foreach (var directory in BundledHostDirectories(baseDirectory ?? AppContext.BaseDirectory))
        {
            var bundled = Path.Combine(directory, "host.jar");
            if (File.Exists(bundled)) return bundled;
        }
        return null;
    }

    private static IEnumerable<string> BundledHostDirectories(string baseDirectory)
    {
        yield return Path.GetFullPath(Path.Combine(baseDirectory, "..", "Resources", "JavaHost"));
        yield return Path.GetFullPath(Path.Combine(baseDirectory, "JavaHost"));
    }
}
