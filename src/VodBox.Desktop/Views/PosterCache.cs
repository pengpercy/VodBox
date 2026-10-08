using Avalonia.Media.Imaging;

namespace VodBox.Desktop.Views;

/// <summary>
/// Shared decoded posters. The budget counts width * height * 4 bytes and decoded entries,
/// including leased images; only idle images can be evicted, so leases may exceed the budget.
/// </summary>
internal sealed class PosterCache : IDisposable
{
    internal static readonly PosterCache Shared = new(LoadBitmapAsync);
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly object _gate = new();
    private readonly Dictionary<Uri, Entry> _entries = new();
    private readonly LinkedList<Entry> _idle = new();
    private readonly Func<Uri, CancellationToken, Task<Bitmap>> _loader;
    private readonly SemaphoreSlim _downloads;
    private readonly long _maxBytes;
    private readonly int _maxImages;
    private readonly TimeSpan _abandonDelay;
    private long _bytes;
    private int _images;
    private bool _disposed;

    internal PosterCache(Func<Uri, CancellationToken, Task<Bitmap>> loader,
        long maxBytes = 64 * 1024 * 1024, int maxImages = 128, int maxConcurrentLoads = 4,
        TimeSpan? abandonDelay = null)
    {
        ArgumentNullException.ThrowIfNull(loader);
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(maxImages);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxConcurrentLoads);
        _loader = loader;
        _maxBytes = maxBytes;
        _maxImages = maxImages;
        _downloads = new(maxConcurrentLoads);
        _abandonDelay = abandonDelay ?? TimeSpan.FromSeconds(2);
        ArgumentOutOfRangeException.ThrowIfLessThan(_abandonDelay, TimeSpan.Zero);
    }

    internal async Task<Lease> AcquireAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Entry entry;
        bool start;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            start = !_entries.TryGetValue(uri, out entry!);
            if (start) _entries.Add(uri, entry = new Entry(uri));
            entry.Consumers++;
            entry.IdleVersion++;
            if (entry.IdleNode is not null)
            {
                _idle.Remove(entry.IdleNode);
                entry.IdleNode = null;
            }
        }
        // Network and decoding never run inline on the UI thread, even for a synchronous loader.
        if (start) _ = Task.Run(() => LoadAsync(entry));
        try
        {
            var bitmap = await entry.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return new Lease(this, entry, bitmap);
        }
        catch
        {
            Release(entry);
            throw;
        }
    }

    private async Task LoadAsync(Entry entry)
    {
        Bitmap? bitmap = null;
        var entered = false;
        try
        {
            await _downloads.WaitAsync(entry.Cancellation.Token).ConfigureAwait(false);
            entered = true;
            bitmap = await _loader(entry.Uri, entry.Cancellation.Token).ConfigureAwait(false);
            var bytes = checked((long)bitmap.PixelSize.Width * bitmap.PixelSize.Height * 4);
            List<Bitmap> evicted;
            lock (_gate)
            {
                // A loader may ignore cancellation; an abandoned generation must never re-enter the cache.
                if (entry.Retired) return;
                entry.Bitmap = bitmap;
                entry.Bytes = bytes;
                entry.Loading = false;
                _bytes += bytes;
                _images++;
                bitmap = null;
                if (entry.Consumers == 0) entry.IdleNode = _idle.AddLast(entry);
                evicted = Trim();
                entry.Completion.TrySetResult(entry.Bitmap);
            }
            DisposeImages(evicted);
        }
        catch (Exception error)
        {
            lock (_gate)
            {
                Retire(entry);
                entry.Loading = false;
                if (error is OperationCanceledException) entry.Completion.TrySetCanceled();
                else
                {
                    entry.Completion.TrySetException(error);
                    // Also observe failures when every waiting consumer has already cancelled.
                    _ = entry.Completion.Task.Exception;
                }
            }
        }
        finally
        {
            bitmap?.Dispose();
            if (entered) _downloads.Release();
            entry.Cancellation.Dispose();
        }
    }

    private void Release(Entry entry)
    {
        List<Bitmap> evicted = new();
        int? idleVersion = null;
        lock (_gate)
        {
            if (--entry.Consumers != 0) return;
            if (entry.Bitmap is not null)
            {
                if (entry.Retired) RemoveImage(entry, evicted);
                else
                {
                    entry.IdleNode = _idle.AddLast(entry);
                    evicted = Trim();
                }
            }
            else if (entry.Loading && !entry.Retired) idleVersion = ++entry.IdleVersion;
        }
        DisposeImages(evicted);
        if (idleVersion is { } version) _ = AbandonAsync(entry, version);
    }

    private async Task AbandonAsync(Entry entry, int version)
    {
        await Task.Delay(_abandonDelay).ConfigureAwait(false);
        lock (_gate)
        {
            if (entry.Retired || !entry.Loading || entry.Consumers != 0 || entry.IdleVersion != version) return;
            Retire(entry);
            entry.Completion.TrySetCanceled();
        }
        Cancel(entry);
    }

    // All metadata transitions occur under _gate; disposal happens only after removing an unleased image.
    private List<Bitmap> Trim()
    {
        List<Bitmap> evicted = new();
        while ((_bytes > _maxBytes || _images > _maxImages) && _idle.First is { } first)
        {
            var entry = first.Value;
            Retire(entry);
            RemoveImage(entry, evicted);
        }
        return evicted;
    }

    private void Retire(Entry entry)
    {
        entry.Retired = true;
        if (_entries.TryGetValue(entry.Uri, out var current) && ReferenceEquals(current, entry))
            _entries.Remove(entry.Uri);
        if (entry.IdleNode is not null)
        {
            _idle.Remove(entry.IdleNode);
            entry.IdleNode = null;
        }
    }

    private void RemoveImage(Entry entry, List<Bitmap> evicted)
    {
        if (entry.Bitmap is not { } bitmap) return;
        _bytes -= entry.Bytes;
        _images--;
        entry.Bitmap = null;
        evicted.Add(bitmap);
    }

    public void Dispose()
    {
        List<Bitmap> evicted = new();
        Entry[] entries;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            entries = _entries.Values.ToArray();
            foreach (var entry in entries)
            {
                Retire(entry);
                if (entry.Consumers == 0) RemoveImage(entry, evicted);
                if (entry.Loading) entry.Completion.TrySetCanceled();
            }
        }
        DisposeImages(evicted);
        foreach (var entry in entries.Where(e => e.Loading)) Cancel(entry);
    }

    private static void Cancel(Entry entry)
    {
        // Loading can finish between retiring the entry and cancelling outside the metadata lock.
        try { entry.Cancellation.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private static void DisposeImages(List<Bitmap> images)
    {
        foreach (var image in images) image.Dispose();
    }

    internal sealed class Entry(Uri uri)
    {
        internal readonly Uri Uri = uri;
        internal readonly CancellationTokenSource Cancellation = new();
        internal readonly TaskCompletionSource<Bitmap> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Bitmap? Bitmap;
        internal long Bytes;
        internal int Consumers;
        internal int IdleVersion;
        internal bool Loading = true;
        internal bool Retired;
        internal LinkedListNode<Entry>? IdleNode;
    }

    internal sealed class Lease : IDisposable
    {
        private PosterCache? _owner;
        private readonly Entry _entry;
        internal Bitmap Bitmap { get; }

        internal Lease(PosterCache owner, Entry entry, Bitmap bitmap)
        {
            _owner = owner;
            _entry = entry;
            Bitmap = bitmap;
        }

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(_entry);
    }

    private static async Task<Bitmap> LoadBitmapAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var response = await Client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var data = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (data.Length + count > 12 * 1024 * 1024) throw new InvalidDataException("Poster exceeds 12 MiB.");
            await data.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
        }
        data.Position = 0;
        cancellationToken.ThrowIfCancellationRequested();
        return Bitmap.DecodeToWidth(data, 460);
    }
}
