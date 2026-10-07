using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace VodBox.Playback.Mpv;

/// <summary>libmpv render API 绑定（opengl render context）。</summary>
internal static partial class MpvNative
{
    [LibraryImport(Library, EntryPoint = "mpv_render_context_create")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int RenderCreate(out nint context, nint client, nint parameters);

    [LibraryImport(Library, EntryPoint = "mpv_render_context_render")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int Render(nint context, nint parameters);

    [LibraryImport(Library, EntryPoint = "mpv_render_context_set_update_callback")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void RenderCallback(nint context, nint callback, nint state);

    [LibraryImport(Library, EntryPoint = "mpv_render_context_update")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial ulong RenderUpdate(nint context);

    [LibraryImport(Library, EntryPoint = "mpv_render_context_free")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void RenderFree(nint context);
}

// mpv_render_param 类型：Kind=mpv_render_param_type（1=API_TYPE 2=OPENGL_INIT_PARAMS
// 3=OPENGL_FBO 4=FLIP_Y 12=BLOCK_FOR_TARGET_TIME），Data=负载指针。
[StructLayout(LayoutKind.Sequential)]
internal struct RenderParameter { public int Kind; public nint Data; }
[StructLayout(LayoutKind.Sequential)]
internal struct GlInitParameters { public nint GetProcAddress; public nint State; }
[StructLayout(LayoutKind.Sequential)]
internal struct GlFramebuffer { public int Id; public int Width; public int Height; public int Format; }
