using System.Collections.Concurrent;
using VodBox.Core;

namespace VodBox.Infrastructure;

/// <summary>站点注册表：持有当前配置的全部内容源实例，负责创建/缓存/切换。</summary>
public sealed class SourceRegistry : IDisposable
{
    private readonly DefaultHttp _http = new();
    private readonly ConcurrentDictionary<string, IContentSource> _sources = new();

    private readonly IPreferences? _preferences;
    private readonly Func<string,CancellationToken,Task<TvBoxConfig>> _loadConfig;
    private long _configurationGeneration;
    public IReadOnlyList<SourceInfo> Sources { get; private set; } = [];
    public IReadOnlyList<SourceInfo> ChangeableSources=>VisibleSources.Where(CanChange).ToArray();
    public IReadOnlyList<SourceInfo> VisibleSources => Sources.Where(source => !IsHidden(source.Key)).ToArray();
    public SourceRegistry(IPreferences? preferences = null)
    {
        _preferences=preferences;
        _loadConfig=async(address,ct)=>File.Exists(address)
            ? ConfigLoader.Parse(await File.ReadAllTextAsync(address,ct))??throw new InvalidDataException("配置解析失败")
            : await new ConfigLoader(_http).LoadAnyAsync(address,ct);
    }
    internal SourceRegistry(Func<string,CancellationToken,Task<TvBoxConfig>> loadConfig,IPreferences? preferences=null)
    { _preferences=preferences;_loadConfig=loadConfig; }
    private static string PreferenceKey(string key, string property) => "site." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key))) + "." + property;
    public bool IsHidden(string key) => _preferences?.GetBool(PreferenceKey(key, "hidden")) ?? false;
    public bool IsStarred(string key) => _preferences?.GetBool(PreferenceKey(key, "starred")) ?? false;
    public bool CanSearch(SourceInfo site) => _preferences?.GetBool(PreferenceKey(site.Key, "searchable"), site.Searchable) ?? site.Searchable;
    public void SetHidden(string key, bool value) { _preferences?.Set(PreferenceKey(key, "hidden"), value); SourcesChanged?.Invoke(); }
    public void SetStarred(string key, bool value) { _preferences?.Set(PreferenceKey(key, "starred"), value); SortSources(); SourcesChanged?.Invoke(); }
    public void SetSearchable(string key, bool value) { _preferences?.Set(PreferenceKey(key, "searchable"), value); SourcesChanged?.Invoke(); }
    public void SetChangeable(string key, bool value) { _preferences?.Set(PreferenceKey(key, "changeable"), value); SourcesChanged?.Invoke(); }
    public bool CanChange(SourceInfo site) => _preferences?.GetBool(PreferenceKey(site.Key, "changeable"), site.Changeable) ?? site.Changeable;
    public void MoveUp(string key)
    {
        var list = Sources.ToList(); var index = list.FindIndex(source => source.Key == key);
        if (index <= 0) return;
        (list[index - 1], list[index]) = (list[index], list[index - 1]);
        for (var position = 0; position < list.Count; position++) _preferences?.Set(PreferenceKey(list[position].Key, "order"), position);
        Sources = list; SourcesChanged?.Invoke();
    }
    private void SortSources() => Sources = Sources.OrderByDescending(source => IsStarred(source.Key))
        .ThenBy(source => _preferences?.GetInt(PreferenceKey(source.Key, "order"), int.MaxValue) ?? int.MaxValue).ToArray();

    public IReadOnlyList<TvBoxParse> Parses { get; private set; } = [];
    public IReadOnlyList<string> ImportWarnings { get; private set; } = [];
    public event Action? SourcesChanged;

    /// <summary>加载 TVBox 配置（URL 或本地路径）并重建源列表。</summary>
    public async Task LoadConfigAsync(string urlOrPath, CancellationToken ct = default)
    {
        var generation=Interlocked.Increment(ref _configurationGeneration);
        var config=await _loadConfig(urlOrPath,ct);
        ct.ThrowIfCancellationRequested();
        if(generation!=Volatile.Read(ref _configurationGeneration))throw new OperationCanceledException("配置请求已被新配置替代。");
        var supported=ConfigLoader.ToSources(config);
        var warnings=new List<string>();
        foreach(var site in config.Sites)
            if(!supported.Any(source=>source.Key==site.Key))warnings.Add($"{site.Name}：未注册（缺少桌面运行时或站点定义无效）");
        var duplicate=supported.GroupBy(source=>source.Key).Where(group=>group.Count()>1).ToArray();
        foreach(var group in duplicate)warnings.Add($"站点键 {group.Key} 重复，只保留第一项");
        Sources = supported.DistinctBy(source=>source.Key).ToArray();
        ImportWarnings=warnings;
        Parses = config.Parses;
        SortSources();
        Rebuild();
    }

    private void Rebuild()
    {
        foreach (var source in _sources.Values.OfType<IDisposable>()) source.Dispose();
        _sources.Clear();
        foreach (var info in Sources)
        {
            switch (info.Runtime)
            {
                case SourceRuntime.MacCms:
                    _sources[info.Key] = new MacCmsSource(info, _http);
                    break;
                case SourceRuntime.NativeSpider:
                    if (NativeSpiders.Create(info) is { } native) _sources[info.Key] = native;
                    break;
                case SourceRuntime.QuickJs:
                    _sources[info.Key] = new DrpySource(info);
                    break;
                // Python/Node（含未适配 csp_）不在册。
            }
        }
        SourcesChanged?.Invoke();
    }

    public IContentSource? Get(string key) => _sources.TryGetValue(key, out var source) ? source : null;

    /// <summary>默认源：第一个已注册且有实现的源（MacCMS 或原生爬虫）。</summary>
    public IContentSource? Default() => VisibleSources.FirstOrDefault(s => Get(s.Key) is not null) is { } info ? Get(info.Key) : null;

    /// <summary>并发聚合搜索全部 searchable 源，结果带站点来源标记。</summary>
    public async Task<IReadOnlyList<(SourceInfo Site, MediaItem Item)>> SearchAllAsync(string query, CancellationToken ct = default) =>
        (await SearchWithFailuresAsync(query, ct).ConfigureAwait(false)).Results;

    /// <summary>
    /// 并发聚合搜索，同时返回逐源失败原因。单站失败不影响其他站点，但失败必须可见——
    /// 静默吞异常会让「搜索结果为 0」与「所有站点都出错」无法区分，排查时无从下手。
    /// </summary>
    public async Task<AggregateSearchResult> SearchWithFailuresAsync(string query, CancellationToken ct = default)
    {
        var results = new ConcurrentBag<(SourceInfo Site, MediaItem Item)>();
        var failures = new ConcurrentBag<(SourceInfo Site, string Error)>();
        var tasks = Sources
            .Where(s => !IsHidden(s.Key) && CanSearch(s) && Get(s.Key) is not null)
            .Select(async site =>
            {
                // 逐源独立超时：慢站点不拖累整体，也不因共享 ct 而互相取消
                using var perSite = CancellationTokenSource.CreateLinkedTokenSource(ct);
                perSite.CancelAfter(SearchTimeout);
                try
                {
                    var page = await Get(site.Key)!.SearchAsync(query, 1, perSite.Token);
                    foreach (var item in page.Items) results.Add((site, item));
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    failures.Add((site, $"超时（{SearchTimeout.TotalSeconds:0}s）"));
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    failures.Add((site, error.Message));
                }
            });
        await Task.WhenAll(tasks);
        return new AggregateSearchResult(results.ToList(), failures.ToList());
    }

    /// <summary>单站聚合搜索超时（独立于调用方 ct）。</summary>
    public static readonly TimeSpan SearchTimeout = TimeSpan.FromSeconds(25);

    public void Dispose()
    {
        foreach (var source in _sources.Values.OfType<IDisposable>()) source.Dispose();
        _sources.Clear();
        _http.Dispose();
    }
}

/// <summary>聚合搜索结果：命中条目 + 逐源失败原因。</summary>
public sealed record AggregateSearchResult(
    IReadOnlyList<(SourceInfo Site, MediaItem Item)> Results,
    IReadOnlyList<(SourceInfo Site, string Error)> Failures);
