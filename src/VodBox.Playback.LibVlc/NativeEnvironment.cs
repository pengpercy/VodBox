using System.ComponentModel;
using System.Runtime.InteropServices;

namespace VodBox.Playback.LibVlc;

internal static partial class NativeEnvironment
{
    public static void SetPluginPath(string path)
    {
        const string name = "VLC_PLUGIN_PATH";
        Environment.SetEnvironmentVariable(name, path);
        // Native AOT's managed environment can differ from libc's getenv view.
        int result = OperatingSystem.IsMacOS() ? SetMac(name, path, 1) : OperatingSystem.IsLinux() ? SetLinux(name, path, 1) : 0;
        if (result != 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
    }
    [LibraryImport("libSystem.B.dylib", EntryPoint = "setenv", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int SetMac(string name, string value, int overwrite);
    [LibraryImport("libc", EntryPoint = "setenv", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int SetLinux(string name, string value, int overwrite);
}
