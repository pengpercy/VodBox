using System.IO.Compression;
using System.Text.RegularExpressions;

namespace VodBox.PluginHost.JarPlugin;

/// <summary>归档的只读事实：能否读取、是否含 DEX、DEX 入口（按加载顺序）、普通 .class 条目数。</summary>
public sealed record JarPluginArchiveInfo(bool Readable, bool HasDex, IReadOnlyList<string> DexEntries, int ClassEntryCount)
{
    /// <summary>归档含普通 Java 字节码（无需 DEX 转换即可直接加载）。</summary>
    public bool HasBytecode => ClassEntryCount > 0;
}

/// <summary>
/// 只读扫描插件归档（JAR/APK），判断是否需要走 DEX→JVM 转换。
///
/// 只做**结构判断**（是否含 {@code classes.dex}/{@code classes2.dex}…），不改写文件、不解析字节码；
/// 真正的转换在 Java 宿主里进行。这样上层能在启动会话前就决定「直接加载」还是「先转换 + 缓存」。
/// </summary>
public static partial class JarPluginArchive
{
    /// <summary>Android 约定的 DEX 入口名：classes.dex / classes2.dex / …（大小写敏感）。</summary>
    [GeneratedRegex(@"^classes(\d*)\.dex$", RegexOptions.CultureInvariant)]
    private static partial Regex DexEntryPattern();

    /// <summary>扫描归档。文件不可读/不是 zip 时返回 <c>Readable=false</c>（不抛，交由加载阶段明确失败）。</summary>
    public static JarPluginArchiveInfo Inspect(string path)
    {
        var dex = new List<(string Name, int Order)>();
        var classes = 0;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.EndsWith('/')) continue;
                var match = DexEntryPattern().Match(entry.FullName);
                if (match.Success)
                {
                    dex.Add((entry.FullName, DexOrder(match.Groups[1].Value)));
                }
                else if (entry.FullName.EndsWith(".class", StringComparison.Ordinal))
                {
                    classes++;
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new JarPluginArchiveInfo(false, false, [], 0);
        }
        var ordered = dex.OrderBy(item => item.Order).ThenBy(item => item.Name, StringComparer.Ordinal).Select(item => item.Name).ToList();
        return new JarPluginArchiveInfo(true, ordered.Count > 0, ordered, classes);
    }

    /// <summary>classes.dex → 1；classesN.dex → N；非法数字后缀按 1（排在显式序号之后由名称排序决定）。</summary>
    private static int DexOrder(string suffix) =>
        suffix.Length == 0 ? 1 : int.TryParse(suffix, out var order) ? order : 1;
}

/// <summary>内容哈希：缓存键的一半（另一半是宿主版本）。</summary>
public static class JarPluginContentHash
{
    /// <summary>文件内容的 SHA-256（小写十六进制，流式）。</summary>
    public static string OfFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant();
    }
}

/// <summary>宿主版本：缓存键的另一半（宿主 JAR 内容哈希），宿主升级即让旧转换产物作废。</summary>
public static class JarPluginHostVersion
{
    public const string Unknown = "unknown";

    /// <summary>宿主 JAR 的内容哈希（取前 16 位十六进制）；不可读时返回 <see cref="Unknown"/>。</summary>
    public static string Compute(string hostJarPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(hostJarPath) || !File.Exists(hostJarPath)) return Unknown;
            return JarPluginContentHash.OfFile(hostJarPath)[..16];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return Unknown;
        }
    }
}
