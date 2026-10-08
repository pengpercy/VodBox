using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace VodBox.PluginHost;

/// <summary>
/// QuickJS-NG C bridge 绑定（LibraryImport 源码生成，AOT 干净）。
/// 原生库 vodbox_quickjs 由 build/quickjs CMake 管线产出；解析器按平台候选路径预载。
/// </summary>
internal static unsafe partial class QuickJsNative
{
    private const string Library = "vodbox_quickjs";

    static QuickJsNative() => InstallResolver();

    /// <summary>按候选路径预载 libvodbox_quickjs（环境变量覆盖 → 应用目录）。</summary>
    private static void InstallResolver()
    {
        NativeLibrary.SetDllImportResolver(typeof(QuickJsNative).Assembly, static (name, _, _) =>
        {
            if (name != Library) return IntPtr.Zero;
            foreach (var candidate in CandidateLibraries())
                if (NativeLibrary.TryLoad(candidate, out var handle)) return handle;
            return IntPtr.Zero;
        });
    }

    private static IEnumerable<string> CandidateLibraries()
    {
        var overridePath = Environment.GetEnvironmentVariable("VODBOX_QUICKJS_LIB");
        if (!string.IsNullOrWhiteSpace(overridePath)) yield return overridePath;
        var baseDir = AppContext.BaseDirectory;
        var platform = OperatingSystem.IsMacOS() ? "osx" : OperatingSystem.IsWindows() ? "win" : "linux";
        var arch = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        var filename = OperatingSystem.IsWindows() ? "vodbox_quickjs.dll"
            : OperatingSystem.IsMacOS() ? "libvodbox_quickjs.dylib" : "libvodbox_quickjs.so";
        yield return Path.Combine(baseDir, filename);
        yield return Path.Combine(baseDir, "runtimes", platform + "-" + arch, "native", filename);
        yield return Path.Combine(baseDir, "..", "Resources", filename);
        if (OperatingSystem.IsMacOS())
        {
            yield return "/usr/local/lib/libvodbox_quickjs.dylib";
            yield return "/opt/homebrew/lib/libvodbox_quickjs.dylib";
        }
    }

    // 回调经 nint 传（函数指针在 QuickJsEngine 里转型），LibraryImport 不认 delegate* 参数
    [LibraryImport(Library, EntryPoint = "vb_create")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint Create();

    [LibraryImport(Library, EntryPoint = "vb_destroy")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void Destroy(nint vm);

    [LibraryImport(Library, EntryPoint = "vb_set_host")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetHost(nint vm, nint host, nint free);

    [LibraryImport(Library, EntryPoint = "vb_set_module_loader")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetModuleLoader(nint vm, nint loader, nint free);

    [LibraryImport(Library, EntryPoint = "vb_set_host_async")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetHostAsync(nint vm, nint hostAsync);

    [LibraryImport(Library, EntryPoint = "vb_set_interrupt")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetInterrupt(nint vm, nint isCancelled);

    [LibraryImport(Library, EntryPoint = "vb_resolve", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void Resolve(nint vm, int requestId, string result, int isError);

    [LibraryImport(Library, EntryPoint = "vb_pump")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int Pump(nint vm);

    [LibraryImport(Library, EntryPoint = "vb_eval", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint Evaluate(nint vm, string script, out int error);

    [LibraryImport(Library, EntryPoint = "vb_eval_module", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint EvaluateModule(nint vm, string filename, out int error);

    [LibraryImport(Library, EntryPoint = "vb_free_string")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void FreeString(nint value);
}
