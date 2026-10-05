using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using LibVLCSharp.Shared;

namespace VodBox.Desktop;

/// <summary>Direct native drawable binding; no assembly-version or property reflection.</summary>
public sealed class VlcVideoSurface : NativeControlHost
{
    public static readonly DirectProperty<VlcVideoSurface, MediaPlayer?> MediaPlayerProperty =
        AvaloniaProperty.RegisterDirect<VlcVideoSurface, MediaPlayer?>(nameof(MediaPlayer), surface => surface.MediaPlayer, (surface, player) => surface.MediaPlayer = player);
    private MediaPlayer? _player;
    private IPlatformHandle? _drawable;
    public MediaPlayer? MediaPlayer
    {
        get => _player;
        set
        {
            if (ReferenceEquals(_player, value)) return;
            SetDrawable(_player, nint.Zero);
            SetAndRaise(MediaPlayerProperty, ref _player, value);
            AttachPlayer();
        }
    }
    public VlcVideoSurface() => Initialized += (_, _) => AttachPlayer();
    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        _drawable = base.CreateNativeControlCore(parent); AttachPlayer(); return _drawable;
    }
    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        SetDrawable(_player, nint.Zero); _drawable = null; base.DestroyNativeControlCore(control);
    }
    private void AttachPlayer()
    { if (IsInitialized && _drawable is not null) SetDrawable(_player, _drawable.Handle); }
    private static void SetDrawable(MediaPlayer? player, nint drawable)
    {
        if (player is null) return;
        if (OperatingSystem.IsWindows()) player.Hwnd = drawable;
        else if (OperatingSystem.IsMacOS()) player.NsObject = drawable;
        else if (OperatingSystem.IsLinux()) player.XWindow = unchecked((uint)drawable);
        else throw new PlatformNotSupportedException("不支持此系统的原生视频窗口。");
    }
}
