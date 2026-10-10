namespace VodBox.PreviewTests;

/// <summary>
/// 测试用临时数据目录的平台安全清理。
///
/// Windows 不允许删除仍被占用的文件（数据库连接未释放时报
/// "The process cannot access the file 'library.db' because it is being used by another process"），
/// 而 macOS/Linux 通常允许。所以清理必须：先释放持有者、再重试删除、最后失败也不上报——
/// 临时目录残留不是被测行为，不该让测试变红。
/// </summary>
internal static class TempDataDirectory
{
    public static string Create(string prefix) =>
        Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");

    /// <summary>尽力删除；删不掉就交给系统清理，不影响测试结论。</summary>
    public static void TryDelete(string directory)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (!Directory.Exists(directory)) return;
                Directory.Delete(directory, recursive: true);
                return;
            }
            catch (IOException)
            {
                // 文件仍被占用（Windows 常见）：让出时间片后重试。
                Thread.Sleep(50 * (attempt + 1));
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(50 * (attempt + 1));
            }
        }
    }
}
