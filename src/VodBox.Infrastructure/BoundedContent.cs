namespace VodBox.Infrastructure;

/// <summary>
/// 有上限的流读取：防止远端返回超大响应耗尽内存。
/// 从旧 MacCmsProvider 的 private static 抽出为共享组件——海报缓存、EPG、弹幕、
/// 媒体代理、爬虫等几乎所有复用文件都依赖它。
/// </summary>
public static class BoundedContent
{
    /// <summary>读取流至多 <paramref name="maximum"/> 字节，超限抛 <see cref="InvalidDataException"/>。</summary>
    public static async Task<byte[]> ReadAsync(Stream stream, int maximum, CancellationToken token = default)
    {
        using var result = new MemoryStream();
        var buffer = new byte[16384];
        int read;
        while ((read = await stream.ReadAsync(buffer, token)) > 0)
        {
            if (result.Length + read > maximum)
                throw new InvalidDataException($"响应超过 {maximum / 1024 / 1024} MiB 限制。");
            result.Write(buffer, 0, read);
        }
        return result.ToArray();
    }
}
