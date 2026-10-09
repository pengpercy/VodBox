namespace VodBox.Infrastructure;

/// <summary>拖入或打开的本地文件属于哪一类。右键菜单、拖放和目录列表共用同一份扩展名判定。</summary>
public enum LocalFileKind { Other, Playlist, Media }

/// <summary>
/// 本地文件分类与路径解析：单点定义「什么是直播播放列表 / 什么是媒体文件」，
/// 避免目录列表、右键菜单、拖放各自维护一份扩展名清单而逐渐不一致。
/// </summary>
public static class LocalMedia
{
    /// <summary>直播播放列表扩展名（含 m3u8：既可能是 HLS 单流，也可能是频道列表，交给内容判定）。</summary>
    public static readonly IReadOnlyList<string> PlaylistExtensions = [".m3u", ".m3u8"];

    /// <summary>可直接交给 libmpv 的媒体扩展名。</summary>
    public static readonly IReadOnlyList<string> MediaExtensions =
        [".mp4", ".mkv", ".avi", ".mov", ".flv", ".ts", ".webm", ".mp3", ".m4a", ".flac", ".wav"];

    public static LocalFileKind Classify(string? path)
    {
        var extension = Extension(path);
        if (PlaylistExtensions.Contains(extension)) return LocalFileKind.Playlist;
        if (MediaExtensions.Contains(extension)) return LocalFileKind.Media;
        // .m3u8 已归入播放列表；其余未知扩展名按媒体试播，由 libmpv 决定成败。
        return LocalFileKind.Other;
    }

    private static string Extension(string? path) =>
        string.IsNullOrWhiteSpace(path) ? "" : Path.GetExtension(path).ToLowerInvariant();

    /// <summary>
    /// 把本地路径或 file:// URI 解析为存在的文件路径；远程地址与不存在的文件返回 false。
    /// </summary>
    public static bool TryResolveFile(string? address, out string path)
    {
        path = "";
        if (string.IsNullOrWhiteSpace(address)) return false;
        var candidate = address.Trim();
        if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri) && uri.IsFile)
        {
            if (!uri.IsLoopback) return false;
            candidate = uri.LocalPath;
        }
        else if (candidate.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        else if (Uri.TryCreate(candidate, UriKind.Absolute, out var remote) && remote.Scheme is not "file")
        {
            return false;
        }
        if (!File.Exists(candidate)) return false;
        path = Path.GetFullPath(candidate);
        return true;
    }
}
