using System.Runtime.CompilerServices;
using System.Threading.Channels;
using VodBox.Core;

namespace VodBox.Infrastructure;

/// <summary>
/// 点播目录：源配置 + 采集客户端 + 聚合搜索/换源/源检测（LunaTV /api/search/ws、/api/detail、/api/admin/source/validate）。
/// </summary>
public sealed class VodCatalog : IDisposable
{
    public SourceConfigStore Config { get; }
    public CmsClient Cms { get; }

    /// <summary>聚合搜索的有限并发：LunaTV 服务端一次打出全部请求，桌面端控制在 32 路以内。</summary>
    public const int SearchConcurrency = 32;
    /// <summary>单源整体超时（含多页），对应 LunaTV 的 20 秒 race。</summary>
    public static readonly TimeSpan SourceTimeout = TimeSpan.FromSeconds(20);

    public VodCatalog(SourceConfigStore config, CmsClient? cms = null)
    {
        Config = config;
        Cms = cms ?? new CmsClient(() => config.Settings);
        config.Changed += Cms.ClearCache;
    }

    public IReadOnlyList<ApiSite> SearchTargets => Config.EnabledSources;
    public ApiSite? Find(string key) => Config.Find(key);

    /// <summary>LunaTV 黄色过滤：type_name 命中敏感分类即丢弃（设置里可关闭）。</summary>
    public static readonly string[] YellowWords =
    [
        "伦理片", "福利", "里番动漫", "门事件", "萝莉少女", "制服诱惑", "国产传媒", "cosplay", "黑丝诱惑", "无码", "日本无码", "有码", "日本有码",
        "SWAG", "网红主播", "色情片", "同性片", "福利视频", "福利片", "写真热舞", "倫理片", "理论片", "韩国伦理", "港台三级", "电影解说", "伦理", "日本伦理",
    ];

    public IReadOnlyList<VodResult> FilterYellow(IReadOnlyList<VodResult> results) => Config.Settings.DisableYellowFilter ? results
        : results.Where(result => !YellowWords.Any(word => (result.TypeName ?? "").Contains(word, StringComparison.Ordinal))).ToArray();

    /// <summary>单源搜索（含多页、黄色过滤与整体超时）。</summary>
    public async Task<IReadOnlyList<VodResult>> SearchSourceAsync(ApiSite site, string query, CancellationToken ct = default)
    {
        using var scope = CancellationTokenSource.CreateLinkedTokenSource(ct);
        scope.CancelAfter(SourceTimeout);
        try { return FilterYellow(await Cms.SearchAsync(site, query, scope.Token).ConfigureAwait(false)); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException($"超时（{SourceTimeout.TotalSeconds:0}s）"); }
    }

    /// <summary>
    /// 流式聚合搜索：每个源完成即产出一批（结果或失败原因），先到先得；单源失败不影响其他源。
    /// </summary>
    public async IAsyncEnumerable<SourceSearchBatch> SearchStreamAsync(string query, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var targets = SearchTargets;
        var channel = Channel.CreateUnbounded<SourceSearchBatch>(new UnboundedChannelOptions { SingleReader = true });
        var producer = Task.Run(async () =>
        {
            try
            {
                await Parallel.ForEachAsync(targets, new ParallelOptions { MaxDegreeOfParallelism = SearchConcurrency, CancellationToken = ct }, async (site, token) =>
                {
                    try { channel.Writer.TryWrite(new SourceSearchBatch(site, await SearchSourceAsync(site, query, token).ConfigureAwait(false))); }
                    catch (Exception error) when (error is not OperationCanceledException || !token.IsCancellationRequested)
                    {
                        channel.Writer.TryWrite(new SourceSearchBatch(site, [], Describe(error)));
                    }
                }).ConfigureAwait(false);
                channel.Writer.TryComplete();
            }
            catch (Exception error) { channel.Writer.TryComplete(error is OperationCanceledException ? null : error); }
        }, CancellationToken.None);
        await foreach (var batch in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false)) yield return batch;
        _ = producer;
    }

    /// <summary>一次性聚合搜索（等待全部源完成）。</summary>
    public async Task<(IReadOnlyList<VodResult> Results, IReadOnlyList<SourceSearchBatch> Failures)> SearchAllAsync(string query, CancellationToken ct = default)
    {
        var results = new List<VodResult>();
        var failures = new List<SourceSearchBatch>();
        await foreach (var batch in SearchStreamAsync(query, ct).ConfigureAwait(false))
        {
            if (batch.Error is not null) failures.Add(batch);
            else results.AddRange(batch.Results);
        }
        return (results, failures);
    }

    /// <summary>
    /// 换源候选（LunaTV play 页 fetchSourcesData）：全源搜索片名，只留标题（去空格、忽略大小写）一致、
    /// 年份一致（已知时）、电影/剧集类型一致（已知时）的结果。
    /// </summary>
    public async IAsyncEnumerable<VodResult> FindSourcesStreamAsync(string title, string? year, string? kind, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var batch in SearchStreamAsync(title.Trim(), ct).ConfigureAwait(false))
            foreach (var result in batch.Results.Where(result => SearchAggregation.Matches(result, title, year, kind)))
                yield return result;
    }

    /// <summary>获取指定源上的详情：先在该源搜索片名精确命中（列表接口自带选集），否则走详情接口。</summary>
    public async Task<VodResult> GetDetailAsync(string sourceKey, string id, string? fallbackTitle = null, CancellationToken ct = default)
    {
        var site = Find(sourceKey) ?? throw new InvalidOperationException($"源「{sourceKey}」不在当前配置中");
        if (!string.IsNullOrWhiteSpace(fallbackTitle))
        {
            try
            {
                var match = (await Cms.SearchAsync(site, fallbackTitle.Trim(), ct).ConfigureAwait(false)).FirstOrDefault(result => result.Id == id);
                if (match is not null) return match;
            }
            catch (Exception error) when (error is not OperationCanceledException || !ct.IsCancellationRequested) { }
        }
        return await Cms.GetDetailAsync(site, id, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 源有效性检测（LunaTV source/validate）：用关键词搜索首页，标题包含关键词 = 有效；
    /// 返回但无匹配 = 无结果；请求失败 = 无效。逐源流式产出。
    /// </summary>
    public async IAsyncEnumerable<SourceValidation> ValidateStreamAsync(string keyword, IReadOnlyList<ApiSite>? sites = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var targets = sites ?? Config.Sources;
        var channel = Channel.CreateUnbounded<SourceValidation>(new UnboundedChannelOptions { SingleReader = true });
        var producer = Task.Run(async () =>
        {
            try
            {
                await Parallel.ForEachAsync(targets, new ParallelOptions { MaxDegreeOfParallelism = SearchConcurrency, CancellationToken = ct }, async (site, token) =>
                {
                    var started = DateTimeOffset.UtcNow;
                    try
                    {
                        using var scope = CancellationTokenSource.CreateLinkedTokenSource(token);
                        scope.CancelAfter(TimeSpan.FromSeconds(10));
                        var (results, _) = await Cms.SearchPageAsync(site, keyword, 1, scope.Token).ConfigureAwait(false);
                        var elapsed = DateTimeOffset.UtcNow - started;
                        var status = results.Any(result => result.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase)) ? SourceValidity.Valid : SourceValidity.NoResults;
                        channel.Writer.TryWrite(new SourceValidation(site, status, elapsed, null));
                    }
                    catch (Exception error) when (error is not OperationCanceledException || !token.IsCancellationRequested)
                    {
                        channel.Writer.TryWrite(new SourceValidation(site, SourceValidity.Invalid, DateTimeOffset.UtcNow - started, Describe(error)));
                    }
                }).ConfigureAwait(false);
                channel.Writer.TryComplete();
            }
            catch (Exception error) { channel.Writer.TryComplete(error is OperationCanceledException ? null : error); }
        }, CancellationToken.None);
        await foreach (var item in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false)) yield return item;
        _ = producer;
    }

    internal static string Describe(Exception error) => error switch
    {
        OperationCanceledException => "超时",
        HttpRequestException { StatusCode: { } status } => $"HTTP {(int)status}",
        HttpRequestException http => http.InnerException?.Message ?? http.Message,
        _ => error.Message,
    };

    public void Dispose() => Cms.Dispose();
}

/// <summary>某个源的一批搜索结果或失败原因。</summary>
public sealed record SourceSearchBatch(ApiSite Site, IReadOnlyList<VodResult> Results, string? Error = null);

public enum SourceValidity { Valid, NoResults, Invalid }

public sealed record SourceValidation(ApiSite Site, SourceValidity Status, TimeSpan Elapsed, string? Error);

/// <summary>聚合搜索分组（LunaTV search 页 aggregatedResults）。</summary>
public static class SearchAggregation
{
    private static string Normalize(string title) => title.Replace(" ", "", StringComparison.Ordinal).Trim();

    /// <summary>聚合键：去空格标题 + 年份 + 电影/剧集（集数为 1 视为电影）。</summary>
    public static string GroupKey(VodResult result) =>
        $"{Normalize(result.Title)}-{(string.IsNullOrEmpty(result.Year) ? "unknown" : result.Year)}-{(result.IsMovie ? "movie" : "tv")}";

    /// <summary>按出现顺序分组。</summary>
    public static IReadOnlyList<IReadOnlyList<VodResult>> Group(IEnumerable<VodResult> results)
    {
        var order = new List<string>();
        var groups = new Dictionary<string, List<VodResult>>(StringComparer.Ordinal);
        foreach (var result in results)
        {
            var key = GroupKey(result);
            if (!groups.TryGetValue(key, out var list)) { groups[key] = list = []; order.Add(key); }
            list.Add(result);
        }
        return order.Select(key => (IReadOnlyList<VodResult>)groups[key]).ToArray();
    }

    /// <summary>组内出现次数最多的豆瓣 id（LunaTV computeGroupStats）。</summary>
    public static string? MajorityDoubanId(IEnumerable<VodResult> group) => group.Where(result => result.DoubanId is { Length: > 0 })
        .GroupBy(result => result.DoubanId!).OrderByDescending(ids => ids.Count()).Select(ids => ids.Key).FirstOrDefault();

    /// <summary>换源匹配：标题一致（去空格、忽略大小写）；年份与类型已知时也需一致。</summary>
    public static bool Matches(VodResult result, string title, string? year, string? kind)
    {
        if (!string.Equals(Normalize(result.Title), Normalize(title), StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.IsNullOrWhiteSpace(year) && year != "unknown" && !string.Equals(result.Year, year, StringComparison.OrdinalIgnoreCase)) return false;
        return kind switch
        {
            "tv" => result.EpisodeCount > 1,
            "movie" => result.EpisodeCount == 1,
            _ => true,
        };
    }

    /// <summary>聚合组转为一张卡片：取首个有海报的条目为代表，带源数量与多数豆瓣 id。</summary>
    public static MediaItem ToCard(IReadOnlyList<VodResult> group)
    {
        var first = group.FirstOrDefault(result => result.Poster.Length > 0) ?? group[0];
        return new MediaItem
        {
            Id = first.Id,
            Title = first.Title,
            Poster = first.Poster,
            Year = first.Year == "unknown" ? null : first.Year,
            TypeName = first.TypeName,
            Remarks = first.EpisodeCount > 1 ? $"{first.EpisodeCount} 集" : first.Remarks,
            SourceKey = first.SourceKey,
            SourceName = group.Count > 1 ? $"{group.Count} 个源" : first.SourceName,
            SourceCount = group.Count,
            DoubanId = MajorityDoubanId(group),
            Kind = first.IsMovie ? "movie" : "tv",
            Area = first.Area,
        };
    }

    /// <summary>单个结果转卡片（非聚合视图）。</summary>
    public static MediaItem ToItem(VodResult result) => new()
    {
        Id = result.Id,
        Title = result.Title,
        Poster = result.Poster,
        Year = result.Year == "unknown" ? null : result.Year,
        TypeName = result.TypeName,
        Remarks = result.Remarks,
        Area = result.Area,
        SourceKey = result.SourceKey,
        SourceName = result.SourceName,
        DoubanId = result.DoubanId,
        Kind = result.IsMovie ? "movie" : "tv",
    };
}
