using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace VodBox.Playback.Mpv;

// ABI definitions follow include/mpv/client.h and render.h; no managed object discovery.
internal static partial class MpvNative
{
    private const string Library = "vodbox-mpv";
    private static readonly Lazy<nint> LibraryHandle = new(LoadLibrary);
    static MpvNative() => NativeLibrary.SetDllImportResolver(typeof(MpvNative).Assembly, (name, _, _) => name == Library ? LibraryHandle.Value : 0);
    private static nint LoadLibrary()
    {
        var configured = Environment.GetEnvironmentVariable("VODBOX_MPV_PATH");
        if (!string.IsNullOrWhiteSpace(configured)) return NativeLibrary.Load(Path.GetFullPath(configured));
        var file = OperatingSystem.IsWindows() ? "libmpv-2.dll" : OperatingSystem.IsMacOS() ? "libmpv.2.dylib" : "libmpv.so.2";
        var bundled = Path.Combine(Core.AppLayout.AssetsDirectory, "native", "mpv", file);
        return NativeLibrary.Load(File.Exists(bundled) ? bundled : file);
    }

    [LibraryImport(Library, EntryPoint = "mpv_create")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial MpvHandle Create();
    [LibraryImport(Library, EntryPoint = "mpv_initialize")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int Initialize(MpvHandle handle);
    [LibraryImport(Library, EntryPoint = "mpv_terminate_destroy")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void Destroy(nint handle);
    [LibraryImport(Library, EntryPoint = "mpv_set_option_string", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int Option(MpvHandle handle, string name, string value);
    [LibraryImport(Library, EntryPoint = "mpv_command")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int Command(MpvHandle handle, nint arguments);
    [LibraryImport(Library, EntryPoint = "mpv_get_property", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int GetDouble(MpvHandle handle, string name, int format, out double value);
    [LibraryImport(Library, EntryPoint = "mpv_wait_event")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint WaitEvent(MpvHandle handle, double timeout);
    [LibraryImport(Library, EntryPoint = "mpv_error_string")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint ErrorString(int error);
    [LibraryImport(Library, EntryPoint = "mpv_request_log_messages", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int RequestLogs(MpvHandle handle, string level);

    internal static void Check(int error)
    {
        if (error < 0) throw new MpvException(error, Marshal.PtrToStringUTF8(ErrorString(error)) ?? "Unknown mpv error");
    }
}

internal sealed class MpvHandle : Microsoft.Win32.SafeHandles.SafeHandleZeroOrMinusOneIsInvalid
{
    public MpvHandle() : base(true) { }
    protected override bool ReleaseHandle() { MpvNative.Destroy(handle); return true; }
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeEvent { public int Id; public int Error; public ulong ReplyUserData; public nint Data; }
[StructLayout(LayoutKind.Sequential)]
internal struct NativeEndFile { public int Reason; public int Error; public long PlaylistEntryId; }
[StructLayout(LayoutKind.Sequential)]
internal struct NativeLogMessage { public nint Prefix; public nint Level; public nint Text; public int LogLevel; }

public sealed class MpvException(int code, string message) : Exception($"mpv ({code}): {message}")
{
    public int Code { get; } = code;
}
