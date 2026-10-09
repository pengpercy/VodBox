namespace VodBox.Infrastructure;

/// <summary>拖入内容的处理意图。</summary>
public enum DroppedIntent
{
    /// <summary>没有可处理的内容（不可接受的拖放，拖放指示器显示禁止）。</summary>
    None,
    /// <summary>当作直播播放列表打开（m3u/m3u8）。</summary>
    PlayLive,
    /// <summary>直接播放媒体文件。</summary>
    PlayMedia,
}

/// <summary>
/// 拖放判定：把一组拖入路径归纳为一个动作。播放列表优先于媒体——
/// 拖入的 m3u 应当展开成频道列表，而不是被当成单条流。
/// </summary>
public static class DroppedFiles
{
    public static (DroppedIntent Intent, string Path) Classify(IEnumerable<string?> paths)
    {
        string? playlist = null;
        string? media = null;
        foreach (var candidate in paths)
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            var path = candidate;
            if (LocalMedia.TryResolveFile(path, out var resolved)) path = resolved;
            else if (!File.Exists(path)) continue;
            if (Directory.Exists(path)) continue;
            switch (LocalMedia.Classify(path))
            {
                case LocalFileKind.Playlist when playlist is null: playlist = path; break;
                case LocalFileKind.Media when media is null: media = path; break;
            }
        }
        if (playlist is not null) return (DroppedIntent.PlayLive, playlist);
        if (media is not null) return (DroppedIntent.PlayMedia, media);
        return (DroppedIntent.None, "");
    }
}
