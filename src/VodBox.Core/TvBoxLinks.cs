namespace VodBox.Core;

/// <summary>TVBox 卡片/图片地址里的约定写法。</summary>
public static class TvBoxLinks
{
    private const string SearchPrefix = "msearch:";

    /// <summary>
    /// 「搜索跳转」卡片：<c>msearch:###片名###图片</c>（豆瓣等导航源）。点击不进详情，而是用片名发起全局搜索。
    /// </summary>
    public static string SearchId(string title, string? poster) => SearchPrefix + "###" + title + "###" + (poster ?? "");

    public static bool TryGetSearchKeyword(string? mediaId, out string keyword)
    {
        keyword = "";
        if (mediaId is null || !mediaId.StartsWith(SearchPrefix, StringComparison.Ordinal)) return false;
        var parts = mediaId[SearchPrefix.Length..].Split("###", StringSplitOptions.None);
        keyword = (parts.Length > 1 ? parts[1] : parts[0]).Trim();
        return keyword.Length > 0;
    }

    /// <summary>
    /// 拆出图片地址后缀里的请求头：<c>https://img/x.jpg@Referer=https://a/@User-Agent=Mozilla/5.0 ...</c>。
    /// 没有后缀时原样返回、头为空。只认 Referer / User-Agent / Cookie 这几个常见键，避免把路径里的 @ 误切。
    /// </summary>
    public static (string Url, IReadOnlyDictionary<string, string> Headers) SplitImageHeaders(string address)
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
