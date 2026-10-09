using System.Security.Cryptography;
using System.Text;

namespace VodBox.Infrastructure;

/// <summary>原始海报磁盘缓存：4MiB响应、7天TTL、160MiB闲置淘汰，原子替换。</summary>
public sealed class PosterDiskCache(string directory, Func<Uri,CancellationToken,Task<byte[]>> fetch,
    long budget=160*1024*1024, TimeSpan? ttl=null)
{
    private readonly SemaphoreSlim _gate=new(1,1);
    private readonly SemaphoreSlim[] _keys=Enumerable.Range(0,64).Select(_=>new SemaphoreSlim(1,1)).ToArray();
    private SemaphoreSlim KeyGate(Uri uri)=>_keys[(uint)StringComparer.Ordinal.GetHashCode(uri.AbsoluteUri)%64];
    private string CachePath(Uri uri)=>Path.Combine(directory,Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri)))+".poster");
    public async Task InvalidateAsync(Uri uri,CancellationToken ct=default)
    {
        var key=KeyGate(uri);await key.WaitAsync(ct);
        try{await _gate.WaitAsync(ct);try{var path=CachePath(uri);if(File.Exists(path))File.Delete(path);}finally{_gate.Release();}}
        finally{key.Release();}
    }
    public async Task<byte[]> GetAsync(Uri uri,CancellationToken ct=default)
    {
        if(uri.Scheme is not ("http" or "https"))throw new InvalidDataException("海报地址无效。");
        var path=CachePath(uri);
        var key=KeyGate(uri);await key.WaitAsync(ct);
        try
        {
            await _gate.WaitAsync(ct);
            try
            {
            Directory.CreateDirectory(directory);
            if(File.Exists(path)&&File.GetLastWriteTimeUtc(path)>DateTime.UtcNow-(ttl??TimeSpan.FromDays(7)))
            {
                if(new FileInfo(path).Length<=4*1024*1024)
                {
                    var cached=await File.ReadAllBytesAsync(path,ct);File.SetLastAccessTimeUtc(path,DateTime.UtcNow);return cached;
                }
                File.Delete(path);
            }
            }
            finally{_gate.Release();}
            var bytes=await fetch(uri,ct);
            if(bytes.Length>4*1024*1024)throw new InvalidDataException("海报超过4MiB。");
            await _gate.WaitAsync(ct);
            try
            {
            await AtomicFile.WriteBytesAsync(path,bytes,ct);File.SetLastAccessTimeUtc(path,DateTime.UtcNow);
            var files=new DirectoryInfo(directory).EnumerateFiles("*.poster").OrderBy(file=>file.LastAccessTimeUtc).ToArray();
            var total=files.Sum(file=>file.Length);
            foreach(var file in files)
            {
                if(total<=budget)break;
                total-=file.Length;file.Delete();
            }
            return bytes;
            }
            finally{_gate.Release();}
        }
        finally{key.Release();}
    }
}
