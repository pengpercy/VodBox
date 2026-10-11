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
        var temporary = target + ".tmp";
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
        File.Move(temporary, target, overwrite: true);
        return target;
    }

    public void Dispose() => _client.Dispose();
}
