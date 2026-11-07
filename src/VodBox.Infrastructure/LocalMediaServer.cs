using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace VodBox.Infrastructure;

/// <summary>
/// 本机媒体代理：只监听 127.0.0.1、只挂 <c>/proxy</c> 与 <c>/cache</c>。
/// 播放器无法直传的请求头（Cookie/Authorization 等）、HLS 去广告与媒体缓存都经它转发；
/// 不提供任何遥控、配对或上传接口。按需启动（首次注册播放地址时）。
/// </summary>
public sealed class LocalMediaServer : IAsyncDisposable
{
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private WebApplication? _app;

    public LocalMediaServer(string? cacheDirectory = null) => Proxy = new MediaProxy(cacheDirectory);

    public MediaProxy Proxy { get; }

    /// <summary>已启动时的基地址（如 <c>http://127.0.0.1:52011</c>），未启动为 null。</summary>
    public string? Address { get; private set; }

    /// <summary>确保代理在回环上运行并返回基地址。</summary>
    public async Task<string> StartAsync(CancellationToken ct = default)
    {
        if (Address is { } running) return running;
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Address is { } existing) return existing;
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseKestrel(options => options.Listen(System.Net.IPAddress.Loopback, 0));
            var app = builder.Build();
            app.Use(async (context, next) =>
            {
                // 仅回环来源、且 Host 必须是回环地址：避免被本机以外的请求（含 DNS 重绑定）触达。
                if (context.Request.Host.Host is not ("127.0.0.1" or "localhost" or "[::1]") ||
                    !System.Net.IPAddress.IsLoopback(context.Connection.RemoteIpAddress ?? System.Net.IPAddress.None))
                { context.Response.StatusCode = 403; return; }
                await next();
            });
            app.MapGet("/proxy/{id}", async (HttpContext context) =>
            {
                try { await Proxy.ServeAsync(context); }
                catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
                catch (Exception error) when (error is HttpRequestException or InvalidDataException or NotSupportedException or System.Xml.XmlException or UriFormatException or TaskCanceledException)
                {
                    if (!context.Response.HasStarted) { context.Response.Clear(); context.Response.StatusCode = 502; }
                    else context.Abort();
                }
            });
            app.MapGet("/cache/{id}", async (HttpContext context) =>
            {
                try { await Proxy.ServeCachedAsync(context); }
                catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
                catch (Exception error) when (error is HttpRequestException or InvalidDataException or IOException or TaskCanceledException)
                {
                    if (!context.Response.HasStarted) { context.Response.Clear(); context.Response.StatusCode = 502; }
                    else context.Abort();
                }
            });
            await app.StartAsync(ct).ConfigureAwait(false);
            _app = app;
            Address = "http://127.0.0.1:" + new Uri(app.Urls.Single()).Port;
            return Address;
        }
        finally { _lifecycle.Release(); }
    }

    /// <summary>注册媒体并返回代理地址（需已 <see cref="StartAsync"/>）。</summary>
    public string RegisterMedia(string url, IReadOnlyDictionary<string, string>? headers = null, bool filterAds = false) => Address is null
        ? throw new InvalidOperationException("媒体代理尚未启动。") : Address + Proxy.Register(url, headers, filterAds);

    /// <summary>注册走磁盘缓存的直链（<c>/cache/</c>）。</summary>
    public string RegisterCachedMedia(string url, IReadOnlyDictionary<string, string>? headers = null) => Address is null
        ? throw new InvalidOperationException("媒体代理尚未启动。") : Address + Proxy.Register(url, headers).Replace("/proxy/", "/cache/", StringComparison.Ordinal);

    /// <summary>
    /// 注册要播放的媒体并返回本机代理地址（按需启动）。代理按来源域隔离、跨源跳转/清单子资源剥离凭据。
    /// </summary>
    public async Task<string> RegisterPlaybackMediaAsync(string url, IReadOnlyDictionary<string, string>? headers = null, CancellationToken ct = default, bool filterAds = false)
    {
        var address = await StartAsync(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        var handle = Proxy.Register(url, headers, filterAds);
        // 只有清单需要扩展名（让 FFmpeg 按 HLS/DASH 探测）；直链保持无扩展名句柄。
        return address + (Path.GetExtension(new Uri(url).AbsolutePath).ToLowerInvariant() is ".m3u8" or ".mpd" ? MediaProxy.WithMediaExtension(handle, url) : handle);
    }

    /// <summary>停止监听并作废全部已注册地址（可再次 <see cref="StartAsync"/>）。</summary>
    public async ValueTask DisposeAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            var app = _app; _app = null; Address = null; Proxy.Clear();
            if (app is null) return;
            await app.StopAsync().ConfigureAwait(false);
            await app.DisposeAsync().ConfigureAwait(false);
        }
        finally { _lifecycle.Release(); }
    }
}
