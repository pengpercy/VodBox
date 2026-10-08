using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace VodBox.PluginHost;

/// <summary>
/// QuickJS 引擎宿主：ES modules + 异步宿主函数 + drpy 依赖库解析。
///
/// 生命周期约束（重要）：回调（Host/HostAsync/ModuleLoader）在引擎线程上同步触发，
/// 异步操作完成后必须用 <see cref="CompleteAsync"/> 回填——它会切回引擎锁内调用
/// vb_resolve 并泵微任务，保证 JS 线程安全（QuickJS 非thread-safe，全部经 _gate 串行）。
/// </summary>
public sealed class QuickJsEngine : IDisposable
{
    private nint _vm;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<int, PendingCall> _pending = new();
    private readonly ConcurrentQueue<(int Id, string Result, bool Failed)> _completions = new();
    private readonly BlockingCollection<EngineCommand> _commands = new(new ConcurrentQueue<EngineCommand>());
    private readonly Thread _engineThread;
    private readonly AutoResetEvent _idle = new(false);
    private volatile bool _disposed;
    private string? _lastError;

    private sealed record PendingCall(string Op, TaskCompletionSource<string> Tcs);
    private sealed record EngineCommand(string Kind, string? Script, int Id, string? Result, bool Failed,
        TaskCompletionSource<string> Tcs);

    /// <summary>模块表：模块名 → 源码（assets:// 虚拟协议 + 随包依赖库）。</summary>
    private readonly Dictionary<string, string> _modules = new(StringComparer.Ordinal);

    /// <summary>
    /// 引擎线程：所有 vb_* 原生调用都在此线程（QuickJS 微任务链有线程关联，
    /// 后台线程 resolve 会静默丢微任务 + 毒化 ctx——实测结论）。
    /// </summary>
    public QuickJsEngine()
    {
        _engineThread = new Thread(EngineLoop) { IsBackground = true, Name = "VodBox-QuickJS" };
        _engineThread.Start();
        // 等引擎初始化完成
        var init = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _commands.Add(new EngineCommand("INIT", null, 0, null, false, init));
        init.Task.Wait(TimeSpan.FromSeconds(10));
        if (init.Task.IsFaulted || _vm == 0)
            throw new InvalidOperationException("QuickJS 初始化失败。" + (_lastError ?? ""));
    }

    private void EngineLoop()
    {
        foreach (var cmd in _commands.GetConsumingEnumerable())
        {
            try
            {
                switch (cmd.Kind)
                {
                    case "INIT":
                        _vm = QuickJsNative.Create();
                        if (_vm == 0) { cmd.Tcs.TrySetException(new InvalidOperationException("vb_create 失败")); break; }
                        unsafe
                        {
                            delegate* unmanaged[Cdecl]<nint, nint, nint> host = &Host;
                            delegate* unmanaged[Cdecl]<nint, void> free = &FreeHost;
                            delegate* unmanaged[Cdecl]<nint, nint> loader = &ModuleLoader;
                            delegate* unmanaged[Cdecl]<nint, nint, nint, int> hostAsync = &HostAsync;
                            QuickJsNative.SetHost(_vm, (nint)host, (nint)free);
                            QuickJsNative.SetModuleLoader(_vm, (nint)loader, (nint)free);
                            QuickJsNative.SetHostAsync(_vm, (nint)hostAsync);
                        }
                        Current = this;
                        try
                        {
                            nint result = QuickJsNative.Evaluate(_vm, BridgeScript, out var error);
                            var value = Marshal.PtrToStringUTF8(result) ?? "";
                            QuickJsNative.FreeString(result);
                            if (error != 0) { cmd.Tcs.TrySetException(new InvalidOperationException(value)); break; }
                        }
                        finally { Current = null; }
                        cmd.Tcs.SetResult("");
                        break;
                    case "EVAL":
                        if (_vm == 0) { cmd.Tcs.TrySetException(new ObjectDisposedException(nameof(QuickJsEngine))); break; }
                        Current = this;
                        try
                        {
                            nint result = QuickJsNative.Evaluate(_vm, cmd.Script!, out var error);
                            string value;
                            try
                            {
                                value = Marshal.PtrToStringUTF8(result) ?? "";
                                if (error != 0) cmd.Tcs.TrySetException(new InvalidOperationException(value));
                                else cmd.Tcs.SetResult(value);
                            }
                            finally { QuickJsNative.FreeString(result); }
                        }
                        finally { Current = null; }
                        break;
                    case "RESOLVE":
                        if (_vm != 0)
                        {
                            _pending.TryRemove(cmd.Id, out _);
                            Current = this;
                            try
                            {
                                QuickJsNative.Resolve(_vm, cmd.Id, cmd.Result ?? "", cmd.Failed ? 1 : 0);
                                for (var i = 0; i < 200; i++)
                                    if (QuickJsNative.Pump(_vm) == 0) break;
                            }
                            finally { Current = null; }
                        }
                        _idle.Set(); // 通知等待方有状态变化
                        break;
                    case "PUMP":
                        int pumped = 0;
                        if (_vm != 0)
                        {
                            Current = this;
                            try
                            {
                                for (var i = 0; i < 200; i++)
                                {
                                    var status = QuickJsNative.Pump(_vm);
                                    if (status == 0) { pumped = 0; break; }
                                    if (status < 0) { pumped = -1; break; }
                                    pumped = 1;
                                }
                            }
                            finally { Current = null; }
                        }
                        cmd.Tcs.SetResult(pumped.ToString());
                        break;
                    case "QUIT":
                        if (_vm != 0) { QuickJsNative.Destroy(_vm); _vm = 0; }
                        cmd.Tcs.SetResult("");
                        return;
                }
            }
            catch (Exception error)
            {
                _lastError = error.Message;
                cmd.Tcs.TrySetException(error);
            }
        }
    }

    /// <summary>注册模块源（ES import 解析用；线程安全）。</summary>
    public void AddModule(string name, string source)
    {
        lock (_gate) _modules[name] = source;
    }

    /// <summary>同步 eval（投递到引擎线程执行）。</summary>
    public string Evaluate(string script)
    {
        ThrowIfDisposed();
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _commands.Add(new EngineCommand("EVAL", script, 0, null, false, tcs));
        return tcs.Task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// 异步 eval：eval 后循环（投递回填 → 泵）直到 pending 空且微任务队列收敛。
    /// 全部原生调用经引擎线程，调用方线程可任意（await 安全）。
    /// </summary>
    public async Task<string> EvaluateAsync(string script, CancellationToken ct = default)
    {
        var result = await Task.Run(() => Evaluate(script), ct).ConfigureAwait(false);
        var idleRounds = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var pumpTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _commands.Add(new EngineCommand("PUMP", null, 0, null, false, pumpTcs));
            int pumped = int.Parse(await pumpTcs.Task.ConfigureAwait(false));
            if (pumped < 0) throw new InvalidOperationException("QuickJS 微任务执行失败（见 stderr）。");
            if (pumped > 0) { idleRounds = 0; continue; }
            if (_pending.IsEmpty && _completions.IsEmpty) break;
            if (++idleRounds > 2000) throw new TimeoutException("QuickJS 异步宿主操作超时（约 10s 无进展）。");
            // 等引擎线程回填（RESOLVE 触发 _idle），最多 5ms 轮询兜底
            await Task.WhenAny(Task.Delay(5, ct), Task.Run(() => _idle.WaitOne(50), ct)).ConfigureAwait(false);
        }
        return result;
    }

    /// <summary>异步宿主操作完成回填（投递到引擎线程）。</summary>
    public void Complete(int requestId, string result, bool isError = false)
    {
        _commands.Add(new EngineCommand("RESOLVE", null, requestId, result, isError, new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously)));
    }


    // ---------- 原生回调（引擎线程，持锁状态） ----------

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint Host(nint operation, nint payload)
    {
        try
        {
            var (op, value) = ReadArgs(operation, payload);
            var result = HostOperation(op, value);
            return Marshal.StringToCoTaskMemUTF8(result);
        }
        catch (Exception)
        {
            return 0; // NULL → JS InternalError
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int HostAsync(nint operation, nint payload, nint requestId)
    {
        try
        {
            var (op, value) = ReadArgs(operation, payload);
            // 引擎实例查找：bridge 没传 VM 指针到回调（简化：全局单例表）
            var engine = Current;
            if (engine is null) return 1;
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            int id = (int)requestId;
            engine._pending[id] = new PendingCall(op, tcs);
            _ = Task.Run(async () =>
            {
                string result;
                bool failed = false;
                try { result = await HostOperationAsync(engine, op, value); }
                catch (Exception error) { result = error.Message; failed = true; }
                // vb_resolve 必须在引擎线程执行（实测后台线程会静默丢微任务并毒化 ctx）
                engine.Complete(id, result, failed);
            });
            return 0; // 受理
        }
        catch (Exception)
        {
            return 1;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint ModuleLoader(nint moduleName)
    {
        try
        {
            var engine = Current;
            var name = Marshal.PtrToStringUTF8(moduleName) ?? "";
            if (engine is null) return 0;
            lock (engine._gate)
            {
                if (engine._modules.TryGetValue(name, out var source))
                    return Marshal.StringToCoTaskMemUTF8(source);
            }
            return 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void FreeHost(nint value) => Marshal.FreeCoTaskMem(value);

    private static (string Op, string Payload) ReadArgs(nint operation, nint payload) =>
        (Marshal.PtrToStringUTF8(operation) ?? "", Marshal.PtrToStringUTF8(payload) ?? "");

    // ---------- 宿主操作实现（drpy 约定） ----------

    /// <summary>同步操作（log / md5 / base64 等纯计算）。</summary>
    internal static string HostOperation(string op, string payload) => op switch
    {
        "log" => Log(payload),
        "md5" => Convert.ToHexString(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(payload))).ToLowerInvariant(),
        "sha256" => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(payload))).ToLowerInvariant(),
        "base64.encode" => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(payload)),
        "base64.decode" => System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(payload)),
        _ => throw new NotSupportedException($"未知的同步宿主操作：{op}")
    };

    /// <summary>异步操作（request / setTimeout 等 IO/定时类）。</summary>
    private static async Task<string> HostOperationAsync(QuickJsEngine engine, string op, string payload)
    {
        switch (op)
        {
            case "request":
                // payload: {url, method, headers, body, timeoutMs}
                using (var doc = JsonDocument.Parse(payload))
                {
                    var root = doc.RootElement;
                    string url = root.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                    string method = root.TryGetProperty("method", out var m) && m.GetString() is { Length: > 0 } mm ? mm : "GET";
                    var request = new HttpRequestMessage(new HttpMethod(method), url);
                    if (root.TryGetProperty("headers", out var h) && h.ValueKind == JsonValueKind.Object)
                        foreach (var header in h.EnumerateObject())
                            request.Headers.TryAddWithoutValidation(header.Name, header.Value.GetString() ?? "");
                    if (root.TryGetProperty("body", out var b) && b.ValueKind is JsonValueKind.String)
                        request.Content = new StringContent(b.GetString() ?? "");
                    int timeoutMs = root.TryGetProperty("timeoutMs", out var t) && t.TryGetInt32(out var ms) ? ms : 15_000;
                    using var cts = new CancellationTokenSource(timeoutMs);
                    using var response = await _http.SendAsync(request, cts.Token);
                    var body = await response.Content.ReadAsStringAsync(cts.Token);
                    // 应答 JSON：{status, headers, body}
                    return JsonSerializer.Serialize(new Dictionary<string, object>
                    {
                        ["status"] = (int)response.StatusCode,
                        ["body"] = body,
                    }, VodBox.Core.Json.Options);
                }
            case "setTimeout":
                var delay = int.TryParse(payload, out var ms2) ? ms2 : 0;
                await Task.Delay(delay);
                return "";
            default:
                throw new NotSupportedException($"未知的异步宿主操作：{op}");
        }
    }

    private static readonly HttpClient _http = new(new SocketsHttpHandler
    {
        UseCookies = false,
        AutomaticDecompression = System.Net.DecompressionMethods.All,
    });

    private static string Log(string message)
    {
        Console.Error.WriteLine($"[drpy] {message}");
        return "";
    }

    /// <summary>当前引擎（回调无 VM 指针传递，用 [ThreadStatic] 桥接；eval 前设置，回调线程=eval线程）。</summary>
    [ThreadStatic] private static QuickJsEngine? Current;

    /// <summary>在 Current 上下文内执行 eval（回调据此找到引擎实例）。</summary>
    private string EvaluateWithCurrent(string script)
    {
        Current = this;
        try { return Evaluate(script); }
        finally { if (ReferenceEquals(Current, this)) Current = null; }
    }

    /// <summary>预热桥脚本：把 __hostapi 原语包装成 drpy 需要的全局。</summary>
    internal const string BridgeScript = """
        globalThis.__resolves = globalThis.__resolves || {};
        globalThis.console = { log: (...a) => __hostapi.__host('log', a.join(' ')), error: (...a) => __hostapi.__host('log', a.join(' ')) };
        globalThis.setTimeout = (fn, ms) => {
          __hostapi.__host_async('setTimeout', String(ms || 0)).then(() => fn());
          return 0;
        };
        """;

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        try { _commands.Add(new EngineCommand("QUIT", null, 0, null, false, tcs)); tcs.Task.Wait(TimeSpan.FromSeconds(2)); }
        catch { /* 引擎线程可能已退出 */ }
        _commands.CompleteAdding();
    }
}
