using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace VodBox.Playback.Mpv;

/// <summary>mpv OpenGL 渲染上下文。所有操作（含 Dispose）都必须在原 OpenGL 上下文 current 时调用。
/// 渲染线程绝不可调用 client 的命令/属性 API。</summary>
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

    /// <summary>适合 UI 定时器轮询：回调只置位，绝不进 dispatcher 或 mpv API。</summary>
    public bool HasUpdate => Volatile.Read(ref _state.Dirty) != 0;

    public void Render(int framebuffer, int pixelWidth, int pixelHeight)
    {
        ObjectDisposedException.ThrowIf(_context == 0, this);
        if (pixelWidth <= 0 || pixelHeight <= 0) return;
        Interlocked.Exchange(ref _state.Dirty, 0);
        MpvNative.RenderUpdate(_context);
        var target = new GlFramebuffer { Id = framebuffer, Width = pixelWidth, Height = pixelHeight };
        var flip = 1;
        var block = 0; // 由 Avalonia 调度呈现，不为 mpv 时序阻塞其渲染线程。
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
        catch { return 0; } // 托管异常绝不可越过 C 回调边界。
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
