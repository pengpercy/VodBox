using VodBox.Core;

namespace VodBox.Application;

/// <summary>Serializes native transitions and cancels obsolete source resolution.</summary>
public sealed class PlaybackCoordinator(IPlaybackEngine engine, ILibraryStore store) : IAsyncDisposable
{
    private readonly SemaphoreSlim _transition = new(1, 1);
    private readonly object _pendingLock = new();
    private CancellationTokenSource? _pending;
    private long _generation;
    private PlaybackRequest? _current;
    private string _configId = "default";
    public bool Incognito { get; set; }
    public PlaybackRequest? Current => _current;
    public long SessionId => Volatile.Read(ref _generation);
    public event EventHandler<PlaybackRequest>? RequestChanged;

    public async Task PlayAsync(Func<CancellationToken, Task<PlaybackRequest>> resolve, string configId,
        CancellationToken cancellationToken = default)
    {
        var generation = Interlocked.Increment(ref _generation);
        var pending = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_pendingLock)
        {
            _pending?.Cancel();
            _pending = pending;
        }
        try
        {
            var request = await resolve(pending.Token);
            pending.Token.ThrowIfCancellationRequested();
            await _transition.WaitAsync(pending.Token);
            try
            {
                if (generation != _generation) return;
                await SaveProgressAsync();
                await engine.StopAsync(pending.Token);
                _current = request;
                _configId = configId;
                RequestChanged?.Invoke(this, request);
                await engine.OpenAsync(request, generation, pending.Token);
            }
            finally { _transition.Release(); }
        }
        finally
        {
            lock (_pendingLock)
            {
                if (ReferenceEquals(_pending, pending)) _pending = null;
                pending.Dispose();
            }
        }
    }

    public Task SaveProgressAsync(CancellationToken cancellationToken = default)
    {
        var request = _current;
        var snapshot = engine.Snapshot;
        if (request is null || request.IsLive || Incognito || snapshot.Position <= TimeSpan.Zero)
            return Task.CompletedTask;
        // Do not resume a completed episode at its final frame, immediately ending it again.
        bool completed = snapshot.Duration > TimeSpan.Zero && snapshot.Position >= snapshot.Duration
            - TimeSpan.FromSeconds(Math.Min(30, snapshot.Duration.TotalSeconds * .05));
        return store.SaveHistoryAsync(new HistoryEntry(_configId, request.SourceId,
            string.IsNullOrEmpty(request.MediaId) ? request.Uri : request.MediaId, request.EpisodeId,
            request.Title, request.Uri, completed ? 0 : (long)snapshot.Position.TotalMilliseconds,
            DateTimeOffset.UtcNow), cancellationToken);
    }

    public async Task StopAsync()
    {
        Interlocked.Increment(ref _generation);
        lock (_pendingLock) _pending?.Cancel();
        await _transition.WaitAsync();
        try { await SaveProgressAsync(); await engine.StopAsync(CancellationToken.None); _current = null; }
        finally { _transition.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        await engine.DisposeAsync();
        _transition.Dispose();
    }
}
