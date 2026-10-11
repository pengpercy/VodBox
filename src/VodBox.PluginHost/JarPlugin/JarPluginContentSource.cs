using System.Text.Json;
using VodBox.Core;

namespace VodBox.PluginHost.JarPlugin;

/// <summary>
/// CatVod JAR 源适配器：把 <see cref="IContentSource"/> 的 home/category/search/detail 映射到宿主命令，
/// 并在播放时通过 <see cref="IResolvingContentSource"/> 现取地址（保留 header/parse/jx/playUrl 信息）。
///
/// 只有宿主可用（Java + host.jar 齐备、存在有效 JAR 引用）时才能创建；否则 <see cref="TryCreate"/>
/// 返回 false，源保持未注册、并给出明确禁用提示——绝不声明可播。
/// </summary>
public sealed class JarPluginContentSource : IContentSource, IResolvingContentSource, IDisposable
{
    private static readonly TimeSpan HomeCacheLifetime = TimeSpan.FromSeconds(30);

    private readonly SourceInfo _site;
    private readonly JarPluginSourceLayout _layout;
    private readonly JarPluginRuntime _runtime;
    private readonly Dictionary<string, MediaDetail> _details = new(StringComparer.Ordinal);
    private readonly object _cacheLock = new();
    private string? _home;
    private DateTimeOffset _homeAt;

    internal JarPluginContentSource(SourceInfo site, JarPluginSourceLayout layout, JarPluginRuntime runtime)
    {
        _site = site;
        _layout = layout;
        _runtime = runtime;
    }

    public string Key => _site.Key;
    public string Name => _site.Name;

    /// <summary>宿主是否可用（Java 运行时 + 宿主 JAR 齐备）。不可用时不得创建 JAR 源。</summary>
    public static bool HostAvailable => JarPluginHostLocator.TryResolve(out _, null);

    /// <summary>宿主不可用或没有有效 JAR 引用时返回 false，调用方据此保持该源禁用。</summary>
    public static bool TryCreate(SourceInfo site, out JarPluginContentSource? source, IJarPluginArtifactStore? artifacts = null, string? workRoot = null)
    {
        source = null;
        var layout = JarPluginSourceLayout.From(site);
        if (layout.EffectiveJar is null) return false;
        // artifacts 为 null 时由运行时构造默认存储（含 DEX→JVM 转换与缓存）。
        if (!JarPluginRuntime.TryCreate(artifacts, out var runtime, workRoot) || runtime is null) return false;
        source = new JarPluginContentSource(site, layout, runtime);
        return true;
    }

    // ---------- 目录 / 首页 ----------

    public async Task<IReadOnlyList<Category>> GetCategoriesAsync(CancellationToken ct = default) =>
        CatVodCatalog.ParseCategories(await HomePayloadAsync(ct).ConfigureAwait(false));

    public async Task<IReadOnlyList<FilterGroup>> GetFiltersAsync(string categoryId, CancellationToken ct = default) =>
        CatVodCatalog.ParseFilters(await HomePayloadAsync(ct).ConfigureAwait(false), categoryId);

    public async Task<MediaPage> GetHomeAsync(CancellationToken ct = default) =>
        CatVodCatalog.ParsePage(await HomePayloadAsync(ct).ConfigureAwait(false), 1);

    public async Task<MediaPage> GetItemsAsync(string categoryId, int page, IReadOnlyDictionary<string, string>? filters, CancellationToken ct = default)
    {
        var parameters = new Dictionary<string, string>
        {
            [JarPluginProtocol.Parameter.Category] = string.IsNullOrEmpty(categoryId) ? "0" : categoryId,
            [JarPluginProtocol.Parameter.Page] = Math.Max(1, page).ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        if (filters is { Count: > 0 })
            parameters[JarPluginProtocol.Parameter.Extend] = JsonSerializer.Serialize(new Dictionary<string, string>(filters), JarPluginJsonContext.Default.DictionaryStringString);
        return CatVodCatalog.ParsePage(await CallAsync(parameters, ct).ConfigureAwait(false), Math.Max(1, page));
    }

    public async Task<MediaPage> SearchAsync(string query, int page, CancellationToken ct = default)
    {
        var parameters = new Dictionary<string, string>
        {
            [JarPluginProtocol.Parameter.Key] = query,
            [JarPluginProtocol.Parameter.Page] = Math.Max(1, page).ToString(System.Globalization.CultureInfo.InvariantCulture),
            [JarPluginProtocol.Parameter.Quick] = "false",
        };
        return CatVodCatalog.ParsePage(await CallAsync(parameters, ct).ConfigureAwait(false), Math.Max(1, page));
    }

    // ---------- 详情 ----------

    public async Task<MediaDetail> GetDetailAsync(string mediaId, CancellationToken ct = default)
    {
        var payload = await CallAsync(new Dictionary<string, string> { [JarPluginProtocol.Parameter.Ids] = mediaId }, ct).ConfigureAwait(false);
        var detail = CatVodCatalog.ParseDetail(payload, mediaId);
        Remember(mediaId, detail);
        return detail;
    }

    // ---------- 播放 ----------

    public async Task<PlaybackRequest> ResolvePlaybackAsync(string mediaId, string episodeId, CancellationToken ct = default)
    {
        var (flag, url) = CatVodCatalog.DecodeEpisodeId(episodeId);
        var parameters = new Dictionary<string, string>
        {
            [JarPluginProtocol.Parameter.Play] = url,
            [JarPluginProtocol.Parameter.Flag] = flag,
            [JarPluginProtocol.Parameter.DetailId] = mediaId,
        };
        var payload = await CallAsync(parameters, ct).ConfigureAwait(false);
        var detail = Recall(mediaId);
        var line = detail?.Lines.FirstOrDefault(candidate => candidate.Id == flag) ?? detail?.Lines.FirstOrDefault(candidate => candidate.Episodes.Any(episode => episode.Id == episodeId));
        return CatVodCatalog.ParsePlayback(payload, url, detail?.Item.Title ?? "", Key, Name, mediaId,
            line?.Id ?? flag, episodeId, detail?.Item.Poster, detail?.Item.Remarks);
    }

    // ---------- 宿主调用 ----------

    private async Task<string> HomePayloadAsync(CancellationToken ct)
    {
        lock (_cacheLock)
            if (_home is not null && DateTimeOffset.UtcNow - _homeAt < HomeCacheLifetime) return _home;
        var payload = await CallAsync(new Dictionary<string, string>(), ct).ConfigureAwait(false);
        lock (_cacheLock)
        {
            _home = payload;
            _homeAt = DateTimeOffset.UtcNow;
        }
        return payload;
    }

    private async Task<string> CallAsync(IReadOnlyDictionary<string, string> parameters, CancellationToken ct)
    {
        var envelope = await _runtime.RequestAsync(_layout, parameters, ct).ConfigureAwait(false);
        if (JarPluginErrors.Classify(envelope) is { } failure) throw new JarPluginException(failure);
        return string.IsNullOrWhiteSpace(envelope.Result) ? "{}" : envelope.Result!;
    }

    private void Remember(string mediaId, MediaDetail detail)
    {
        lock (_cacheLock)
        {
            _details[mediaId] = detail;
            if (_details.Count > 4) _details.Remove(_details.Keys.First());
        }
    }

    private MediaDetail? Recall(string mediaId)
    {
        lock (_cacheLock) return _details.TryGetValue(mediaId, out var detail) ? detail : null;
    }

    public void Dispose() => _runtime.DisposeAsync().AsTask().GetAwaiter().GetResult();
}
