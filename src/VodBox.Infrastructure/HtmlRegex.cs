using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace VodBox.Infrastructure;

/// <summary>
/// HTML 爬虫的按模式缓存正则（Singleline | IgnoreCase | CultureInvariant，2 秒超时）。
/// <c>new Regex</c> 每次都要重新解析模式，而 Text/Attr 一类辅助方法每张卡片都会调用；缓存后整页解析只构造一次。
/// 调用方只能传入由固定标签/类名/属性名拼出的模式，不得拼接站点返回的内容，以免缓存无界增长。
/// </summary>
internal static class HtmlRegex
{
    private static readonly ConcurrentDictionary<string, Regex> Cache = new(StringComparer.Ordinal);

    public static Regex Get(string pattern) => Cache.GetOrAdd(pattern, static value =>
        new Regex(value, RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2)));
}
