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
            }).ToList()
        };
    }

    public static void Validate(VodBoxConfig config)
    {
        if (config.SchemaVersion != 1) throw new InvalidDataException($"不支持配置版本 {config.SchemaVersion}，当前为 1。");
        if (string.IsNullOrWhiteSpace(config.Id)) throw new InvalidDataException("配置 id 不能为空。");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in config.Sources)
        {
            if (string.IsNullOrWhiteSpace(source.Id) || !ids.Add(source.Id))
                throw new InvalidDataException($"内容源 id 为空或重复：{source.Id}");
            if (!Enum.IsDefined(source.Runtime)) throw new InvalidDataException("未知源运行时。");
            if (string.IsNullOrWhiteSpace(source.Name)) throw new InvalidDataException("内容源 name 不能为空。");
            if (source.Runtime != ProviderRuntime.Csharp && string.IsNullOrWhiteSpace(source.Entry))
                throw new InvalidDataException($"脚本源 {source.Id} 缺少 entry。");
        }
    }
}
