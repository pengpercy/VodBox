using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using VodBox.Core;
using VodBox.PluginHost.JarPlugin;

namespace VodBox.Infrastructure;

/// <summary>站点注册表：持有当前配置的全部内容源实例，负责创建/缓存/切换。</summary>
public sealed class SourceRegistry : IDisposable
{
    private readonly DefaultHttp _http = new();
    private readonly ConcurrentDictionary<string, IContentSource> _sources = new();

    private readonly IPreferences? _preferences;
    private readonly Func<string,CancellationToken,Task<TvBoxConfig>> _loadConfig;
    private readonly bool _jarHostEnabled;
    private string? _subscriptionOrigin;
    private long _configurationGeneration;
    public IReadOnlyList<SourceInfo> Sources { get; private set; } = [];
    // TVBox 的 changeable 不等于手动选站权限；这里只检查显示状态与实际运行时实现。
    public IReadOnlyList<SourceInfo> SelectableSources => VisibleSources.Where(source => Get(source.Key) is not null).ToArray();
    public IReadOnlyList<SourceInfo> ChangeableSources=>VisibleSources.Where(CanChange).ToArray();
    public IReadOnlyList<SourceInfo> VisibleSources => Sources.Where(source => !IsHidden(source.Key)).ToArray();
    public SourceRegistry(IPreferences? preferences = null, bool enableJarPluginHost = false)
    {
        _preferences=preferences;
        _jarHostEnabled=enableJarPluginHost;
        _loadConfig=async(address,ct)=>
        {
            if(File.Exists(address))
            {
                _subscriptionOrigin=null;
                return ConfigLoader.Parse(await File.ReadAllTextAsync(address,ct))??throw new InvalidDataException("配置解析失败");
            }
            var loaded=await new ConfigLoader(_http).LoadAnyWithOriginAsync(address,ct);
            _subscriptionOrigin=loaded.Origin;
            return loaded.Config;
        };
    }
    internal SourceRegistry(Func<string,CancellationToken,Task<TvBoxConfig>> loadConfig,IPreferences? preferences=null,bool enableJarPluginHost=false)
    { _preferences=preferences;_loadConfig=loadConfig;_jarHostEnabled=enableJarPluginHost; }
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
        var supported=ConfigLoader.ToSources(config,_subscriptionOrigin,_jarHostEnabled);
        var warnings=new List<string>();
        foreach(var site in config.Sites)
            if(!supported.Any(source=>source.Key==site.Key))warnings.Add($"{site.Name}：未注册（缺少桌面运行时或站点定义无效）");
        if(_jarHostEnabled) warnings.AddRange(JarPluginWarnings(supported));
        var duplicate=supported.GroupBy(source=>source.Key).Where(group=>group.Count()>1).ToArray();
        foreach(var group in duplicate)warnings.Add($"站点键 {group.Key} 重复，只保留第一项");
        Sources = supported.DistinctBy(source=>source.Key).ToArray();
        ImportWarnings=warnings;
        Parses = config.Parses;
        SortSources();
        Rebuild();
    }

    /// <summary>
    /// 启用了 JAR 宿主时，为每个 JAR 源生成可见的禁用说明：宿主不可用、或缺 JAR 引用。
    /// 只有能真正运行的源才不产生提示——避免「导入后静默少一截」。
    /// </summary>
    private static IEnumerable<string> JarPluginWarnings(IReadOnlyList<SourceInfo> supported)
    {
        var jarSources = supported.Where(source => source.Runtime == SourceRuntime.Jar).ToArray();
        if (jarSources.Length == 0) return [];
        if (!JarPluginContentSource.HostAvailable)
            return jarSources.Select(source => $"{source.Name}：需要 JAR 插件宿主（未找到 Java 运行时或 host.jar），已禁用");
        return jarSources
            .Where(source => JarPluginSourceLayout.From(source).EffectiveJar is null)
            .Select(source => $"{source.Name}：站点与配置均未提供插件 JAR，已禁用");
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
                case SourceRuntime.Type4:
                    _sources[info.Key] = new Type4Source(info, _http);
                    break;
                case SourceRuntime.NativeSpider:
                    if (NativeSpiders.Create(info) is { } native) _sources[info.Key] = native;
                    break;
                case SourceRuntime.QuickJs:
                    _sources[info.Key] = new DrpySource(info);
                    break;
                case SourceRuntime.Jar:
                    // 只在宿主可用且有 JAR 引用时创建；否则保持未注册（禁用），
                    // 由 ImportWarnings 明确提示，而非伪造一个不可播放的源。
                    if (JarPluginContentSource.TryCreate(info, out var jarPlugin) && jarPlugin is not null)
                        _sources[info.Key] = jarPlugin;
                    break;
                // Python/Node（含未适配 csp_）不在册。
            }
        }
        SourcesChanged?.Invoke();
    }

    public IContentSource? Get(string key) => _sources.TryGetValue(key, out var source) ? source : null;

    /// <summary>默认源：第一个已注册且有实现的源（MacCMS 或原生爬虫）。</summary>
    public IContentSource? Default() => VisibleSources.FirstOrDefault(s => Get(s.Key) is not null) is { } info ? Get(info.Key) : null;

    /// <summary>参与聚合搜索的源：可见（未被隐藏）、已开启搜索、且确实注册了桌面运行时实现。</summary>
    public IReadOnlyList<SourceInfo> SearchTargets =>
        Sources.Where(source => !IsHidden(source.Key) && CanSearch(source) && Get(source.Key) is not null).ToArray();

    /// <summary>并发聚合搜索全部 searchable 源，结果带站点来源标记。</summary>
    public async Task<IReadOnlyList<(SourceInfo Site, MediaItem Item)>> SearchAllAsync(string query, CancellationToken ct = default) =>
        (await SearchWithFailuresAsync(query, ct).ConfigureAwait(false)).Results;

    /// <summary>
    /// 并发聚合搜索，同时返回逐源失败原因。单站失败不影响其他站点，但失败必须可见——
    /// 静默吞异常会让「搜索结果为 0」与「所有站点都出错」无法区分，排查时无从下手。
    /// </summary>
    public async Task<AggregateSearchResult> SearchWithFailuresAsync(string query, CancellationToken ct = default)
    {
        var results = new List<(SourceInfo Site, MediaItem Item)>();
        var failures = new List<(SourceInfo Site, string Error)>();
        await foreach (var batch in SearchStreamAsync(query, ct).ConfigureAwait(false))
        {
            if (batch.Error is { } error) failures.Add((batch.Site, error));
            else foreach (var item in batch.Items) results.Add((batch.Site, item));
        }
        return new AggregateSearchResult(results, failures);
    }

    /// <summary>
    /// 流式聚合搜索：每完成一个站点就产出一批（该站的命中条目，或该站的失败原因），先到先得。
    /// 单站失败/超时只记录该站，其他站点的成功结果照常产出（部分失败保留成功）；
    /// 并发受 <see cref="SearchConcurrency"/> 限制，每站独立 <see cref="SearchTimeout"/>。
    /// </summary>
    public async IAsyncEnumerable<AggregateSearchBatch> SearchStreamAsync(
        string query, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var channel = Channel.CreateUnbounded<AggregateSearchBatch>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        var producer = Task.Run(async () =>
        {
            try
            {
                await Parallel.ForEachAsync(
                    SearchTargets,
                    new ParallelOptions { MaxDegreeOfParallelism = SearchConcurrency, CancellationToken = ct },
                    async (site, token) =>
                    {
                        // 逐源独立超时：慢站点不拖累整体，也不因共享 ct 而互相取消
                        using var perSite = CancellationTokenSource.CreateLinkedTokenSource(token);
                        perSite.CancelAfter(SearchTimeout);
                        try
                        {
                            var page = await Get(site.Key)!.SearchAsync(query, 1, perSite.Token).ConfigureAwait(false);
                            channel.Writer.TryWrite(new AggregateSearchBatch(site, page.Items.ToArray()));
                        }
                        catch (OperationCanceledException) when (!token.IsCancellationRequested)
                        {
                            channel.Writer.TryWrite(new AggregateSearchBatch(site, [], $"超时（{SearchTimeout.TotalSeconds:0}s）"));
                        }
                        catch (Exception error) when (error is not OperationCanceledException)
                        {
                            channel.Writer.TryWrite(new AggregateSearchBatch(site, [], error.Message));
                        }
                    }).ConfigureAwait(false);
                channel.Writer.TryComplete();
            }
            catch (Exception error) { channel.Writer.TryComplete(error); }
        });
        try
        {
            await foreach (var batch in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false)) yield return batch;
        }
        finally
        {
            // 消费者提前退出（取消/异常）时不再等生产者：取消已传给每个站点，它会自行收尾。
            _ = producer;
        }
    }

    /// <summary>聚合搜索的有限并发上限：站点多时不能一次打出几十个请求，也要保留足够并行度。</summary>
    public const int SearchConcurrency = 4;

    /// <summary>单站聚合搜索超时（独立于调用方 ct）。</summary>
    public static readonly TimeSpan SearchTimeout = TimeSpan.FromSeconds(25);

    public void Dispose()
    {
        foreach (var source in _sources.Values.OfType<IDisposable>()) source.Dispose();
        _sources.Clear();
        _http.Dispose();
    }
}

/// <summary>
/// 流式聚合搜索的一批结果：某个站点的命中条目，或该站点的失败原因（二者互斥）。
/// 站点名/键始终随批次返回，调用方据此保留每条结果的来源身份。
/// </summary>
public sealed record AggregateSearchBatch(SourceInfo Site, IReadOnlyList<MediaItem> Items, string? Error = null)
{
    /// <summary>该站没有成功条目，只有失败原因。</summary>
    public bool Failed => Error is not null;
}

/// <summary>聚合搜索结果：命中条目 + 逐源失败原因。</summary>
public sealed record AggregateSearchResult(
    IReadOnlyList<(SourceInfo Site, MediaItem Item)> Results,
    IReadOnlyList<(SourceInfo Site, string Error)> Failures);
