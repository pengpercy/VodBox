using System.Security.Cryptography;
using System.Text;

namespace VodBox.Infrastructure;

/// <summary>受控媒体小文件缓存；不缓存任意公网URL接口，不保留半截下载。</summary>
public sealed class MediaCache(string directory, int maxEntryBytes = 16 * 1024 * 1024)
{
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private const long MaxTotalBytes = 128 * 1024 * 1024;

    public async Task ClearAsync(CancellationToken ct=default)
    {
        await _writeGate.WaitAsync(ct);
        try
        {
            if(!Directory.Exists(directory))return;
            foreach(var file in Directory.EnumerateFiles(directory,"*.cache")){ct.ThrowIfCancellationRequested();File.Delete(file);}
        }
        finally{_writeGate.Release();}
    }

    public async Task<string> GetAsync(string url, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
    {
        var key = url + "\n" + string.Join("\n", headers.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).Select(pair => pair.Key.ToLowerInvariant() + ":" + pair.Value));
        var path = Path.Combine(directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".cache");
        await _writeGate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(directory);
            if (File.Exists(path) && File.GetLastWriteTimeUtc(path) > DateTime.UtcNow.AddHours(-1)) return path;
            using var http = new DefaultHttp(allowRedirect:false);
            var bytes = await http.GetBoundedAsync(url, maxEntryBytes, ct, headers);
            await AtomicFile.WriteBytesAsync(path, bytes, ct);
            var files = new DirectoryInfo(directory).EnumerateFiles("*.cache").OrderBy(file => file.LastWriteTimeUtc).ToList();
            var size = files.Sum(file => file.Length);
            foreach (var file in files)
            {
                if (size <= MaxTotalBytes) break;
                if (file.FullName == Path.GetFullPath(path)) continue;
                size -= file.Length; file.Delete();
            }
            return path;
        }
        finally { _writeGate.Release(); }
    }
}
