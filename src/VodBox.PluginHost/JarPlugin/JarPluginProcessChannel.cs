using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace VodBox.PluginHost.JarPlugin;

/// <summary>
/// 真实通道：把宿主当作外部进程运行，通过 request.json + stdin 命令 + responses/*.json 通信。
///
/// 关键约束（与生命周期绑定）：
/// - 插件向 stdout 打印不会污染协议——命令响应走文件，stdout/stderr 只作诊断；
/// - 任何超时/取消都必须杀掉进程：Java 线程无法从外部安全回收，留着的进程等于泄漏；
/// - 响应/就绪文件由宿主先写 .tmp 再原子改名，因此轮询只会在文件完整后读到。
/// </summary>
public sealed class JarPluginProcessChannel : IJarPluginHostChannel
{
    private readonly JarPluginHostSettings _settings;
    private readonly JarPluginSessionDescriptor _descriptor;
    private readonly string _cacheDirectory;
    private readonly TimeSpan _pollInterval = TimeSpan.FromMilliseconds(50);
    private Process? _process;
    private long _commands;

    public JarPluginProcessChannel(JarPluginSessionDescriptor descriptor, JarPluginHostSettings settings)
    {
        _descriptor = descriptor;
        _settings = settings;
        _cacheDirectory = descriptor.WorkingDirectory;
    }

    public bool IsRunning => _process is { HasExited: false };

    public async Task<JarPluginEnvelope> StartAsync(CancellationToken ct)
    {
        if (_process is not null) return new JarPluginEnvelope { Ready = true };
        // Upstream artifact storage bypasses legacy preflight: enforce the native RID boundary here.
        if (_settings.UsesUpstreamRuntime && !NativeBackendCapability.IsSupported()
            && JarPluginNativeLibraryScanner.Scan(_descriptor.JarPath).Libraries.Count > 0)
            throw new JarPluginException(JarPluginErrors.UnsupportedNativeLibrary, JarPluginFailureKind.Unsupported,
                NativeBackendCapability.UnsupportedReason());
        Directory.CreateDirectory(_cacheDirectory);
        Directory.CreateDirectory(Path.Combine(_cacheDirectory, JarPluginProtocol.ResponsesDirectory));
        var request = new JarPluginLaunchRequest
        {
            Jar = _descriptor.JarPath,
            Cache = _cacheDirectory,
            Profile = Path.Combine(_cacheDirectory, "profile"),
            Api = _descriptor.Layout.Api,
            Key = _descriptor.Layout.SourceKey,
            Ext = _descriptor.Layout.Ext,
            Spider = _descriptor.Layout.SpiderClass,
            GlobalSpider = _descriptor.Layout.GlobalSpider?.Raw ?? "",
            Origin = _descriptor.Layout.Origin ?? "",
            Site = _descriptor.Layout.SiteJson,
        };
        var requestPath = Path.Combine(_cacheDirectory, "request.json");
        await File.WriteAllTextAsync(requestPath, JarPluginJson.Serialize(request), Encoding.UTF8, ct).ConfigureAwait(false);

        var startInfo = new ProcessStartInfo
        {
            FileName = _settings.JavaExecutable,
            WorkingDirectory = _cacheDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in _settings.BuildJvmArguments()) startInfo.ArgumentList.Add(argument);
        foreach (var argument in _settings.JvmArguments) startInfo.ArgumentList.Add(argument);
        startInfo.ArgumentList.Add("-cp");
        startInfo.ArgumentList.Add(_settings.BuildClasspath());
        startInfo.ArgumentList.Add(_settings.HostEntryPoint);
        startInfo.ArgumentList.Add(JarPluginProtocol.ServeFlag);
        startInfo.ArgumentList.Add(requestPath);
        try
        {
            _process = Process.Start(startInfo) ?? throw new JarPluginException(JarPluginErrors.HostLaunchFailed, JarPluginFailureKind.Protocol, "无法启动插件宿主进程。");
        }
        catch (Exception error) when (error is not JarPluginException)
        {
            throw new JarPluginException(JarPluginErrors.HostLaunchFailed, JarPluginFailureKind.Protocol, $"无法启动插件宿主进程：{error.Message}", error);
        }
        // 持续排空 stdout/stderr：管道写满会让宿主阻塞在打印上，看起来像卡死。
        _ = DrainAsync(_process.StandardOutput);
        _ = DrainAsync(_process.StandardError);

        var readyPath = Path.Combine(_cacheDirectory, JarPluginProtocol.ReadyFile);
        var deadline = DateTime.UtcNow + _settings.StartupTimeout;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (File.Exists(readyPath)) return await ReadEnvelopeAsync(readyPath, ct).ConfigureAwait(false);
            if (!IsRunning) throw new JarPluginException(JarPluginErrors.HostExited, JarPluginFailureKind.Temporary, "插件宿主在就绪前退出。");
            if (DateTime.UtcNow >= deadline)
                throw new JarPluginException(JarPluginErrors.HostTimeout, JarPluginFailureKind.Temporary, $"插件宿主在 {_settings.StartupTimeout.TotalSeconds:0}s 内未就绪。");
            await Task.Delay(_pollInterval, ct).ConfigureAwait(false);
        }
    }

    public async Task<JarPluginEnvelope> SendAsync(IReadOnlyDictionary<string, string> parameters, CancellationToken ct)
    {
        var process = _process ?? throw new JarPluginException(JarPluginErrors.HostExited, JarPluginFailureKind.Temporary, "插件宿主尚未启动。");
        var id = $"r{Interlocked.Increment(ref _commands):x}-{Guid.NewGuid():N}"[..24];
        if (!JarPluginProtocol.IsValidIdentifier(id))
            throw new JarPluginException(JarPluginErrors.ProtocolError, JarPluginFailureKind.Protocol, "非法的请求编号。");
        var command = JarPluginJson.Serialize(new JarPluginCommand { Id = id, Params = new Dictionary<string, string>(parameters) });
        try
        {
            await process.StandardInput.WriteAsync(command.AsMemory(), ct).ConfigureAwait(false);
            await process.StandardInput.WriteAsync("\n".AsMemory(), ct).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
            throw new JarPluginException(JarPluginErrors.HostExited, JarPluginFailureKind.Temporary, "向插件宿主写入命令失败，进程可能已退出。", error);
        }
        var responsePath = Path.Combine(_cacheDirectory, JarPluginProtocol.ResponsesDirectory, id + ".json");
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (File.Exists(responsePath))
            {
                var envelope = await ReadEnvelopeAsync(responsePath, ct).ConfigureAwait(false);
                TryDelete(responsePath);
                return envelope;
            }
            if (!IsRunning) throw new JarPluginException(JarPluginErrors.HostExited, JarPluginFailureKind.Temporary, "插件宿主在响应前退出。");
            await Task.Delay(_pollInterval, ct).ConfigureAwait(false);
        }
    }

    private static async Task<JarPluginEnvelope> ReadEnvelopeAsync(string path, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                var text = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
                var envelope = JsonSerializer.Deserialize(text, JarPluginJsonContext.Default.JarPluginEnvelope);
                return envelope ?? new JarPluginEnvelope { ErrorCode = JarPluginErrors.ProtocolError, Error = "宿主响应为空。" };
            }
            catch (IOException) when (attempt < 9)
            {
                // 极少数情况下原子改名或读取存在短暂竞争，稍等重读。
                await Task.Delay(20, ct).ConfigureAwait(false);
            }
            catch (JsonException error)
            {
                return new JarPluginEnvelope { ErrorCode = JarPluginErrors.ProtocolError, Error = "宿主响应不是合法 JSON：" + error.Message };
            }
        }
        return new JarPluginEnvelope { ErrorCode = JarPluginErrors.ProtocolError, Error = "无法读取宿主响应。" };
    }

    private static async Task DrainAsync(StreamReader reader)
    {
        try
        {
            var buffer = new char[4096];
            while (await reader.ReadAsync(buffer).ConfigureAwait(false) > 0) { /* 仅排空，保持管道畅通。 */ }
        }
        catch (Exception) { /* 进程结束时管道关闭属正常。 */ }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public async ValueTask DisposeAsync()
    {
        var process = _process;
        _process = null;
        if (process is null) return;
        try { process.StandardInput.Close(); } catch (Exception) { /* 已关闭。 */ }
        try
        {
            if (!process.HasExited)
            {
                using var killWindow = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await process.WaitForExitAsync(killWindow.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { /* 超时后强杀。 */ }
            }
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception) { /* 进程状态竞态，忽略。 */ }
        process.Dispose();
    }
}

/// <summary>默认通道工厂：返回真实外部进程通道。</summary>
public sealed class JarPluginProcessChannelFactory(JarPluginHostSettings settings) : IJarPluginHostChannelFactory
{
    public JarPluginHostSettings Settings { get; } = settings;

    public IJarPluginHostChannel Create(JarPluginSessionDescriptor descriptor) => new JarPluginProcessChannel(descriptor, Settings);
}
