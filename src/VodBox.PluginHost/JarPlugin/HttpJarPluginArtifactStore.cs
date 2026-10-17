using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace VodBox.PluginHost.JarPlugin;

/// <summary>
/// 默认的 JAR 落地实现：本地路径直接使用；HTTP(S) 按 URL 哈希缓存到临时目录。
/// 缓存键**不**使用配置里的 md5 声明——那只是保留的元信息，本层不做校验也无法假装校验。
/// </summary>
public sealed class HttpJarPluginArtifactStore : IJarPluginArtifactStore, IDisposable
{
    private const long MaxBytes = 128L * 1024 * 1024;

    private readonly HttpClient _client;
    private readonly string _cacheRoot;
    // 同一份配置里很多站点共用一个爬虫 JAR：并发搜索会同时请求同一地址。按落地路径单飞，
    // 后来者等同一个下载，而不是各自打开同一个临时文件（"The process cannot access the file"）。
    // 静态：每个站点各有自己的存储实例，但落地目录是共享的。
    private static readonly ConcurrentDictionary<string, Lazy<Task<string>>> InFlight = new(StringComparer.Ordinal);

    public HttpJarPluginArtifactStore(string? cacheRoot = null, HttpClient? client = null)
    {
        _cacheRoot = cacheRoot ?? Path.Combine(Path.GetTempPath(), "vodbox-jarhost", "plugins");
        _client = client ?? new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
    }

    public async Task<string> AcquireAsync(JarReference jar, CancellationToken ct)
    {
        var url = jar.Url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var address) || address.Scheme is not ("http" or "https"))
        {
            var local = Path.GetFullPath(url);
            if (!File.Exists(local)) throw new FileNotFoundException("插件 JAR 不存在：", local);
            return local;
        }
        Directory.CreateDirectory(_cacheRoot);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(address.AbsoluteUri)));
        var target = Path.Combine(_cacheRoot, hash + ".jar");
        if (File.Exists(target)) return target;
        var download = InFlight.GetOrAdd(target, _ => new Lazy<Task<string>>(() => DownloadAsync(address, target)));
        try
        {
            // 调用方取消只放弃等待；共享下载不随某一个调用方取消（受 HttpClient 超时约束）。
            return await download.Value.WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            if (download.Value.IsCompleted) InFlight.TryRemove(new KeyValuePair<string, Lazy<Task<string>>>(target, download));
        }
    }

    private async Task<string> DownloadAsync(Uri address, string target)
    {
        await Task.Yield();
        var ct = CancellationToken.None;
        // 临时文件名唯一：另一个进程（或残留的旧 .tmp）不会与本次写入冲突；落地用原子替换。
        var temporary = $"{target}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            await WriteAsync(address, temporary, ct).ConfigureAwait(false);
            File.Move(temporary, target, overwrite: true);
            return target;
        }
        finally
        {
            try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private async Task WriteAsync(Uri address, string temporary, CancellationToken ct)
    {
        using (var response = await _client.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > MaxBytes) throw new InvalidDataException("插件 JAR 超过大小上限。");
            await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var output = File.Create(temporary);
            var buffer = new byte[81920];
            long total = 0;
            int count;
            while ((count = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                total += count;
                if (total > MaxBytes) throw new InvalidDataException("插件 JAR 超过大小上限。");
                await output.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
            }
        }
    }

    public void Dispose() => _client.Dispose();
}
