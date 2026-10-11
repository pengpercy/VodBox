using VodBox.Core;
using VodBox.Infrastructure;

namespace VodBox.Desktop.Services;

/// <summary>内置节目表的对外状态。</summary>
public enum EpgServiceState
{
    /// <summary>尚未加载，也没有缓存。</summary>
    Idle,
    /// <summary>首次读缓存。</summary>
    Loading,
    /// <summary>已有可用数据。</summary>
    Ready,
    /// <summary>后台更新中（旧数据仍可用）。</summary>
    Updating,
    /// <summary>失败；是否有可用缓存由 <see cref="EpgService.HasData"/> 判断。</summary>
    Failed,
}

/// <summary>
/// 内置节目表服务：后台下载 / 解压 / 解析 / 去重建索引，按来源保存 (sourceId, channelId)，
/// 提供 2 小时刷新、失败退避、请求去重与取消代次。
/// 与直播播放完全解耦：本服务不触碰播放器、不切台、不换线。
/// </summary>
public sealed class EpgService
{
    /// <summary>刷新间隔：首次打开立即拉取，之后每 2 小时检查。</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(2);
    private static readonly TimeSpan MinimumBackoff = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan MaximumBackoff = TimeSpan.FromHours(2);

    private const string LastSuccessKey = "live.epg-builtin-last-success";
    private const string LastAttemptKey = "live.epg-builtin-last-attempt";

    /// <summary>手动订阅地址；非空表示用户显式覆盖，内置来源让位（设置页与直播页共用此键）。</summary>
    public const string ManualUrlKey = "live.epg-url";
    /// <summary>手动导入 / 手动订阅保存的节目 XML；与内置来源互斥。</summary>
    public const string ManualXmlKey = "live.epg-xml";

    private readonly string _cacheDirectory;
    private readonly IPreferences _prefs;
    private readonly Func<Uri, CancellationToken, Task<byte[]>> _fetch;

    private readonly object _gate = new();
    private Dictionary<string, EpgProgrammeIndex> _indexes = new(StringComparer.Ordinal);
    private long _generation;
    private Task<bool>? _refresh;
    private CancellationTokenSource? _cancellation;
    private int _failureCount;
    private DateTimeOffset _nextAttemptAt;
    private EpgServiceState _state = EpgServiceState.Idle;
    private string _status = "尚未加载节目表";
    private bool _manualOverride;

    public EpgService(string cacheDirectory, IPreferences prefs, Func<Uri, CancellationToken, Task<byte[]>>? fetch = null)
    {
        _cacheDirectory = cacheDirectory;
        _prefs = prefs;
        _fetch = fetch ?? DefaultFetchAsync;
        _manualOverride = prefs.GetString(ManualUrlKey).Length > 0;
    }

    public event Action? Changed;

    public EpgServiceState State { get { lock (_gate) return _state; } }
    public bool HasData { get { lock (_gate) return _indexes.Count > 0; } }
    public DateTimeOffset? LastSuccessAt => ReadTime(LastSuccessKey);
    public DateTimeOffset? LastAttemptAt => ReadTime(LastAttemptKey);

    /// <summary>手动订阅覆盖生效时，内置节目表让位（不与内置自动混合）。</summary>
    public bool IsManualOverride { get { lock (_gate) return _manualOverride; } }

    /// <summary>数据覆盖包络：主源（其次补充源）所有频道最后结束时间的最大值，仅用于展示。</summary>
    public DateTimeOffset? CoverageEnd
    {
        get
        {
            lock (_gate)
            {
                if (_indexes.TryGetValue(EpgSources.PrimaryId, out var primary) && primary.CoverageEnd is { } a) return a;
                foreach (var index in _indexes.Values) if (index.CoverageEnd is { } b) return b;
                return null;
            }
        }
    }

    /// <summary>状态入口的短文案（正常 / 异常都不超过一行）。</summary>
    public string StatusLabel
    {
        get
        {
            if (IsManualOverride) return "手动订阅节目表";
            lock (_gate)
            {
                if (_state == EpgServiceState.Failed && _indexes.Count == 0) return "节目表加载失败";
                if (_state == EpgServiceState.Failed) return "更新失败，使用缓存";
                return _status;
            }
        }
    }

    public EpgProgrammeIndex? IndexFor(string sourceId)
    {
        lock (_gate) return _indexes.GetValueOrDefault(sourceId);
    }

    /// <summary>当前主源索引（无则 null）。调用方按来源命名空间匹配，绝不跨源合并。</summary>
    public EpgProgrammeIndex? PrimaryIndex => IndexFor(EpgSources.PrimaryId);
    public EpgProgrammeIndex? SupplementIndex => IndexFor(EpgSources.SupplementId);

    /// <summary>手动订阅保存成功 / 清除覆盖 / 恢复内置后调用：重算覆盖状态并回到一致的展示。</summary>
    public void NotifyManualOverrideChanged()
    {
        lock (_gate)
        {
            _manualOverride = _prefs.GetString(ManualUrlKey).Length > 0;
            if (_manualOverride) _status = "手动订阅节目表";
            else
            {
                _state = _indexes.Count > 0 ? EpgServiceState.Ready : EpgServiceState.Idle;
                _status = _indexes.Count > 0 ? "内置节目表" : "尚未加载节目表";
            }
        }
        RaiseChanged();
    }

    /// <summary>读缓存并回填状态。首次打开直播时调用，立即返回，不发网络请求。</summary>
    public async Task LoadCacheAsync(CancellationToken ct = default)
    {
        SetState(EpgServiceState.Loading, "正在加载节目表…");
        var loaded = new Dictionary<string, EpgProgrammeIndex>(StringComparer.Ordinal);
        foreach (var source in EpgSources.BuiltIn)
        {
            try
            {
                var path = CachePath(source.Id);
                if (!File.Exists(path)) continue;
                var xml = await File.ReadAllTextAsync(path, ct);
                // 解析放到后台线程：112114 缓存是多兆 XML，在 UI 线程解析会卡住直播页首次打开。
                var data = await Task.Run(() => XmlTvParser.ParseSource(xml, source.Id), ct);
                if (data.Programmes.Count > 0) loaded[source.Id] = EpgProgrammeIndex.Build(data);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                System.Diagnostics.Debug.WriteLine($"[epg] 缓存 {source.Id} 读取失败：{error.Message}");
            }
        }
        lock (_gate)
        {
            if (loaded.Count > 0) _indexes = loaded;
            _state = _indexes.Count > 0 ? EpgServiceState.Ready : EpgServiceState.Idle;
            _status = _indexes.Count > 0 ? "内置节目表" : "尚未加载节目表";
        }
        RaiseChanged();
    }

    /// <summary>首次打开立即调用；之后每次恢复到前台/打开直播时调用，仅在超过 2 小时且不在退避窗口内才真正更新。</summary>
    public Task EnsureFreshAsync(CancellationToken ct = default)
    {
        if (IsManualOverride) return Task.CompletedTask;
        var last = LastSuccessAt;
        if (last is { } time && DateTimeOffset.UtcNow - time < RefreshInterval) return Task.CompletedTask;
        return RefreshAsync(force: false, ct);
    }

    /// <summary>手动重试 / 周期刷新入口。同一时刻只有一个下载任务（请求去重）。</summary>
    public Task<bool> RefreshAsync(bool force, CancellationToken ct = default)
    {
        if (IsManualOverride) return Task.FromResult(false);
        long generation;
        CancellationToken token;
        var placeholder = Task.FromResult(false);
        lock (_gate)
        {
            if (_refresh is { } running) return running;
            if (!force && _indexes.Count > 0 && DateTimeOffset.UtcNow < _nextAttemptAt) return Task.FromResult(false);
            generation = ++_generation;
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
            token = _cancellation.Token;
            _refresh = placeholder; // 占位，避免并发重复下载
        }
        var task = RunRefreshAsync(generation, token);
        // 若任务已同步跑完（finally 已清空 _refresh），不要再把已完成的旧任务写回，否则会挡住后续刷新。
        lock (_gate) { if (ReferenceEquals(_refresh, placeholder) && !task.IsCompleted) _refresh = task; }
        return task;
    }

    /// <summary>取消在途请求并作废旧结果（切源 / 退出时调用）。</summary>
    public void Cancel()
    {
        lock (_gate)
        {
            _generation++;
            var cancellation = _cancellation;
            _cancellation = null;
            _refresh = null;
            // Cancel() 之后不再等待结果；CTS 交给 GC，避免在请求仍持有 token 时 Dispose。
            cancellation?.Cancel();
        }
    }

    private async Task<bool> RunRefreshAsync(long generation, CancellationToken ct)
    {
        SetState(HasData ? EpgServiceState.Updating : EpgServiceState.Loading, HasData ? "正在更新…" : "正在加载节目表…");
        _prefs.Set(LastAttemptKey, (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var now = DateTimeOffset.Now;

        // 候选从「当前索引」起步：某个来源本次失败时必须保留它的旧数据，
        // 而不是用失败结果把已经可用的内置节目表整体清空（数据错误/过期保留有效缓存）。
        var refreshed = new Dictionary<string, EpgProgrammeIndex>(_indexes, StringComparer.Ordinal);
        var failures = new List<string>();
        try
        {
            foreach (var source in EpgSources.BuiltIn)
            {
                try
                {
                    var bytes = await _fetch(source.Url, ct);
                    ct.ThrowIfCancellationRequested();
                    var xml = await Task.Run(() => Decompress(bytes), ct);
                    var data = await Task.Run(() => XmlTvParser.ParseSource(xml, source.Id), ct);
                    ct.ThrowIfCancellationRequested();
                    if (data.Programmes.Count == 0) { failures.Add($"{source.Id}: 没有节目"); continue; }
                    // 有效缓存不被「空 / 过期」数据覆盖：新数据整段相对 now 都已结束（源滞后或返回陈旧快照），
                    // 而该来源现有缓存仍有未结束节目时，保留旧的有效缓存，不拿更差的数据顶替。
                    if (IsStale(data.Programmes, now) && ExistingIsFresh(refreshed, source.Id, now))
                    { failures.Add($"{source.Id}: 数据已过期"); continue; }
                    refreshed[source.Id] = EpgProgrammeIndex.Build(data);
                    // 缓存按来源独立写入；只有解析成功的数据才会覆盖旧缓存（数据错误保留有效缓存）。
                    try { await ProgrammeStoreAt(source.Id).SaveAsync(data.Programmes, ct); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    { System.Diagnostics.Debug.WriteLine($"[epg] 缓存 {source.Id} 写入失败：{error.Message}"); }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception error) { failures.Add($"{source.Id}: {error.Message}"); }
            }
        }
        finally
        {
            lock (_gate)
            {
                _refresh = null;
                if (generation == _generation) { _cancellation?.Dispose(); _cancellation = null; }
            }
        }

        var primaryFailed = failures.Any(f => f.StartsWith(EpgSources.PrimaryId + ":", StringComparison.Ordinal));
        lock (_gate)
        {
            // 取消 / 已被更新的代次取代：不提交，旧索引保持原样。
            if (generation != _generation || ct.IsCancellationRequested) return false;
            var hasPrimary = refreshed.ContainsKey(EpgSources.PrimaryId);
            // 原子提交：仅在本次确实拿到数据时替换；失败来源沿用旧索引。
            if (refreshed.Count > 0) _indexes = refreshed;
            if (hasPrimary && !primaryFailed)
            {
                _failureCount = 0;
                _nextAttemptAt = default;
                _state = EpgServiceState.Ready;
                _status = "内置节目表";
                _prefs.Set(LastSuccessKey, (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            }
            else
            {
                _failureCount = Math.Min(_failureCount + 1, 8);
                var backoff = TimeSpan.FromTicks(Math.Min(MaximumBackoff.Ticks, MinimumBackoff.Ticks * (1L << (_failureCount - 1))));
                _nextAttemptAt = DateTimeOffset.UtcNow + backoff;
                _state = EpgServiceState.Failed;
                _status = hasPrimary ? "更新失败，使用缓存" : "节目表加载失败";
            }
        }
        if (failures.Count > 0)
            VodBoxLog.Warn("epg", "节目表来源失败：" + string.Join("；", failures));
        RaiseChanged();
        return !primaryFailed;
    }

    /// <summary>整段数据相对 now 都已结束：没有任何「当前或将来」的节目，说明源滞后或返回了陈旧快照。</summary>
    private static bool IsStale(IReadOnlyList<Programme> programmes, DateTimeOffset now) =>
        programmes.All(programme => programme.End <= now);

    /// <summary>该来源现有缓存是否仍有效（至少有一条节目尚未结束）。</summary>
    private static bool ExistingIsFresh(IReadOnlyDictionary<string, EpgProgrammeIndex> indexes, string sourceId, DateTimeOffset now) =>
        indexes.TryGetValue(sourceId, out var index) && index.CoverageEnd is { } end && end > now;

    private VodBox.Infrastructure.ProgrammeStore ProgrammeStoreAt(string sourceId) =>
        new(Path.Combine(_cacheDirectory, sourceId + ".xml"));

    private string CachePath(string sourceId) => Path.Combine(_cacheDirectory, sourceId + ".xml");

    private void SetState(EpgServiceState state, string status)
    {
        lock (_gate) { _state = state; _status = status; }
        RaiseChanged();
    }

    private DateTimeOffset? ReadTime(string key)
    {
        var seconds = _prefs.GetInt(key, 0);
        return seconds > 0 ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;
    }

    private static string Decompress(byte[] bytes)
    {
        // 112114 / 51zmt 镜像都是 gzip；允许纯 XML 以容忍镜像变更。
        if (bytes.Length < 2 || bytes[0] != 0x1f || bytes[1] != 0x8b) return TextEncoding.Decode(bytes);
        using var input = new MemoryStream(bytes);
        using var gzip = new System.IO.Compression.GZipStream(input, System.IO.Compression.CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, leaveOpen: false);
        var buffer = new char[8192];
        var text = new System.Text.StringBuilder();
        int count;
        while ((count = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (text.Length + count > 16 * 1024 * 1024) throw new InvalidDataException("节目表超过 16 MiB 字符上限。");
            text.Append(buffer, 0, count);
        }
        return text.ToString();
    }

    private static async Task<byte[]> DefaultFetchAsync(Uri url, CancellationToken ct)
    {
        using var http = new DefaultHttp();
        return await http.GetBoundedAsync(url.AbsoluteUri, EpgSources.MaxDownloadBytes, ct,
            new Dictionary<string, string> { ["User-Agent"] = DefaultHttp.BrowserUserAgent });
    }

    /// <summary>通知订阅者。订阅者异常不能反过来让后台刷新任务失败（后台失败不冒泡到播放/启动路径）。</summary>
    private void RaiseChanged()
    {
        try { Changed?.Invoke(); }
        catch (Exception error) { System.Diagnostics.Debug.WriteLine($"[epg] 状态通知失败：{error.Message}"); }
    }
}
