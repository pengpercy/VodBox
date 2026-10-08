using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace VodBox.PluginHost;

/// <summary>
/// QuickJS 引擎宿主：ES modules + 异步宿主函数 + drpy 依赖库解析。
///
/// 生命周期约束（重要）：回调（Host/HostAsync/ModuleLoader）在引擎线程上同步触发，
/// 异步操作完成后必须用 <see cref="Complete"/> 回填——它会切回引擎锁内调用
/// vb_resolve 并泵微任务，保证 JS 线程安全（QuickJS 非thread-safe，全部经 _gate 串行）。
/// </summary>
public sealed class QuickJsEngine : IDisposable
{
    private nint _vm;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<int, PendingCall> _pending = new();
    private readonly BlockingCollection<EngineCommand> _commands = new(new ConcurrentQueue<EngineCommand>());
    private readonly Thread _engineThread;
    private readonly SemaphoreSlim _evaluations = new(1);
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationToken _activeToken;
    private volatile bool _disposed;
    private Exception? _hostError;

    private sealed record PendingCall(string Op);
    private sealed record EngineCommand(string Kind, string? Script, int Id, string? Result, bool Failed,
        TaskCompletionSource<string> Tcs, CancellationToken Token = default);

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
        try { init.Task.GetAwaiter().GetResult(); }
        catch { Dispose(); throw; }
    }

    private void EngineLoop()
    {
        foreach (var cmd in _commands.GetConsumingEnumerable())
        {
            try
            {
                _activeToken = cmd.Token;
                if (cmd.Kind is "EVAL" or "PUMP") cmd.Token.ThrowIfCancellationRequested();
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
                            delegate* unmanaged[Cdecl]<nint, nint, int, int> hostAsync = &HostAsync;
                            QuickJsNative.SetHost(_vm, (nint)host, (nint)free);
                            QuickJsNative.SetModuleLoader(_vm, (nint)loader, (nint)free);
                            QuickJsNative.SetHostAsync(_vm, (nint)hostAsync);
                            delegate* unmanaged[Cdecl]<int> interrupt = &IsCancelled;
                            QuickJsNative.SetInterrupt(_vm, (nint)interrupt);
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
                                if (cmd.Token.IsCancellationRequested) cmd.Tcs.TrySetCanceled(cmd.Token);
                                else if (error != 0) cmd.Tcs.TrySetException(new InvalidOperationException(value));
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
                        cmd.Tcs.TrySetResult("");
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
                cmd.Tcs.TrySetException(error);
            }
        }
    }

    /// <summary>注册模块源（ES import 解析用；线程安全）。</summary>
    public void AddModule(string name, string source)
    {
        lock (_gate) _modules[name] = source;
    }

    /// <summary>同步 eval 同样通过引擎队列，不能在回调内重入。</summary>
    public string Evaluate(string script) => EvaluateAsync(script).GetAwaiter().GetResult();

    /// <summary>完整 eval/宿主/微任务链串行；取消同时传到 HTTP、定时器和原生中断。</summary>
    public async Task<string> EvaluateAsync(string script, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        using var scope = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        scope.CancelAfter(TimeSpan.FromSeconds(25));
        await _evaluations.WaitAsync(scope.Token).ConfigureAwait(false);
        try
        {
            _hostError = null;
            var result = await SendAsync("EVAL", script, scope.Token).ConfigureAwait(false);
            while (true)
            {
                scope.Token.ThrowIfCancellationRequested();
                var pumped = await SendAsync("PUMP", null, scope.Token).ConfigureAwait(false);
                if (_hostError is { } hostError)
                    throw new InvalidOperationException("QuickJS 宿主操作失败：" + hostError.Message, hostError);
                if (pumped == "-1") throw new InvalidOperationException("QuickJS 微任务执行失败。");
                if (pumped == "0" && _pending.IsEmpty) return result;
                if (pumped == "0") await Task.Delay(5, scope.Token).ConfigureAwait(false);
            }
        }
        catch
        {
            // 中途退出不能将旧 Promise/全局状态带到下一次调用。
            Dispose();
            throw;
        }
        finally { _evaluations.Release(); }
    }

    private Task<string> SendAsync(string kind, string? script, CancellationToken token)
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            ThrowIfDisposed();
            _commands.Add(new EngineCommand(kind, script, 0, null, false, tcs, token));
        }
        return tcs.Task;
    }

    private void Complete(int requestId, string result, bool isError, CancellationToken token)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _commands.Add(new EngineCommand("RESOLVE", null, requestId, result, isError,
                new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously), token));
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int IsCancelled() => Current is { } engine &&
        (engine._activeToken.IsCancellationRequested || engine._lifetime.IsCancellationRequested) ? 1 : 0;

    // ---------- 原生回调（引擎线程，持锁状态） ----------

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint Host(nint operation, nint payload)
    {
        string op = "";
        try
        {
            (op, var value) = ReadArgs(operation, payload);
            var result = op == "req"
                ? HostOperationAsync(Current!, op, value, Current!._activeToken).GetAwaiter().GetResult()
                : HostOperation(op, value);
            return Marshal.StringToCoTaskMemUTF8(result);
        }
        catch (Exception error)
        {
            // drpy 的 request() 会捕获 JS 异常并返回空串；保留宿主错误，避免伪成功。
            if (Current is { } engine) engine._hostError ??= error;
            Console.Error.WriteLine($"[engine] host op '{op}' failed: {error.GetType().Name}: {error.Message}");
            return 0; // NULL → JS InternalError
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int HostAsync(nint operation, nint payload, int requestId)
    {
        try
        {
            var (op, value) = ReadArgs(operation, payload);
            // 引擎实例查找：bridge 没传 VM 指针到回调（简化：全局单例表）
            var engine = Current;
            if (engine is null) return 1;
            int id = (int)requestId;
            var token = engine._activeToken;
            engine._pending[id] = new PendingCall(op);
            _ = Task.Run(async () =>
            {
                string result;
                bool failed = false;
                try { result = await HostOperationAsync(engine, op, value, token).ConfigureAwait(false); }
                catch (Exception error) { result = error.Message; failed = true; }
                // vb_resolve 必须在引擎线程执行（实测后台线程会静默丢微任务并毒化 ctx）
                engine.Complete(id, result, failed, token);
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
        "joinUrl" => JoinUrl(payload),
        _ => throw new NotSupportedException($"未知的同步宿主操作：{op}")
    };

    /// <summary>drpy req 是同步契约；HTTP 本身异步，不回引擎队列。</summary>
    private static async Task<string> HostOperationAsync(QuickJsEngine engine, string op, string payload, CancellationToken token)
    {
        if (op == "setTimeout")
        {
            var delay = int.TryParse(payload, out var ms) ? Math.Max(0, ms) : 0;
            await Task.Delay(delay, token).ConfigureAwait(false);
            return "";
        }
        if (op is not ("req" or "request")) throw new NotSupportedException($"未知宿主操作：{op}");
        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;
        var options = op == "req" && root.TryGetProperty("obj", out var obj) ? obj : root;
        string Text(string key, string fallback = "") => options.ValueKind == JsonValueKind.Object && options.TryGetProperty(key, out var value) ? value.ToString() : fallback;
        var method = Text("method", "GET");
        using var request = new HttpRequestMessage(new HttpMethod(method), root.GetProperty("url").GetString());
        var body = Text("body");
        if (options.ValueKind == JsonValueKind.Object && options.TryGetProperty("data", out var data) && data.ValueKind != JsonValueKind.Null)
        {
            if (data.ValueKind != JsonValueKind.Object) throw new InvalidDataException("drpy req.data 不是对象。");
            request.Content = new FormUrlEncodedContent(data.EnumerateObject().Select(p =>
                new KeyValuePair<string, string>(p.Name, p.Value.ValueKind == JsonValueKind.Null ? "" : p.Value.ToString())));
        }
        else if (body.Length > 0 || method.Equals("POST", StringComparison.OrdinalIgnoreCase))
            request.Content = new StringContent(body);
        if (options.ValueKind == JsonValueKind.Object && options.TryGetProperty("headers", out var h) && h.ValueKind == JsonValueKind.Object)
        {
            // 大小写重复的 UA（真实兔小贝 lazy）取最后值，避免发两份。
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var header in h.EnumerateObject()) headers[header.Name] = header.Value.ToString();
            foreach (var header in headers)
            {
                if (request.Headers.TryAddWithoutValidation(header.Key, header.Value)) continue;
                request.Content ??= new StringContent("");
                request.Content.Headers.Remove(header.Key);
                request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }
        var timeoutMs = int.TryParse(Text(op == "req" ? "timeout" : "timeoutMs"), out var timeout) ? Math.Clamp(timeout, 1, 30_000) : 15_000;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token, engine._lifetime.Token);
        cts.CancelAfter(timeoutMs);
        var client = Text("redirect", "1") == "0" ? NoRedirectHttp : Http;
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
        if (response.Content.Headers.ContentLength > 8 * 1024 * 1024) throw new InvalidDataException("drpy HTTP 响应超过 8MiB。");
        await using var input = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await input.ReadAsync(buffer, cts.Token).ConfigureAwait(false)) > 0)
        {
            if (output.Length + read > 8 * 1024 * 1024) throw new InvalidDataException("drpy HTTP 响应超过 8MiB。");
            output.Write(buffer, 0, read);
        }
        var bytes = output.ToArray();
        var charset = response.Content.Headers.ContentType?.CharSet?.Trim('"');
        var encoding = string.IsNullOrWhiteSpace(charset) ? System.Text.Encoding.UTF8 :
            System.Text.CodePagesEncodingProvider.Instance.GetEncoding(charset) ?? System.Text.Encoding.GetEncoding(charset);
        var content = Text("buffer") == "2" ? Convert.ToBase64String(bytes) : encoding.GetString(bytes);
        using var json = new MemoryStream();
        using (var writer = new Utf8JsonWriter(json))
        {
            writer.WriteStartObject();
            writer.WriteString(op == "req" ? "content" : "body", content);
            writer.WriteNumber("status", (int)response.StatusCode);
            writer.WriteStartObject("headers");
            foreach (var header in response.Headers.Concat(response.Content.Headers))
                writer.WriteString(header.Key.ToLowerInvariant(), string.Join(", ", header.Value));
            writer.WriteNumber("status", (int)response.StatusCode);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(json.ToArray());
    }

    private static HttpClient CreateHttp(bool redirect) => new(new SocketsHttpHandler
    {
        UseCookies = false, AllowAutoRedirect = redirect,
        AutomaticDecompression = System.Net.DecompressionMethods.All,
    }) { Timeout = Timeout.InfiniteTimeSpan };
    private static readonly HttpClient Http = CreateHttp(true);
    private static readonly HttpClient NoRedirectHttp = CreateHttp(false);

    private static string JoinUrl(string payload)
    {
        using var doc = JsonDocument.Parse(payload);
        return new Uri(new Uri(doc.RootElement.GetProperty("base").GetString()!, UriKind.Absolute),
            doc.RootElement.GetProperty("rel").GetString()!).AbsoluteUri;
    }

    private static string Log(string message)
    {
        Console.Error.WriteLine($"[drpy] {message}");
        return "";
    }

    /// <summary>当前引擎（回调无 VM 指针传递，用 [ThreadStatic] 桥接；eval 前设置，回调线程=eval线程）。</summary>
    [ThreadStatic] private static QuickJsEngine? Current;

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
        Task<string> quit;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _lifetime.Cancel();
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _commands.Add(new EngineCommand("QUIT", null, 0, null, false, tcs));
            _commands.CompleteAdding();
            quit = tcs.Task;
        }
        // 中断回调/HTTP 取消保证运行中的命令退出，再由引擎线程释放 VM。
        if (Thread.CurrentThread != _engineThread)
        {
            quit.GetAwaiter().GetResult();
            _engineThread.Join();
        }
        _pending.Clear();
    }
}
