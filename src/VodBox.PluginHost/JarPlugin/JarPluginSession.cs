namespace VodBox.PluginHost.JarPlugin;

/// <summary>
/// 一个源的宿主会话：串行化请求（CatVod spider 在 detail→play 之间保留状态，不能并发），
/// 并在超时/取消/协议异常时**终止进程**而非只丢引用——Java 线程不可安全回收。
/// </summary>
public sealed class JarPluginSession : IAsyncDisposable
{
    private readonly JarPluginSessionDescriptor _descriptor;
    private readonly IJarPluginHostChannelFactory _factory;
    private readonly JarPluginHostSettings _settings;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IJarPluginHostChannel? _channel;
    private DateTimeOffset _lastUse = DateTimeOffset.UtcNow;
    private int _busy;

    public JarPluginSession(JarPluginSessionDescriptor descriptor, IJarPluginHostChannelFactory factory, JarPluginHostSettings settings)
    {
        _descriptor = descriptor;
        _factory = factory;
        _settings = settings;
    }

    public string SourceKey => _descriptor.SourceKey;
    public string SessionKey => _descriptor.Layout.SessionKey;
    public bool IsRunning => _channel is { IsRunning: true };
    public DateTimeOffset LastUse => _lastUse;
    public bool IsIdle(DateTimeOffset now) => now - _lastUse >= _settings.IdleTimeout;
    /// <summary>是否有请求正在执行；回收时跳过，避免打断进行中的详情/播放。</summary>
    public bool IsBusy => Volatile.Read(ref _busy) > 0;

    public async Task<JarPluginEnvelope> RequestAsync(IReadOnlyDictionary<string, string> parameters, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _busy);
        try
        {
            return await RequestCoreAsync(parameters, ct).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _busy);
        }
    }

    private async Task<JarPluginEnvelope> RequestCoreAsync(IReadOnlyDictionary<string, string> parameters, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _channel ??= _factory.Create(_descriptor);
            var channel = _channel;
            // 首次启动：只受调用方 ct 与该通道内部 StartupTimeout 约束，不套用请求超时。
            var readiness = await channel.StartAsync(ct).ConfigureAwait(false);
            if (JarPluginErrors.Classify(readiness) is { } startupFailure)
            {
                await KillAsync().ConfigureAwait(false);
                throw new JarPluginException(startupFailure);
            }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(_settings.RequestTimeout);
            try
            {
                var envelope = await channel.SendAsync(parameters, deadline.Token).ConfigureAwait(false);
                if (envelope.ErrorCode == JarPluginErrors.ProtocolError) await KillAsync().ConfigureAwait(false);
                return envelope;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                await KillAsync().ConfigureAwait(false);
                throw new JarPluginException(JarPluginErrors.HostTimeout, JarPluginFailureKind.Temporary,
                    $"插件请求超时（{_settings.RequestTimeout.TotalSeconds:0}s），已终止该源会话。");
            }
            catch
            {
                // 取消（调用方）或通道异常：进程状态不可信，直接终止。
                await KillAsync().ConfigureAwait(false);
                throw;
            }
        }
        catch
        {
            // Startup can also fail or be cancelled before a readiness envelope is returned.
            await KillAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            _lastUse = DateTimeOffset.UtcNow;
            _gate.Release();
        }
    }

    private async Task KillAsync()
    {
        var channel = _channel;
        _channel = null;
        if (channel is null) return;
        try { await channel.DisposeAsync().ConfigureAwait(false); }
        catch (Exception) { /* 进程已不可用。 */ }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await KillAsync().ConfigureAwait(false); }
        finally { _gate.Release(); _gate.Dispose(); }
    }
}
