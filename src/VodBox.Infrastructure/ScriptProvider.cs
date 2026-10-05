using System.Diagnostics;
using System.Text.Json;
using VodBox.Core;

namespace VodBox.Infrastructure;

/// <summary>One worker per source; a cancelled call kills the worker so late replies cannot leak into the next call.</summary>
public sealed class ScriptProvider(SourceDefinition source, string pluginHostPath, string assetsDirectory) : IContentProvider
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    private long _requestId;
    private bool _initialized;
    private bool _disposed;
    public string SourceId => source.Id;

    private Process Start()
    {
        if (source.Entry is null || !Uri.TryCreate(source.Entry, UriKind.Absolute, out var entry) || !entry.IsFile)
            throw new InvalidDataException("脚本 entry 必须是本地文件 URI。请先将插件保存到受管理目录。");
        var info = new ProcessStartInfo { RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, StandardInputEncoding = new System.Text.UTF8Encoding(false),
            StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(entry.LocalPath)! };
        if (source.Runtime == ProviderRuntime.Quickjs)
        {
            if (pluginHostPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            { info.FileName = "dotnet"; info.ArgumentList.Add(pluginHostPath); }
            else info.FileName = pluginHostPath;
            info.ArgumentList.Add(entry.LocalPath);
        }
        else
        {
            var name = source.Runtime == ProviderRuntime.Python ? "python" : "node";
            var executable = OperatingSystem.IsWindows() ? name + ".exe" : name == "python" ? "python3" : name;
            var bundled = Path.Combine(assetsDirectory, "runtimes", name, OperatingSystem.IsWindows() ? executable : "bin/" + executable);
            info.FileName = File.Exists(bundled) ? bundled : Environment.GetEnvironmentVariable("VODBOX_" + name.ToUpperInvariant()) ?? executable;
            if (name == "python") { info.ArgumentList.Add("-u"); info.ArgumentList.Add("-B"); }
            info.ArgumentList.Add(Path.Combine(assetsDirectory, "plugins", name, name == "python" ? "worker.py" : "worker.mjs"));
            info.ArgumentList.Add(entry.LocalPath);
        }
        var process = Process.Start(info) ?? throw new InvalidOperationException("无法启动脚本运行时。");
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) Console.Error.WriteLine($"[{source.Id}] {e.Data}"); };
        process.BeginErrorReadLine();
        return process;
    }
    private async Task<T> CallAsync<T>(string method, JsonElement parameters, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            if (_process is null || _process.HasExited) { Reset(); _process = Start(); }
            if (!_initialized) { await ExchangeAsync<JsonElement>("init", JsonSerializer.SerializeToElement(source.Options, VodBoxJson.Default.DictionaryStringJsonElement), timeout.Token); _initialized = true; }
            return await ExchangeAsync<T>(method, parameters, timeout.Token);
        }
        catch { Reset(); throw; }
        finally { _gate.Release(); }
    }
    private async Task<T> ExchangeAsync<T>(string method, JsonElement parameters, CancellationToken token)
    {
        long id = ++_requestId;
        var request = JsonSerializer.Serialize(new RpcRequest(1, id, SourceId, method, parameters), VodBoxJson.Default.RpcRequest);
        await _process!.StandardInput.WriteLineAsync(request.AsMemory(), token);
        await _process.StandardInput.FlushAsync(token);
        string? line = await _process.StandardOutput.ReadLineAsync(token);
        if (line is null) throw new IOException("脚本运行时退出，未返回结果。");
        if (line.Length > 8 * 1024 * 1024) throw new InvalidDataException("插件结果超过 8 MiB。");
        using var result = JsonDocument.Parse(line);
        var root = result.RootElement;
        if (root.GetProperty("apiVersion").GetInt32() != 1 || root.GetProperty("requestId").GetInt64() != id)
            throw new InvalidDataException("插件协议版本或 requestId 不匹配。");
        if (root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
            throw new InvalidOperationException("插件执行失败：" + error.GetString());
        return root.GetProperty("result").Deserialize(WireJson.TypeInfo<T>()) ?? throw new InvalidDataException("插件结果为空。");
    }
    private static JsonElement Args(ScriptParams parameters) => JsonSerializer.SerializeToElement(parameters, VodBoxJson.Default.ScriptParams);
    private void Reset()
    {
        if (_process is not null) { try { if (!_process.HasExited) _process.Kill(true); } catch (InvalidOperationException) { } _process.Dispose(); }
        _process = null; _initialized = false;
    }
    public Task<IReadOnlyList<Category>> GetCategoriesAsync(CancellationToken token) => CallAsync<IReadOnlyList<Category>>("categories", Args(new()), token);
    public Task<MediaPage> GetItemsAsync(string? categoryId, string? cursor, CancellationToken token) => CallAsync<MediaPage>("items", Args(new(CategoryId: categoryId, Cursor: cursor)), token);
    public Task<MediaPage> GetItemsFilteredAsync(string? categoryId, string? cursor, IReadOnlyDictionary<string, string> filters, CancellationToken token) => CallAsync<MediaPage>("items", Args(new(CategoryId: categoryId, Cursor: cursor, Filters: new(filters))), token);
    public Task<MediaPage> SearchAsync(string query, CancellationToken token) => SearchPageAsync(query, null, token);
    public Task<MediaPage> SearchPageAsync(string query, string? cursor, CancellationToken token) => CallAsync<MediaPage>("search", Args(new(Query: query, Cursor: cursor)), token);
    public Task<MediaDetail> GetDetailAsync(string mediaId, CancellationToken token) => CallAsync<MediaDetail>("detail", Args(new(MediaId: mediaId)), token);
    public async Task<PlaybackRequest> ResolvePlaybackAsync(string mediaId, string episodeId, CancellationToken token) =>
        (await CallAsync<PlaybackRequest>("resolvePlayback", Args(new(MediaId: mediaId, EpisodeId: episodeId)), token)) with { SourceId = SourceId, MediaId = mediaId, EpisodeId = episodeId };
    public async ValueTask DisposeAsync() { await _gate.WaitAsync(); try { _disposed = true; Reset(); } finally { _gate.Release(); } }
}
