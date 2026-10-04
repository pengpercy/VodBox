using System.Text.Json;
using VodBox.Core;

namespace VodBox.Infrastructure;

public sealed class ConfigLoader(HttpClient http)
{
    public async Task<VodBoxConfig> LoadAsync(string location, CancellationToken cancellationToken = default)
    {
        Uri origin = Uri.TryCreate(location, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "file"
            ? uri : new Uri(Path.GetFullPath(location));
        string json = origin.IsFile ? await File.ReadAllTextAsync(origin.LocalPath, cancellationToken)
            : await http.GetStringAsync(origin, cancellationToken);
        var config = JsonSerializer.Deserialize(json, VodBoxJson.Default.VodBoxConfig)
            ?? throw new InvalidDataException("配置为空。");
        config = config with { Resolvers = config.Resolvers ?? [], Sources = config.Sources ?? [], LiveSources = config.LiveSources ?? [] };
        Validate(config);
        return config with
        {
            Sources = config.Sources.Select(source => source with
            {
                Entry = source.Entry is null ? null : new Uri(origin, source.Entry).ToString()
            }).ToList(),
            LiveSources = config.LiveSources.Select(live => live with
            {
                Uri = new Uri(origin, live.Uri).ToString(),
                Epg = live.Epg is null ? null : new Uri(origin, live.Epg).ToString()
            }).ToList(),
            Resolvers = config.Resolvers.Select(resolver => resolver with { Entry = resolver.Entry is null ? null : new Uri(origin, resolver.Entry).ToString() }).ToList()
        };
    }

    public static void Validate(VodBoxConfig config)
    {
        if (config.SchemaVersion != 1) throw new InvalidDataException($"不支持配置版本 {config.SchemaVersion}，当前为 1。");
        if (string.IsNullOrWhiteSpace(config.Id)) throw new InvalidDataException("配置 id 不能为空。");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var resolverIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var resolver in config.Resolvers)
        {
            if (string.IsNullOrWhiteSpace(resolver.Id) || !resolverIds.Add(resolver.Id) || resolver.Id.StartsWith('_')) throw new InvalidDataException("解析器 ID 为空、重复或使用了保留前缀。");
            if (string.IsNullOrWhiteSpace(resolver.Name) || resolver.Kind == ResolutionKind.Json && string.IsNullOrWhiteSpace(resolver.Entry)) throw new InvalidDataException("解析器名称或 JSON 入口为空。");
            if (!Enum.IsDefined(resolver.Kind) || resolver.TimeoutSeconds is < 1 or > 120) throw new InvalidDataException("解析器类型或超时设置无效。");
        }
        foreach (var resolver in config.Resolvers)
            if (resolver.NextResolverId is not null && !resolverIds.Contains(resolver.NextResolverId)) throw new InvalidDataException("解析器链引用了不存在的解析器。");
        foreach (var source in config.Sources)
        {
            if (string.IsNullOrWhiteSpace(source.Id) || !ids.Add(source.Id))
                throw new InvalidDataException($"内容源 id 为空或重复：{source.Id}");
            if (!Enum.IsDefined(source.Runtime)) throw new InvalidDataException("未知源运行时。");
            if (string.IsNullOrWhiteSpace(source.Name)) throw new InvalidDataException("内容源 name 不能为空。");
            if (source.Runtime != ProviderRuntime.Csharp && string.IsNullOrWhiteSpace(source.Entry))
                throw new InvalidDataException($"脚本源 {source.Id} 缺少 entry。");
            if (source.ResolverId is not null && source.ResolverId is not ("_direct" or "_browser") && !resolverIds.Contains(source.ResolverId))
                throw new InvalidDataException($"内容源 {source.Id} 引用了不存在的解析器。");
        }
    }
}
