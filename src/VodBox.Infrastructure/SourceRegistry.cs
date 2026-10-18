using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using VodBox.Core;

namespace VodBox.Infrastructure;

/// <summary>站点注册表：持有当前配置的全部内容源实例，负责创建/缓存/切换。</summary>
public sealed class SourceRegistry : IDisposable
{
    private readonly DefaultHttp _http = new();
    private readonly ConcurrentDictionary<string, IContentSource> _sources = new();

    private readonly IPreferences? _preferences;
    private readonly Func<string,CancellationToken,Task<LoadedConfig>> _loadConfig;
    private readonly string? _configCacheDirectory;
    private string? _subscriptionOrigin;
    private long _configurationGeneration;
    public IReadOnlyList<SourceInfo> Sources { get; private set; } = [];
    // TVBox 的 changeable 不等于手动选站权限；这里只检查显示状态与实际运行时实现。
    public IReadOnlyList<SourceInfo> SelectableSources => VisibleSources.Where(source => Get(source.Key) is not null).ToArray();
    // 换源菜单只列真正能运行的站点：启用 JAR 宿主后，缺少插件 JAR 的 csp_ 站点也会出现在 Sources 里，但未注册。
    public IReadOnlyList<SourceInfo> ChangeableSources=>VisibleSources.Where(source=>CanChange(source)&&Get(source.Key) is not null).ToArray();
    public IReadOnlyList<SourceInfo> VisibleSources => Sources.Where(source => !IsHidden(source.Key)).ToArray();
    /// <param name="configCacheDirectory">远程配置的本地副本目录；为空时不缓存，启动只能等网络。</param>
    public SourceRegistry(IPreferences? preferences = null, string? configCacheDirectory = null)
    {
        _preferences=preferences;
        _configCacheDirectory=configCacheDirectory;
        _loadConfig=async(address,ct)=>
        {
            if(File.Exists(address))
                return new LoadedConfig(ConfigLoader.Parse(await File.ReadAllTextAsync(address,ct))??throw new InvalidDataException("配置解析失败"),null);
            return await new ConfigLoader(_http).LoadAnyWithOriginAsync(address,ct);
        };
    }
    internal SourceRegistry(Func<string,CancellationToken,Task<TvBoxConfig>> loadConfig,IPreferences? preferences=null,string? configCacheDirectory=null)
    {
        _preferences=preferences;_configCacheDirectory=configCacheDirectory;
        _loadConfig=async(address,ct)=>new LoadedConfig(await loadConfig(address,ct),null);
    }
    internal SourceRegistry(Func<string,CancellationToken,Task<LoadedConfig>> loadConfig,string configCacheDirectory)
    { _loadConfig=loadConfig;_configCacheDirectory=configCacheDirectory; }
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
    public async Task LoadConfigAsync(string urlOrPath, CancellationToken ct = default) =>
        await LoadConfigCoreAsync(urlOrPath, onlyIfChanged: false, ct);

    /// <summary>
    /// 后台刷新远程配置：内容与本地副本一致时不重建源列表（避免打断已在用的源），返回 false；
    /// 有变化才应用新配置并返回 true，调用方据此决定是否重载首页。
    /// </summary>
    public Task<bool> RefreshConfigAsync(string urlOrPath, CancellationToken ct = default) =>
        LoadConfigCoreAsync(urlOrPath, onlyIfChanged: true, ct);

    /// <summary>
    /// 启动快路径：用上次成功抓取的远程配置副本立即建好源列表，不等网络（订阅站点常需 1~3 秒）。
    /// 没有可用副本返回 false，调用方改走 <see cref="LoadConfigAsync"/>。
    /// </summary>
    public async Task<bool> TryLoadCachedConfigAsync(string urlOrPath, CancellationToken ct = default)
    {
        var generation=Interlocked.Increment(ref _configurationGeneration);
        if(await ReadConfigCacheAsync(urlOrPath,ct) is not { } cached)return false;
        var config=ConfigLoader.ParseBytes(cached.Raw)??ConfigLoader.Parse(DefaultHttp.Decode(cached.Raw));
        if(config is null||generation!=Volatile.Read(ref _configurationGeneration))return false;
        Apply(new LoadedConfig(config,cached.Origin,cached.Raw));
        return true;
    }

    private async Task<bool> LoadConfigCoreAsync(string urlOrPath, bool onlyIfChanged, CancellationToken ct)
    {
        var generation=Interlocked.Increment(ref _configurationGeneration);
        var loaded=await _loadConfig(urlOrPath,ct);
        ct.ThrowIfCancellationRequested();
        if(generation!=Volatile.Read(ref _configurationGeneration))throw new OperationCanceledException("配置请求已被新配置替代。");
        if(loaded.Raw is { } raw)
        {
            var cached=await ReadConfigCacheAsync(urlOrPath,ct);
            var unchanged=cached is not null&&cached.Origin==loaded.Origin&&cached.Raw.AsSpan().SequenceEqual(raw);
            if(!unchanged)await WriteConfigCacheAsync(urlOrPath,loaded);
            if(onlyIfChanged&&unchanged)return false;
        }
        Apply(loaded);
        return true;
    }

    private void Apply(LoadedConfig loaded)
    {
        var config=loaded.Config;
        _subscriptionOrigin=loaded.Origin;
        var supported=ConfigLoader.ToSources(config,_subscriptionOrigin);
        var warnings=new List<string>();
        var registered=supported.Select(source=>source.Key).ToHashSet(StringComparer.Ordinal);
        foreach(var site in config.Sites)
            if(!registered.Contains(site.Key))
            {
                // 未迁移的 csp_ 入口（Java/JAR 插件）单独说明，避免和真正无效的站点定义混在一起。
                var reason=site.Runtime==SourceRuntime.Node
                    ?"该 Java/JAR 插件尚未迁移为 C# 原生实现，已跳过"
                    :"未注册（缺少桌面运行时或站点定义无效）";
                warnings.Add($"{site.Name}：{reason}");
            }
        var duplicate=supported.GroupBy(source=>source.Key).Where(group=>group.Count()>1).ToArray();
        foreach(var group in duplicate)warnings.Add($"站点键 {group.Key} 重复，只保留第一项");
        Sources = supported.DistinctBy(source=>source.Key).ToArray();
        ImportWarnings=warnings;
        Parses = config.Parses;
        SortSources();
        Rebuild();
    }

    // 副本按地址散列存两份：原始字节（与网络响应逐字节一致，便于判断有无变化）+ 跟随重定向后的 origin。
    private string? ConfigCachePath(string urlOrPath)=>_configCacheDirectory is null?null
        :Path.Combine(_configCacheDirectory,Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(urlOrPath))));

    private sealed record CachedConfig(byte[] Raw,string? Origin);

    private async Task<CachedConfig?> ReadConfigCacheAsync(string urlOrPath,CancellationToken ct)
    {
        if(ConfigCachePath(urlOrPath) is not { } path||!File.Exists(path+".bin"))return null;
        try
        {
            var raw=await File.ReadAllBytesAsync(path+".bin",ct);
            var origin=File.Exists(path+".origin")?await File.ReadAllTextAsync(path+".origin",ct):null;
            return new CachedConfig(raw,string.IsNullOrEmpty(origin)?null:origin);
        }
        catch(IOException){return null;}
    }

    private async Task WriteConfigCacheAsync(string urlOrPath,LoadedConfig loaded)
    {
        if(ConfigCachePath(urlOrPath) is not { } path)return;
        try
        {
            Directory.CreateDirectory(_configCacheDirectory!);
            // 先写临时文件再替换，进程中途退出也不会留下半截副本。
            await File.WriteAllTextAsync(path+".origin.tmp",loaded.Origin??"");
            await File.WriteAllBytesAsync(path+".bin.tmp",loaded.Raw!);
            File.Move(path+".origin.tmp",path+".origin",overwrite:true);
            File.Move(path+".bin.tmp",path+".bin",overwrite:true);
        }
        catch(Exception error) when(error is IOException or UnauthorizedAccessException)
        {
            VodBoxLog.Warn("config",$"配置副本写入失败：{error.Message}");
        }
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
                // Python/Node（含未适配 csp_）不在册。
            }
        }
        SourcesChanged?.Invoke();
    }

    public IContentSource? Get(string key) => _sources.TryGetValue(key, out var source) ? source : null;

    /// <summary>
    /// 默认源（首页推荐）：第一个可运行的原生源。
    /// </summary>
    public IContentSource? Default()
    {
        var runnable = VisibleSources.Where(source => Get(source.Key) is not null).ToArray();
        // 首页默认源优先选能出内容的站点：跳过只给切源提示的信息源与只列片单的导航源，
        // 避免订阅里排在最前的“点我切源/导航”提醒项成为默认首页。找不到再回退第一项。
        return (runnable.FirstOrDefault(source => Get(source.Key) is not (IInformationalContentSource or INavigationContentSource))
            ?? runnable.FirstOrDefault()) is { } info ? Get(info.Key) : null;
    }

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
    /// 单站失败/超时只记录该站，其他站点的成功结果照常产出（部分失败保留成功）。
    /// </summary>
    public async IAsyncEnumerable<AggregateSearchBatch> SearchStreamAsync(
        string query, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var channel = Channel.CreateUnbounded<AggregateSearchBatch>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        var targets = SearchTargets;
        Task Lane(IEnumerable<SourceInfo> sites, int concurrency, TimeSpan timeout) => Parallel.ForEachAsync(
            sites,
            new ParallelOptions { MaxDegreeOfParallelism = concurrency, CancellationToken = ct },
            async (site, token) =>
            {
                // 逐源独立超时：慢站点不拖累整体，也不因共享 ct 而互相取消
                using var perSite = CancellationTokenSource.CreateLinkedTokenSource(token);
                perSite.CancelAfter(timeout);
                try
                {
                    var page = await Get(site.Key)!.SearchAsync(query, 1, perSite.Token).ConfigureAwait(false);
                    channel.Writer.TryWrite(new AggregateSearchBatch(site, page.Items.ToArray()));
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    channel.Writer.TryWrite(new AggregateSearchBatch(site, [], $"超时（{timeout.TotalSeconds:0}s）"));
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    channel.Writer.TryWrite(new AggregateSearchBatch(site, [], error.Message));
                }
            });
        var producer = Task.Run(async () =>
        {
            try
            {
                await Lane(targets, SearchConcurrency, SearchTimeout).ConfigureAwait(false);
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
