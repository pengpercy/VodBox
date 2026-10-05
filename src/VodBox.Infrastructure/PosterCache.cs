using System.Security.Cryptography;
using System.Text;

namespace VodBox.Infrastructure;

public sealed class PosterCache(HttpClient http, string directory, long maximumBytes = 160 * 1024 * 1024) : IDisposable
{
    private readonly SemaphoreSlim _downloads = new(4, 4);
    private readonly object _eviction = new();
    public async Task<string?> GetAsync(string? address, CancellationToken token)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri)) return null;
        if (uri.IsFile) return File.Exists(uri.LocalPath) ? uri.LocalPath : null;
        if (uri.Scheme is not ("http" or "https")) return null;
        string path = Path.Combine(directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri))) + ".image");
        await _downloads.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < TimeSpan.FromDays(7))
            { File.SetLastAccessTimeUtc(path, DateTime.UtcNow); return path; }
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false); response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > 4 * 1024 * 1024) throw new InvalidDataException("海报超过 4 MiB。");
            await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            byte[] data = await BoundedContent.ReadAsync(input, 4 * 1024 * 1024, token).ConfigureAwait(false);
            Directory.CreateDirectory(directory); string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { await File.WriteAllBytesAsync(temporary, data, token).ConfigureAwait(false); token.ThrowIfCancellationRequested(); File.Move(temporary, path, true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            Evict(path); return path;
        }
        finally { _downloads.Release(); }
    }
    private void Evict(string protectedPath)
    {
        lock (_eviction)
        {
            var files = new DirectoryInfo(directory).EnumerateFiles("*.image").OrderBy(x => x.LastAccessTimeUtc).ToArray();
            long total = files.Sum(x => x.Length);
            foreach (var file in files)
            {
                if (total <= maximumBytes) break;
                if (file.FullName == protectedPath) continue;
                try { long length = file.Length; file.Delete(); total -= length; } catch (IOException) { }
            }
        }
    }
    public void Dispose() => _downloads.Dispose();
}
