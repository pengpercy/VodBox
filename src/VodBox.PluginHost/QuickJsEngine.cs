using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace VodBox.PluginHost;

/// <summary>Generated P/Invoke to QuickJS-NG; no reflection or CLR type exposure.</summary>
public sealed unsafe class QuickJsEngine : IDisposable
{
    private nint _handle;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };
    public QuickJsEngine()
    {
        _handle = Native.Create();
        if (_handle == 0) throw new InvalidOperationException("QuickJS 初始化失败。");
        Native.SetHost(_handle, &Host, &FreeHost);
    }
    public string Evaluate(string script)
    {
        if (_handle == 0) throw new ObjectDisposedException(nameof(QuickJsEngine));
        nint result = Native.Evaluate(_handle, script, out int error);
        try { string value = Marshal.PtrToStringUTF8(result) ?? ""; if (error != 0) throw new InvalidOperationException(value); return value; }
        finally { Native.FreeString(result); }
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint Host(nint operation, nint payload)
    {
        try
        {
            string name = Marshal.PtrToStringUTF8(operation) ?? "", value = Marshal.PtrToStringUTF8(payload) ?? "";
            string result = name switch
            {
                "fetch" => Http.GetStringAsync(value).GetAwaiter().GetResult(),
                "sha256" => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant(),
                "log" => Log(value), _ => throw new NotSupportedException("Unknown host operation")
            };
            return Marshal.StringToCoTaskMemUTF8(result);
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 0; }
    }
    private static string Log(string message) { Console.Error.WriteLine(message); return ""; }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void FreeHost(nint value) => Marshal.FreeCoTaskMem(value);
    public void Dispose() { if (_handle != 0) { Native.Destroy(_handle); _handle = 0; } }
}
internal static unsafe partial class Native
{
    private const string Library = "vodbox_quickjs";
    [LibraryImport(Library, EntryPoint = "vb_create")] internal static partial nint Create();
    [LibraryImport(Library, EntryPoint = "vb_destroy")] internal static partial void Destroy(nint vm);
    [LibraryImport(Library, EntryPoint = "vb_set_host")] internal static partial void SetHost(nint vm, delegate* unmanaged[Cdecl]<nint, nint, nint> host, delegate* unmanaged[Cdecl]<nint, void> free);
    [LibraryImport(Library, EntryPoint = "vb_eval", StringMarshalling = StringMarshalling.Utf8)] internal static partial nint Evaluate(nint vm, string script, out int error);
    [LibraryImport(Library, EntryPoint = "vb_free_string")] internal static partial void FreeString(nint value);
}
