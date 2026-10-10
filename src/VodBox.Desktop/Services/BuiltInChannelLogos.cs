using System.Text.Json;
using System.Text;
using Avalonia.Platform;

namespace VodBox.Desktop.Services;

/// <summary>随安装包携带的频道台标 URL 索引：先精确匹配，再去除频道名中的格式与清晰度标记。</summary>
internal static class BuiltInChannelLogos
{
    private const string Root = "avares://VodBox.Desktop/Assets/ChannelLogos/";
    private static readonly Lazy<Dictionary<string, string>> Index = new(Load);

    internal static bool IsReady => Index.IsValueCreated;

    private static Dictionary<string, string> Load()
    {
        using var stream = AssetLoader.Open(new Uri(Root + "index.json"));
        using var document = JsonDocument.Parse(stream);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in document.RootElement.EnumerateObject())
            result.TryAdd(Normalize(entry.Name), entry.Value.GetString()!);
        return result;
    }

    private static string Normalize(string name) => string.Concat(name.Normalize(NormalizationForm.FormKC)
        .Where(c => !char.IsWhiteSpace(c) && c is not '-' and not '_' and not '·')).ToUpperInvariant();

    internal static Uri? Find(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var key = Normalize(name);
        if (Index.Value.TryGetValue(key, out var file)) return new Uri(file);
        string previous;
        do
        {
            previous = key;
            foreach (var suffix in new[] { "超高清", "高清", "标清", "HD", "SD", "频道" })
                if (key.EndsWith(suffix, StringComparison.Ordinal)) key = key[..^suffix.Length];
        } while (previous != key);
        if (Index.Value.TryGetValue(key, out file)) return new Uri(file);
        // CCTV-1 综合 / CCTV5+ 体育赛事等节目名称附缀，保留数字和 +，避免误配 5 与 5+。
        foreach (var prefix in new[] { "CCTV", "CETV" })
        {
            if (!key.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var end = prefix.Length;
            while (end < key.Length && char.IsAsciiDigit(key[end])) end++;
            if (end == prefix.Length) continue;
            if (end < key.Length && key[end] == '+') end++;
            if (Index.Value.TryGetValue(key[..end], out file)) return new Uri(file);
        }
        return null;
    }
}
