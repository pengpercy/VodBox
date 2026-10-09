using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace VodBox.Playback.Mpv;

/// <summary>libmpv C API 绑定（LibraryImport 源码生成 + 手工 NULL 结尾 UTF-8 封送，AOT 安全）。</summary>
internal static partial class MpvNative
{
    // 单一常量名，解析器按平台候选路径预加载真实动态库。
    private const string Library = "vodbox-mpv";

    static MpvNative() => InstallResolver();

    /// <summary>按候选路径预加载 libmpv（环境变量覆盖 → 应用目录 → 系统目录）。</summary>
    private static void InstallResolver()
    {
        NativeLibrary.SetDllImportResolver(typeof(MpvNative).Assembly, static (name, _, _) =>
        {
            if (name != Library) return IntPtr.Zero;
            foreach (var candidate in CandidateLibraries())
            {
                if (NativeLibrary.TryLoad(candidate, out var handle)) return handle;
            }
            return IntPtr.Zero;
        });
    }

    private static IEnumerable<string> CandidateLibraries()
    {
        var overridePath = Environment.GetEnvironmentVariable("VODBOX_MPV_LIB");
        if (!string.IsNullOrWhiteSpace(overridePath)) yield return overridePath;
        if (OperatingSystem.IsOSPlatform("OSX"))
        {
            var baseDir = AppContext.BaseDirectory;
            yield return Path.Combine(baseDir, "libmpv.dylib");
            yield return Path.Combine(baseDir, "..", "Resources", "libmpv.dylib"); // .app bundle
            yield return "/usr/local/lib/libmpv.dylib";
            yield return "/opt/homebrew/lib/libmpv.dylib";
        }
        else if (OperatingSystem.IsWindows())
        {
            yield return Path.Combine(AppContext.BaseDirectory, "mpv-2.dll");
        }
        else if (OperatingSystem.IsLinux())
        {
            yield return Path.Combine(AppContext.BaseDirectory, "libmpv.so");
            // Distro packages expose versioned sonames; Ubuntu 22.04 ships libmpv.so.1, 24.04 ships libmpv.so.2.
            yield return "libmpv.so.2";
            yield return "libmpv.so.1";
            yield return "/usr/lib/x86_64-linux-gnu/libmpv.so.2";
            yield return "/usr/lib/aarch64-linux-gnu/libmpv.so.2";
            yield return "/usr/lib/x86_64-linux-gnu/libmpv.so.1";
            yield return "/usr/lib/aarch64-linux-gnu/libmpv.so.1";
        }
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

    [LibraryImport(Library, EntryPoint = "mpv_get_property_string", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint GetString(MpvHandle handle, string name);

    [LibraryImport(Library, EntryPoint = "mpv_free")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void Free(nint pointer);

    [LibraryImport(Library, EntryPoint = "mpv_observe_property", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int Observe(MpvHandle handle, ulong id, string name, int format);

    [LibraryImport(Library, EntryPoint = "mpv_wait_event")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint WaitEvent(MpvHandle handle, double timeout);

    [LibraryImport(Library, EntryPoint = "mpv_wakeup")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void Wakeup(MpvHandle handle);

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

/// <summary>mpv 句柄（SafeHandle：引用计数保证 render context 与销毁顺序安全）。</summary>
internal sealed class MpvHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public MpvHandle() : base(true) { }
    protected override bool ReleaseHandle()
    {
        MpvNative.Destroy(handle);
        return true;
    }
}

/// <summary>mpv 错误（携带原生错误码）。</summary>
public sealed class MpvException(int code, string message) : Exception($"mpv ({code}): {message}")
{
    public int Code { get; } = code;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeEvent { public int Id; public int Error; public ulong ReplyUserData; public nint Data; }
[StructLayout(LayoutKind.Sequential)]
internal struct NativeEndFile { public int Reason; public int Error; public long PlaylistEntryId; }
[StructLayout(LayoutKind.Sequential)]
internal struct NativeLogMessage { public nint Prefix; public nint Level; public nint Text; public int LogLevel; }
[StructLayout(LayoutKind.Sequential)]
internal struct NativeProperty { public nint Name; public int Format; public nint Data; }
