using System.Collections.Concurrent;
using VodBox.Core;

namespace VodBox.Infrastructure;

/// <summary>站点注册表：持有当前配置的全部内容源实例，负责创建/缓存/切换。</summary>
public sealed class SourceRegistry : IDisposable
{
    private readonly DefaultHttp _http = new();
    private readonly ConcurrentDictionary<string, IContentSource> _sources = new();

    public IReadOnlyList<SourceInfo> Sources { get; private set; } = [];

    public event Action? SourcesChanged;

    /// <summary>加载 TVBox 配置（URL 或本地路径）并重建源列表。</summary>
    public async Task LoadConfigAsync(string urlOrPath, CancellationToken ct = default)
    {
        TvBoxConfig config;
        if (File.Exists(urlOrPath))
            config = ConfigLoader.Parse(await File.ReadAllTextAsync(urlOrPath, ct))
                     ?? throw new InvalidDataException($"配置解析失败：{urlOrPath}");
        else
            config = await new ConfigLoader(_http).LoadAnyAsync(urlOrPath, ct);
        Sources = ConfigLoader.ToSources(config);
        Rebuild();
    }

    private void Rebuild()
    {
        _sources.Clear();
        foreach (var info in Sources.Where(s => s.Runtime == SourceRuntime.MacCms))
            _sources[info.Key] = new MacCmsSource(info, _http);
        SourcesChanged?.Invoke();
    }

    public IContentSource? Get(string key) => _sources.TryGetValue(key, out var source) ? source : null;

    /// <summary>默认源：第一个可用的 MacCMS 源。</summary>
    public IContentSource? Default() => Sources.FirstOrDefault(s => s.Runtime == SourceRuntime.MacCms) is { } info ? Get(info.Key) : null;

    /// <summary>并发聚合搜索全部 searchable 源，结果带站点来源标记。</summary>
    public async Task<IReadOnlyList<(SourceInfo Site, MediaItem Item)>> SearchAllAsync(string query, CancellationToken ct = default)
    {
        var results = new ConcurrentBag<(SourceInfo, MediaItem)>();
        var tasks = Sources
            .Where(s => s.Searchable && s.Runtime == SourceRuntime.MacCms)
            .Select(async site =>
            {
                try
                {
                    var source = Get(site.Key);
                    if (source is null) return;
                    var page = await source.SearchAsync(query, 1, ct);
                    foreach (var item in page.Items) results.Add((site, item));
                }
                catch
                {
                    // 单站搜索失败不影响其他站点
                }
            });
        await Task.WhenAll(tasks);
        return results.ToList();
    }

    public void Dispose() => _http.Dispose();
}
