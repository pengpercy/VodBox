namespace VodBox.Core;

public enum PlaybackEngineMode { Automatic, LibVlc, Mpv }
public enum PlaybackEngineKind { LibVlc, Mpv }
[Flags]
public enum PlaybackNeeds { Playback = 0, NetworkBrowsing = 1, Casting = 2 }

public sealed record PlaybackEngineSelection(PlaybackEngineKind Kind, string Reason);

/// <summary>Selection policy shared by the future router and settings; does not load either native library.</summary>
public static class PlaybackEnginePolicy
{
    public static PlaybackEngineSelection Select(PlaybackEngineMode mode, string location, PlaybackNeeds needs = PlaybackNeeds.Playback)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(location);
        if (mode is not (PlaybackEngineMode.Automatic or PlaybackEngineMode.LibVlc or PlaybackEngineMode.Mpv)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (mode == PlaybackEngineMode.LibVlc) return new(PlaybackEngineKind.LibVlc, "用户固定选择 LibVLC");
        if (mode == PlaybackEngineMode.Mpv)
        {
            if (needs != PlaybackNeeds.Playback) throw new NotSupportedException("网络浏览与投屏需要 LibVLC，请切换内核或使用自动模式。");
            return new(PlaybackEngineKind.Mpv, "用户固定选择 libmpv");
        }
        return RequiresVlc(location, needs)
            ? new(PlaybackEngineKind.LibVlc, "网络浏览、投屏或网络文件系统使用 LibVLC")
            : new(PlaybackEngineKind.Mpv, "点播、直播与本地媒体优先使用 libmpv");
    }

    // Router may attempt this only once per playback session. Manual choices never silently change.
    public static PlaybackEngineKind? Fallback(PlaybackEngineMode mode, PlaybackEngineKind current, string location, PlaybackNeeds needs = PlaybackNeeds.Playback)
    {
        if (mode != PlaybackEngineMode.Automatic || RequiresVlc(location, needs)) return null;
        return current == PlaybackEngineKind.Mpv ? PlaybackEngineKind.LibVlc : PlaybackEngineKind.Mpv;
    }
    private static bool RequiresVlc(string location, PlaybackNeeds needs) => needs != PlaybackNeeds.Playback ||
        (Uri.TryCreate(location, UriKind.Absolute, out var uri) && uri.Scheme is "smb" or "ftp" or "sftp" or "nfs" or "upnp" or "dlna");
}
