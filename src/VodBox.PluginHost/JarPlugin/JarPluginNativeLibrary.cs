using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace VodBox.PluginHost.JarPlugin;

/// <summary>
/// 归档里一个候选 ARM64 原生库的只读事实。**不加载、不执行**——本层只负责把库安全地取出来并校验。
/// </summary>
/// <param name="EntryName">归档内条目名，如 <c>assets/fishguard-v8.so</c> 或 <c>lib/arm64-v8a/libfoo.so</c>。</param>
/// <param name="FileName">叶名（已去目录），如 <c>fishguard-v8.so</c>。</param>
/// <param name="Kind">来源约定：<see cref="JarPluginNativeLibraryKind.Guard"/> 或 <see cref="JarPluginNativeLibraryKind.AndroidAbi"/>。</param>
/// <param name="Size">归档声明的未压缩大小；未知时为 -1。</param>
/// <param name="DeclaredMd5">归档内 <c>assets/&lt;stem&gt;.md5</c> 声明的 arm64-v8a 摘要；无声明为 null。</param>
public sealed record JarPluginNativeLibraryEntry(
    string EntryName,
    string FileName,
    JarPluginNativeLibraryKind Kind,
    long Size,
    string? DeclaredMd5)
{
    /// <summary>归档里是否存在 md5 声明。<b>不等于</b>内容已校验。</summary>
    public bool HasDeclaredMd5 => !string.IsNullOrEmpty(DeclaredMd5);

    /// <summary>是否为上游 FTY（饭太硬）特有的 guard 库名（普通 guard 之外的单独分支）。</summary>
    public bool IsFtyGuard => JarPluginNativeLibraryScanner.IsFtyGuard(FileName);
}

/// <summary>候选原生库的来源约定。</summary>
public enum JarPluginNativeLibraryKind
{
    /// <summary><c>assets/&lt;name&gt;[-_]v8.so</c>：CatVod 原生 guard 的命名约定；上游据此分流普通/FTY 分支。</summary>
    Guard,
    /// <summary><c>lib/arm64-v8a/lib&lt;name&gt;.so</c>：Android ABI 目录下的原生库，按名解析。</summary>
    AndroidAbi,
}

/// <summary>一次归档扫描的结果。归档不可读时 <see cref="Readable"/> 为 false（不抛，交由上层明确失败）。</summary>
public sealed record JarPluginNativeGuardScan(bool Readable, IReadOnlyList<JarPluginNativeLibraryEntry> Libraries)
{
    public bool HasLibraries => Libraries.Count > 0;

    /// <summary>是否需要原生 guard 模拟（存在 <c>assets/*[-_]v8.so</c> 约定库）。</summary>
    public bool RequiresGuardEmulation => Libraries.Any(library => library.Kind == JarPluginNativeLibraryKind.Guard);
}

/// <summary>
/// ARM64 <b>小端</b> ELF 的最小结构校验。
///
/// 只读前 20 字节的 ELF 头固定字段：magic、EI_CLASS(64 位)、EI_DATA(小端)、e_machine(AArch64=183)。
/// 这是「能交给模拟器」的必要条件，<b>不是</b>充分条件，也<b>不</b>代表已证明安全——真正的执行隔离
/// 由后端负责（见 <see cref="IJarPluginNativeGuardBackend"/>）。本层绝不 dlopen。
/// </summary>
public static class JarPluginArm64Elf
{
    /// <summary>e_ident(16) + e_type(2) + e_machine(2)：校验所需的最小前缀长度。</summary>
    public const int HeaderSize = 20;
    /// <summary>EI_CLASS = ELFCLASS64。</summary>
    public const byte Class64 = 2;
    /// <summary>EI_DATA = ELFDATA2LSB（小端）。</summary>
    public const byte DataLittleEndian = 1;
    /// <summary>e_machine = EM_AARCH64。</summary>
    public const int MachineAArch64 = 183;

    /// <summary>校验前 20 字节；通过返回 true，否则给出人类可读原因。</summary>
    public static bool TryValidate(ReadOnlySpan<byte> bytes, out string? reason)
    {
        if (bytes.Length < HeaderSize)
        {
            reason = $"文件过短（{bytes.Length} 字节），不是完整的 ELF 头";
            return false;
        }
        if (bytes[0] != 0x7F || bytes[1] != (byte)'E' || bytes[2] != (byte)'L' || bytes[3] != (byte)'F')
        {
            reason = "缺少 ELF magic（\\x7fELF）";
            return false;
        }
        if (bytes[4] != Class64)
        {
            reason = "不是 64 位 ELF（EI_CLASS）";
            return false;
        }
        if (bytes[5] != DataLittleEndian)
        {
            reason = "不是小端 ELF（EI_DATA）";
            return false;
        }
        // e_machine 是偏移 18 处的 2 字节小端字段：低字节 0xB7、高字节 0x00。
        if (bytes[18] != (byte)MachineAArch64 || bytes[19] != 0)
        {
            reason = "不是 AArch64 机器类型（e_machine != EM_AARCH64）";
            return false;
        }
        reason = null;
        return true;
    }

    /// <summary>目标是否为本层接受的小端 ARM64 ELF。</summary>
    public static bool IsArm64LittleEndianElf(ReadOnlySpan<byte> bytes) => TryValidate(bytes, out _);
}

/// <summary>
/// 只读扫描并安全抽取插件归档（JAR/APK）里的 ARM64 原生库。
///
/// 覆盖两类来源（对齐上游可观测约定）：
/// <list type="bullet">
///   <item><c>assets/&lt;name&gt;[-_]v8.so</c>——CatVod 原生 guard 约定；</item>
///   <item><c>lib/arm64-v8a/lib&lt;name&gt;.so</c>——Android ABI 目录；</item>
/// </list>
/// 以及按名解析的候选顺序（对应上游的 <c>resolve(name)</c>）。
///
/// <b>安全约束</b>：抽取到隔离目录，逐字节校验大小与 ELF 头，可选比对归档内 md5 声明；
/// 全程<b>不</b>调用任何 dlopen/<c>System.load*</c>，产物永远不会被本进程当作动态库载入。
/// </summary>
public static partial class JarPluginNativeLibraryScanner
{
    /// <summary>单个原生库的硬上限：16 MiB（超出即拒绝，不读取全部内容）。</summary>
    public const long MaxLibraryBytes = 16L * 1024 * 1024;
    /// <summary>md5 声明文件的上限：4 KiB（超出即拒绝）。</summary>
    public const int MaxChecksumBytes = 4096;

    /// <summary>FTY（饭太硬）特有的 guard 库名。</summary>
    private static readonly string[] FtyGuardNames = ["ftyguard_v8.so", "ftyguard-v8.so"];

    /// <summary><c>assets/&lt;leaf&gt;[-_]v8.so</c>（单层，无子目录）。</summary>
    [GeneratedRegex(@"^assets/[^/]+[-_]v8\.so$", RegexOptions.CultureInvariant)]
    private static partial Regex GuardEntryPattern();

    /// <summary><c>lib/arm64-v8a/&lt;leaf&gt;.so</c>（单层，无子目录）。</summary>
    [GeneratedRegex(@"^lib/arm64-v8a/[^/]+\.so$", RegexOptions.CultureInvariant)]
    private static partial Regex AndroidAbiEntryPattern();

    /// <summary>合法的库名（拼进路径前先约束字符集，拒绝 <c>..</c>/<c>/</c>）。</summary>
    [GeneratedRegex(@"^[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeLibraryNamePattern();

    /// <summary>按名解析时，先去掉 <c>.so</c> 后缀得到 base。</summary>
    public static bool IsFtyGuard(string fileName) =>
        FtyGuardNames.Contains(fileName, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 只读扫描归档里的候选原生库（不解压、不校验内容）。归档不可读时返回 <c>Readable=false</c>。
    /// </summary>
    public static JarPluginNativeGuardScan Scan(string archivePath)
    {
        var libraries = new List<JarPluginNativeLibraryEntry>();
        try
        {
            using var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.EndsWith('/')) continue;
                var kind = Classify(entry.FullName);
                if (kind is null) continue;
                var fileName = Path.GetFileName(entry.FullName);
                if (fileName.Length == 0) continue;
                libraries.Add(new JarPluginNativeLibraryEntry(entry.FullName, fileName, kind.Value, entry.Length, DeclaredMd5For(zip, fileName)));
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new JarPluginNativeGuardScan(false, []);
        }
        var ordered = libraries
            .OrderBy(library => library.Kind)
            .ThenBy(library => library.EntryName, StringComparer.Ordinal)
            .ToList();
        return new JarPluginNativeGuardScan(true, ordered);
    }

    /// <summary>
    /// 按名解析归档里的 ARM64 库（对齐上游候选顺序）；找不到返回 null。
    /// <c>name</c> 只允许 <c>[A-Za-z0-9_.-]</c>，否则抛 <see cref="JarPluginException"/>（unsupported_native_library）。
    /// </summary>
    public static string? Resolve(string archivePath, string name)
    {
        if (string.IsNullOrEmpty(name) || !SafeLibraryNamePattern().IsMatch(name))
            throw Unsupported(name, "非法的原生库名");
        var baseName = StripGuardSuffix(name);
        var candidates = new[]
        {
            "lib/arm64-v8a/lib" + baseName + ".so",
            "assets/lib" + baseName + ".so",
            "assets/" + baseName + ".so",
            "assets/" + baseName + "-v8.so",
            "assets/" + baseName + "_v8.so",
        };
        try
        {
            using var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            foreach (var candidate in candidates)
                if (zip.GetEntry(candidate) is not null) return candidate;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
        return null;
    }

    /// <summary>
    /// 安全抽取 + 校验 <see cref="Scan"/> 命中的每个库到 <paramref name="stagingDirectory"/>。
    ///
    /// 每个库都做：大小上限（16 MiB）、ARM64 小端 ELF 头、可选 md5 声明比对。任一项不满足即抛
    /// <see cref="JarPluginException"/>（<c>unsupported_native_library</c>），**不**写出该库。
    /// 写出用「先临时文件再原子改名」，与方法名一致的稳定产物路径。
    /// </summary>
    public static IReadOnlyList<JarPluginNativeLibraryEntry> Extract(JarPluginNativeGuardScan scan, string archivePath, string stagingDirectory,
        long maxLibraryBytes = MaxLibraryBytes, int maxChecksumBytes = MaxChecksumBytes)
    {
        ArgumentNullException.ThrowIfNull(scan);
        if (!scan.HasLibraries) return [];
        var written = new List<JarPluginNativeLibraryEntry>();
        try
        {
            using var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            Directory.CreateDirectory(stagingDirectory);
            foreach (var library in scan.Libraries)
            {
                var entry = zip.GetEntry(library.EntryName)
                    ?? throw Unsupported(library.FileName, "归档内条目已消失（扫描后被改写）");
                var bytes = ReadBounded(entry, zip, maxLibraryBytes, library.FileName);
                if (!JarPluginArm64Elf.TryValidate(bytes, out var reason))
                    throw Unsupported(library.FileName, reason ?? "非法 ELF");
                if (library.HasDeclaredMd5 && !Md5Matches(bytes, library.DeclaredMd5!))
                    throw Unsupported(library.FileName, "与归档内 md5 声明不一致");
                WriteAtomically(stagingDirectory, library.FileName, bytes);
                written.Add(library with { Size = bytes.Length });
            }
        }
        catch (JarPluginException)
        {
            // 校验失败不能留下已抽取的半成品，避免下一次把不完整目录当作可用缓存。
            if (Directory.Exists(stagingDirectory)) Directory.Delete(stagingDirectory, recursive: true);
            throw;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            if (Directory.Exists(stagingDirectory)) Directory.Delete(stagingDirectory, recursive: true);
            throw new JarPluginException(JarPluginErrors.UnsupportedNativeLibrary, JarPluginFailureKind.Unsupported,
                $"读取插件原生库失败：{error.Message}", error);
        }
        return written;
    }

    /// <summary>把校验后的字节原子写入隔离目录（先临时文件再改名，避免半截产物）。</summary>
    private static void WriteAtomically(string directory, string fileName, byte[] bytes)
    {
        var target = Path.Combine(directory, Path.GetFileName(fileName));
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllBytes(temporary, bytes);
        File.Move(temporary, target, overwrite: true);
    }

    /// <summary>读取条目内容，超过上限即拒绝（最多读到上限+1 字节，避免把超大库读进内存）。</summary>
    private static byte[] ReadBounded(ZipArchiveEntry entry, ZipArchive zip, long maxBytes, string library)
    {
        using var input = entry.Open();
        var buffer = new MemoryStream();
        var chunk = new byte[81920];
        long total = 0;
        int read;
        while ((read = input.Read(chunk, 0, chunk.Length)) > 0)
        {
            total += read;
            if (total > maxBytes)
                throw Unsupported(library, $"超过 {maxBytes / (1024 * 1024)} MiB 上限");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    /// <summary>按归档内 <c>assets/&lt;stem&gt;.md5</c> 声明（属性键 <c>arm64-v8a</c>）比对内容摘要。</summary>
    private static bool Md5Matches(byte[] bytes, string declared)
    {
        var actual = Convert.ToHexString(MD5.HashData(bytes)).ToLowerInvariant();
        return string.Equals(actual, declared.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>读取并解析归档内与该库同名的 md5 声明（仅 <c>assets/</c> 约定）。非法/超限按「无声明」处理。</summary>
    private static string? DeclaredMd5For(ZipArchive zip, string fileName)
    {
        var stem = StripGuardSuffix(fileName);
        var checksumEntry = zip.GetEntry("assets/" + stem + ".md5");
        if (checksumEntry is null) return null;
        try
        {
            using var input = checksumEntry.Open();
            var buffer = new byte[MaxChecksumBytes + 1];
            var read = input.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            if (read > MaxChecksumBytes) return null; // 超大声明不可信，按无声明处理。
            var text = Encoding.UTF8.GetString(buffer, 0, read);
            return ParseProperties(text, "arm64-v8a");
        }
        catch (Exception error) when (error is IOException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>极小 Properties 解析：只取目标键，支持 <c>key=value</c>/<c>key:value</c>/<c>key value</c>。</summary>
    private static string? ParseProperties(string text, string key)
    {
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim().TrimEnd('\r');
            if (line.Length == 0 || line[0] is '#' or '!') continue;
            var separator = line.IndexOfAny(['=', ':']);
            string name, value;
            if (separator >= 0)
            {
                name = line[..separator].Trim();
                value = line[(separator + 1)..].Trim();
            }
            else
            {
                var space = line.IndexOfAny([' ', '\t']);
                if (space < 0) continue;
                name = line[..space];
                value = line[(space + 1)..].Trim();
            }
            if (string.Equals(name, key, StringComparison.Ordinal)) return value.Length == 0 ? null : value;
        }
        return null;
    }

    /// <summary>去掉 guard 命名后缀，得到 md5 声明的 stem。</summary>
    private static string StripGuardSuffix(string fileName)
    {
        foreach (var suffix in new[] { "-v8.so", "_v8.so", "-v8", "_v8" })
            if (fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return fileName[..^suffix.Length];
        if (fileName.EndsWith(".so", StringComparison.OrdinalIgnoreCase))
            return fileName[..^3];
        return fileName;
    }

    private static JarPluginNativeLibraryKind? Classify(string entryName)
    {
        if (GuardEntryPattern().IsMatch(entryName)) return JarPluginNativeLibraryKind.Guard;
        if (AndroidAbiEntryPattern().IsMatch(entryName)) return JarPluginNativeLibraryKind.AndroidAbi;
        return null;
    }

    internal static JarPluginException Unsupported(string? library, string reason)
    {
        var failure = JarPluginErrors.Classify(JarPluginErrors.UnsupportedNativeLibrary, reason, library: library);
        return new JarPluginException(failure);
    }
}
