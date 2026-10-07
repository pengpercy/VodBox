using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using VodBox.Core;

namespace VodBox.Infrastructure;

/// <summary>
/// TVBox 订阅加载：URL →（隐写/base64 解码 + 注释剥离，对齐 FongMi Decoder）→ TvBoxConfig → 运行时 SourceInfo。
/// 支持三种形态：
/// 1. 明文 JSON / JS 变量包裹；
/// 2. JPEG 等二进制尾部附加 base64（饭太硬 in.bmp：前缀干扰 + "**" 分隔）；
/// 3. 含 "//{...}" 行内注释的配置（剥落后再解析）。
/// </summary>
public sealed class ConfigLoader(DefaultHttp http)
{
    public async Task<TvBoxConfig> LoadAsync(string url, CancellationToken ct = default)
    {
        var raw = await http.GetStringAsync(url, ct: ct);
        return Parse(raw) ?? throw new InvalidDataException($"配置解析失败：{url}");
    }

    public async Task<TvBoxConfig> LoadBytesAsync(string url, CancellationToken ct = default)
    {
        var bytes = await http.GetByteArrayAsync(url, ct: ct);
        return ParseBytes(bytes) ?? throw new InvalidDataException($"配置解析失败：{url}");
    }

    public static TvBoxConfig? Parse(string raw)
    {
        raw = raw.Trim();
        if (!raw.StartsWith('{'))
        {
            var start = raw.IndexOf('{');
            if (start < 0) return null;
            raw = raw[start..];
        }
        raw = StripComments(raw);
        try
        {
            return JsonSerializer.Deserialize<TvBoxConfig>(raw, Json.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>解析二进制载体（JPEG/BMP 头 + 尾部 base64 载荷）。</summary>
    public static TvBoxConfig? ParseBytes(byte[] bytes)
    {
        // 找 base64 纯 ASCII 长尾（至少 64 字符连续 base64 字母表）
        var tail = ExtractBase64Tail(bytes);
        return tail is null ? null : Parse(Encoding.UTF8.GetString(Convert.FromBase64String(tail)));
    }

    private static string? ExtractBase64Tail(byte[] bytes)
    {
        // 从尾部向前找最长连续 base64 段
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/=*$#";
        var start = -1;
        for (var i = bytes.Length - 1; i >= 0; i--)
        {
            var c = (char)bytes[i];
            if (char.IsAsciiLetterOrDigit(c) || c is '+' or '/' or '=' or '*' or '$' or '#' or '\n' or '\r') continue;
            start = i + 1;
            break;
        }
        if (start < 0) start = 0;
        var text = Encoding.ASCII.GetString(bytes[start..]).Trim();
        // 饭太硬形态：任意前缀 + "**" + base64
        var marker = text.IndexOf("**", StringComparison.Ordinal);
        if (marker >= 0 && marker < 64) text = text[(marker + 2)..];
        text = text.Trim();
        // 清洗分隔符噪声
        text = text.Replace("\n", "").Replace("\r", "");
        if (text.Length < 64) return null;
        if (text.Length % 4 != 0) text = text[..(text.Length - text.Length % 4)];
        return text;
    }

    /// <summary>剥 "//" 行注释（FongMi Decoder.extract 的注释剥离：如 "//{...}" 整段被注释的站点行）。</summary>
    public static string StripComments(string json)
    {
        // 只处理行首 //（含缩进），避免误伤 URL 里的 "//"（https:）
        return Regex.Replace(json, @"^[ \t]*//.*$", "", RegexOptions.Multiline);
    }

    /// <summary>把 TvBoxConfig 转为可用的内容源列表（过滤桌面端不支持的 csp_ 站点）。</summary>
    public static List<SourceInfo> ToSources(TvBoxConfig config)
    {
        var sources = new List<SourceInfo>();
        foreach (var site in config.Sites)
        {
            if (site.Runtime == SourceRuntime.Node) continue; // csp_ Java 爬虫，桌面端无 JVM
            if (string.IsNullOrWhiteSpace(site.Key) || string.IsNullOrWhiteSpace(site.Name)) continue;
            sources.Add(new SourceInfo
            {
                Key = site.Key,
                Name = site.Name,
                Runtime = site.Runtime,
                Api = site.Api,
                Ext = site.Ext is { ValueKind: JsonValueKind.String } s ? s.GetString() : site.Ext?.GetRawText(),
                Type = site.Type,
                Searchable = site.Searchable != 0,
                Changeable = site.Changeable != 0,
                Categories = site.Categories,
            });
        }
        return sources;
    }
}
