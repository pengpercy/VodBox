using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VodBox.Core;

namespace VodBox.Infrastructure;

public sealed partial class ConfigLoader
{
    private const int MaximumConfigBytes = 8 * 1024 * 1024;
    private async Task<string> ReadConfigurationAsync(Uri location, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(15));
        await using var stream = location.IsFile ? File.OpenRead(location.LocalPath) : await http.GetStreamAsync(location, deadline.Token);
        var bytes = await BoundedContent.ReadAsync(stream, MaximumConfigBytes, deadline.Token);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xd8 }))
        {
            // TV subscriptions may append **base64-json after the JPEG end marker.
            int end = bytes.AsSpan().IndexOf(new byte[] { 0xff, 0xd9 });
            int marker = end < 0 ? -1 : bytes.AsSpan(end + 2).IndexOf("**"u8);
            if (marker < 0) throw new InvalidDataException("该地址返回图片，未找到配置数据。");
            bytes = DecodeBase64(Encoding.ASCII.GetString(bytes, end + 2 + marker + 2, bytes.Length - end - 2 - marker - 2));
        }
        string text = TextEncoding.Decode(bytes).Trim();
        if (text.StartsWith("clan://", StringComparison.Ordinal)) text = TextEncoding.Decode(DecodeBase64(text[7..])).Trim();
        if (text.StartsWith('<')) throw new InvalidDataException("该地址返回网页，请检查播放源配置地址。");
        return text;
    }
    private static byte[] DecodeBase64(string text)
    {
        try { return Convert.FromBase64String(text.Trim()); }
        catch (FormatException error) { throw new InvalidDataException("播放源包装中的 Base64 数据无效。", error); }
    }
    private static string Text(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String or JsonValueKind.Number ? value.ToString() : "";
    private async Task<VodBoxConfig> ImportTvAsync(JsonElement root, Uri origin, CancellationToken token)
    {
        if (!root.TryGetProperty("sites", out var sites) || sites.ValueKind != JsonValueKind.Array || sites.GetArrayLength() > 1000)
            throw new InvalidDataException("播放源 sites 必须是最多 1000 项的数组。");
        var config = new VodBoxConfig { Id = "subscription-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(origin.AbsoluteUri)))[..16] };
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var entries = sites.EnumerateArray().ToArray();
        foreach (var site in entries)
        {
            string id = Text(site, "key"), name = Text(site, "name");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name) || !ids.Add(id)) throw new InvalidDataException("播放源站点 key/name 为空或 key 重复。");
        }
        using var requests = new SemaphoreSlim(4,4);
        var results = await Task.WhenAll(entries.Select(async site =>
        {
            await requests.WaitAsync(token);
            try
            {
                string id = Text(site, "key"), name = Text(site, "name"), api = Text(site, "api"), type = Text(site, "type");
                if (type is "0" or "1" && Uri.TryCreate(origin, api, out var apiUri) && apiUri.Scheme is "https" or "http")
                    return (Source: new SourceDefinition { Id = id, Name = name, Provider = type == "0" ? "maccms-xml" : "maccms-json", Entry = apiUri.AbsoluteUri }, Warning: (string?)null);
                if (api is "csp_Bili" or "csp_BiliGuard")
                {
                    try { return (Source: await ImportBilibiliAsync(site, id, name, origin, token), Warning: (string?)null); }
                    catch (Exception error) when (error is not OperationCanceledException || !token.IsCancellationRequested)
                    { return (Source: (SourceDefinition?)null, Warning: $"{name}：分类配置导入失败（{error.Message}）。"); }
                }
                if (TryImportPublicRule(site, id, name, api, out var publicSource))
                    return (Source: publicSource, Warning: (string?)null);
                if (api is "csp_FirstAid" or "csp_FirstAidGuard")
                    return (Source: new SourceDefinition { Id = id, Name = name, Provider = "firstaid", Entry = "https://m.youlai.cn/" }, Warning: (string?)null);
                if (api is "csp_YGP" or "csp_YGPGuard")
                    return (Source: new SourceDefinition { Id = id, Name = name, Provider = "trailers", Entry = "https://www.6huo.com/" }, Warning: (string?)null);
                return (Source: (SourceDefinition?)null, Warning: $"{name}：暂未适配 {api}。");
            }
            finally { requests.Release(); }
        }));
        foreach (var result in results) { if (result.Source is not null) config.Sources.Add(result.Source); if (result.Warning is not null) config.ImportWarnings.Add(result.Warning); }
        if (root.TryGetProperty("lives", out var lives) && lives.ValueKind == JsonValueKind.Array)
        {
            if (lives.GetArrayLength() > 100) throw new InvalidDataException("直播源数量超过 100。");
            int index = 0;
            foreach (var live in lives.EnumerateArray())
            {
                string url = Text(live, "url");
                if (Uri.TryCreate(origin, url, out var uri) && uri.Scheme is "http" or "https" && url.Length > 0)
                    config.LiveSources.Add(new($"tv-live-{index++}", Text(live, "name"), uri.AbsoluteUri, UserAgent: string.IsNullOrWhiteSpace(Text(live, "ua")) ? null : Text(live, "ua")));
            }
        }
        if (config.Sources.Count == 0 && config.LiveSources.Count == 0)
            throw new InvalidDataException("该配置暂无可用的已适配站点或直播源。" + string.Join("；", config.ImportWarnings.Take(3)));
        return config;
    }
    private static bool TryImportPublicRule(JsonElement site, string id, string name, string api, out SourceDefinition? source)
    {
        source = null;
        if (!Uri.TryCreate(api, UriKind.Absolute, out var engine) || engine.Scheme is not ("http" or "https") || !engine.AbsolutePath.EndsWith("/drpy2.min.js", StringComparison.Ordinal)) return false;
        if (!site.TryGetProperty("ext", out var ext) || ext.ValueKind != JsonValueKind.String || !Uri.TryCreate(ext.GetString(), UriKind.Absolute, out var rule) || rule.Scheme is not ("http" or "https")) return false;
        string path = Uri.UnescapeDataString(rule.AbsolutePath);
        string? profile = null;
        foreach (var mapping in new[] { (Rule: "兔小贝.js", Provider: "tuxiaobei"), (Rule: "虎牙.js", Provider: "huya"), (Rule: "斗鱼直播.js", Provider: "douyu") })
            if (path.EndsWith("/fantaiying7/EXT/refs/heads/main/" + mapping.Rule, StringComparison.Ordinal) || path.EndsWith("/fantaiying7/EXT/main/" + mapping.Rule, StringComparison.Ordinal)) profile = mapping.Provider;
        if (profile is null) return false;
        source = new() { Id = id, Name = name, Provider = profile, Entry = profile switch { "tuxiaobei" => "https://www.tuxiaobei.com/", "huya" => "https://www.huya.com/", _ => "https://m.douyu.com/" } };
        return true;
    }
    private async Task<SourceDefinition> ImportBilibiliAsync(JsonElement site, string id, string name, Uri origin, CancellationToken token)
    {
        var source = new SourceDefinition { Id = id, Name = name, Provider = "bilibili" };
        if (!site.TryGetProperty("ext", out var ext) || ext.ValueKind != JsonValueKind.Object || Text(ext, "json").Length == 0) return source;
        string data = await ReadConfigurationAsync(new Uri(origin, Text(ext, "json")), token);
        using var document = JsonDocument.Parse(data, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        if (!document.RootElement.TryGetProperty("class", out var classes) || classes.ValueKind != JsonValueKind.Array || classes.GetArrayLength() > 500)
            throw new InvalidDataException("未识别的哔哩哔哩分类格式");
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray(); int index = 0;
            foreach (var item in classes.EnumerateArray())
            {
                string query = Text(item, "type_id"), title = Text(item, "type_name");
                if (query == "peizhi" || string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(title)) continue;
                writer.WriteStartObject(); writer.WriteString("id", $"import-{index++}"); writer.WriteString("name", title); writer.WriteString("query", query); writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        using var categories = JsonDocument.Parse(buffer.ToArray()); source.Options["categories"] = categories.RootElement.Clone();
        return source;
    }
}
