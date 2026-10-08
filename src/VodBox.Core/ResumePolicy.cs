namespace VodBox.Core;

/// <summary>续播位置校验：到达媒体末尾的旧记录从头播放；未知时长保留位置。</summary>
public static class ResumePolicy
{
    public static long Position(long positionMs, long durationMs)
    {
        if (positionMs <= 0) return 0;
        if (durationMs > 0 && positionMs >= durationMs) return 0;
        return positionMs;
    }
}
