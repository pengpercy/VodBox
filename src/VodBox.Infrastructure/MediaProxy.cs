using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using VodBox.Core;

namespace VodBox.Infrastructure;

/// <summary>Loopback-only streaming proxy for headers VLC cannot apply to every subresource.</summary>
public sealed partial class MediaProxy : IAsyncDisposable
{
    private sealed record Route(Uri Target, bool Directory);
    private sealed class Session(PlaybackRequest request)
    {
        public PlaybackRequest Request { get; } = request;
        public DateTimeOffset Created { get; } = DateTimeOffset.UtcNow;
        public ConcurrentDictionary<string, Route> Routes { get; } = new();
        public ConcurrentDictionary<string, string> Keys { get; } = new();
    }
    private readonly HttpClient _http = new(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All, UseCookies = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(20) };
    private readonly ConcurrentDictionary<string, Session> _sessions = new();
    private readonly ConcurrentDictionary<TcpClient, Task> _clients = new();
    private readonly SemaphoreSlim _slots = new(16, 16);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _start = new();
    private TcpListener? _listener;
    private Uri? _origin;
    private Task _accept = Task.CompletedTask;
    private long _bytes;
    public long BytesForwarded => Interlocked.Read(ref _bytes);
    [GeneratedRegex("URI=\"([^\"]+)\"", RegexOptions.IgnoreCase)] private static partial Regex PlaylistUri();

    public PlaybackRequest Register(PlaybackRequest request)
    {
        if (!Uri.TryCreate(request.Uri, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new NotSupportedException("请求头代理只支持 HTTP/HTTPS 媒体。");
        lock (_start)
        {
            ObjectDisposedException.ThrowIf(_shutdown.IsCancellationRequested, this);
            if (_listener is null)
            {
                _listener = new(IPAddress.Loopback, 0); _listener.Start(32);
                _origin = new($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/");
                _accept = AcceptAsync();
            }
        }
        var session = new Session(request); string id = Guid.NewGuid().ToString("N"); _sessions[id] = session;
        while (_sessions.Count > 3)
        {
            var oldest = _sessions.OrderBy(x => x.Value.Created).First(); _sessions.TryRemove(oldest.Key, out _);
        }
        return request with { Uri = Map(id, session, uri), OriginalUri = request.OriginalUri ?? request.Uri, Headers = [] };
    }
    private string Map(string id, Session session, Uri target, bool directory = false)
    {
        string key = target.AbsoluteUri + (directory ? "|directory" : "");
        string token = session.Keys.GetOrAdd(key, _ => Guid.NewGuid().ToString("N"));
        session.Routes[token] = new(target, directory);
        if (session.Routes.Count > 4096)
        {
            // Keep directory routes (DASH) stable, while expiring old individual live segments.
            foreach (var old in session.Routes.Where(x => !x.Value.Directory && x.Key != token).Take(512))
            { session.Routes.TryRemove(old.Key, out _); foreach (var entry in session.Keys.Where(x => x.Value == old.Key)) session.Keys.TryRemove(entry.Key, out _); }
        }
        string name = directory ? "" : Path.GetFileName(target.AbsolutePath);
        if (!directory && name.Length == 0) name = "stream";
        return new Uri(_origin!, $"media/{id}/{token}/{name}").AbsoluteUri;
    }
    private async Task AcceptAsync()
    {
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                var client = await _listener!.AcceptTcpClientAsync(_shutdown.Token);
                try { await _slots.WaitAsync(_shutdown.Token); }
                catch { client.Dispose(); throw; }
                var task = ServeAsync(client); _clients[client] = task;
                _ = task.ContinueWith(_ => _clients.TryRemove(client, out var ignored), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        catch (Exception ex) when (_shutdown.IsCancellationRequested && ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }
    private async Task ServeAsync(TcpClient client)
    {
        try
        {
            using (client)
            {
                var stream = client.GetStream(); using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token); timeout.CancelAfter(TimeSpan.FromSeconds(10));
                string header = await ReadHeaderAsync(stream, timeout.Token); var lines = header.Split("\r\n", StringSplitOptions.None);
                string[] first = lines[0].Split(' ');
                if (first.Length < 2 || first[0] is not ("GET" or "HEAD")) { await ErrorAsync(stream, 405); return; }
                var local = new Uri(_origin!, first[1]); string[] route = local.AbsolutePath.Split('/', 5);
                if (route.Length < 4 || route[1] != "media" || !_sessions.TryGetValue(route[2], out var session) || !session.Routes.TryGetValue(route[3], out var mapping))
                { await ErrorAsync(stream, 404); return; }
                var target = mapping.Directory ? new Uri(mapping.Target, (route.Length > 4 ? route[4] : "") + local.Query) : mapping.Target;
                if (target.Scheme is not ("http" or "https")) { await ErrorAsync(stream, 400); return; }
                using var request = new HttpRequestMessage(first[0] == "HEAD" ? HttpMethod.Head : HttpMethod.Get, target);
                foreach (var entry in session.Request.Headers)
                {
                    string name = entry.Key.ToLowerInvariant();
                    if (name is "host" or "connection" or "content-length" or "transfer-encoding" or "accept-encoding") continue;
                    if (name is not ("user-agent" or "referer" or "accept") && !SameOrigin(target, new Uri(session.Request.Uri))) continue;
                    if (entry.Key.Contains('\r') || entry.Key.Contains('\n') || entry.Value.Contains('\r') || entry.Value.Contains('\n')) throw new InvalidDataException("请求头含换行。");
                    request.Headers.TryAddWithoutValidation(entry.Key, entry.Value);
                }
                request.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
                foreach (var line in lines.Skip(1))
                {
                    int separator = line.IndexOf(':'); if (separator <= 0) continue;
                    string name = line[..separator];
                    if (name.Equals("Range", StringComparison.OrdinalIgnoreCase) || name.Equals("If-Range", StringComparison.OrdinalIgnoreCase))
                        request.Headers.TryAddWithoutValidation(name, line[(separator + 1)..].Trim());
                }
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, _shutdown.Token);
                await using var body = await response.Content.ReadAsStreamAsync(_shutdown.Token);
                Uri final = response.RequestMessage?.RequestUri ?? target;
                string mime = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
                bool hls = mime.Contains("mpegurl", StringComparison.OrdinalIgnoreCase) || final.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase);
                bool dash = mime.Contains("dash+xml", StringComparison.OrdinalIgnoreCase) || final.AbsolutePath.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase);
                if (dash) { await ErrorAsync(stream, 501); return; } // DASH templates require a dedicated route model.
                byte[]? rewritten = null;
                if (first[0] == "GET" && response.IsSuccessStatusCode && hls)
                {
                    var bytes = await BoundedContent.ReadAsync(body, 8 * 1024 * 1024, _shutdown.Token);
                    rewritten = Encoding.UTF8.GetBytes(RewriteHls(TextEncoding.Decode(bytes), final, route[2], session));
                }
                var headers = new StringBuilder($"HTTP/1.1 {(int)response.StatusCode} {response.ReasonPhrase}\r\nConnection: close\r\nContent-Type: {mime}\r\n");
                if (response.Headers.Location is { } location) headers.Append("Location: ").Append(Map(route[2], session, new Uri(target, location))).Append("\r\n");
                long? length = rewritten?.LongLength ?? response.Content.Headers.ContentLength;
                if (length is not null) headers.Append("Content-Length: ").Append(length.Value.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
                if (response.Content.Headers.ContentRange is { } range) headers.Append("Content-Range: ").Append(range).Append("\r\n");
                if (response.Headers.AcceptRanges.Count > 0) headers.Append("Accept-Ranges: ").AppendJoin(",", response.Headers.AcceptRanges).Append("\r\n");
                headers.Append("\r\n"); await stream.WriteAsync(Encoding.ASCII.GetBytes(headers.ToString()), _shutdown.Token);
                if (first[0] == "HEAD") return;
                if (rewritten is not null) { await stream.WriteAsync(rewritten, _shutdown.Token); Interlocked.Add(ref _bytes, rewritten.Length); }
                else
                {
                    var buffer = new byte[81920]; int read;
                    while ((read = await body.ReadAsync(buffer, _shutdown.Token)) > 0)
                    { await stream.WriteAsync(buffer.AsMemory(0, read), _shutdown.Token); Interlocked.Add(ref _bytes, read); }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or SocketException or OperationCanceledException or ObjectDisposedException) { }
        catch (Exception ex) { Console.Error.WriteLine("媒体代理：" + ex.Message); client.Dispose(); }
        finally { _slots.Release(); }
    }
    private string RewriteHls(string content, Uri origin, string id, Session session) => string.Join('\n', content.Split('\n').Select(raw =>
    {
        string line = raw.TrimEnd('\r'); if (line.Length == 0) return line;
        if (!line.StartsWith('#')) return Map(id, session, new Uri(origin, line.Trim()));
        return PlaylistUri().Replace(line, match => "URI=\"" + Map(id, session, new Uri(origin, match.Groups[1].Value)) + "\"");
    }));
    private static bool SameOrigin(Uri first, Uri second) => first.Scheme == second.Scheme && first.Host == second.Host && first.Port == second.Port;
    private static async Task<string> ReadHeaderAsync(NetworkStream stream, CancellationToken token)
    {
        var buffer = new byte[16384]; int count = 0;
        while (count < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(count, Math.Min(512, buffer.Length - count)), token);
            if (read == 0) throw new IOException("请求未完成。"); count += read;
            string header = Encoding.ASCII.GetString(buffer, 0, count);
            if (header.Contains("\r\n\r\n", StringComparison.Ordinal)) return header[..header.IndexOf("\r\n\r\n", StringComparison.Ordinal)];
        }
        throw new InvalidDataException("HTTP 请求头超过 16 KiB。");
    }
    private static Task ErrorAsync(NetworkStream stream, int status) => stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Error\r\nConnection: close\r\nContent-Length: 0\r\n\r\n")).AsTask();
    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel(); _listener?.Stop(); foreach (var client in _clients.Keys) client.Dispose();
        await _accept; await Task.WhenAll(_clients.Values); _sessions.Clear(); _http.Dispose(); _slots.Dispose(); _shutdown.Dispose();
    }
}
