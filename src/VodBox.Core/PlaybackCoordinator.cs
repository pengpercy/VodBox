namespace VodBox.Core;

/// <summary>取消过期解析；串行切换原生引擎。解析器即使忽略取消，也不能打开过期结果。</summary>
public sealed class PlaybackCoordinator(IPlaybackEngine engine) : IDisposable
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _transitions = new(1);
    private CancellationTokenSource? _active;
    private long _generation;
    private bool _disposed;
    public long SessionId { get { lock (_gate) return _generation; } }

    public Task OpenAsync(PlaybackRequest request, CancellationToken ct = default) =>
        OpenAsync(_ => Task.FromResult(request), ct);

    public async Task OpenAsync(Func<CancellationToken, Task<PlaybackRequest>> resolve, CancellationToken ct = default)
    {
        CancellationTokenSource scope;
        long generation;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _active?.Cancel();
            scope = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _active = scope;
            generation = ++_generation;
        }
        try
        {
            var request = await resolve(scope.Token).ConfigureAwait(false);
            scope.Token.ThrowIfCancellationRequested();
            await _transitions.WaitAsync(scope.Token).ConfigureAwait(false);
            try
            {
                scope.Token.ThrowIfCancellationRequested();
                await engine.OpenAsync(request, generation, scope.Token).ConfigureAwait(false);
                scope.Token.ThrowIfCancellationRequested();
            }
            finally { _transitions.Release(); }
        }
        finally
        {
            lock (_gate) { if (ReferenceEquals(_active, scope)) _active = null; }
            scope.Dispose();
        }
    }

    public async Task CloseAsync(CancellationToken ct = default)
    {
        long generation;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _active?.Cancel();
            generation = ++_generation;
        }
        await _transitions.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // 关闭等待期间若用户已开始新播放，不再停止新会话。
            lock (_gate) { if (generation != _generation) return; }
            await engine.StopAsync(ct).ConfigureAwait(false);
        }
        finally { _transitions.Release(); }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _active?.Cancel();
            ++_generation;
        }
    }
}
