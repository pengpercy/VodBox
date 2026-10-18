using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace VodBox.Infrastructure;

/// <summary>
/// 本机回环 Range 服务的公共部分（<see cref="TorrentStreamServer"/>、<see cref="AiDjMediaProxy"/>）：
/// 随机端口 Kestrel、只接受本机且 Host 精确匹配的请求、单段 bytes Range 解析与 200/206/416 响应头。
/// </summary>
internal static class LoopbackRangeServer
{
    /// <summary>在 127.0.0.1 随机端口启动只挂一条 GET/HEAD 路由的 Kestrel，返回应用与基地址。</summary>
    public static async Task<(WebApplication App, string Address)> StartAsync(string pattern, RequestDelegate handler, CancellationToken ct)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        app.MapMethods(pattern, ["GET", "HEAD"], handler);
        try
        {
            await app.StartAsync(ct).ConfigureAwait(false);
            return (app, app.Urls.Single());
        }
        catch { await app.DisposeAsync().ConfigureAwait(false); throw; }
    }

    /// <summary>请求来自本机且 Host 与监听地址一致（挡住 DNS rebinding 一类经浏览器转来的请求）。</summary>
    public static bool IsLocalRequest(HttpContext context, string? address) =>
        IPAddress.IsLoopback(context.Connection.RemoteIpAddress ?? IPAddress.None) && address is not null &&
        Uri.TryCreate(address, UriKind.Absolute, out var local) &&
        string.Equals(context.Request.Host.Value, local.Authority, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 按 Range 头写好状态码与响应头。返回 false 表示响应已完整（416 或 HEAD），调用方不再写正文；
    /// 否则 <paramref name="start"/>..<paramref name="end"/>（含）是要写出的字节区间。
    /// </summary>
    public static bool BeginResponse(HttpContext context, long total, string contentType, out long start, out long end)
    {
        start = 0; end = total - 1;
        bool ranged = context.Request.Headers.TryGetValue("Range", out var range);
        if (ranged && !ParseRange(range.ToString(), total, out start, out end))
        {
            context.Response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
            context.Response.Headers.ContentRange = "bytes */" + total;
            return false;
        }
        context.Response.ContentType = contentType;
        context.Response.ContentLength = end - start + 1;
        context.Response.Headers.AcceptRanges = "bytes";
        context.Response.Headers.CacheControl = "no-store";
        context.Response.StatusCode = ranged ? StatusCodes.Status206PartialContent : StatusCodes.Status200OK;
        if (ranged) context.Response.Headers.ContentRange = $"bytes {start}-{end}/{total}";
        return !HttpMethods.IsHead(context.Request.Method);
    }

    internal static bool ParseRange(string text, long total, out long start, out long end)
    {
        start = 0; end = total - 1;
        if (total <= 0 || !text.StartsWith("bytes=", StringComparison.Ordinal) || text.Contains(',')) return false;
        var parts = text[6..].Split('-'); if (parts.Length != 2) return false;
        if (parts[0].Length == 0)
        {
            if (!long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var suffix) || suffix <= 0) return false;
            start = Math.Max(0, total - suffix); return true;
        }
        if (!long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out start) || start >= total) return false;
        if (parts[1].Length > 0)
        { if (!long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out end) || end < start) return false; end = Math.Min(end, total - 1); }
        return true;
    }
}
