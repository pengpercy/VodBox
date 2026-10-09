using System.Globalization;
using VodBox.Core;

namespace VodBox.Infrastructure;

/// <summary>显式 catchup-source 模板；只支持已知变量，不猜测服务端协议。</summary>
public static class CatchupResolver
{
    public static string Resolve(LiveChannel channel, Programme programme, DateTimeOffset now)
    {
        if (programme.Start >= now || programme.End > now || programme.End <= programme.Start)
            throw new InvalidOperationException("只能回看已经播出的完整节目。");
        if (string.IsNullOrWhiteSpace(channel.CatchupSource)) throw new NotSupportedException("当前频道未配置回看地址。");
        if (channel.CatchupDays <= 0 || programme.Start < now.AddDays(-channel.CatchupDays))
            throw new InvalidOperationException("节目超出频道允许的回看期限。");
        var url = channel.CatchupSource
            .Replace("{utc}", programme.Start.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture))
            .Replace("{utcend}", programme.End.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture))
            .Replace("{duration}", ((long)(programme.End - programme.Start).TotalSeconds).ToString(CultureInfo.InvariantCulture))
            .Replace("{start}", programme.Start.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture))
            .Replace("{end}", programme.End.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        if (url.Contains('{') || url.Contains('}') || !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https")) throw new InvalidDataException("回看模板含不支持的变量或地址。");
        return uri.AbsoluteUri;
    }
}
