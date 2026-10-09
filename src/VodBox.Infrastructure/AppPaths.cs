namespace VodBox.Infrastructure;

/// <summary>
/// 跨平台数据/缓存/配置目录：macOS 用 ~/Library，Linux 用 XDG，Windows 用 LocalApplicationData。
/// 替换 AppServices 中硬编码的 ~/.vodbox（不符合各平台规范，且 macOS 下会被漫游备份漏掉）。
/// </summary>
public static class AppPaths
{
    /// <summary>配置目录（Linux 走 XDG_CONFIG_HOME，其余与数据目录一致）。</summary>
    public static string ConfigurationDirectory => OperatingSystem.IsLinux()
        ? Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
            ?? Path.Combine(Home, ".config"), "VodBox")
        : DataDirectory;

    /// <summary>缓存目录（海报等可重建内容；清理时无需备份）。</summary>
    public static string CacheDirectory => OperatingSystem.IsMacOS()
        ? Path.Combine(Home, "Library", "Caches", "VodBox")
        : OperatingSystem.IsLinux()
            ? Path.Combine(Environment.GetEnvironmentVariable("XDG_CACHE_HOME")
                ?? Path.Combine(Home, ".cache"), "VodBox")
            : Path.Combine(DataDirectory, "cache");

    /// <summary>数据目录（SQLite 库、偏好等需持久化的内容）。</summary>
    public static string DataDirectory
    {
        get
        {
            var isolated=Environment.GetEnvironmentVariable("VODBOX_DATA_DIR");
            if(!string.IsNullOrWhiteSpace(isolated))
            {
                if(!Path.IsPathFullyQualified(isolated))throw new InvalidDataException("VODBOX_DATA_DIR必须为绝对路径。");
                return isolated;
            }
            var root = OperatingSystem.IsMacOS()
                ? Path.Combine(Home, "Library", "Application Support")
                : OperatingSystem.IsWindows()
                    ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
                    : Environment.GetEnvironmentVariable("XDG_DATA_HOME")
                      ?? Path.Combine(Home, ".local", "share");
            return Path.Combine(root, "VodBox");
        }
    }

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
}
