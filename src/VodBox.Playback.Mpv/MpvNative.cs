using System.Reflection;
using System.Runtime.InteropServices;

namespace VodBox.Playback.Mpv;

/// <summary>libmpv C API 绑定（LibraryImport 源码生成，AOT 安全）。</summary>
internal static partial class MpvNative
{
    static MpvNative() => InstallResolver();

    /// <summary>macOS 需按候选路径预加载 dylib（应用 bundle 内 / 环境变量）；Windows/Linux 由 dyld/ld 按名解析。</summary>
    private static void InstallResolver()
    {
        NativeLibrary.SetDllImportResolver(typeof(MpvNative).Assembly, static (name, _, _) =>
        {
            if (name is not ("mpv" or "libmpv" or "mpv-2" or "libmpv-2")) return IntPtr.Zero;
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
        }
    }

    [LibraryImport("mpv", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IntPtr mpv_create();

    [LibraryImport("mpv")]
    internal static partial int mpv_initialize(IntPtr ctx);

    [LibraryImport("mpv")]
    internal static partial void mpv_destroy(IntPtr ctx);

    [LibraryImport("mpv")]
    internal static partial void mpv_terminate_destroy(IntPtr ctx);

    [LibraryImport("mpv", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int mpv_command(IntPtr ctx, string[] args);

    [LibraryImport("mpv", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int mpv_set_option_string(IntPtr ctx, string name, string value);

    [LibraryImport("mpv", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int mpv_set_property_string(IntPtr ctx, string name, string value);

    [LibraryImport("mpv", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IntPtr mpv_get_property_string(IntPtr ctx, string name);

    [LibraryImport("mpv")]
    internal static partial void mpv_free(IntPtr data);

    [LibraryImport("mpv")]
    internal static partial ulong mpv_client_api_version();

    // ---------- 事件 ----------
    [LibraryImport("mpv")]
    internal static partial IntPtr mpv_wait_event(IntPtr ctx, int timeout);

    [LibraryImport("mpv")]
    internal static partial void mpv_wakeup(IntPtr ctx);

    internal const int MpvFormatNone = 0;
    internal const int MpvFormatString = 1;
    internal const int MpvEventIdle = 0;
    internal const int MpvEventShutdown = 1;
    internal const int MpvEventLogMessage = 5;
    internal const int MpvEventEndFile = 7;
    internal const int MpvEventFileLoaded = 9;
    internal const int MpvEventPropertyChange = 11;

    /// <summary>mpv_event 结构：event_id(int) + error(int) + reply_userdata(ulong) + data(void*)。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct MpvEvent
    {
        public int EventId;
        public int ErrorCode;
        public ulong ReplyUserdata;
        public IntPtr Data;
    }
}
