using System.Text.Json;
using VodBox.Core;

namespace VodBox.Infrastructure;

/// <summary>
/// 源配置存储（LunaTV AdminConfig 的桌面单用户版）：采集源 / 自定义分类 / 直播源 / 站点设置 / 订阅状态。
/// 订阅配置文件与手动维护合并（LunaTV refineConfig 语义）：配置文件里的条目标记 config，
/// 只覆盖名称与地址、保留用户的启用状态与顺序；不在配置文件里的条目转为 custom 保留。
/// 落盘为单个 JSON（原子替换），所有变更在锁内生成新快照，读取方拿到的永远是不可变快照。
/// </summary>
public sealed class SourceConfigStore
{
    private readonly string? _path;
    private readonly object _gate = new();
    private SourceConfigData _data;

    public event Action? Changed;

    public SourceConfigStore(string? path)
    {
        _path = path;
        _data = Load(path);
    }

    public SourceConfigData Data { get { lock (_gate) return _data; } }
    public SiteSettings Settings => Data.Settings;
    public IReadOnlyList<ApiSite> Sources => Data.Sources;
    /// <summary>启用中的采集源（顺序即用户排序）。</summary>
    public IReadOnlyList<ApiSite> EnabledSources => Data.Sources.Where(site => !site.Disabled).ToArray();
    public IReadOnlyList<CustomCategory> EnabledCategories => Data.CustomCategories.Where(category => !category.Disabled).ToArray();
    public IReadOnlyList<LiveSite> EnabledLives => Data.Lives.Where(live => !live.Disabled).ToArray();
    public ApiSite? Find(string key) => Data.Sources.FirstOrDefault(site => site.Key == key);

    private static SourceConfigData Load(string? path)
    {
        if (path is null || !File.Exists(path)) return new SourceConfigData();
        try
        {
            return SelfCheck(JsonSerializer.Deserialize(File.ReadAllText(path), Json.TypeInfo<SourceConfigData>()) ?? new SourceConfigData());
        }
        catch (Exception error) when (error is JsonException or IOException)
        {
            VodBoxLog.Warn("config", $"源配置读取失败，使用空配置：{error.Message}");
            return new SourceConfigData();
        }
    }

    /// <summary>LunaTV configSelfCheck：去重并补齐缺省集合。</summary>
    internal static SourceConfigData SelfCheck(SourceConfigData data) => data with
    {
        Subscription = data.Subscription ?? new SubscriptionState(),
        Settings = data.Settings ?? new SiteSettings(),
        Sources = (data.Sources ?? []).Where(site => !string.IsNullOrWhiteSpace(site.Key)).DistinctBy(site => site.Key).ToList(),
        CustomCategories = (data.CustomCategories ?? []).Where(category => !string.IsNullOrWhiteSpace(category.Query)).DistinctBy(category => category.Identity).ToList(),
        Lives = (data.Lives ?? []).Where(live => !string.IsNullOrWhiteSpace(live.Key)).DistinctBy(live => live.Key).ToList(),
        DeletedSourceKeys = (data.DeletedSourceKeys ?? []).Where(key => !string.IsNullOrWhiteSpace(key)).Distinct(StringComparer.Ordinal).ToList(),
    };

    private void Mutate(Func<SourceConfigData, SourceConfigData> change)
    {
        SourceConfigData next;
        lock (_gate)
        {
            next = SelfCheck(change(_data));
            _data = next;
            Save(next);
        }
        Changed?.Invoke();
    }

    private void Save(SourceConfigData data)
    {
        if (_path is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(data, Json.TypeInfo<SourceConfigData>()));
            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            VodBoxLog.Warn("config", $"源配置写入失败：{error.Message}");
        }
    }

    // ---------- 配置文件 / 订阅 ----------

    /// <summary>应用配置文件（已解码 JSON），与现有条目合并。返回解析出的配置供调用方展示统计。</summary>
    public LunaConfigFile ApplyConfigFile(string json)
    {
        var file = SourceConfigParser.Parse(json);
        Mutate(data => Refine(data with { ConfigFile = json }, file));
        return file;
    }

    /// <summary>LunaTV refineConfig：配置文件条目覆盖名称/地址并标记 config，其余转 custom 保留。</summary>
    internal static SourceConfigData Refine(SourceConfigData data, LunaConfigFile file)
    {
        var deleted = (data.DeletedSourceKeys ?? []).ToHashSet(StringComparer.Ordinal);
        var fileSites = (file.ApiSite ?? []).Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value.Api) && !deleted.Contains(pair.Key)).ToArray();
        var fileSiteKeys = fileSites.Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal);
        var sources = data.Sources.Select(site => fileSiteKeys.Contains(site.Key) ? site : site with { From = SourceOrigin.Custom }).ToList();
        foreach (var (key, site) in fileSites)
        {
            var index = sources.FindIndex(existing => existing.Key == key);
            var name = string.IsNullOrWhiteSpace(site.Name) ? key : site.Name.Trim();
            var detail = string.IsNullOrWhiteSpace(site.Detail) ? null : site.Detail.Trim();
            if (index >= 0) sources[index] = sources[index] with { Name = name, Api = site.Api.Trim(), Detail = detail, From = SourceOrigin.Config };
            else sources.Add(new ApiSite { Key = key, Name = name, Api = site.Api.Trim(), Detail = detail, From = SourceOrigin.Config });
        }

        var fileCategories = (file.CustomCategory ?? []).Where(category => !string.IsNullOrWhiteSpace(category.Query))
            .Select(category => new CustomCategory { Name = string.IsNullOrWhiteSpace(category.Name) ? category.Query : category.Name, Type = category.Type is "tv" ? "tv" : "movie", Query = category.Query.Trim() })
            .ToArray();
        var fileCategoryKeys = fileCategories.Select(category => category.Identity).ToHashSet(StringComparer.Ordinal);
        var categories = data.CustomCategories.Select(category => fileCategoryKeys.Contains(category.Identity) ? category : category with { From = SourceOrigin.Custom }).ToList();
        foreach (var category in fileCategories)
        {
            var index = categories.FindIndex(existing => existing.Identity == category.Identity);
            if (index >= 0) categories[index] = categories[index] with { Name = category.Name, From = SourceOrigin.Config };
            else categories.Add(category);
        }

        var fileLives = (file.Lives ?? []).Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value.Url)).ToArray();
        var fileLiveKeys = fileLives.Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal);
        var lives = data.Lives.Select(live => fileLiveKeys.Contains(live.Key) ? live : live with { From = SourceOrigin.Custom }).ToList();
        foreach (var (key, live) in fileLives)
        {
            var index = lives.FindIndex(existing => existing.Key == key);
            var name = string.IsNullOrWhiteSpace(live.Name) ? key : live.Name.Trim();
            if (index >= 0) lives[index] = lives[index] with { Name = name, Url = live.Url.Trim(), Ua = live.Ua, Epg = live.Epg, From = SourceOrigin.Config };
            else lives.Add(new LiveSite { Key = key, Name = name, Url = live.Url.Trim(), Ua = live.Ua, Epg = live.Epg });
        }

        var settings = file.CacheTime is > 0 ? data.Settings with { CacheTimeSeconds = file.CacheTime.Value } : data.Settings;
        return data with { Sources = sources, CustomCategories = categories, Lives = lives, Settings = settings };
    }

    public void SetSubscription(SubscriptionState subscription) => Mutate(data => data with { Subscription = subscription });

    public void UpdateSettings(Func<SiteSettings, SiteSettings> change) => Mutate(data => data with { Settings = change(data.Settings) });

    // ---------- 采集源维护 ----------

    /// <summary>手动添加采集源（custom）。key 重复抛出。</summary>
    public void AddSource(string key, string name, string api, string? detail)
    {
        key = key.Trim(); name = name.Trim(); api = api.Trim();
        if (key.Length == 0 || name.Length == 0 || api.Length == 0) throw new ArgumentException("名称、键与接口地址都不能为空。");
        if (!Uri.TryCreate(api, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) throw new ArgumentException("接口地址必须是 HTTP(S) 地址。");
        if (Find(key) is not null) throw new ArgumentException($"源键「{key}」已存在。");
        Mutate(data => data with
        {
            Sources = [.. data.Sources, new ApiSite { Key = key, Name = name, Api = api, Detail = string.IsNullOrWhiteSpace(detail) ? null : detail.Trim(), From = SourceOrigin.Custom }],
            DeletedSourceKeys = data.DeletedSourceKeys.Where(deleted => deleted != key).ToList(),
        });
    }

    /// <summary>编辑采集源名称/地址（config 源下次刷新订阅会被配置文件覆盖）。</summary>
    public void UpdateSource(string key, string name, string api, string? detail)
    {
        if (!Uri.TryCreate(api.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) throw new ArgumentException("接口地址必须是 HTTP(S) 地址。");
        Mutate(data => data with
        {
            Sources = data.Sources.Select(site => site.Key == key ? site with { Name = name.Trim().Length > 0 ? name.Trim() : site.Name, Api = api.Trim(), Detail = string.IsNullOrWhiteSpace(detail) ? null : detail.Trim() } : site).ToList(),
        });
    }

    /// <summary>删除单个采集源（见 <see cref="DeleteSources"/>）。</summary>
    public bool DeleteSource(string key) => DeleteSources([key]) > 0;

    /// <summary>
    /// 批量删除采集源，返回实际删除数。订阅（config）源记入 <see cref="SourceConfigData.DeletedSourceKeys"/>，
    /// 下次刷新订阅不会再加回来；<see cref="RestoreDeletedSources"/> 可恢复。
    /// </summary>
    public int DeleteSources(IEnumerable<string> keys)
    {
        var set = keys.ToHashSet(StringComparer.Ordinal);
        var removed = 0;
        Mutate(data =>
        {
            var targets = data.Sources.Where(site => set.Contains(site.Key)).ToArray();
            removed = targets.Length;
            return data with
            {
                Sources = data.Sources.Where(site => !set.Contains(site.Key)).ToList(),
                DeletedSourceKeys = [.. data.DeletedSourceKeys, .. targets.Where(site => site.From == SourceOrigin.Config).Select(site => site.Key)],
            };
        });
        return removed;
    }

    public int DeletedSourceCount => Data.DeletedSourceKeys.Count;

    /// <summary>恢复删除过的订阅源：清空删除记录并按最近一次配置文件重新合并（排到末尾、默认启用）。</summary>
    public int RestoreDeletedSources()
    {
        var restored = 0;
        Mutate(data =>
        {
            var cleared = data with { DeletedSourceKeys = [] };
            if (data.DeletedSourceKeys.Count == 0 || string.IsNullOrWhiteSpace(data.ConfigFile)) return cleared;
            LunaConfigFile file;
            try { file = SourceConfigParser.Parse(data.ConfigFile); }
            catch (InvalidDataException) { return cleared; }
            var next = Refine(cleared, file);
            restored = next.Sources.Count - data.Sources.Count;
            return next;
        });
        return restored;
    }

    public void SetSourceDisabled(string key, bool disabled) =>
        Mutate(data => data with { Sources = data.Sources.Select(site => site.Key == key ? site with { Disabled = disabled } : site).ToList() });

    public void SetSourcesDisabled(IEnumerable<string> keys, bool disabled)
    {
        var set = keys.ToHashSet(StringComparer.Ordinal);
        Mutate(data => data with { Sources = data.Sources.Select(site => set.Contains(site.Key) ? site with { Disabled = disabled } : site).ToList() });
    }

    /// <summary>把源移动到新位置（拖拽排序 / 上移 / 置顶共用）。</summary>
    public void MoveSource(string key, int newIndex) => Mutate(data =>
    {
        var list = data.Sources.ToList();
        var index = list.FindIndex(site => site.Key == key);
        if (index < 0) return data;
        var site = list[index];
        list.RemoveAt(index);
        list.Insert(Math.Clamp(newIndex, 0, list.Count), site);
        return data with { Sources = list };
    });

    // ---------- 自定义分类 / 直播源 ----------

    public void AddCategory(string? name, string type, string query) =>
        Mutate(data => data with { CustomCategories = [.. data.CustomCategories, new CustomCategory { Name = name, Type = type is "tv" ? "tv" : "movie", Query = query.Trim(), From = SourceOrigin.Custom }] });

    public void DeleteCategory(string identity) =>
        Mutate(data => data with { CustomCategories = data.CustomCategories.Where(category => category.Identity != identity).ToList() });

    public void SetCategoryDisabled(string identity, bool disabled) =>
        Mutate(data => data with { CustomCategories = data.CustomCategories.Select(category => category.Identity == identity ? category with { Disabled = disabled } : category).ToList() });

    public void AddLive(string key, string name, string url, string? ua, string? epg)
    {
        if (Data.Lives.Any(live => live.Key == key)) throw new ArgumentException($"直播源键「{key}」已存在。");
        Mutate(data => data with { Lives = [.. data.Lives, new LiveSite { Key = key.Trim(), Name = name.Trim(), Url = url.Trim(), Ua = ua, Epg = epg, From = SourceOrigin.Custom }] });
    }

    public void DeleteLive(string key) => Mutate(data => data with { Lives = data.Lives.Where(live => live.Key != key).ToList() });

    public void SetLiveDisabled(string key, bool disabled) =>
        Mutate(data => data with { Lives = data.Lives.Select(live => live.Key == key ? live with { Disabled = disabled } : live).ToList() });
}
