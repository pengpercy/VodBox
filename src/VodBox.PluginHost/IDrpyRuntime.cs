namespace VodBox.PluginHost;

/// <summary>单站 drpy 协议；运行时由内容源拥有，JSON 结果由映射层解释。</summary>
public interface IDrpyRuntime : IDisposable
{
    Task InitAsync(string ext, CancellationToken ct = default);
    Task<string> HomeAsync(CancellationToken ct = default);
    Task<string> HomeVodAsync(CancellationToken ct = default);
    Task<string> CategoryAsync(string tid, int page, IReadOnlyDictionary<string, string>? filters, CancellationToken ct = default);
    Task<string> DetailAsync(string mediaId, CancellationToken ct = default);
    Task<string> SearchAsync(string query, int page, CancellationToken ct = default);
    Task<string> PlayAsync(string flag, string id, IReadOnlyList<string> flags, CancellationToken ct = default);
}
