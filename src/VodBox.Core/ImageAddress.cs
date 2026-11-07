namespace VodBox.Core;

/// <summary>图片地址约定写法。</summary>
public static class ImageAddress
{
    /// <summary>
    /// 拆出图片地址后缀里的请求头：<c>https://img/x.jpg@Referer=https://a/@User-Agent=Mozilla/5.0 ...</c>。
    /// 没有后缀时原样返回、头为空。只认 Referer / User-Agent / Cookie 这几个常见键，避免把路径里的 @ 误切。
    /// </summary>
    public static (string Url, IReadOnlyDictionary<string, string> Headers) SplitHeaders(string address)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int first = -1;
        foreach (var key in new[] { "@Referer=", "@User-Agent=", "@Cookie=" })
        {
            int index = address.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (index >= 0 && (first < 0 || index < first)) first = index;
        }
        if (first < 0) return (address, headers);
        foreach (var part in address[(first + 1)..].Split('@', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = part.IndexOf('=');
            if (equals <= 0) continue;
            string name = part[..equals], value = part[(equals + 1)..];
            if (name is "Referer" or "User-Agent" or "Cookie" && value.IndexOfAny(['\r', '\n', '\0']) < 0) headers[name] = value;
        }
        return (address[..first], headers);
    }
}
