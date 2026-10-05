using System.Runtime.InteropServices;

namespace VodBox.Playback.Mpv;

public readonly record struct MpvEvent(int Id, int Error, ulong ReplyUserData,
    int? EndReason = null, int? EndError = null, long? PlaylistEntryId = null, string? LogText = null,
    string? PropertyName = null, double? PropertyDouble = null, bool? PropertyFlag = null);

public enum MpvPropertyFormat { None = 0, Flag = 3, Double = 5 }
public interface IMpvClient : IDisposable
{
    void Command(params string[] arguments);
    double? GetDouble(string property);
    string? GetString(string property);
    void Observe(string property, ulong id, MpvPropertyFormat format);
    MpvEvent PollEvent();
}

/// <summary>Low-level, serialized client API. Run calls on a worker, never the GUI/render thread.</summary>
public sealed class MpvClient : IMpvClient
{
    private readonly object _gate = new();
    private readonly MpvHandle _handle;

    // Options are applied before initialization. The default does not load user config or scripts.
    public MpvClient(IReadOnlyDictionary<string, string>? options = null)
    {
        _handle = MpvNative.Create();
        if (_handle.IsInvalid) { _handle.Dispose(); throw new InvalidOperationException("mpv_create returned a null handle."); }
        try
        {
            SetOption("config", "no");
            SetOption("load-scripts", "no", optional: true);
            SetOption("ytdl", "no", optional: true);
            SetOption("terminal", "no");
            SetOption("idle", "yes");
            if (options is not null) foreach (var (name, value) in options) SetOption(name, value);
            MpvNative.Check(MpvNative.Initialize(_handle));
        }
        catch { _handle.Dispose(); throw; }
    }

    private void SetOption(string name, string value, bool optional = false)
    {
        Validate(name); Validate(value);
        var result = MpvNative.Option(_handle, name, value);
        // These options are absent when mpv was built without the corresponding scripting feature.
        if (optional && result == -5 /* option not found */) return;
        MpvNative.Check(result);
    }

    // Null-terminated UTF-8 argv avoids command-string quoting and URL/path interpretation bugs.
    public unsafe void Command(params string[] arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Length is < 1 or > 64) throw new ArgumentException("mpv commands require 1–64 arguments.", nameof(arguments));
        foreach (var argument in arguments) Validate(argument);
        lock (_gate)
        {
            EnsureAlive();
            var argv = stackalloc nint[arguments.Length + 1];
            new Span<nint>(argv, arguments.Length + 1).Clear();
            try
            {
                for (var i = 0; i < arguments.Length; i++) argv[i] = Marshal.StringToCoTaskMemUTF8(arguments[i]);
                MpvNative.Check(MpvNative.Command(_handle, (nint)argv));
            }
            finally { for (var i = 0; i < arguments.Length; i++) if (argv[i] != 0) Marshal.FreeCoTaskMem(argv[i]); }
        }
    }

    public double? GetDouble(string property)
    {
        Validate(property);
        lock (_gate)
        {
            EnsureAlive();
            var error = MpvNative.GetDouble(_handle, property, 5 /* MPV_FORMAT_DOUBLE */, out var value);
            if (error == -10 /* unavailable */) return null;
            MpvNative.Check(error);
            return value;
        }
    }

    public string? GetString(string property)
    {
        Validate(property);
        lock (_gate)
        {
            EnsureAlive(); var pointer = MpvNative.GetString(_handle, property);
            if (pointer == 0) return null;
            try { return Marshal.PtrToStringUTF8(pointer); }
            finally { MpvNative.Free(pointer); }
        }
    }
    public void Observe(string property, ulong id, MpvPropertyFormat format)
    {
        Validate(property);
        if (format is not (MpvPropertyFormat.None or MpvPropertyFormat.Flag or MpvPropertyFormat.Double)) throw new ArgumentOutOfRangeException(nameof(format));
        lock (_gate) { EnsureAlive(); MpvNative.Check(MpvNative.Observe(_handle, id, property, (int)format)); }
    }

    // Copy primitive fields before the next wait; native event payload ownership stays with mpv.
    public unsafe MpvEvent PollEvent()
    {
        lock (_gate)
        {
            EnsureAlive();
            var pointer = MpvNative.WaitEvent(_handle, 0);
            if (pointer == 0) throw new InvalidOperationException("mpv_wait_event returned null.");
            var value = *(NativeEvent*)pointer;
            if (value.Id == 22 && value.Data != 0)
            {
                var property = *(NativeProperty*)value.Data;
                return new(value.Id, value.Error, value.ReplyUserData, PropertyName: Marshal.PtrToStringUTF8(property.Name),
                    PropertyDouble: property.Format == 5 && property.Data != 0 ? *(double*)property.Data : null,
                    PropertyFlag: property.Format == 3 && property.Data != 0 ? *(int*)property.Data != 0 : null);
            }
            if (value.Id == 7 && value.Data != 0)
            {
                var end = *(NativeEndFile*)value.Data;
                return new(value.Id, value.Error, value.ReplyUserData, end.Reason, end.Error, end.PlaylistEntryId);
            }
            if (value.Id == 2 && value.Data != 0)
            {
                var log = *(NativeLogMessage*)value.Data;
                return new(value.Id, value.Error, value.ReplyUserData, LogText: Marshal.PtrToStringUTF8(log.Text));
            }
            return new(value.Id, value.Error, value.ReplyUserData);
        }
    }

    public void RequestLogs(string level = "warn")
    {
        if (level is not ("no" or "fatal" or "error" or "warn" or "info" or "v" or "debug" or "trace")) throw new ArgumentException("Unknown mpv log level.", nameof(level));
        lock (_gate) { EnsureAlive(); MpvNative.Check(MpvNative.RequestLogs(_handle, level)); }
    }

    private static void Validate(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Contains('\0')) throw new ArgumentException("mpv strings cannot contain NUL.");
    }
    private void EnsureAlive() => ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
    internal MpvHandle AcquireRenderHandle()
    {
        lock (_gate)
        {
            EnsureAlive();
            var added = false;
            _handle.DangerousAddRef(ref added);
            return _handle;
        }
    }
    public void Dispose()
    {
        lock (_gate) _handle.Dispose();
        GC.SuppressFinalize(this);
    }
}
