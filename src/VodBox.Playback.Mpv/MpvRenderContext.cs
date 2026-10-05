using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace VodBox.Playback.Mpv;

/// <summary>All operations, including Dispose, require the original OpenGL context to be current.
/// Never call the client command/property API from the render thread.</summary>
public sealed unsafe class MpvRenderContext : IDisposable
{
    private sealed class CallbackState(Func<string, nint> getProcAddress)
    {
        public readonly Func<string, nint> GetProcAddress = getProcAddress;
        public int Dirty = 1;
    }
    private readonly MpvHandle _client;
    private readonly CallbackState _state;
    private GCHandle _root;
    private nint _context;

    public MpvRenderContext(MpvClient client, Func<string, nint> getProcAddress)
    {
        ArgumentNullException.ThrowIfNull(client); ArgumentNullException.ThrowIfNull(getProcAddress);
        _state = new(getProcAddress);
        _client = client.AcquireRenderHandle();
        _root = GCHandle.Alloc(_state);
        try
        {
            var init = new GlInitParameters
            {
                GetProcAddress = (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint>)&GetProc,
                State = GCHandle.ToIntPtr(_root)
            };
            byte* api = stackalloc byte[] { (byte)'o', (byte)'p', (byte)'e', (byte)'n', (byte)'g', (byte)'l', 0 };
            var parameters = stackalloc RenderParameter[] { new() { Kind = 1, Data = (nint)api }, new() { Kind = 2, Data = (nint)(&init) }, default };
            MpvNative.Check(MpvNative.RenderCreate(out _context, _client.DangerousGetHandle(), (nint)parameters));
            MpvNative.RenderCallback(_context, (nint)(delegate* unmanaged[Cdecl]<nint, void>)&Updated, GCHandle.ToIntPtr(_root));
        }
        catch
        {
            if (_context != 0) MpvNative.RenderFree(_context);
            _root.Free(); _client.DangerousRelease(); throw;
        }
    }

    // Safe for a UI timer: callbacks only set this flag and never enter the dispatcher or mpv API.
    public bool HasUpdate => Volatile.Read(ref _state.Dirty) != 0;
    public void Render(int framebuffer, int pixelWidth, int pixelHeight)
    {
        ObjectDisposedException.ThrowIf(_context == 0, this);
        if (pixelWidth <= 0 || pixelHeight <= 0) return;
        Interlocked.Exchange(ref _state.Dirty, 0);
        MpvNative.RenderUpdate(_context);
        var target = new GlFramebuffer { Id = framebuffer, Width = pixelWidth, Height = pixelHeight };
        var flip = 1;
        var block = 0; // Avalonia schedules presentation; do not block its render thread for mpv timing.
        var parameters = stackalloc RenderParameter[]
        {
            new() { Kind = 3, Data = (nint)(&target) }, new() { Kind = 4, Data = (nint)(&flip) },
            new() { Kind = 12, Data = (nint)(&block) }, default
        };
        MpvNative.Check(MpvNative.Render(_context, (nint)parameters));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint GetProc(nint state, nint name)
    {
        try { return ((CallbackState)GCHandle.FromIntPtr(state).Target!).GetProcAddress(Marshal.PtrToStringUTF8(name)!); }
        catch { return 0; } // Managed exceptions must never cross the C callback boundary.
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Updated(nint state)
    {
        var callback = (CallbackState)GCHandle.FromIntPtr(state).Target!;
        Interlocked.Exchange(ref callback.Dirty, 1);
    }
    public void Dispose()
    {
        if (_context == 0) return;
        MpvNative.RenderCallback(_context, 0, 0);
        MpvNative.RenderFree(_context); _context = 0;
        _root.Free(); _client.DangerousRelease();
        GC.SuppressFinalize(this);
    }
}
