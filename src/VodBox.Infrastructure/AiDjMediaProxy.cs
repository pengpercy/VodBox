using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace VodBox.Infrastructure;

/// <summary>Loopback-only CENC range server. Random handles expire and never contain upstream keys/URLs.</summary>
internal sealed class AiDjMediaProxy : IAsyncDisposable
{
    internal static readonly AiDjMediaProxy Shared = new();
    private sealed record Entry(Uri Url, AiDjCenc Index, DateTimeOffset Expires);
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly SemaphoreSlim _initialization = new(1, 1);
    private readonly HttpClient _http;
    private WebApplication? _app;
    private string? _address;
    private bool _disposed;
    internal AiDjMediaProxy() : this(new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.None })) { }
    internal AiDjMediaProxy(HttpClient http) => _http = http;

    internal async Task<string> RegisterAsync(string url, byte[] key, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidDataException("AiDj media URL invalid.");
        if (key.Length != 16) throw new InvalidDataException("AiDj media key invalid.");
        await _initialization.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var entry in _entries.Where(p => p.Value.Expires <= DateTimeOffset.UtcNow).ToArray()) _entries.TryRemove(entry.Key, out _);
            if (_entries.Count >= 4) throw new InvalidOperationException("AiDj 最多同时保留 4 个播放会话，请稍后重试。");
            var first = await RangeAsync(uri, 0, 15, 16, null, ct).ConfigureAwait(false);
            long total = first.Total, cursor = 0; byte[]? moov = null; long moovStart = 0;
            for (int i = 0; i < 128 && cursor < total; i++)
            {
                var head = cursor == 0 ? first.Data : (await RangeAsync(uri, cursor, Math.Min(cursor + 15, total - 1), 16, total, ct).ConfigureAwait(false)).Data;
                if (head.Length < 8) throw new InvalidDataException("AiDj truncated media box header.");
                uint size32 = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(head);
                int header = size32 == 1 ? 16 : 8;
                if (head.Length < header) throw new InvalidDataException("AiDj truncated media extended box header.");
                ulong rawSize = size32 == 0 ? (ulong)(total - cursor) : size32 == 1 ? System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(head.AsSpan(8)) : size32;
                if (rawSize < (ulong)header || rawSize > (ulong)(total - cursor)) throw new InvalidDataException("AiDj media box exceeds total length.");
                long size = (long)rawSize;
                if (head.AsSpan(4, 4).SequenceEqual("moov"u8))
                {
                    if (size > 8 * 1024 * 1024) throw new InvalidDataException("AiDj moov too large.");
                    moovStart = cursor; moov = (await RangeAsync(uri, cursor, cursor + size - 1, 8 * 1024 * 1024, total, ct).ConfigureAwait(false)).Data; break;
                }
                cursor += size;
            }
            if (moov is null) throw new InvalidDataException("AiDj progressive MP4 moov not found.");
            var index = new AiDjCenc(moov, moovStart, total, key);
            await EnsureStartedAsync(ct).ConfigureAwait(false);
            var id = Guid.NewGuid().ToString("N"); _entries[id] = new(uri, index, DateTimeOffset.UtcNow.AddMinutes(15));
            return _address + "/aidj/" + id + ".mp4";
        }
        finally { _initialization.Release(); }
    }
    private async Task EnsureStartedAsync(CancellationToken ct)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this); if (_app is not null) return;
            var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders();
            builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
            var app = builder.Build(); app.MapMethods("/aidj/{id}.mp4", ["GET", "HEAD"], ServeAsync);
            try
            {
                await app.StartAsync(ct).ConfigureAwait(false);
                _address = app.Urls.Single(); _app = app;
            }
            catch { await app.DisposeAsync().ConfigureAwait(false); throw; }
        }
        finally { _lifecycle.Release(); }
    }
    private async Task ServeAsync(HttpContext context)
    {
        if (!IPAddress.IsLoopback(context.Connection.RemoteIpAddress ?? IPAddress.None) || _address is null ||
            !Uri.TryCreate(_address, UriKind.Absolute, out var local) || !string.Equals(context.Request.Host.Value, local.Authority, StringComparison.OrdinalIgnoreCase) ||
            context.Request.Headers.TryGetValue("Origin", out var origin) && origin.ToString() != _address)
        { context.Response.StatusCode = StatusCodes.Status403Forbidden; return; }
        var id = context.Request.RouteValues["id"]?.ToString() ?? "";
        if (!_entries.TryGetValue(id, out var entry) || entry.Expires <= DateTimeOffset.UtcNow)
        { _entries.TryRemove(id, out _); context.Response.StatusCode = StatusCodes.Status404NotFound; return; }
        long total = entry.Index.TotalLength, start = 0, end = total - 1;
        bool ranged = context.Request.Headers.TryGetValue("Range", out var range);
        if (ranged && !ParseRange(range.ToString(), total, out start, out end))
        { context.Response.StatusCode = StatusCodes.Status416RangeNotSatisfiable; context.Response.Headers.ContentRange = "bytes */" + total; return; }
        context.Response.ContentType = "video/mp4"; context.Response.ContentLength = end - start + 1;
        context.Response.Headers.AcceptRanges = "bytes"; context.Response.Headers.CacheControl = "no-store";
        context.Response.StatusCode = ranged ? StatusCodes.Status206PartialContent : StatusCodes.Status200OK;
        if (ranged) context.Response.Headers.ContentRange = $"bytes {start}-{end}/{total}";
        if (HttpMethods.IsHead(context.Request.Method)) return;
        try
        {
            for (long cursor = start; cursor <= end;)
            {
                long stop = Math.Min(end, cursor + 1024 * 1024 - 1);
                var result = await RangeAsync(entry.Url, cursor, stop, 1024 * 1024, total, context.RequestAborted).ConfigureAwait(false);
                entry.Index.PatchRange(result.Data, cursor);
                await context.Response.Body.WriteAsync(result.Data, context.RequestAborted).ConfigureAwait(false); cursor = stop + 1;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or OperationCanceledException or IOException)
        { context.Abort(); }
    }
    internal static bool ParseRange(string text, long total, out long start, out long end)
    {
        start = 0; end = total - 1;
        if (total <= 0 || !text.StartsWith("bytes=", StringComparison.Ordinal) || text.Contains(',')) return false;
        var parts = text[6..].Split('-'); if (parts.Length != 2) return false;
        if (parts[0].Length == 0)
        {
            if (!long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var suffix) || suffix <= 0) return false;
            start = Math.Max(0, total - suffix); return true;
        }
        if (!long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out start) || start >= total) return false;
        if (parts[1].Length > 0)
        { if (!long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out end) || end < start) return false; end = Math.Min(end, total - 1); }
        return true;
    }
    private async Task<(byte[] Data, long Total)> RangeAsync(Uri url, long start, long end, int maximum, long? expectedTotal, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(start, end); request.Headers.TryAddWithoutValidation("User-Agent", "com.phoenix.read/71332");
        request.Headers.TryAddWithoutValidation("Referer", "https://novel.snssdk.com/");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
        var contentRange = response.Content.Headers.ContentRange;
        if (response.StatusCode != HttpStatusCode.PartialContent || contentRange is null || contentRange.Unit != "bytes" || contentRange.From != start || contentRange.To != end ||
            contentRange.Length is not { } total || total <= end || expectedTotal is { } expected && expected != total || response.RequestMessage?.RequestUri is { } final && final != url)
            throw new InvalidDataException("AiDj CDN must return exact stable byte ranges without redirects.");
        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
        var data = await BoundedContent.ReadAsync(stream, maximum, deadline.Token).ConfigureAwait(false);
        if (data.Length != end - start + 1) throw new InvalidDataException("AiDj CDN returned truncated byte range.");
        return (data, total);
    }
    public async ValueTask DisposeAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try { if (_disposed) return; _disposed = true; _entries.Clear(); if (_app is not null) { await _app.StopAsync().ConfigureAwait(false); await _app.DisposeAsync().ConfigureAwait(false); _app = null; } }
        finally { _lifecycle.Release(); }
    }
}
