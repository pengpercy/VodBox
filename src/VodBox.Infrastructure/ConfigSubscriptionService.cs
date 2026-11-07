using System.Net;
using VodBox.Core;

namespace VodBox.Infrastructure;

/// <summary>
/// 配置订阅（LunaTV admin/config_subscription）：拉取订阅地址 → JSON 或 base58 解码 → 校验 → 合并进源配置。
/// 也接受本地文件路径与直接粘贴的配置 JSON（LunaTV「配置文件」编辑框）。
/// </summary>
public sealed class ConfigSubscriptionService(SourceConfigStore store, HttpMessageHandler? handler = null) : IDisposable
{
    private readonly HttpClient _http = new(handler ?? new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All, ConnectTimeout = TimeSpan.FromSeconds(8) })
        { Timeout = TimeSpan.FromSeconds(20) };
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>自动更新的最短间隔：启动时若距上次拉取不足该时长则跳过。</summary>
    public static readonly TimeSpan AutoUpdateInterval = TimeSpan.FromHours(1);

    public sealed record Result(int Sources, int Categories, int Lives);

    /// <summary>拉取订阅原文（URL 或本地路径）并解码为配置 JSON。</summary>
    public async Task<string> FetchAsync(string address, CancellationToken ct = default)
    {
        address = address.Trim();
        string content;
        if (LocalMedia.TryResolveFile(address, out var path)) content = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        else
        {
            if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) throw new ArgumentException("订阅地址必须是 HTTP(S) 地址或本地文件。");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.TryAddWithoutValidation("User-Agent", CmsClient.UserAgent);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new HttpRequestException($"请求失败：HTTP {(int)response.StatusCode}", null, response.StatusCode);
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            content = TextEncoding.Decode(await BoundedContent.ReadAsync(stream, 8 * 1024 * 1024, ct).ConfigureAwait(false));
        }
        return SourceConfigParser.Decode(content);
    }

    /// <summary>应用一份配置 JSON（粘贴或已拉取），返回统计。</summary>
    public Result ApplyText(string json)
    {
        var decoded = SourceConfigParser.Decode(json);
        var file = SourceConfigParser.Parse(decoded);
        SourceConfigParser.Validate(file);
        store.ApplyConfigFile(decoded);
        return new Result(file.ApiSite?.Count ?? 0, file.CustomCategory?.Count ?? 0, file.Lives?.Count ?? 0);
    }

    /// <summary>拉取并应用订阅，记录订阅地址与拉取时间。</summary>
    public async Task<Result> SubscribeAsync(string address, bool autoUpdate, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var json = await FetchAsync(address, ct).ConfigureAwait(false);
            var result = ApplyText(json);
            store.SetSubscription(new SubscriptionState { Url = address.Trim(), AutoUpdate = autoUpdate, LastCheck = DateTimeOffset.Now });
            VodBoxLog.Info("config", $"订阅已更新：{result.Sources} 个采集源，{result.Lives} 个直播源");
            return result;
        }
        finally { _gate.Release(); }
    }

    /// <summary>启动时自动更新：开启了自动更新且距上次拉取超过间隔才拉取；返回是否有更新。失败只记日志。</summary>
    public async Task<bool> RefreshIfDueAsync(CancellationToken ct = default)
    {
        var subscription = store.Data.Subscription;
        if (!subscription.AutoUpdate || string.IsNullOrWhiteSpace(subscription.Url)) return false;
        if (subscription.LastCheck is { } last && DateTimeOffset.Now - last < AutoUpdateInterval && store.Sources.Count > 0) return false;
        try
        {
            var before = store.Data.ConfigFile;
            await SubscribeAsync(subscription.Url, true, ct).ConfigureAwait(false);
            return store.Data.ConfigFile != before;
        }
        catch (Exception error) when (error is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            VodBoxLog.Warn("config", $"订阅自动更新失败，继续使用本地配置：{error.Message}");
            return false;
        }
    }

    public void Dispose() { _http.Dispose(); _gate.Dispose(); }
}
