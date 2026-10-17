using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using MonoTorrent;
using MonoTorrent.Client;

namespace VodBox.Infrastructure;

/// <summary>
/// 本机 loopback BitTorrent 串流后端：magnet → DHT/tracker 元数据 → 文件选择 → 可 seek 的 HTTP Range。
/// 播放器（libmpv）读入的始终是本地回环地址，真实 peer/分片下载由 MonoTorrent 在图外完成。
/// </summary>
public sealed class TorrentStreamServer : IAsyncDisposable
{
    /// <summary>应用级共享实例；进程退出时随宿主一并释放。</summary>
    internal static readonly TorrentStreamServer Shared = new();

    private const int MaxSessions = 4;
    private static readonly TimeSpan IdleLifetime = TimeSpan.FromMinutes(30);

    /// <summary>元数据齐全后优先选择体积最大的可播放视频文件；否则退回体积最大的文件。</summary>
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".avi", ".mov", ".m4v", ".webm", ".ts", ".m2ts", ".wmv", ".flv", ".mpg", ".mpeg", ".rmvb",
    };

    private sealed class Session
    {
        public required TorrentManager Manager { get; init; }
        public required ITorrentManagerFile File { get; init; }
        public required Stream Content { get; init; }
        public required string Extension { get; init; }
        /// <summary>LocalStream 同一时刻只允许一个读位置；串行化 seek+read，保证 Range 语义。</summary>
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public DateTimeOffset Expires { get; set; }
    }

    private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly SemaphoreSlim _registration = new(1, 1);
    private readonly string _cacheDirectory;
    private ClientEngine? _engine;
    private WebApplication? _app;
    private string? _address;
    private bool _disposed;

    public TorrentStreamServer() : this(Path.Combine(Path.GetTempPath(), "vodbox-torrent")) { }

    public TorrentStreamServer(string cacheDirectory)
    {
        if (string.IsNullOrWhiteSpace(cacheDirectory)) throw new ArgumentException("缓存目录不能为空。", nameof(cacheDirectory));
        _cacheDirectory = cacheDirectory;
    }

    /// <summary>
    /// 解析 magnet、等待元数据、选择文件并返回本机回环播放地址。同一 magnet 复用已有会话。
    /// </summary>
    public async Task<string> RegisterAsync(string magnetUri, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(magnetUri) || magnetUri.Length > 8192) throw new InvalidDataException("磁力链接缺失或过长。");
        if (!MagnetLink.TryParse(magnetUri.Trim(), out var magnet)) throw new InvalidDataException("磁力链接无法解析。");
        await _registration.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await ExpireAsync().ConfigureAwait(false);
            foreach (var existing in _sessions)
            {
                if (!existing.Value.Manager.InfoHashes.Equals(magnet.InfoHashes)) continue;
                existing.Value.Expires = DateTimeOffset.UtcNow.Add(IdleLifetime);
                await EnsureStartedAsync(ct).ConfigureAwait(false);
                return LocalUrl(existing.Key, existing.Value.Extension);
            }
            // 磁力分集各是一个独立 swarm：容量满时淘汰最久未使用会话，避免连续切集失败。
            if (_sessions.Count >= MaxSessions)
            {
                var oldest = _sessions.OrderBy(pair => pair.Value.Expires).First();
                if (_sessions.TryRemove(oldest.Key, out var stale)) await StopSessionAsync(stale).ConfigureAwait(false);
            }

            var engine = await EnsureEngineAsync(ct).ConfigureAwait(false);
            var manager = await engine.AddStreamingAsync(magnet, _cacheDirectory).ConfigureAwait(false);
            try
            {
                await manager.StartAsync().ConfigureAwait(false);
                await manager.WaitForMetadataAsync(ct).ConfigureAwait(false);
                var file = SelectFile(manager.Files) ?? throw new InvalidDataException("磁力元数据未包含可播放文件。");
                var provider = manager.StreamProvider ?? throw new InvalidDataException("磁力会话未启用串流提供器。");
                // prebuffer:false 先返回地址；真正读取指定 Range 时才分别拉取对应分片。
                var content = await provider.CreateStreamAsync(file, prebuffer: false, ct).ConfigureAwait(false);
                await EnsureStartedAsync(ct).ConfigureAwait(false);
                var id = Guid.NewGuid().ToString("N");
                var session = new Session { Manager = manager, File = file, Content = content, Extension = Extension(file.Path), Expires = DateTimeOffset.UtcNow.Add(IdleLifetime) };
                _sessions[id] = session;
                return LocalUrl(id, session.Extension);
            }
            catch
            {
                await StopManagerAsync(engine, manager).ConfigureAwait(false);
                throw;
            }
        }
        finally { _registration.Release(); }
    }

    /// <summary>停止并移除所有磁力会话，释放引擎与本地监听。</summary>
    public async Task StopAllAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            foreach (var pair in _sessions) await StopSessionAsync(pair.Value).ConfigureAwait(false);
            _sessions.Clear();
        }
        finally { _lifecycle.Release(); }
    }

    /// <summary>单个磁力会话的连接、tracker 与进度快照（在线验收证据，不参与播放路径）。</summary>
    public readonly record struct TorrentSessionStatus(
        string Id, string State, int OpenConnections, int Seeds, int Leeches,
        int KnownPeers, int Trackers);

    /// <summary>诊断当前串流会话的 peer/tracker 状态，供在线验收记录证据。</summary>
    public IReadOnlyList<TorrentSessionStatus> DescribeSessions()
    {
        return _sessions.Select(pair =>
        {
            var manager = pair.Value.Manager;
            var peers = manager.Peers;
            int trackers;
            try { trackers = manager.TrackerManager.Tiers.Count; }
            catch (Exception error) when (error is InvalidOperationException or NullReferenceException) { trackers = 0; }
            return new TorrentSessionStatus(
                pair.Key, manager.State.ToString(), manager.OpenConnections,
                peers.Seeds, peers.Leechs, peers.Available + peers.Seeds + peers.Leechs, trackers);
        }).ToArray();
    }

    private async Task<ClientEngine> EnsureEngineAsync(CancellationToken ct)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_engine is not null) return _engine;
            Directory.CreateDirectory(_cacheDirectory);
            // 默认 EngineSettings 启用 DHT（bootstrap 路由）与 UPnP/NAT-PMP 端口映射。
            _engine = new ClientEngine();
            return _engine;
        }
        finally { _lifecycle.Release(); }
    }

    private async Task EnsureStartedAsync(CancellationToken ct)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_app is not null) return;
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
            var app = builder.Build();
            app.MapMethods("/torrent/{id}.{ext}", ["GET", "HEAD"], ServeAsync);
            try
            {
                await app.StartAsync(ct).ConfigureAwait(false);
                _address = app.Urls.Single();
                _app = app;
            }
            catch { await app.DisposeAsync().ConfigureAwait(false); throw; }
        }
        finally { _lifecycle.Release(); }
    }

    private string LocalUrl(string id, string extension)
    {
        string? address = _address;
        if (address is null) throw new InvalidOperationException("磁力串流监听尚未就绪。");
        return address.TrimEnd('/') + "/torrent/" + id + extension;
    }

    private async Task ExpireAsync()
    {
        foreach (var pair in _sessions.ToArray())
        {
            if (pair.Value.Expires > DateTimeOffset.UtcNow) continue;
            if (_sessions.TryRemove(pair.Key, out var removed)) await StopSessionAsync(removed).ConfigureAwait(false);
        }
    }

    private async Task ServeAsync(HttpContext context)
    {
        if (!IPAddress.IsLoopback(context.Connection.RemoteIpAddress ?? IPAddress.None) || _address is null ||
            !Uri.TryCreate(_address, UriKind.Absolute, out var local) ||
            !string.Equals(context.Request.Host.Value, local.Authority, StringComparison.OrdinalIgnoreCase))
        { context.Response.StatusCode = StatusCodes.Status403Forbidden; return; }
        var id = context.Request.RouteValues["id"]?.ToString() ?? "";
        if (!_sessions.TryGetValue(id, out var session) || session.Expires <= DateTimeOffset.UtcNow)
        { _sessions.TryRemove(id, out _); context.Response.StatusCode = StatusCodes.Status404NotFound; return; }
        long total = session.File.Length, start = 0, end = total - 1;
        bool ranged = context.Request.Headers.TryGetValue("Range", out var range);
        if (ranged && !ParseRange(range.ToString(), total, out start, out end))
        { context.Response.StatusCode = StatusCodes.Status416RangeNotSatisfiable; context.Response.Headers.ContentRange = "bytes */" + total; return; }
        session.Expires = DateTimeOffset.UtcNow.Add(IdleLifetime);
        context.Response.ContentType = ContentType(session.Extension);
        context.Response.ContentLength = end - start + 1;
        context.Response.Headers.AcceptRanges = "bytes";
        context.Response.Headers.CacheControl = "no-store";
        context.Response.StatusCode = ranged ? StatusCodes.Status206PartialContent : StatusCodes.Status200OK;
        if (ranged) context.Response.Headers.ContentRange = $"bytes {start}-{end}/{total}";
        if (HttpMethods.IsHead(context.Request.Method)) return;
        try
        {
            var buffer = new byte[64 * 1024];
            for (long cursor = start; cursor <= end;)
            {
                // LocalStream 只允许一个读位置：每个分块内串行 seek+read，块间释放以便并发 Range 交错。
                int read;
                await session.Gate.WaitAsync(context.RequestAborted).ConfigureAwait(false);
                try
                {
                    if (session.Content.Position != cursor) session.Content.Seek(cursor, SeekOrigin.Begin);
                    read = await session.Content.ReadAsync(buffer.AsMemory(0, (int)Math.Min(end - cursor + 1, buffer.Length)), context.RequestAborted).ConfigureAwait(false);
                }
                finally { session.Gate.Release(); }
                if (read <= 0) break;
                await context.Response.Body.WriteAsync(buffer.AsMemory(0, read), context.RequestAborted).ConfigureAwait(false);
                cursor += read;
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

    private static ITorrentManagerFile? SelectFile(IList<ITorrentManagerFile> files)
    {
        if (files.Count == 0) return null;
        var video = files.Where(f => VideoExtensions.Contains(Extension(f.Path))).OrderByDescending(f => f.Length).FirstOrDefault();
        return video ?? files.OrderByDescending(f => f.Length).First();
    }

    private static string Extension(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Length is > 0 and <= 8 && extension.All(c => char.IsAsciiLetterOrDigit(c) || c == '.') ? extension.ToLowerInvariant() : ".bin";
    }

    private static string ContentType(string extension) => extension switch
    {
        ".mp4" or ".m4v" => "video/mp4",
        ".webm" => "video/webm",
        ".mkv" => "video/x-matroska",
        ".avi" => "video/x-msvideo",
        ".mov" => "video/quicktime",
        ".ts" or ".m2ts" => "video/mp2t",
        ".flv" => "video/x-flv",
        ".wmv" => "video/x-ms-wmv",
        _ => "application/octet-stream",
    };

    private static async Task StopSessionAsync(Session session)
    {
        try { session.Content.Dispose(); } catch { /* 释放路径吞掉异常，避免掩盖上层取消。 */ }
        if (session.Manager.Engine is { } engine) await StopManagerAsync(engine, session.Manager).ConfigureAwait(false);
    }

    private static async Task StopManagerAsync(ClientEngine engine, TorrentManager manager)
    {
        try { await manager.StopAsync().ConfigureAwait(false); } catch { }
        try { await engine.RemoveAsync(manager).ConfigureAwait(false); } catch { }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var session in _sessions.Values)
                await StopSessionAsync(session).ConfigureAwait(false);
            _sessions.Clear();
            if (_app is not null) { await _app.StopAsync().ConfigureAwait(false); await _app.DisposeAsync().ConfigureAwait(false); _app = null; }
            if (_engine is not null)
            {
                try { await _engine.StopAllAsync().ConfigureAwait(false); } catch { }
                _engine.Dispose();
                _engine = null;
            }
        }
        finally { _lifecycle.Release(); }
    }
}
