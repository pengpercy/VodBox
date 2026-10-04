using System.Diagnostics;
using System.Net.WebSockets;
using System.Text.Json;
using VodBox.Core;

namespace VodBox.Infrastructure;

/// <summary>Runs an isolated Chromium profile; never attaches to the user's browser session.</summary>
public sealed class BrowserMediaResolver(HttpClient http)
{
    public static string? FindBrowser(string? configured = null)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return File.Exists(configured) ? configured : null;
        string? environment = Environment.GetEnvironmentVariable("VODBOX_BROWSER");
        if (!string.IsNullOrWhiteSpace(environment) && File.Exists(environment)) return environment;
        string[] candidates = OperatingSystem.IsMacOS()
            ? ["/Applications/Google Chrome.app/Contents/MacOS/Google Chrome", "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge", "/Applications/Chromium.app/Contents/MacOS/Chromium"]
            : OperatingSystem.IsWindows()
                ? [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google/Chrome/Application/chrome.exe"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft/Edge/Application/msedge.exe"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google/Chrome/Application/chrome.exe")]
                : ["/usr/bin/google-chrome", "/usr/bin/chromium", "/usr/bin/chromium-browser", "/opt/google/chrome/chrome"];
        return candidates.FirstOrDefault(File.Exists);
    }

    public async Task<PlaybackRequest> ResolveAsync(PlaybackRequest request, ResolverDefinition definition, CancellationToken token)
    {
        string executable = FindBrowser(definition.BrowserExecutable) ?? throw new FileNotFoundException("网页嗅探需要 Chrome、Edge 或 Chromium。请安装浏览器，或配置 VODBOX_BROWSER。");
        if (!Uri.TryCreate(request.Uri, UriKind.Absolute, out var page) || page.Scheme is not ("http" or "https")) throw new InvalidDataException("嗅探页面必须是 HTTP/HTTPS。");
        if (request.Headers.Keys.Any(x => !x.Equals("User-Agent", StringComparison.OrdinalIgnoreCase) && !x.Equals("Referer", StringComparison.OrdinalIgnoreCase)))
            throw new NotSupportedException("浏览器入口暂只接受 User-Agent 和 Referer；带认证的页面请使用 JSON 解析器。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(definition.TimeoutSeconds)); token = timeout.Token;
        string profile = Path.Combine(Path.GetTempPath(), "vodbox-browser-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(profile);
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = !definition.VisibleBrowser, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (string argument in new[] { "--user-data-dir=" + profile, "--remote-debugging-port=0", "--remote-debugging-address=127.0.0.1", "--no-first-run", "--no-default-browser-check", "--autoplay-policy=no-user-gesture-required", "--disable-background-networking", "about:blank" }) start.ArgumentList.Add(argument);
        if (!definition.VisibleBrowser) start.ArgumentList.Insert(0, "--headless=new");
        using var process = Process.Start(start) ?? throw new IOException("无法启动嗅探浏览器。");
        // Drain pipes without retaining browser logs or page credentials.
        Task stdout = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null); Task stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
        try
        {
            string active = Path.Combine(profile, "DevToolsActivePort");
            while (!File.Exists(active)) { if (process.HasExited) throw new IOException("浏览器启动失败。"); await Task.Delay(100, token); }
            string[] lines = await File.ReadAllLinesAsync(active, token);
            if (lines.Length == 0 || !int.TryParse(lines[0], out int port) || port is < 1 or > 65535) throw new InvalidDataException("浏览器调试端口无效。");
            using var create = new HttpRequestMessage(HttpMethod.Put, $"http://127.0.0.1:{port}/json/new?about:blank");
            using var response = await http.SendAsync(create, token); response.EnsureSuccessStatusCode();
            using var tab = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            var endpoint = new Uri(tab.RootElement.GetProperty("webSocketDebuggerUrl").GetString()!);
            if (!endpoint.IsLoopback || endpoint.Port != port || endpoint.Scheme != "ws") throw new InvalidDataException("浏览器调试地址不是本地地址。");
            using var socket = new ClientWebSocket(); await socket.ConnectAsync(endpoint, token);
            int id = 0;
            await SendAsync(socket, ++id, "Network.enable", null, token);
            await SendAsync(socket, ++id, "Page.enable", null, token);
            if (request.Headers.FirstOrDefault(x => x.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase)).Value is { } agent)
                await SendAsync(socket, ++id, "Network.setUserAgentOverride", new() { ["userAgent"] = agent }, token);
            var navigate = new Dictionary<string, string> { ["url"] = page.AbsoluteUri };
            if (request.Headers.FirstOrDefault(x => x.Key.Equals("Referer", StringComparison.OrdinalIgnoreCase)).Value is { } referer) navigate["referrer"] = referer;
            await SendAsync(socket, ++id, "Page.navigate", navigate, token);
            var headers = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
            while (true)
            {
                using var message = await ReceiveAsync(socket, token); var root = message.RootElement;
                if (root.TryGetProperty("error", out var error)) throw new IOException("浏览器协议错误：" + error.GetProperty("message").GetString());
                if (!root.TryGetProperty("method", out var method) || !root.TryGetProperty("params", out var parameters)) continue;
                string? name = method.GetString();
                if (name is "Network.requestWillBeSent" or "Network.requestWillBeSentExtraInfo")
                {
                    string requestId = parameters.GetProperty("requestId").GetString()!;
                    var raw = name == "Network.requestWillBeSent" ? parameters.GetProperty("request").GetProperty("headers") : parameters.GetProperty("headers");
                    if (!headers.TryGetValue(requestId, out var collected)) headers[requestId] = collected = new(StringComparer.OrdinalIgnoreCase);
                    foreach (var header in raw.EnumerateObject()) if (!header.Name.StartsWith(':') && header.Value.ValueKind == JsonValueKind.String) collected[header.Name] = header.Value.GetString()!;
                    if (headers.Count > 1024) headers.Remove(headers.Keys.First());
                }
                else if (name == "Page.loadEventFired")
                    await SendAsync(socket, ++id, "Runtime.evaluate", new() { ["expression"] = "document.querySelectorAll('video,audio').forEach(v=>v.play().catch(()=>{}))" }, token);
                else if (name == "Network.responseReceived")
                {
                    var media = parameters.GetProperty("response"); string url = media.GetProperty("url").GetString()!;
                    string mime = media.GetProperty("mimeType").GetString() ?? ""; int status = media.GetProperty("status").GetInt32();
                    if (status is < 200 or >= 300 || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) continue;
                    bool manifest = mime.Contains("mpegurl", StringComparison.OrdinalIgnoreCase) || mime.Contains("dash+xml", StringComparison.OrdinalIgnoreCase) || uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) || uri.AbsolutePath.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase);
                    bool file = uri.AbsolutePath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) || uri.AbsolutePath.EndsWith(".webm", StringComparison.OrdinalIgnoreCase) || uri.AbsolutePath.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase);
                    if (!manifest && !file) continue; // Avoid selecting one HLS segment as the whole movie.
                    string requestId = parameters.GetProperty("requestId").GetString()!;
                    var resultHeaders = headers.GetValueOrDefault(requestId) ?? new(StringComparer.OrdinalIgnoreCase);
                    resultHeaders.TryAdd("Referer", page.AbsoluteUri);
                    return request with { Uri = uri.AbsoluteUri, Headers = resultHeaders };
                }
            }
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(); await Task.WhenAll(stdout, stderr);
            try { Directory.Delete(profile, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task SendAsync(ClientWebSocket socket, int id, string method, Dictionary<string, string>? parameters, CancellationToken token)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject(); writer.WriteNumber("id", id); writer.WriteString("method", method); writer.WriteStartObject("params");
            if (parameters is not null) foreach (var entry in parameters) writer.WriteString(entry.Key, entry.Value);
            writer.WriteEndObject(); writer.WriteEndObject();
        }
        await socket.SendAsync(stream.ToArray(), WebSocketMessageType.Text, true, token);
    }
    private static async Task<JsonDocument> ReceiveAsync(ClientWebSocket socket, CancellationToken token)
    {
        using var stream = new MemoryStream(); byte[] buffer = new byte[16384]; WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, token);
            if (result.MessageType == WebSocketMessageType.Close) throw new IOException("嗅探浏览器断开连接。");
            if (stream.Length + result.Count > 4 * 1024 * 1024) throw new InvalidDataException("浏览器事件超过 4 MiB。");
            stream.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return JsonDocument.Parse(stream.ToArray());
    }
}
